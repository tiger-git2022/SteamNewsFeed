using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SteamNewsFeed;

/// <summary>全部设置，存在 exe 旁边的 snf.config.json。</summary>
public sealed class Config
{
	public string Theme = "system";

	public string Backdrop = "acrylic";

	public int TintPercent = 30;

	/// <summary>点关闭时缩到托盘（通知区域），程序在后台继续刷新。</summary>
	public bool CloseToTray = true;

	/// <summary>已经提示过「还在后台运行」。</summary>
	public bool TrayHinted;

	public bool CheckAppUpdate = true;

	/// <summary>自动刷新间隔（分钟），0 = 不自动刷新。</summary>
	public int RefreshMinutes = 30;

	/// <summary>DLC（包括原声带）也推送。</summary>
	public bool IncludeDlc = true;

	/// <summary>只推送关注的发行商 / 开发商自己的游戏（鉴赏家推荐的别家游戏不算）。</summary>
	public bool OnlyOwnGames = true;

	/// <summary>往回找多少天内发售的游戏。</summary>
	public int LookbackDays = 30;

	/// <summary>窗口缩在托盘时，发现新发售的游戏弹通知。</summary>
	public bool Notify = true;

	/// <summary>价格后面按汇率显示人民币。</summary>
	public bool ShowCny = true;

	/// <summary>新闻流里也显示 Steam「精选」的热门游戏新闻（Steam 新闻中心默认不显示）。</summary>
	public bool ShowFeatured;

	/// <summary>屏蔽的游戏：appid → 名字（设置里显示用）。</summary>
	public Dictionary<int, string> BlockedApps = new Dictionary<int, string>();

	/// <summary>屏蔽的账号（发行商 / 开发商 / 鉴赏家 / 组）：clan id → 名字。</summary>
	public Dictionary<int, string> BlockedClans = new Dictionary<int, string>();

	// ---- 翻译（大模型） ----

	/// <summary>接口格式：openai（OpenAI 兼容）/ anthropic（Claude）。</summary>
	public string AiFormat = "openai";

	/// <summary>接口地址，空 = 默认（DeepSeek / api.anthropic.com）。</summary>
	public string AiBaseUrl = "";

	/// <summary>API 密钥，用 Windows DPAPI 加密（只有当前 Windows 用户能解开）。</summary>
	public string AiKeyEnc = "";

	public string AiModel = "";

	/// <summary>思考时换用的模型（可选，比如 deepseek-reasoner）。</summary>
	public string AiThinkModel = "";

	/// <summary>思考：off 不思考 / on 思考 / auto 超过 AiThinkWords 个词才思考。</summary>
	public string AiThink = "auto";

	public int AiThinkWords = 800;

	/// <summary>流式输出：边翻边显示。</summary>
	public bool AiStream = true;

	/// <summary>不是中文的新闻和评论自动翻译。</summary>
	public bool AutoTranslate;

	public string TranslateTo = "简体中文";

	public static Config Load(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				Config c = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path, Encoding.UTF8)) ?? new Config();
				c.TintPercent = Math.Max(0, Math.Min(100, c.TintPercent));
				c.RefreshMinutes = Math.Max(0, Math.Min(24 * 60, c.RefreshMinutes));
				c.LookbackDays = Math.Max(3, Math.Min(180, c.LookbackDays));
				c.BlockedApps ??= new Dictionary<int, string>();
				c.BlockedClans ??= new Dictionary<int, string>();
				return c;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("读取设置失败，使用默认设置：" + ex.Message);
		}
		return new Config();
	}
}

/// <summary>关注的发行商 / 鉴赏家在商店里出过的一个游戏（或 DLC）。</summary>
public sealed class AppRec
{
	public int AppId;

	public string Name = "";

	/// <summary>商店的类型：0 游戏，4 DLC，11 原声带……</summary>
	public int Type;

	public bool Released;

	/// <summary>Steam 上的发售时间（Unix 秒）；没发售时为 0 或将来的时间。</summary>
	public long ReleaseTs;

