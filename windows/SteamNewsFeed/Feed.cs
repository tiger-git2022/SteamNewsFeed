using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SteamNewsFeed;

/// <summary>新闻流里的一条（只有 id 和时间，内容用到时再取）。</summary>
public sealed class Doc
{
	public string Gid = "";

	/// <summary>发新闻的 clan（账号 id）。</summary>
	public int Clan;

	/// <summary>新闻属于哪个游戏；发行商自己发的为 0。</summary>
	public int AppId;

	public long T;

	public int Type;

	/// <summary>Steam 新闻中心给这条的来源标记（库 1、愿望单 2、关注 4、推荐 8、Steam 16、必显示 32、精选 64、鉴赏家 128、转发 256）。</summary>
	public int Source;
}

/// <summary>时间线上的一条：新闻流里的新闻、游戏页上的发售新闻、或者自动生成的“现已正式发售”。</summary>
public sealed class Entry
{
	public string Key = "";

	public long T;

	/// <summary>新闻（新闻流里的或游戏页上的）的 gid；自动生成的发售卡片为空。</summary>
	public string Gid = "";

	public int Clan;

	/// <summary>这条和哪个游戏的发售有关（0 = 普通新闻）。</summary>
	public int ReleaseApp;

	/// <summary>新闻流里没有、从游戏页面推过来的发售新闻。</summary>
	public bool FromGamePage;

	/// <summary>别的账号发的同一篇（去重时合并进来的 gid）。</summary>
	public List<string> Dups = new List<string>();

	/// <summary>去重时被合并掉了。</summary>
	public bool Hidden;

	/// <summary>新闻实际发布的时间（T 可能被挪到发现新发售的时间）。</summary>
	public long PostedT;
}

/// <summary>
/// 时间线的数据：Steam 新闻流（和新闻中心一样）+ 关注的发行商新发售的游戏。
/// 新游戏的发售新闻如果已经在新闻流里就不重复；新闻流里没有、但游戏页上有，就把游戏页上的那条推过来；都没有就生成一张“现已正式发售”卡片。
/// </summary>
public static class Feed
{
	private const string EventTypes = "28,9,27,22,23,24,35,25,26,12,13,14,10,29,16,15,32,20,21,31,34";

	private const long Day = 86400;

	private static readonly object L = new object();

	/// <summary>关注的发行商 / 开发商 / 鉴赏家（clan 账号 id → 名字、头像）。</summary>
	private static Dictionary<int, ClanRec> _followed = new Dictionary<int, ClanRec>();

	private static HashSet<int> _wish = new HashSet<int>();

	private static HashSet<int> _owned = new HashSet<int>();

	private static readonly Dictionary<string, Doc> Docs = new Dictionary<string, Doc>();

	/// <summary>已经连续读到的最老一条新闻的时间。</summary>
	private static long _oldestDoc = long.MaxValue;

	private static bool _backDone;

	private static readonly Dictionary<string, JObject> Events = new Dictionary<string, JObject>();

	private static readonly Dictionary<int, JObject> Apps = new Dictionary<int, JObject>();

	private static readonly Dictionary<int, long> AppsAt = new Dictionary<int, long>();

	private static Dictionary<int, string> _tags;

	private static readonly HashSet<int> ClanTried = new HashSet<int>();

	private static readonly SemaphoreSlim DocsGate = new SemaphoreSlim(1, 1);

	private static int _running;

	public static bool Refreshing { get; private set; }

	public static string Error { get; private set; } = "";

	public static string Progress { get; private set; } = "";

	/// <summary>状态变了（开始 / 结束刷新、有新内容）。</summary>
	public static event Action Changed;

	/// <summary>这次刷新新发现的发售（游戏名）。</summary>
	public static event Action<List<string>> NewReleases;

	private static long Now => Store.Now;

	// ---- 状态 ----

	public static JObject StatusJson()
	{
		lock (Store.Lock)
		{
			return new JObject
			{
				["loggedIn"] = Steam.LoggedIn,
				["fresh"] = Steam.LoginFresh,
				["steamId"] = Steam.SteamId,
				["refreshing"] = Refreshing,
				["progress"] = Progress,
				["error"] = Error,
				["lastRefresh"] = Store.St.LastRefresh,
				["followed"] = _followed.Count,
				["country"] = Store.St.Country,
				["currency"] = Store.St.Currency,
				["rate"] = Store.St.CnyRate,
				["showCny"] = Store.Cfg.ShowCny,
				["tr"] = Translator.Configured,
				["autoTr"] = Store.Cfg.AutoTranslate && Translator.Configured,
				["trTo"] = Store.Cfg.TranslateTo,
			};
		}
	}

	private static void SetProgress(string text)
	{
		Progress = text;
		Changed?.Invoke();
	}

	// ---- 刷新 ----

	public static async Task RefreshAsync()
	{
		if (Interlocked.Exchange(ref _running, 1) == 1)
		{
			return;
		}
		Refreshing = true;
		var found = new List<string>();
		try
		{
			SetProgress("正在刷新…");
			if (!Steam.LoggedIn)
			{
				Error = "还没有登录 Steam";
				return;
			}
			await Steam.EnsureFreshAsync().ConfigureAwait(false);
			SetProgress("正在读取关注列表…");
			await LoadUserDataAsync().ConfigureAwait(false);
			await LoadCountryAsync().ConfigureAwait(false);
			await LoadRateAsync().ConfigureAwait(false);
			SetProgress("正在读取新闻…");
			int lookback;
			lock (Store.Lock)
			{
				lookback = Store.Cfg.LookbackDays;
			}
			await FetchTopDocsAsync(Now - (lookback + 3) * Day).ConfigureAwait(false);
			SetProgress("正在读取新闻内容…");
			await LoadAllDetailsAsync().ConfigureAwait(false);
			await CheckReleasesAsync(found).ConfigureAwait(false);
			lock (Store.Lock)
			{
				Store.St.LastRefresh = Now;
			}
			Error = "";
			Store.SaveSoon();
			Logger.Log("刷新完成：新闻 " + Docs.Count + " 条，发售 " + Store.St.Releases.Count + " 个" + (found.Count > 0 ? "，新发现 " + string.Join("、", found) : ""));
		}
		catch (Exception ex)
		{
			Error = ex is SteamException ? ex.Message : "刷新失败：" + ex.Message;
			Logger.Log("刷新失败：" + ex);
		}
		finally
		{
			Refreshing = false;
			Progress = "";
			Interlocked.Exchange(ref _running, 0);
			Changed?.Invoke();
			if (found.Count > 0)
			{
				NewReleases?.Invoke(found);
			}
		}
	}

