using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace SteamNewsFeed;

public sealed class SteamException : Exception
{
	public int Status;

	public SteamException(int status, string message) : base(message)
	{
		Status = status;
	}
}

/// <summary>所有 WebView2 共用一个浏览器环境（同一份 Steam 登录状态，存在 exe 旁边的 webview2 文件夹）。</summary>
public static class WebEnv
{
	private static Task<CoreWebView2Environment> _env;

	/// <summary>主窗口的句柄：后台读 cookie、续期登录用的隐藏浏览器挂在它下面（窗口没显示也有句柄）。</summary>
	public static IntPtr HostHwnd;

	public static Task<CoreWebView2Environment> GetAsync()
	{
		return _env ??= CoreWebView2Environment.CreateAsync(null, Paths.WebData);
	}
}

/// <summary>
/// 访问 Steam 商店：用户在内嵌的 WebView2 里登录一次 Steam，之后用它的 cookie 直接发请求。
/// - 新闻流：events/ajaxgetusereventcalendarrange（和 Steam 新闻中心同一个接口）+ events/ajaxgeteventdetails
/// - 关注的发行商 / 鉴赏家、愿望单：dynamicstore/userdata
/// - 发行商的游戏：curator/{id}/ajaxgetfilteredrecommendations?sort=newreleases
/// - 游戏信息：公开的 IStoreBrowseService/GetItems（一次多个）
/// - 加入 / 移出愿望单：网页上那个按钮用的 api/addtowishlist（sessionid 表单）
/// 登录票据（steamLoginSecure）大约一天过期，快过期时用隐藏的 WebView2 打开一次商店页面，Steam 会自己换新票据。
/// </summary>
public static class Steam
{
	public const string StoreUrl = "https://store.steampowered.com";

	public const string Api = "https://api.steampowered.com";

	public const string ClanCdn = "https://clan.akamai.steamstatic.com/images/";

	public const string AssetCdn = "https://shared.akamai.steamstatic.com/store_item_assets/";

	public const string AvatarCdn = "https://avatars.akamai.steamstatic.com/";

	/// <summary>clan 的 64 位 id 减去这个数就是 clan 的账号 id（网址里用的那个）。</summary>
	public const long ClanBase = 103582791429521408L;

	private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

	private static readonly HttpClient Http = CreateClient();

	private static Dictionary<string, string> _cookies = new Dictionary<string, string>();

	private static readonly object CookieLock = new object();

	public static string SteamId { get; private set; } = "";

	public static bool LoggedIn => SteamId.Length > 0;

	/// <summary>steamLoginSecure 的到期时间（UTC，JWT 里的 exp）。</summary>
	public static DateTime LoginExpires { get; private set; } = DateTime.MinValue;

	public static bool LoginFresh => LoggedIn && LoginExpires > DateTime.UtcNow;

	public static event Action AuthChanged;

	private static Task _renewing;

	private static DateTime _lastRenew = DateTime.MinValue;