	/// <summary>它是这些关注的发行商 / 开发商出的（clan id）。</summary>
	public List<int> Creators = new List<int>();

	/// <summary>在这些关注对象的列表里出现过（包括鉴赏家推荐）。</summary>
	public List<int> ListedBy = new List<int>();

	public long CheckedAt;
}

/// <summary>时间线里的一条“新发售”。</summary>
public sealed class ReleaseRec
{
	public int AppId;

	/// <summary>在时间线里排序用的时间。</summary>
	public long T;

	public long FoundAt;

	/// <summary>新闻流里已经有这款游戏的发售新闻（那条新闻的 gid）：不再单独显示。</summary>
	public string CoveredBy = "";

	/// <summary>游戏自己页面上的发售新闻（新闻流里没有）：直接把这条新闻推过来。</summary>
	public string EventGid = "";

	public string EventClan = "";

	/// <summary>不再找发售新闻了（找到了，或者发售超过 3 天还没有）。</summary>
	public bool NewsDone;
}

public sealed class ClanRec
{
	public string Name = "";

	public string Avatar = "";
}

/// <summary>运行状态（阅读进度、发现过的游戏等），存在 snf.state.json。</summary>
public sealed class State
{
	/// <summary>页面自己的状态（阅读进度、看过的条目、上次的位置），原样保存。</summary>
	public JObject Page = new JObject();

	public Dictionary<int, AppRec> Apps = new Dictionary<int, AppRec>();

	public Dictionary<int, ReleaseRec> Releases = new Dictionary<int, ReleaseRec>();

	public Dictionary<int, ClanRec> Clans = new Dictionary<int, ClanRec>();

	/// <summary>第一次检查完成之前发现的游戏按发售时间排；之后发现的、发售了一阵才发现的放到发现的时候。</summary>
	public bool BaselineDone;

	public string Country = "";

	public string Currency = "";

	public long CountryCheckedAt;

	public double CnyRate;

	public string CnyRateCurrency = "";

	public long CnyRateAt;

	public long LastRefresh;

	/// <summary>翻译过的新闻（gid|目标语言 → 标题、摘要、正文、时间），最多留 300 条。</summary>
	public Dictionary<string, JObject> Translations = new Dictionary<string, JObject>();
}

public static class Store
{
	public static readonly object Lock = new object();

	public static Config Cfg;

	public static State St;

	private static Timer _saveTimer;

	public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	public static void Load()
	{
		Cfg = Config.Load(Paths.Config);
		try
		{
			St = File.Exists(Paths.State) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(Paths.State, Encoding.UTF8)) : null;
		}
		catch (Exception ex)
		{
			Logger.Log("读取状态失败，重新开始：" + ex.Message);
		}
		St ??= new State();
		St.Page ??= new JObject();
		St.Apps ??= new Dictionary<int, AppRec>();
		St.Releases ??= new Dictionary<int, ReleaseRec>();
		St.Clans ??= new Dictionary<int, ClanRec>();
		St.Translations ??= new Dictionary<string, JObject>();
	}

	public static void SaveConfig()
	{
		try
		{
			string json;
			lock (Lock)
			{
				json = JsonConvert.SerializeObject(Cfg, Formatting.Indented);
			}
			WriteAtomic(Paths.Config, json);
		}
		catch (Exception ex)
		{
			Logger.Log("保存设置失败：" + ex.Message);
		}
	}

	public static void SaveSoon()
	{
		lock (Lock)
		{
			_saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
			_saveTimer.Change(1000, Timeout.Infinite);
		}
	}

	public static void SaveNow()
	{
		try
		{
			string json;
			lock (Lock)
			{
				json = JsonConvert.SerializeObject(St);
			}
			WriteAtomic(Paths.State, json);
		}
		catch (Exception ex)
		{
			Logger.Log("保存状态失败：" + ex.Message);
		}
	}

	public static void WriteAtomic(string path, string text)
	{
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, text, new UTF8Encoding(false));
		if (File.Exists(path))
		{
			File.Replace(tmp, path, null);
		}
		else
		{
			File.Move(tmp, path);
		}
	}
}