	/// <summary>关注的发行商 / 鉴赏家、愿望单、已拥有的游戏。</summary>
	private static async Task LoadUserDataAsync()
	{
		JObject j = await Steam.GetStoreJsonAsync("/dynamicstore/userdata/?t=" + Now).ConfigureAwait(false);
		var followed = new Dictionary<int, ClanRec>();
		if (j["rgCurators"] is JObject curators)
		{
			foreach (JProperty p in curators.Properties())
			{
				if (p.Value is JObject c && int.TryParse(p.Name, out int id))
				{
					followed[id] = new ClanRec { Name = c.Value<string>("name") ?? "", Avatar = c.Value<string>("avatar") ?? "" };
				}
			}
		}
		HashSet<int> Ints(string key) => new HashSet<int>((j[key] as JArray)?.Select(t => t.Value<int>()) ?? Enumerable.Empty<int>());
		lock (L)
		{
			_followed = followed;
			_wish = Ints("rgWishlist");
			_owned = Ints("rgOwnedApps");
		}
		lock (Store.Lock)
		{
			foreach (var kv in followed)
			{
				Store.St.Clans[kv.Key] = kv.Value;
			}
		}
	}

	/// <summary>账号的商店地区（决定价格的货币），半天查一次。</summary>
	private static async Task LoadCountryAsync()
	{
		lock (Store.Lock)
		{
			if (Store.St.Country != "" && Store.St.Currency != "" && Now - Store.St.CountryCheckedAt < Day / 2)
			{
				return;
			}
		}
		try
		{
			string html = await Steam.GetStoreAsync("/news/?l=schinese").ConfigureAwait(false);
			Match m = Regex.Match(html, "COUNTRY&quot;:&quot;([A-Z]{2})&quot;");
			if (!m.Success)
			{
				m = Regex.Match(html, "\"COUNTRY\":\"([A-Z]{2})\"");
			}
			string country = m.Success ? m.Groups[1].Value : "";
			string currency = "";
			if (country != "")
			{
				foreach (int probe in new[] { 292030, 1091500, 271590, 1245620 })
				{
					try
					{
						JObject r = await Steam.GetStoreJsonAsync("/api/appdetails?appids=" + probe + "&cc=" + country + "&filters=price_overview").ConfigureAwait(false);
						currency = r[probe.ToString()]?["data"]?["price_overview"]?.Value<string>("currency") ?? "";
						if (currency != "")
						{
							break;
						}
					}
					catch (Exception)
					{
					}
				}
			}
			lock (Store.Lock)
			{
				if (country != "")
				{
					Store.St.Country = country;
				}
				if (currency != "")
				{
					Store.St.Currency = currency;
				}
				Store.St.CountryCheckedAt = Now;
			}
			Logger.Log("商店地区：" + country + "，货币：" + currency);
		}
		catch (Exception ex)
		{
			Logger.Log("读取商店地区失败：" + ex.Message);
		}
	}

	/// <summary>当前货币 → 人民币的汇率，半天更新一次。</summary>
	private static async Task LoadRateAsync()
	{
		string cur;
		lock (Store.Lock)
		{
			cur = Store.St.Currency;
			if (!Store.Cfg.ShowCny || cur == "" || cur == "CNY" || (Store.St.CnyRateCurrency == cur && Store.St.CnyRate > 0 && Now - Store.St.CnyRateAt < Day / 2))
			{
				return;
			}
		}
		double rate = 0;
		try
		{
			JObject r = await Steam.GetPublicJsonAsync("https://open.er-api.com/v6/latest/" + cur).ConfigureAwait(false);
			rate = r["rates"]?.Value<double?>("CNY") ?? 0;
		}
		catch (Exception ex)
		{
			Logger.Log("汇率（open.er-api.com）读取失败：" + ex.Message);
		}
		if (rate <= 0)
		{
			try
			{
				string c = cur.ToLowerInvariant();
				JObject r = await Steam.GetPublicJsonAsync("https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/" + c + ".json").ConfigureAwait(false);
				rate = r[c]?.Value<double?>("cny") ?? 0;
			}
			catch (Exception ex)
			{
				Logger.Log("汇率（备用）读取失败：" + ex.Message);
			}
		}
		if (rate > 0)
		{
			lock (Store.Lock)
			{
				Store.St.CnyRate = rate;
				Store.St.CnyRateCurrency = cur;
				Store.St.CnyRateAt = Now;
			}
			Logger.Log("汇率：1 " + cur + " = " + rate.ToString("0.#####", CultureInfo.InvariantCulture) + " CNY");
		}
	}

	// ---- 新闻流 ----

	private static string CalendarUrl(long maxTime) =>
		"/events/ajaxgetusereventcalendarrange/?minTime=0&maxTime=" + maxTime + "&ascending=false&maxResults=250&populateEvents=0" +
		"&appTypes=library,wishlist,following,recommended,steam,curator&eventTypes=" + EventTypes + "&l=schinese";

