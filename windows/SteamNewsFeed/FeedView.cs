using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Appearance;

namespace SteamNewsFeed;

/// <summary>
/// 「时间线」页：一个透明的 WebView2，显示内嵌的 feed.html。页面用 postMessage 调用 Dispatch 里的方法。
/// 页面里的链接都用系统默认浏览器打开。
/// </summary>
public sealed class FeedView : Grid
{
	public const string Host = "https://snf.local/";

	private readonly MainWindow _main;

	private readonly WebView2 _web;

	private readonly TextBlock _message;

	private bool _started;

	private bool _ready;

	private readonly Queue<string> _outbox = new Queue<string>();

	public FeedView(MainWindow main)
	{
		_main = main;
		// 右边和下边留几像素给窗口自己：WebView2 是单独的子窗口，会把鼠标抢走，贴边的话窗口右边、下边就拖不动改大小了
		_web = new WebView2 { Visibility = Visibility.Hidden, Margin = new Thickness(0, 0, 6, 6) };
		_message = new TextBlock
		{
			Text = "正在加载…",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 460,
			TextAlignment = TextAlignment.Center
		};
		_message.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		Children.Add(_message);
		Children.Add(_web);
	}

	public async void Start()
	{
		if (_started)
		{
			return;
		}
		_started = true;
		try
		{
			CoreWebView2Environment env = await WebEnv.GetAsync();
			await _web.EnsureCoreWebView2Async(env);
			CoreWebView2 core = _web.CoreWebView2;
			core.Settings.IsStatusBarEnabled = false;
			core.Settings.IsZoomControlEnabled = false;
			core.Settings.AreDefaultContextMenusEnabled = true;
			// 透明：窗口的亚克力背景透出来
			_web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
			core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
			core.WebResourceRequested += OnResource;
			core.WebMessageReceived += OnMessage;
			core.NewWindowRequested += (s, e) =>
			{
				e.Handled = true;
				OpenExternal(e.Uri);
			};
			core.NavigationStarting += (s, e) =>
			{
				if (!e.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
				{
					e.Cancel = true;
					OpenExternal(e.Uri);
				}
			};
			core.NavigationCompleted += (s, e) =>
			{
				_web.Visibility = Visibility.Visible;
				_message.Visibility = Visibility.Collapsed;
			};
			core.Navigate(Host + "feed.html?theme=" + (Dark() ? "dark" : "light") + "&accent=" + Uri.EscapeDataString(AccentHex()));
		}
		catch (Exception ex)
		{
			Logger.Log("页面初始化失败：" + ex);
			_message.Text = "页面打不开：" + ex.Message + "\n\n需要 Microsoft Edge WebView2 运行时（Windows 11 自带；Windows 10 可到微软官网下载安装）。";
		}
	}

	public static void OpenExternal(string uri)
	{
		try
		{
			if (Uri.TryCreate(uri, UriKind.Absolute, out Uri u) && (u.Scheme == "https" || u.Scheme == "http"))
			{
				Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
			}
		}
		catch (Exception ex)
		{
			Logger.Log("打开链接失败：" + ex.Message);
		}
	}

	private static bool Dark() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

	public static string AccentHex()
	{
		try
		{
			System.Windows.Media.Color c = ApplicationAccentColorManager.PrimaryAccent;
			return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
		}
		catch
		{
			return "#60cdff";
		}
	}

	public void PostTheme() => Post(new JObject { ["event"] = "theme", ["theme"] = Dark() ? "dark" : "light", ["accent"] = AccentHex() });

	public void PostStatus() => Post(new JObject { ["event"] = "status", ["status"] = StatusJson() });

	/// <summary>刷新完成、有新内容：页面在顶部时直接重新加载，否则提示有几条新的。</summary>
	public void PostUpdated(bool manual = false) => Post(new JObject { ["event"] = "updated", ["manual"] = manual });

	/// <summary>退出前：让页面马上交出阅读进度和位置并存盘（不等页面里 0.8 秒的延迟保存）。</summary>
	public async Task FlushStateAsync()
	{
		if (!_ready || _web.CoreWebView2 == null)
		{
			return;
		}
		try
		{
			Task<string> run = _web.CoreWebView2.ExecuteScriptAsync("window.snfFlush ? window.snfFlush() : null");
			if (await Task.WhenAny(run, Task.Delay(2000)) != run)
			{
				return;
			}
			if (JToken.Parse(run.Result) is JObject state)
			{
				lock (Store.Lock)
				{
					Store.St.Page = state;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("退出前保存阅读进度失败：" + ex.Message);
		}
	}

	public void PostUnread(int n) => Post(new JObject { ["event"] = "unread", ["n"] = n });

	/// <summary>托盘菜单的「全部标为已读」等：让页面执行一个动作。</summary>
	public void PostAction(string action) => Post(new JObject { ["event"] = "action", ["action"] = action });

	private static JObject StatusJson()
	{
		JObject s = Feed.StatusJson();
		s["ready"] = MainWindow.FirstRefreshDone;
		return s;
	}

	private void Post(JObject o)
	{
		string json = o.ToString(Formatting.None);
		if (!_ready || _web.CoreWebView2 == null)
		{
			if (o.Value<string>("event") != "status")
			{
				_outbox.Enqueue(json);
			}
			return;
		}
		try
		{
			_web.CoreWebView2.PostWebMessageAsJson(json);
		}
		catch
		{
		}
	}

	private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
		Stream s = path == "feed.html" ? typeof(FeedView).Assembly.GetManifestResourceStream("SteamNewsFeed.feed.html") : null;
		CoreWebView2Environment env = _web.CoreWebView2.Environment;
		e.Response = s == null
			? env.CreateWebResourceResponse(null, 404, "Not Found", "")
			: env.CreateWebResourceResponse(s, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
	}

	private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		if (!e.Source.StartsWith(Host, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		JObject msg;
		try
		{
			msg = JObject.Parse(e.WebMessageAsJson);
		}
		catch
		{
			return;
		}
		string method = msg.Value<string>("method");
		if (method == "ready")
		{
			_ready = true;
			while (_outbox.Count > 0)
			{
				try
				{
					_web.CoreWebView2.PostWebMessageAsJson(_outbox.Dequeue());
				}
				catch
				{
				}
			}
			PostStatus();
			return;
		}
		if (method == "log")
		{
			Logger.Log("页面：" + msg["args"]?.Value<string>("message"));
			return;
		}
		var reply = new JObject { ["id"] = msg["id"] };
		try
		{
			reply["result"] = await Dispatch(method, msg["args"] as JObject ?? new JObject());
			reply["ok"] = true;
		}
		catch (Exception ex)
		{
			if (!(ex is SteamException))
			{
				Logger.Log("页面调用 " + method + " 出错：" + ex);
			}
			reply["ok"] = false;
			reply["error"] = ex.Message;
			reply["status"] = ex is SteamException se ? se.Status : 0;
		}
		try
		{
			_web.CoreWebView2?.PostWebMessageAsJson(reply.ToString(Formatting.None));
		}
		catch
		{
		}
	}

	private async Task<JToken> Dispatch(string method, JObject a)
	{
		switch (method)
		{
		case "status":
			return StatusJson();
		case "entries":
			return await Task.Run(() => Feed.EntriesAsync(a.Value<int?>("offset") ?? 0, Math.Max(1, Math.Min(60, a.Value<int?>("count") ?? 20)), a.Value<string>("filter") ?? "all"));
		case "refresh":
			_ = _main.RefreshAsync(true);
			return true;
		case "loadState":
			lock (Store.Lock)
			{
				return Store.St.Page.DeepClone();
			}
		case "saveState":
			if (a["state"] is JObject state)
			{
				lock (Store.Lock)
				{
					Store.St.Page = state;
				}
				Store.SaveSoon();
				_main.UpdateUnreadSoon();
			}
			return true;
		case "wish":
			return await Task.Run(() => Feed.SetWishAsync(a.Value<int>("appid"), a.Value<bool>("on")));
		case "login":
			await _main.LoginAsync();
			return Feed.StatusJson();
		case "open":
			OpenExternal(a.Value<string>("url"));
			return true;
		case "settings":
			_main.SelectPage(1);
			return true;
		case "translate":
		{
			string gid = a.Value<string>("gid") ?? "";
			int reqId = a.Value<int?>("req") ?? 0;
			string cacheKey;
			lock (Store.Lock)
			{
				cacheKey = gid + "|" + Store.Cfg.TranslateTo;
				if (gid != "" && a.Value<bool?>("force") != true && Store.St.Translations.TryGetValue(cacheKey, out JObject hit))
				{
					return hit;
				}
			}
			// 流式：边翻边把目前的输出推给页面（最多每 0.12 秒一次）
			var clock = Stopwatch.StartNew();
			long last = -1000;
			Action<string> onText = text =>
			{
				long now = clock.ElapsedMilliseconds;
				if (now - last < 120)
				{
					return;
				}
				last = now;
				_web.Dispatcher.BeginInvoke((Action)(() => Post(new JObject { ["event"] = "tr", ["req"] = reqId, ["text"] = text })));
			};
			JObject result = await Task.Run(() => Translator.TranslatePostAsync(a.Value<string>("title"), a.Value<string>("summary"), a.Value<string>("body"), onText));
			if (gid != "")
			{
				result["t"] = Store.Now;
				lock (Store.Lock)
				{
					Store.St.Translations[cacheKey] = result;
					foreach (string old in Store.St.Translations.OrderByDescending(kv => kv.Value.Value<long?>("t") ?? 0).Skip(300).Select(kv => kv.Key).ToList())
					{
						Store.St.Translations.Remove(old);
					}
				}
				Store.SaveSoon();
			}
			return result;
		}
		case "translateTexts":
		{
			var texts = (a["texts"] as JArray)?.Select(t => t.Type == JTokenType.String ? t.Value<string>() : "").ToList() ?? new List<string>();
			if (texts.Count == 0)
			{
				return new JArray();
			}
			return await Task.Run(() => Translator.TranslateTextsAsync(texts));
		}
		case "comments":
			return await Task.Run(() => Feed.CommentsAsync(a.Value<string>("gid"), Math.Max(0, a.Value<int?>("start") ?? 0), Math.Max(1, Math.Min(50, a.Value<int?>("count") ?? 10))));
		case "block":
		{
			string kind = a.Value<string>("kind") == "app" ? "app" : "clan";
			int id = a.Value<int?>("id") ?? 0;
			if (id <= 0)
			{
				throw new InvalidOperationException("没有可屏蔽的对象");
			}
			Feed.SetBlocked(kind, id, a.Value<string>("name"), a.Value<bool?>("on") ?? true);
			_main.BlockedChanged(false);
			return true;
		}
		default:
			throw new InvalidOperationException("未知方法 " + method);
		}
	}
}