	private static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
		var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
		return c;
	}

	// ---- 登录状态 ----

	/// <summary>从 WebView2 复制商店的 cookie，并据此判断登录的是谁。在界面线程上调用。</summary>
	public static async Task SyncCookiesAsync(CoreWebView2 core)
	{
		var map = new Dictionary<string, string>();
		try
		{
			foreach (CoreWebView2Cookie c in await core.CookieManager.GetCookiesAsync(StoreUrl))
			{
				map[c.Name] = c.Value;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("读取 Steam cookie 失败：" + ex.Message);
		}
		string id = "";
		DateTime expires = DateTime.MinValue;
		if (map.TryGetValue("steamLoginSecure", out string sls))
		{
			string decoded = Uri.UnescapeDataString(sls);
			int bar = decoded.IndexOf('|');
			if (bar > 0 && Regex.IsMatch(decoded.Substring(0, bar), "^\\d{17}$"))
			{
				id = decoded.Substring(0, bar);
			}
			expires = JwtExpiry(decoded.Substring(decoded.LastIndexOf('|') + 1));
		}
		// sessionid 只是防跨站的随机值，表单里和 cookie 里一样即可；还没有时自己生成一个
		if (id.Length > 0 && !map.ContainsKey("sessionid"))
		{
			string sid = RandomHex(24);
			map["sessionid"] = sid;
			try
			{
				core.CookieManager.AddOrUpdateCookie(core.CookieManager.CreateCookie("sessionid", sid, "store.steampowered.com", "/"));
			}
			catch
			{
			}
		}
		string old;
		lock (CookieLock)
		{
			_cookies = map;
			old = SteamId;
			SteamId = id;
			LoginExpires = expires;
		}
		if (id != old)
		{
			Logger.Log(id.Length > 0 ? "已登录 Steam " + id + "（票据到 " + expires.ToLocalTime().ToString("MM-dd HH:mm") + "）" : "未登录 Steam");
			AuthChanged?.Invoke();
		}
	}

	/// <summary>用一个看不见的浏览器读一次 cookie（启动时、登录窗口关闭后）。在界面线程上调用。</summary>
	public static async Task SyncCookiesHiddenAsync()
	{
		await WithHiddenBrowserAsync(async core => await SyncCookiesAsync(core));
	}

	public static async Task LogoutAsync()
	{
		await WithHiddenBrowserAsync(async core =>
		{
			core.CookieManager.DeleteAllCookies();
			await SyncCookiesAsync(core);
		});
	}

	/// <summary>在主窗口下面临时建一个看不见的浏览器（和页面共用登录状态）。</summary>
	private static async Task WithHiddenBrowserAsync(Func<CoreWebView2, Task> action)
	{
		if (WebEnv.HostHwnd == IntPtr.Zero)
		{
			return;
		}
		CoreWebView2Controller ctl = null;
		try
		{
			CoreWebView2Environment env = await WebEnv.GetAsync();
			ctl = await env.CreateCoreWebView2ControllerAsync(WebEnv.HostHwnd);
			ctl.IsVisible = false;
			await action(ctl.CoreWebView2);
		}
		catch (Exception ex)
		{
			Logger.Log("隐藏浏览器出错：" + ex.Message);
		}
		finally
		{
			try
			{
				ctl?.Close();
			}
			catch
			{
			}
		}
	}

	/// <summary>JWT 里的 exp（UTC）；读不出来时当作还有效。</summary>
	private static DateTime JwtExpiry(string jwt)
	{
		try
		{
			string[] parts = jwt.Split('.');
			string b64 = parts[1].Replace('-', '+').Replace('_', '/');
			b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
			JObject payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
			long exp = payload.Value<long?>("exp") ?? 0;
			if (exp > 0)
			{
				return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
			}
		}
		catch
		{
		}
		return DateTime.MaxValue;
	}

	/// <summary>登录票据快到期（或已过期）时续期，几个请求同时发现时只续一次。</summary>
	public static async Task EnsureFreshAsync(bool force = false)
	{
		if (!LoggedIn || (!force && LoginExpires > DateTime.UtcNow.AddMinutes(10)))
		{
			return;
		}
		Task t = _renewing;
		if (t == null || t.IsCompleted)
		{
			// 续期失败（长期的刷新票据也失效了）时别每个请求都重试
			if ((DateTime.UtcNow - _lastRenew).TotalMinutes < 10)
			{
				return;
			}
			_lastRenew = DateTime.UtcNow;
			_renewing = t = RenewOnceAsync();
		}
		await t;
	}

	private static async Task RenewOnceAsync()
	{
		DateTime before = LoginExpires;
		try
		{
			Application app = Application.Current;
			if (app == null)
			{
				return;
			}
			await await app.Dispatcher.InvokeAsync(RenewOnUiAsync);
		}
		catch (Exception ex)
		{
			Logger.Log("续期 Steam 登录出错：" + ex.Message);
		}
		Logger.Log(LoginExpires > DateTime.UtcNow.AddMinutes(10) ? "Steam 登录已续期，到 " + LoginExpires.ToLocalTime().ToString("MM-dd HH:mm") : "Steam 登录续期失败（票据到 " + before.ToLocalTime().ToString("MM-dd HH:mm") + "），需要重新登录");
	}

	/// <summary>
	/// 在看不见的浏览器里打开一次商店页面：票据过期时 Steam 会跳到 login.steampowered.com 用长期的刷新票据换一张新的再跳回来；
	/// 然后重新读 cookie。
	/// </summary>
	private static async Task RenewOnUiAsync()
	{
		await WithHiddenBrowserAsync(async core =>
		{
			var back = new TaskCompletionSource<bool>();
			core.NewWindowRequested += (s, e) => e.Handled = true;
			core.NavigationCompleted += (s, e) =>
			{
				string url = core.Source ?? "";
				if (url.StartsWith(StoreUrl, StringComparison.OrdinalIgnoreCase) && url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) < 0)
				{
					back.TrySetResult(true);
				}
			};
			core.Navigate(StoreUrl + "/account/");
			await Task.WhenAny(back.Task, Task.Delay(TimeSpan.FromSeconds(30)));
			await SyncCookiesAsync(core);
		});
	}

	private static string RandomHex(int n)
	{
		var bytes = new byte[n / 2];
		using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
		{
			rng.GetBytes(bytes);
		}
		return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
	}

	private static string CookieHeader()
	{
		lock (CookieLock)
		{
			return string.Join("; ", _cookies.Select(kv => kv.Key + "=" + kv.Value));
		}
	}

	private static string SessionId
	{
		get
		{
			lock (CookieLock)
			{
				return _cookies.TryGetValue("sessionid", out string s) ? s : "";
			}
		}
	}

	// ---- http ----

	/// <summary>GET 商店（带登录 cookie）。</summary>
	public static async Task<string> GetStoreAsync(string pathAndQuery)
	{
		await EnsureFreshAsync().ConfigureAwait(false);
		string url = pathAndQuery.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathAndQuery : StoreUrl + pathAndQuery;
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", CookieHeader());
		req.Headers.Referrer = new Uri(StoreUrl + "/news/");
		return await SendAsync(req).ConfigureAwait(false);
	}

	public static async Task<JObject> GetStoreJsonAsync(string pathAndQuery)
	{
		string text = await GetStoreAsync(pathAndQuery).ConfigureAwait(false);
		return ParseJson(text, pathAndQuery);
	}

	/// <summary>GET 不需要登录的地址（公开接口、汇率等）。</summary>
	public static async Task<string> GetPublicAsync(string url)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		return await SendAsync(req).ConfigureAwait(false);
	}

	public static async Task<JObject> GetPublicJsonAsync(string url)
	{
		return ParseJson(await GetPublicAsync(url).ConfigureAwait(false), url);
	}

	/// <summary>POST 不需要登录的表单（社区的评论）。</summary>
	public static async Task<JObject> PostPublicAsync(string url, Dictionary<string, string> form)
	{
		using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
		return ParseJson(await SendAsync(req).ConfigureAwait(false), url);
	}

	/// <summary>POST 商店的表单（自动带上 sessionid）。</summary>
	public static async Task<JObject> PostStoreAsync(string path, Dictionary<string, string> form)
	{
		if (!LoggedIn)
		{
			throw new SteamException(401, "还没有登录 Steam");
		}
		await EnsureFreshAsync().ConfigureAwait(false);
		form["sessionid"] = SessionId;
		using var req = new HttpRequestMessage(HttpMethod.Post, StoreUrl + path) { Content = new FormUrlEncodedContent(form) };
		req.Headers.Add("Cookie", CookieHeader());
		req.Headers.Add("Origin", StoreUrl);
		req.Headers.Referrer = new Uri(StoreUrl + "/news/");
		return ParseJson(await SendAsync(req).ConfigureAwait(false), path);
	}

	private static async Task<string> SendAsync(HttpRequestMessage req)
	{
		HttpResponseMessage resp;
		try
		{
			resp = await Http.SendAsync(req).ConfigureAwait(false);
		}
		catch (TaskCanceledException)
		{
			throw new SteamException(0, "连接 Steam 超时（网络不通或需要代理）");
		}
		catch (HttpRequestException ex)
		{
			throw new SteamException(0, "连不上 Steam：" + (ex.InnerException?.Message ?? ex.Message));
		}
		using (resp)
		{
			string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				int code = (int)resp.StatusCode;
				throw new SteamException(code, code == 429 ? "Steam 说请求太频繁了，过一会儿再试" : "Steam 返回 " + code);
			}
			return text;
		}
	}

	private static JObject ParseJson(string text, string what)
	{
		try
		{
			return JObject.Parse(text);
		}
		catch (Exception)
		{
			string head = text.TrimStart();
			if (head.StartsWith("<"))
			{
				throw new SteamException(0, "Steam 返回了网页而不是数据（可能需要重新登录）");
			}
			throw new SteamException(0, "看不懂 Steam 的返回：" + what);
		}
	}

	// ---- 登录窗口 ----

	/// <summary>
	/// 弹出一个窗口显示 Steam 官方登录页，用户自己登录（账号密码、手机令牌、扫码都在 Steam 页面里完成），登录成功后自动关闭。
	/// </summary>
	public static async Task ShowLoginAsync(Window owner)
	{
		// 可能在页面消息的回调里被调用：WebView2 不允许在那里创建新的 WebView，先离开回调
		await Task.Yield();
		CoreWebView2Environment env = await WebEnv.GetAsync();
		var web = new Microsoft.Web.WebView2.Wpf.WebView2();
		var win = new Window
		{
			Title = "登录 Steam",
			Width = 560,
			Height = 760,
			WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
			Owner = owner != null && owner.IsVisible ? owner : null,
			Content = web
		};
		bool done = false;
		var closed = new TaskCompletionSource<bool>();
		win.Closed += delegate { closed.TrySetResult(true); };
		win.SourceInitialized += delegate
		{
			try
			{
				if (Program.WantDark())
				{
					Wpf.Ui.Appearance.WindowBackgroundManager.ApplyDarkThemeToWindow(win);
				}
			}
			catch
			{
			}
		};
		win.Loaded += async delegate
		{
			try
			{
				await web.EnsureCoreWebView2Async(env);
				web.CoreWebView2.NewWindowRequested += (s, e) =>
				{
					e.Handled = true;
					FeedView.OpenExternal(e.Uri);
				};
				web.CoreWebView2.NavigationCompleted += async (s, e) =>
				{
					string url = web.CoreWebView2.Source ?? "";
					if (done || url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 || !url.StartsWith(StoreUrl, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
					await SyncCookiesAsync(web.CoreWebView2);
					// 过期的旧票据也带着账号 id，要等 Steam 换成新的才算登录好了
					if (LoginFresh)
					{
						done = true;
						win.Close();
					}
				};
				web.CoreWebView2.Navigate(StoreUrl + "/login/?redir=" + Uri.EscapeDataString("news/"));
			}
			catch (Exception ex)
			{
				Logger.Log("Steam 登录窗口出错：" + ex.Message);
				win.Close();
			}
		};
		win.Show();
		await closed.Task;
		web.Dispose();
		await SyncCookiesHiddenAsync();
	}

	// ---- 小工具 ----

	public static int ClanAccount(string clanSteamId)
	{
		return long.TryParse(clanSteamId, out long v) && v > ClanBase ? (int)(v - ClanBase) : 0;
	}

	/// <summary>加入 / 移出愿望单。</summary>
	public static async Task<bool> SetWishlistAsync(int appid, bool on)
	{
		JObject r = await PostStoreAsync(on ? "/api/addtowishlist" : "/api/removefromwishlist", new Dictionary<string, string> { ["appid"] = appid.ToString() }).ConfigureAwait(false);
		if (r.Value<bool?>("success") != true)
		{
			throw new SteamException(0, on ? "加入愿望单失败（可能需要重新登录）" : "移出愿望单失败（可能需要重新登录）");
		}
		return on;
	}
}