	/// <summary>读一页（最多 250 条，比 maxTime 早的），返回 (最老的时间, 是不是到头了)。</summary>
	private static async Task<(long Oldest, bool End)> FetchDocPageAsync(long maxTime)
	{
		JObject j = await Steam.GetStoreJsonAsync(CalendarUrl(maxTime)).ConfigureAwait(false);
		if (j.Value<int?>("success") is int s && s != 1)
		{
			throw new SteamException(0, "Steam 新闻读取失败（" + s + "）");
		}
		var list = (j["documents"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
		var appSrc = new Dictionary<int, int>();
		var clanSrc = new Dictionary<int, int>();
		foreach (JObject a in (j["apps"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			appSrc[a.Value<int?>("appid") ?? 0] = a.Value<int?>("source") ?? 0;
		}
		foreach (JObject c in (j["clans"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
		{
			clanSrc[c.Value<int?>("clanid") ?? 0] = c.Value<int?>("source") ?? 0;
		}
		long oldest = long.MaxValue;
		lock (L)
		{
			foreach (JObject d in list)
			{
				string gid = d.Value<string>("unique_id") ?? "";
				if (gid == "")
				{
					continue;
				}
				var doc = new Doc { Gid = gid, Clan = d.Value<int?>("clanid") ?? 0, AppId = d.Value<int?>("appid") ?? 0, T = d.Value<long?>("start_time") ?? 0, Type = d.Value<int?>("event_type") ?? 0 };
				// 和 Steam 新闻中心一样：游戏有来源标记就用游戏的，否则用发新闻的 clan 的
				doc.Source = doc.AppId != 0 && appSrc.TryGetValue(doc.AppId, out int sa) ? sa : (clanSrc.TryGetValue(doc.Clan, out int sc) ? sc : 0);
				Docs[gid] = doc;
				oldest = Math.Min(oldest, doc.T);
			}
		}
		bool end = list.Count == 0 || j.Value<bool?>("backwardComplete") == true;
		return (list.Count == 0 ? maxTime : oldest, end);
	}

	/// <summary>从现在往回读，读到 until 并且和已经读过的接上为止。</summary>
	private static async Task FetchTopDocsAsync(long until)
	{
		await DocsGate.WaitAsync().ConfigureAwait(false);
		try
		{
			long newestBefore;
			lock (L)
			{
				newestBefore = Docs.Count > 0 ? Docs.Values.Max(d => d.T) : 0;
			}
			long maxT = Now;
			long oldest = maxT;
			bool joined = false;
			for (int i = 0; i < 12; i++)
			{
				var page = await FetchDocPageAsync(maxT).ConfigureAwait(false);
				oldest = Math.Min(oldest, page.Oldest);
				if (page.End)
				{
					_backDone = true;
					joined = true;
					break;
				}
				if (newestBefore > 0 && oldest <= newestBefore)
				{
					joined = true;
				}
				if (oldest <= until && (newestBefore == 0 || joined))
				{
					break;
				}
				maxT = oldest - 1;
			}
			lock (L)
			{
				if (newestBefore > 0 && !joined)
				{
					// 中间断开了：扔掉更老的那一段，往下翻时重新读
					foreach (string gid in Docs.Values.Where(d => d.T < oldest).Select(d => d.Gid).ToList())
					{
						Docs.Remove(gid);
					}
					_oldestDoc = oldest;
					_backDone = false;
				}
				else
				{
					_oldestDoc = Math.Min(_oldestDoc, oldest);
				}
			}
		}
		finally
		{
			DocsGate.Release();
		}
	}

	/// <summary>往下翻：再读一页更老的新闻。</summary>
	private static async Task FetchOlderDocsAsync()
	{
		await DocsGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_backDone)
			{
				return;
			}
			long from;
			lock (L)
			{
				from = _oldestDoc == long.MaxValue ? Now : _oldestDoc - 1;
			}
			var page = await FetchDocPageAsync(from).ConfigureAwait(false);
			lock (L)
			{
				_oldestDoc = Math.Min(_oldestDoc, page.Oldest);
				if (page.End)
				{
					_backDone = true;
				}
			}
		}
		finally
		{
			DocsGate.Release();
		}
	}

	/// <summary>取新闻的内容（标题、正文、图片），一次最多 20 条。</summary>
	private static async Task LoadEventDetailsAsync(IEnumerable<(int Clan, string Gid)> wanted)
	{
		List<(int Clan, string Gid)> todo;
		lock (L)
		{
			todo = wanted.Where(w => w.Gid != "" && !Events.ContainsKey(w.Gid)).Distinct().ToList();
		}
		var gate = new SemaphoreSlim(4, 4);
		var tasks = new List<Task>();
		for (int i = 0; i < todo.Count; i += 20)
		{
			var batch = todo.Skip(i).Take(20).ToList();
			await gate.WaitAsync().ConfigureAwait(false);
			tasks.Add(Task.Run(async () =>
			{
			try
			{
				JObject j = await Steam.GetStoreJsonAsync("/events/ajaxgeteventdetails/?clanid_list=" + string.Join(",", batch.Select(b => b.Clan)) +
					"&uniqueid_list=" + string.Join(",", batch.Select(b => b.Gid)) + "&lang_list=6_0").ConfigureAwait(false);
				lock (L)
				{
					foreach (JObject ev in (j["events"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
					{
						string gid = ev.Value<string>("gid") ?? "";
						if (gid != "")
						{
							Events[gid] = ev;
						}
					}
					foreach (var b in batch)
					{
						if (!Events.ContainsKey(b.Gid))
						{
							Events[b.Gid] = new JObject { ["missing"] = true };
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("读取新闻内容失败：" + ex.Message);
			}
			finally
			{
				gate.Release();
			}
			}));
		}
		await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	/// <summary>新闻流里所有新闻的内容都读下来（去重要用标题和正文）。读过的不再读。</summary>
	private static Task LoadAllDetailsAsync()
	{
		List<(int, string)> all;
		lock (L)
		{
			all = Docs.Values.Select(d => (d.Clan, d.Gid)).ToList();
		}
		return LoadEventDetailsAsync(all);
	}

	// ---- 评论 ----

	/// <summary>一条新闻的评论（Steam 社区的讨论串），新的在前。返回 total 和 Steam 渲染好的 html，页面里再解析。</summary>
	public static async Task<JObject> CommentsAsync(string gid, int start, int count)
	{
		JObject ev;
		lock (L)
		{
			Events.TryGetValue(gid ?? "", out ev);
		}
		string type = ev?.Value<string>("comment_type") ?? "";
		string owner = ev?.Value<string>("clan_steamid") ?? "";
		string feature = ev?.Value<string>("gidfeature") ?? "";
		string feature2 = ev?.Value<string>("gidfeature2") ?? "";
		if (type == "" || owner == "" || feature == "" || feature == "0" || !Regex.IsMatch(type + owner + feature + feature2, @"^[A-Za-z0-9]+$"))
		{
			return new JObject { ["total"] = 0, ["html"] = "" };
		}
		var form = new Dictionary<string, string>
		{
			["start"] = start.ToString(CultureInfo.InvariantCulture),
			["count"] = count.ToString(CultureInfo.InvariantCulture),
			["feature2"] = feature2,
			["extended_data"] = "{\"topic_permissions\":{\"can_view\":1}}",
		};
		JObject j = await Steam.PostPublicAsync("https://steamcommunity.com/comment/" + type + "/render/" + owner + "/" + feature + "/", form).ConfigureAwait(false);
		if (j.Value<bool?>("success") != true)
		{
			throw new SteamException(0, "评论读取失败：" + (j.Value<string>("error") ?? "Steam 没有返回内容"));
		}
		return new JObject { ["total"] = j.Value<int?>("total_count") ?? 0, ["html"] = j.Value<string>("comments_html") ?? "", ["start"] = start };
	}

	// ---- 屏蔽 ----

	/// <summary>屏蔽 / 取消屏蔽一个游戏（kind = app）或账号（kind = clan）。</summary>
	public static void SetBlocked(string kind, int id, string name, bool on)
	{
		lock (Store.Lock)
		{
			Dictionary<int, string> map = kind == "app" ? Store.Cfg.BlockedApps : Store.Cfg.BlockedClans;
			if (on)
			{
				map[id] = string.IsNullOrWhiteSpace(name) ? (kind == "app" ? "App " + id : "账号 " + id) : name.Trim();
			}
			else
			{
				map.Remove(id);
			}
		}
		Store.SaveConfig();
		Logger.Log((on ? "屏蔽 " : "取消屏蔽 ") + (kind == "app" ? "游戏 " : "账号 ") + id + " " + name);
	}

	private static bool BlockedDoc(Doc d)
	{
		lock (Store.Lock)
		{
			return (d.AppId != 0 && Store.Cfg.BlockedApps.ContainsKey(d.AppId)) || Store.Cfg.BlockedClans.ContainsKey(d.Clan);
		}
	}

	/// <summary>去重用的指纹：标题和正文都一样，就当成同一篇。</summary>
	private static string DupKey(JObject ev)
	{
		if (ev == null || ev["missing"] != null)
		{
			return null;
		}
		string title = Norm(ev.Value<string>("event_name") ?? "");
		if (title.Length < 4)
		{
			return null;
		}
		string body = Norm(ev["announcement_body"]?.Value<string>("body") ?? "");
		return title + "|" + body.Length + "|" + (body.Length > 200 ? body.Substring(0, 200) : body);
	}

	// ---- 游戏信息 ----

	private static readonly Dictionary<int, string> _desc = new Dictionary<int, string>();

	/// <summary>商店页「关于这款游戏」（BBCode），展开新发售卡片时才读。图片地址里的 {STEAM_APP_IMAGE} 换成真实地址。</summary>
	public static async Task<JToken> AppDescAsync(int appid)
	{
		if (appid <= 0)
		{
			throw new InvalidOperationException("没有这个游戏");
		}
		lock (L)
		{
			if (_desc.TryGetValue(appid, out string hit))
			{
				return hit;
			}
		}
		string country;
		lock (Store.Lock)
		{
			country = Store.St.Country != "" ? Store.St.Country : "US";
		}
		var input = new JObject
		{
			["ids"] = new JArray(new JObject { ["appid"] = appid }),
			["context"] = new JObject { ["language"] = "schinese", ["country_code"] = country, ["steam_realm"] = 1 },
			["data_request"] = new JObject { ["include_full_description"] = true },
		};
		JObject j = await Steam.GetPublicJsonAsync(Steam.Api + "/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input.ToString(Formatting.None))).ConfigureAwait(false);
		JToken it = (j["response"]?["store_items"] as JArray)?.FirstOrDefault();
		if (it == null || it.Value<int?>("success") != 1)
		{
			throw new InvalidOperationException("商店页没有返回这个游戏的信息");
		}
		string desc = (it.Value<string>("full_description_bbcode") ?? "").Replace("{STEAM_APP_IMAGE}", Steam.AssetCdn + "steam/apps/" + appid);
		lock (L)
		{
			_desc[appid] = desc;
		}
		return desc;
	}

	/// <summary>用公开的 GetItems 一次取多个游戏的名字、图片、标签、发售时间、价格、发行商。</summary>
	private static async Task FetchItemsAsync(IEnumerable<int> ids)
	{
		var list = ids.Where(i => i > 0).Distinct().ToList();
		string country;
		lock (Store.Lock)
		{
			country = Store.St.Country != "" ? Store.St.Country : "US";
		}
		for (int i = 0; i < list.Count; i += 30)
		{
			var batch = list.Skip(i).Take(30).ToList();
			var input = new JObject
			{
				["ids"] = new JArray(batch.Select(a => new JObject { ["appid"] = a })),
				["context"] = new JObject { ["language"] = "schinese", ["country_code"] = country, ["steam_realm"] = 1 },
				["data_request"] = new JObject { ["include_assets"] = true, ["include_release"] = true, ["include_tag_count"] = 8, ["include_basic_info"] = true, ["include_reviews"] = true },
			};
			try
			{
				JObject j = await Steam.GetPublicJsonAsync(Steam.Api + "/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input.ToString(Formatting.None))).ConfigureAwait(false);
				lock (L)
				{
					foreach (JObject it in (j["response"]?["store_items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
					{
						int appid = it.Value<int?>("appid") ?? it.Value<int?>("id") ?? 0;
						if (appid > 0)
						{
							Apps[appid] = it.Value<int?>("success") == 1 ? it : new JObject { ["missing"] = true };
							AppsAt[appid] = Now;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("读取游戏信息失败：" + ex.Message);
			}
		}
	}

	private static async Task EnsureAppsAsync(IEnumerable<int> ids, long maxAge)
	{
		List<int> todo;
		lock (L)
		{
			todo = ids.Where(a => a > 0 && (!AppsAt.TryGetValue(a, out long at) || Now - at > maxAge)).Distinct().ToList();
		}
		if (todo.Count > 0)
		{
			await FetchItemsAsync(todo).ConfigureAwait(false);
		}
	}

	private static async Task EnsureTagsAsync()
	{
		if (_tags != null)
		{
			return;
		}
		try
		{
			JObject j = await Steam.GetPublicJsonAsync(Steam.Api + "/IStoreService/GetTagList/v1/?language=schinese").ConfigureAwait(false);
			_tags = ((j["response"]?["tags"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
				.GroupBy(t => t.Value<int>("tagid")).ToDictionary(g => g.Key, g => g.First().Value<string>("name") ?? "");
		}
		catch (Exception ex)
		{
			Logger.Log("读取标签名失败：" + ex.Message);
		}
	}

	/// <summary>不在关注列表里的 clan（偶尔有官方或别人的新闻）：从它的商店页读名字和头像，记下来。</summary>
	private static async Task EnsureClansAsync(IEnumerable<int> clans)
	{
		List<int> todo;
		lock (Store.Lock)
		{
			todo = clans.Where(c => c > 0 && !Store.St.Clans.ContainsKey(c) && !ClanTried.Contains(c)).Distinct().Take(6).ToList();
			foreach (int c in todo)
			{
				ClanTried.Add(c);
			}
		}
		foreach (int c in todo)
		{
			try
			{
				string html = await Steam.GetStoreAsync("/curator/" + c + "/?l=schinese").ConfigureAwait(false);
				string title = WebUtilityDecode(Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline).Groups[1].Value.Trim());
				int colon = Math.Max(title.LastIndexOf('：'), title.LastIndexOf(':'));
				string name = colon >= 0 ? title.Substring(colon + 1).Trim() : title;
				string avatar = Regex.Match(html, @"avatars\.[a-z]+\.steamstatic\.com/([0-9a-f]{40})").Groups[1].Value;
				if (name != "" && !name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase))
				{
					lock (Store.Lock)
					{
						Store.St.Clans[c] = new ClanRec { Name = name, Avatar = avatar };
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("读取组 " + c + " 的名字失败：" + ex.Message);
			}
		}
	}

	private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s ?? "");

	// ---- 新发售 ----

	private static bool TypeOk(int type, bool dlc) => type == 0 || (dlc && (type == 4 || type == 11));

	/// <summary>按现在的设置，这个游戏要不要推送。</summary>
	private static bool Qualifies(AppRec rec) => QualifiesRaw(rec) && !BlockedRelease(rec);

	/// <summary>屏蔽了这个游戏，或者把它算进来的关注对象（发行商 / 开发商 / 鉴赏家）都被屏蔽了。</summary>
	private static bool BlockedRelease(AppRec rec)
	{
		lock (Store.Lock)
		{
			if (Store.Cfg.BlockedApps.ContainsKey(rec.AppId))
			{
				return true;
			}
			var who = rec.Creators.Concat(rec.ListedBy).Distinct().ToList();
			return who.Count > 0 && who.All(c => Store.Cfg.BlockedClans.ContainsKey(c));
		}
	}

	private static bool QualifiesRaw(AppRec rec)
	{
		Config c = Store.Cfg;
		return rec != null && rec.Released && TypeOk(rec.Type, c.IncludeDlc) && (!c.OnlyOwnGames || rec.Creators.Count > 0);
	}

	private static async Task CheckReleasesAsync(List<string> found)
	{
		Dictionary<int, ClanRec> followed;
		lock (L)
		{
			followed = _followed;
		}
		// 1. 每个关注对象最新的游戏
		var listed = new Dictionary<int, HashSet<int>>();
		int n = 0;
		foreach (int clan in followed.Keys)
		{
			SetProgress("正在检查新游戏：" + (++n) + " / " + followed.Count);
			try
			{
				JObject j = await Steam.GetStoreJsonAsync("/curator/" + clan + "/ajaxgetfilteredrecommendations/?query=&start=0&count=12&dynamic_data=&tagids=&sort=newreleases&app_types=&curations=&reset=false&l=schinese").ConfigureAwait(false);
				string html = j.Value<string>("results_html") ?? "";
				foreach (Match m in Regex.Matches(html, "data-ds-appid=\"(\\d+)\""))
				{
					int appid = int.Parse(m.Groups[1].Value);
					if (!listed.TryGetValue(appid, out HashSet<int> set))
					{
						listed[appid] = set = new HashSet<int>();
					}
					set.Add(clan);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("读取 " + followed[clan].Name + " 的游戏失败：" + ex.Message);
			}
			await Task.Delay(120).ConfigureAwait(false);
		}

		// 2. 没见过的、还没发售的（每 3 小时）重新查一次
		long now = Now;
		List<int> check;
		lock (Store.Lock)
		{
			check = listed.Keys.Where(a => !Store.St.Apps.TryGetValue(a, out AppRec r) || r.CheckedAt == 0 || (!r.Released && now - r.CheckedAt > 3 * 3600)).ToList();
		}
		if (check.Count > 0)
		{
			SetProgress("正在读取 " + check.Count + " 个游戏的信息…");
			await FetchItemsAsync(check).ConfigureAwait(false);
		}
		int lookback;
		bool baseline;
		lock (Store.Lock)
		{
			lookback = Store.Cfg.LookbackDays;
			baseline = Store.St.BaselineDone;
			foreach (var kv in listed)
			{
				if (!Store.St.Apps.TryGetValue(kv.Key, out AppRec rec))
				{
					Store.St.Apps[kv.Key] = rec = new AppRec { AppId = kv.Key };
				}
				rec.ListedBy = rec.ListedBy.Union(kv.Value).ToList();
				JObject it;
				lock (L)
				{
					Apps.TryGetValue(kv.Key, out it);
				}
				if (check.Contains(kv.Key) && it != null && it["missing"] == null)
				{
					rec.Name = it.Value<string>("name") ?? rec.Name;
					rec.Type = it.Value<int?>("type") ?? 0;
					JToken rel = it["release"];
					long ts = rel?.Value<long?>("steam_release_date") ?? rel?.Value<long?>("original_release_date") ?? 0;
					bool soon = rel?.Value<bool?>("is_coming_soon") == true || ts == 0 || ts > now;
					rec.Released = !soon;
					rec.ReleaseTs = ts;
					var makers = new HashSet<int>();
					foreach (string group in new[] { "developers", "publishers", "franchises" })
					{
						foreach (JObject who in (it["basic_info"]?[group] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
						{
							int cid = who.Value<int?>("creator_clan_account_id") ?? 0;
							if (cid > 0 && followed.ContainsKey(cid))
							{
								makers.Add(cid);
							}
						}
					}
					rec.Creators = makers.ToList();
					rec.CheckedAt = now;
				}
				if (rec.Released && rec.ReleaseTs >= now - lookback * Day && TypeOk(rec.Type, true) && !Store.St.Releases.ContainsKey(rec.AppId))
				{
					// 第一次检查时按发售时间排；之后发售了一阵才发现的（比如刚关注的发行商）放到现在，免得落在已读的部分
					long t = baseline && now - rec.ReleaseTs > 6 * 3600 ? now : rec.ReleaseTs;
					Store.St.Releases[rec.AppId] = new ReleaseRec { AppId = rec.AppId, T = t, FoundAt = now };
					if (baseline && Qualifies(rec))
					{
						found.Add(rec.Name);
					}
				}
			}
			Store.St.BaselineDone = true;
			// 太老的不要了
			foreach (int old in Store.St.Releases.Values.Where(r => r.T < now - (lookback + 60) * Day).Select(r => r.AppId).ToList())
			{
				Store.St.Releases.Remove(old);
			}
		}

		// 3. 给要推送的发售找发售新闻
		List<(ReleaseRec Rel, AppRec App)> pending;
		lock (Store.Lock)
		{
			pending = Store.St.Releases.Values.Where(r => !r.NewsDone && Store.St.Apps.TryGetValue(r.AppId, out AppRec a) && Qualifies(a))
				.Select(r => (r, Store.St.Apps[r.AppId])).ToList();
		}
		int k = 0;
		foreach (var p in pending)
		{
			SetProgress("正在找发售新闻：" + (++k) + " / " + pending.Count);
			try
			{
				await MatchNewsAsync(p.Rel, p.App).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Logger.Log("找《" + p.App.Name + "》的发售新闻失败：" + ex.Message);
			}
		}
	}

	private static readonly Regex ReleaseWords = new Regex(@"发售|發售|开售|開售|发行|發行|上线|上線|推出|发布|發布|正式|上市|登场|登場|解禁|首发|首發|release|launch|out now|available now|now available|is live|now live|発売|リリース|配信開始", RegexOptions.IgnoreCase);

	private static readonly Regex NotReleaseWords = new Regex(@"demo|試玩|试玩|体验版|體驗版|愿望|願望|wishlist|预告|預告|预约|預約|预购|預購|倒数|倒數|countdown|playtest|即将|即將|coming soon|更新|update|patch|hotfix|修复|修復|特惠|sale|折扣", RegexOptions.IgnoreCase);

	private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

	private static bool NameMatch(string title, string name)
	{
		string t = Norm(title), n = Norm(name);
		if (n.Length < 2)
		{
			return false;
		}
		return t.Contains(n.Substring(0, Math.Min(n.Length, 6))) || (n.Length >= 4 && t.Contains(n.Substring(0, 4)));
	}

	/// <summary>这条新闻是不是这个游戏的发售新闻。</summary>
	private static bool IsReleaseNews(JObject ev, AppRec app)
	{
		if (ev == null || ev["missing"] != null)
		{
			return false;
		}
		string title = ev.Value<string>("event_name") ?? ev["announcement_body"]?.Value<string>("headline") ?? "";
		int type = ev.Value<int?>("event_type") ?? 0;
		string body = ev["announcement_body"]?.Value<string>("body") ?? "";
		bool about = (ev.Value<int?>("appid") ?? 0) == app.AppId || NameMatch(title, app.Name) || body.Contains("/app/" + app.AppId + "/") || body.Contains("/app/" + app.AppId + "]");
		if (!about)
		{
			try
			{
				JObject jd = JObject.Parse(ev.Value<string>("jsondata") ?? "{}");
				about = (jd["referenced_appids"] as JArray)?.Any(a => a.Value<int>() == app.AppId) == true;
			}
			catch
			{
			}
		}
		if (!about)
		{
			return false;
		}
		if (type == 10)
		{
			return true;
		}
		return ReleaseWords.IsMatch(title) && !NotReleaseWords.IsMatch(title);
	}

	private static async Task MatchNewsAsync(ReleaseRec rel, AppRec app)
	{
		long lo = app.ReleaseTs - 3 * Day, hi = app.ReleaseTs + 14 * Day;
		// 1. 新闻流里已经有了吗（游戏自己的新闻，或者发行商发的）
		List<Doc> candidates;
		lock (L)
		{
			candidates = Docs.Values.Where(d => d.T >= lo && d.T <= hi && (d.AppId == app.AppId || (d.AppId == 0 && (app.Creators.Contains(d.Clan) || app.ListedBy.Contains(d.Clan)))))
				.OrderBy(d => d.T).ToList();
		}
		await LoadEventDetailsAsync(candidates.Select(d => (d.Clan, d.Gid))).ConfigureAwait(false);
		foreach (Doc d in candidates)
		{
			JObject ev;
			lock (L)
			{
				Events.TryGetValue(d.Gid, out ev);
			}
			if (IsReleaseNews(ev, app))
			{
				lock (Store.Lock)
				{
					rel.CoveredBy = d.Gid;
					rel.NewsDone = true;
				}
				return;
			}
		}
		// 2. 游戏自己页面上的新闻
		if (Now >= lo)
		{
			JObject j = await Steam.GetStoreJsonAsync("/events/ajaxgetpartnereventspageable/?clan_accountid=0&appid=" + app.AppId + "&offset=0&count=15&l=schinese").ConfigureAwait(false);
			JObject best = null;
			foreach (JObject ev in (j["events"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
			{
				long t = ev.Value<long?>("rtime32_start_time") ?? 0;
				if (t >= lo && t <= hi && IsReleaseNews(ev, app) && (best == null || t < best.Value<long>("rtime32_start_time")))
				{
					best = ev;
				}
			}
			if (best != null)
			{
				string gid = best.Value<string>("gid") ?? "";
				lock (L)
				{
					Events[gid] = best;
				}
				lock (Store.Lock)
				{
					rel.EventGid = gid;
					rel.EventClan = Steam.ClanAccount(best.Value<string>("clan_steamid")).ToString();
					rel.NewsDone = true;
				}
				Logger.Log("《" + app.Name + "》的发售新闻在游戏页面上：" + best.Value<string>("event_name"));
				return;
			}
		}
		if (Now > app.ReleaseTs + 3 * Day)
		{
			lock (Store.Lock)
			{
				rel.NewsDone = true;
			}
		}
	}

	// ---- 时间线 ----

	/// <summary>把新闻流和发售合起来，按时间从新到旧。filter：all / release。</summary>
	private static List<Entry> Build(string filter)
	{
		var list = new List<Entry>();
		lock (Store.Lock)
		lock (L)
		{
			var covered = new Dictionary<string, int>();
			// 晚发现的新发售（比如刚关注的发行商最近出的游戏）：发售新闻也排到发现的时间，免得落在已读的部分
			var coveredT = new Dictionary<string, long>();
			foreach (ReleaseRec r in Store.St.Releases.Values)
			{
				if (!Store.St.Apps.TryGetValue(r.AppId, out AppRec app) || !Qualifies(app))
				{
					continue;
				}
				if (r.CoveredBy != "" && Docs.ContainsKey(r.CoveredBy))
				{
					covered[r.CoveredBy] = r.AppId;
					coveredT[r.CoveredBy] = r.T;
				}
				else if (r.EventGid != "" && Docs.ContainsKey(r.EventGid))
				{
					covered[r.EventGid] = r.AppId;
					coveredT[r.EventGid] = r.T;
				}
				else if (r.EventGid != "")
				{
					list.Add(new Entry { Key = "e:" + r.EventGid, T = r.T, Gid = r.EventGid, Clan = int.TryParse(r.EventClan, out int c) ? c : 0, ReleaseApp = r.AppId, FromGamePage = true });
				}
				else if (r.CoveredBy == "")
				{
					list.Add(new Entry { Key = "r:" + r.AppId, T = r.T, ReleaseApp = r.AppId });
				}
			}
			// 多个账号发的同一篇（标题、正文都一样，前后 3 天内）只留一条：留最早发的，带发售卡片的优先
			var groups = new Dictionary<string, Entry>();
			foreach (Doc d in Docs.Values.OrderBy(x => x.T))
			{
				covered.TryGetValue(d.Gid, out int rel);
				if ((rel == 0 && !Visible(d)) || BlockedDoc(d))
				{
					continue;
				}
				long shown = rel != 0 && coveredT.TryGetValue(d.Gid, out long rt) && rt > d.T + 6 * 3600 ? rt : d.T;
				var en = new Entry { Key = "e:" + d.Gid, T = shown, PostedT = d.T, Gid = d.Gid, Clan = d.Clan, ReleaseApp = rel };
				Events.TryGetValue(d.Gid, out JObject ev);
				string k = DupKey(ev);
				if (k != null && groups.TryGetValue(k, out Entry keep) && d.T - keep.PostedT <= 3 * Day)
				{
					if (keep.ReleaseApp == 0 && rel != 0)
					{
						// 这条带发售卡片：换它来显示
						en.Dups.Add(keep.Gid);
						en.Dups.AddRange(keep.Dups);
						keep.Dups.Clear();
						keep.Hidden = true;
						groups[k] = en;
						list.Add(en);
					}
					else
					{
						keep.Dups.Add(d.Gid);
					}
					continue;
				}
				if (k != null)
				{
					groups[k] = en;
				}
				list.Add(en);
			}
			list.RemoveAll(x => x.Hidden || (filter == "release" && x.ReleaseApp == 0));
		}
		return list.OrderByDescending(e => e.T).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
	}

	/// <summary>
	/// 和 Steam 新闻中心默认的筛选一样：库、愿望单、正在关注、已推荐、Steam、鉴赏家的显示，
	/// 只因为是「精选」（Steam 推的热门游戏）才出现的不显示（设置里可以打开）。发售新闻不受影响。
	/// </summary>
	private static bool Visible(Doc d)
	{
		int mask = 1 | 2 | 4 | 8 | 16 | 32 | 128 | 256 | (Store.Cfg.ShowFeatured ? 64 : 0);
		return (d.Source & mask) != 0;
	}

	/// <summary>时间线的一段（页面往下翻时一段一段地要）。</summary>
	public static async Task<JObject> EntriesAsync(int offset, int count, string filter)
	{
		await LoadAllDetailsAsync().ConfigureAwait(false);
		List<Entry> list = Build(filter);
		for (int i = 0; i < 6 && offset + count > list.Count && !_backDone && Steam.LoggedIn; i++)
		{
			await FetchOlderDocsAsync().ConfigureAwait(false);
			await LoadAllDetailsAsync().ConfigureAwait(false);
			list = Build(filter);
		}
		List<Entry> slice = list.Skip(offset).Take(count).ToList();
		var docs = new Dictionary<string, Doc>();
		lock (L)
		{
			foreach (Entry e in slice)
			{
				if (Docs.TryGetValue(e.Gid, out Doc d))
				{
					docs[e.Gid] = d;
				}
			}
		}
		await LoadEventDetailsAsync(slice.Where(e => e.Gid != "").Select(e => (e.Clan, e.Gid))).ConfigureAwait(false);
		var appIds = new List<int>();
		var clans = new List<int>();
		lock (L)
		{
			foreach (Entry e in slice)
			{
				if (e.ReleaseApp > 0)
				{
					appIds.Add(e.ReleaseApp);
				}
				foreach (string g in new[] { e.Gid }.Concat(e.Dups))
				{
					if (g != "" && Events.TryGetValue(g, out JObject ev) && ev["missing"] == null)
					{
						int a = ev.Value<int?>("appid") ?? 0;
						if (a > 0)
						{
							appIds.Add(a);
						}
						clans.Add(Steam.ClanAccount(ev.Value<string>("clan_steamid")));
					}
				}
			}
		}
		await EnsureTagsAsync().ConfigureAwait(false);
		await EnsureAppsAsync(appIds, 6 * 3600).ConfigureAwait(false);
		await EnsureClansAsync(clans).ConfigureAwait(false);
		var items = new JArray();
		foreach (Entry e in slice)
		{
			JObject o = EntryJson(e);
			if (o != null)
			{
				items.Add(o);
			}
		}
		return new JObject { ["items"] = items, ["total"] = list.Count, ["done"] = _backDone && offset + count >= list.Count, ["offset"] = offset };
	}

	/// <summary>没读过的条数：比已读时间新、又没看过的。</summary>
	public static int CountUnread()
	{
		long readUpTo;
		JObject seen;
		lock (Store.Lock)
		{
			readUpTo = Store.St.Page.Value<long?>("readUpTo") ?? 0;
			seen = Store.St.Page["seen"] as JObject;
		}
		if (readUpTo <= 0)
		{
			return 0;
		}
		return Build("all").Count(e => e.T > readUpTo && (seen == null || seen[e.Key] == null));
	}

	private static string Loc(JToken arr)
	{
		if (arr is JArray a)
		{
			string pick(int i) => i < a.Count && a[i].Type == JTokenType.String ? a[i].Value<string>() : null;
			return pick(6) ?? pick(0) ?? a.Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
		}
		return null;
	}

	private static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string>
	{
		[28] = "新闻", [12] = "小更新", [13] = "更新", [14] = "重大更新", [10] = "发售", [15] = "发售", [16] = "即将推出",
		[11] = "直播", [20] = "直播", [21] = "直播", [22] = "直播", [23] = "特惠", [24] = "特惠", [9] = "活动",
		[25] = "游戏内活动", [26] = "游戏内活动", [27] = "游戏内活动", [29] = "游戏内活动", [31] = "免费试玩", [32] = "活动", [34] = "推荐", [35] = "游戏内活动",
	};

	private static JObject EntryJson(Entry e)
	{
		lock (Store.Lock)
		lock (L)
		{
			JObject o = new JObject { ["key"] = e.Key, ["t"] = e.T };
			if (e.ReleaseApp > 0)
			{
				o["release"] = AppJson(e.ReleaseApp);
				if (o["release"] == null)
				{
					return null;
				}
			}
			if (e.Gid == "")
			{
				o["kind"] = "release";
				Store.St.Apps.TryGetValue(e.ReleaseApp, out AppRec rec);
				var makers = (rec?.Creators.Count > 0 ? rec.Creators : rec?.ListedBy ?? new List<int>()).Where(c => Store.St.Clans.ContainsKey(c))
					.Select(c => new JObject { ["name"] = Store.St.Clans[c].Name, ["icon"] = AvatarUrl(Store.St.Clans[c].Avatar), ["url"] = Steam.StoreUrl + "/curator/" + c + "/" }).ToList();
				o["source"] = makers.FirstOrDefault() ?? new JObject { ["name"] = "Steam", ["icon"] = "", ["url"] = "" };
				var rb = new JArray { new JObject { ["kind"] = "app", ["id"] = e.ReleaseApp, ["name"] = rec?.Name ?? "" } };
				foreach (int c in (rec?.Creators.Count > 0 ? rec.Creators : rec?.ListedBy ?? new List<int>()).Where(c => Store.St.Clans.ContainsKey(c)))
				{
					rb.Add(new JObject { ["kind"] = "clan", ["id"] = c, ["name"] = Store.St.Clans[c].Name });
				}
				o["block"] = rb;
				return o;
			}
			if (!Events.TryGetValue(e.Gid, out JObject ev) || ev["missing"] != null)
			{
				return null;
			}
			o["kind"] = "event";
			o["fromGamePage"] = e.FromGamePage;
			o["postedT"] = e.PostedT > 0 ? e.PostedT : ev.Value<long?>("rtime32_start_time") ?? e.T;
			int clan = Steam.ClanAccount(ev.Value<string>("clan_steamid"));
			int appid = ev.Value<int?>("appid") ?? 0;
			JObject jd;
			try
			{
				jd = JObject.Parse(ev.Value<string>("jsondata") ?? "{}");
			}
			catch
			{
				jd = new JObject();
			}
			string capsule = Loc(jd["localized_capsule_image"]);
			string titleImg = Loc(jd["localized_title_image"]);
			o["gid"] = e.Gid;
			o["title"] = ev.Value<string>("event_name") ?? ev["announcement_body"]?.Value<string>("headline") ?? "";
			o["subtitle"] = Loc(jd["localized_subtitle"]) ?? "";
			o["summary"] = Loc(jd["localized_summary"]) ?? "";
			o["image"] = capsule != null ? Steam.ClanCdn + clan + "/" + capsule : (titleImg != null ? Steam.ClanCdn + clan + "/" + titleImg : "");
			o["body"] = ev["announcement_body"]?.Value<string>("body") ?? "";
			o["clan"] = clan;
			o["appid"] = appid;
			o["type"] = TypeNames.TryGetValue(ev.Value<int?>("event_type") ?? 0, out string tn) ? tn : "活动";
			o["votes"] = ev.Value<int?>("votes_up") ?? 0;
			o["comments"] = Math.Max(ev.Value<int?>("comment_count") ?? 0, ev["announcement_body"]?.Value<int?>("commentcount") ?? 0);
			o["url"] = appid > 0 ? Steam.StoreUrl + "/news/app/" + appid + "/view/" + e.Gid : Steam.StoreUrl + "/news/group/" + clan + "/view/" + e.Gid;
			if (appid > 0 && Apps.TryGetValue(appid, out JObject app) && app["missing"] == null)
			{
				o["source"] = new JObject { ["name"] = app.Value<string>("name") ?? "", ["icon"] = AppIcon(appid, app), ["url"] = Steam.StoreUrl + "/app/" + appid + "/" };
				o["app"] = new JObject { ["appid"] = appid, ["name"] = app.Value<string>("name") ?? "", ["wish"] = _wish.Contains(appid), ["owned"] = _owned.Contains(appid), ["url"] = Steam.StoreUrl + "/app/" + appid + "/" };
			}
			else if (Store.St.Clans.TryGetValue(clan, out ClanRec cr) && cr.Name != "")
			{
				o["source"] = new JObject { ["name"] = cr.Name, ["icon"] = AvatarUrl(cr.Avatar), ["url"] = Steam.StoreUrl + "/curator/" + clan + "/" };
			}
			else
			{
				o["source"] = new JObject { ["name"] = appid > 0 ? "App " + appid : "Steam", ["icon"] = "", ["url"] = "" };
			}
			var blocks = new JArray();
			if (appid > 0)
			{
				blocks.Add(new JObject { ["kind"] = "app", ["id"] = appid, ["name"] = o["app"]?.Value<string>("name") ?? ("App " + appid) });
			}
			if (Store.St.Clans.TryGetValue(clan, out ClanRec bc) && bc.Name != "")
			{
				blocks.Add(new JObject { ["kind"] = "clan", ["id"] = clan, ["name"] = bc.Name });
			}
			else if (appid == 0)
			{
				blocks.Add(new JObject { ["kind"] = "clan", ["id"] = clan, ["name"] = o["source"].Value<string>("name") });
			}
			if (e.ReleaseApp > 0 && e.ReleaseApp != appid)
			{
				blocks.Insert(0, new JObject { ["kind"] = "app", ["id"] = e.ReleaseApp, ["name"] = o["release"]?.Value<string>("name") ?? ("App " + e.ReleaseApp) });
			}
			o["block"] = blocks;
			// 同一篇也被别的账号发了：列出来
			var also = new JArray();
			foreach (string dg in e.Dups)
			{
				if (Events.TryGetValue(dg, out JObject dev) && dev["missing"] == null)
				{
					int da = dev.Value<int?>("appid") ?? 0;
					int dc = Steam.ClanAccount(dev.Value<string>("clan_steamid"));
					string dn = da > 0 && Apps.TryGetValue(da, out JObject dapp) && dapp["missing"] == null ? dapp.Value<string>("name")
						: Store.St.Clans.TryGetValue(dc, out ClanRec dcr) && dcr.Name != "" ? dcr.Name : null;
					if (!string.IsNullOrEmpty(dn) && dn != o["source"].Value<string>("name") && !also.Any(x => x.Value<string>() == dn))
					{
						also.Add(dn);
					}
				}
			}
			o["dups"] = e.Dups.Count;
			o["also"] = also;
			return o;
		}
	}

	private static string AvatarUrl(string hash) => string.IsNullOrEmpty(hash) ? "" : Steam.AvatarCdn + hash + "_full.jpg";

	private static string AppIcon(int appid, JObject app)
	{
		string icon = app["assets"]?.Value<string>("community_icon");
		return string.IsNullOrEmpty(icon) ? "" : "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/" + appid + "/" + icon + ".jpg";
	}

	/// <summary>游戏卡片要的全部信息。调用方持有锁。</summary>
	private static JObject AppJson(int appid)
	{
		if (!Apps.TryGetValue(appid, out JObject it) || it["missing"] != null)
		{
			return null;
		}
		JToken assets = it["assets"];
		string fmt = assets?.Value<string>("asset_url_format") ?? "";
		string U(string key)
		{
			string file = assets?.Value<string>(key);
			return string.IsNullOrEmpty(file) || fmt == "" ? "" : Steam.AssetCdn + fmt.Replace("${FILENAME}", file);
		}
		int type = it.Value<int?>("type") ?? 0;
		JToken rel = it["release"];
		var o = new JObject
		{
			["appid"] = appid,
			["name"] = it.Value<string>("name") ?? "",
			["type"] = type,
			["typeName"] = type == 4 ? "DLC" : type == 11 ? "原声带" : "游戏",
			["url"] = Steam.StoreUrl + "/app/" + appid + "/",
			["capsule"] = U("main_capsule") != "" ? U("main_capsule") : U("header"),
			["header"] = U("header"),
			["hero"] = U("library_hero"),
			["icon"] = AppIcon(appid, it),
			["short"] = it["basic_info"]?.Value<string>("short_description") ?? "",
			["releaseTs"] = rel?.Value<long?>("steam_release_date") ?? rel?.Value<long?>("original_release_date") ?? 0,
			["comingSoon"] = rel?.Value<bool?>("is_coming_soon") == true,
			["free"] = it.Value<bool?>("is_free") == true,
			["wish"] = _wish.Contains(appid),
			["owned"] = _owned.Contains(appid),
		};
		var tags = new JArray();
		foreach (JToken t in (it["tagids"] as JArray ?? new JArray()).Take(5))
		{
			if (_tags != null && _tags.TryGetValue(t.Value<int>(), out string name))
			{
				tags.Add(name);
			}
		}
		o["tags"] = tags;
		var makers = new JArray();
		foreach (string group in new[] { "developers", "publishers" })
		{
			foreach (JObject who in (it["basic_info"]?[group] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
			{
				string name = who.Value<string>("name") ?? "";
				if (name != "" && !makers.Any(m => m.Value<string>() == name))
				{
					makers.Add(name);
				}
			}
		}
		o["makers"] = makers;
		// 评测：已发售的才显示（等级、好评率、总数；好评数按好评率算）
		if (it["reviews"]?["summary_filtered"] is JObject rv && o.Value<bool>("comingSoon") == false)
		{
			int count = rv.Value<int?>("review_count") ?? 0;
			int pct = rv.Value<int?>("percent_positive") ?? 0;
			o["reviews"] = new JObject
			{
				["count"] = count,
				["pct"] = pct,
				["positive"] = (int)Math.Round(count * pct / 100.0),
				["score"] = rv.Value<int?>("review_score") ?? 0,
				["label"] = rv.Value<string>("review_score_label") ?? "",
			};
		}
		if (it["best_purchase_option"] is JObject bpo)
		{
			long finalCents = long.TryParse(bpo.Value<string>("final_price_in_cents"), out long f) ? f : 0;
			var price = new JObject
			{
				["final"] = bpo.Value<string>("formatted_final_price") ?? "",
				["original"] = bpo.Value<string>("formatted_original_price") ?? "",
				["pct"] = bpo.Value<int?>("discount_pct") ?? 0,
				["end"] = (bpo["active_discounts"] as JArray)?.OfType<JObject>().Select(d => d.Value<long?>("discount_end_date") ?? 0).FirstOrDefault() ?? 0,
			};
			Config c = Store.Cfg;
			State st = Store.St;
			if (c.ShowCny && finalCents > 0 && st.Currency != "" && st.Currency != "CNY" && st.CnyRate > 0 && st.CnyRateCurrency == st.Currency)
			{
				price["cny"] = Math.Round(finalCents / 100.0 * st.CnyRate, 2);
			}
			o["price"] = price;
		}
		return o;
	}

	// ---- 页面的操作 ----

	public static async Task<bool> SetWishAsync(int appid, bool on)
	{
		await Steam.SetWishlistAsync(appid, on).ConfigureAwait(false);
		lock (L)
		{
			if (on)
			{
				_wish.Add(appid);
			}
			else
			{
				_wish.Remove(appid);
			}
		}
		return on;
	}

	/// <summary>换了账号或退出登录：清掉和账号有关的内容。</summary>
	public static void Reset()
	{
		lock (L)
		{
			Docs.Clear();
			_oldestDoc = long.MaxValue;
			_backDone = false;
			_followed = new Dictionary<int, ClanRec>();
			_wish = new HashSet<int>();
			_owned = new HashSet<int>();
		}
		Changed?.Invoke();
	}
}
