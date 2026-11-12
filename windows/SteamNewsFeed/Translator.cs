using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SteamNewsFeed;

/// <summary>
/// 用大模型翻译新闻和评论。两种接口：
/// OpenAI 兼容（DeepSeek、通义千问、Kimi、智谱、OpenRouter……，直接发 HTTP）和 Anthropic Claude（官方 SDK）。
/// 可选思考 / 不思考 / 超过多少字才思考，流式 / 非流式。
/// </summary>
public static class Translator
{
	private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3, 3);

	public const string DefaultOpenAiBase = "https://api.deepseek.com/v1";

	public const string DefaultOpenAiModel = "deepseek-chat";

	public const string DefaultClaudeBase = "https://api.anthropic.com";

	public const string DefaultClaudeModel = "claude-opus-5-5";

	public static bool Configured
	{
		get
		{
			lock (Store.Lock)
			{
				return !string.IsNullOrEmpty(Store.Cfg.AiKeyEnc) && !string.IsNullOrWhiteSpace(Model(Store.Cfg));
			}
		}
	}

	private static string Model(Config c) => string.IsNullOrWhiteSpace(c.AiModel) ? (c.AiFormat == "anthropic" ? DefaultClaudeModel : DefaultOpenAiModel) : c.AiModel.Trim();

	private static string BaseUrl(Config c) => (string.IsNullOrWhiteSpace(c.AiBaseUrl) ? (c.AiFormat == "anthropic" ? DefaultClaudeBase : DefaultOpenAiBase) : c.AiBaseUrl.Trim()).TrimEnd('/');

	// ---- 密钥：用 Windows 的 DPAPI 加密后存进设置文件，只有当前 Windows 用户能解开 ----

	public static string Protect(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return "";
		}
		byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
		return Convert.ToBase64String(data);
	}

	public static string Unprotect(string enc)
	{
		if (string.IsNullOrEmpty(enc))
		{
			return "";
		}
		try
		{
			return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser));
		}
		catch
		{
			return "";
		}
	}

	// ---- 要不要思考 ----

	/// <summary>粗略的「词数」：中日韩的字一个算一个词，其他按空格分的词算。</summary>
	public static int CountWords(string text)
	{
		text = Regex.Replace(text ?? "", @"\[[^\]]{1,200}\]", " ");
		int cjk = Regex.Matches(text, @"[぀-ヿ㐀-鿿가-힯]").Count;
		int words = Regex.Matches(text, @"[\p{L}\p{N}]+").Cast<Match>().Count(m => !Regex.IsMatch(m.Value, @"[぀-ヿ㐀-鿿가-힯]"));
		return cjk + words;
	}

	private static bool ShouldThink(Config c, string text)
	{
		switch (c.AiThink)
		{
		case "on":
			return true;
		case "auto":
			return CountWords(text) > Math.Max(0, c.AiThinkWords);
		default:
			return false;
		}
	}

	// ---- 翻译 ----

	private static string Target(Config c) => string.IsNullOrWhiteSpace(c.TranslateTo) ? "简体中文" : c.TranslateTo;

	/// <summary>翻译一篇新闻：标题、摘要、正文（BBCode）。onText 收到的是到目前为止的原始输出（流式时会多次调用）。</summary>
	public static async Task<JObject> TranslatePostAsync(string title, string summary, string body, Action<string> onText)
	{
		Config c = Snapshot();
		string system =
			"你是专业的游戏新闻翻译。把用户给出的 Steam 新闻翻译成" + Target(c) + "。\n" +
			"规则：\n" +
			"1. 原样保留所有 BBCode 标签（方括号里的内容，例如 [b]、[/b]、[url=...]、[img]...[/img]、[list]、[*]、[h2]）、网址、{STEAM_CLAN_IMAGE} 和换行，只翻译标签之间的文字。\n" +
			"2. 游戏名、人名、公司名有通行译名就用译名，没有就保留原文。\n" +
			"3. 保留 ===TITLE===、===SUMMARY===、===BODY=== 这三个分隔行，每部分的译文放在对应分隔行下面；原文某部分为空，译文也留空。\n" +
			"4. 原文已经是目标语言的部分照抄。只输出译文，不要任何解释。";
		string user = "===TITLE===\n" + (title ?? "") + "\n===SUMMARY===\n" + (summary ?? "") + "\n===BODY===\n" + (body ?? "");
		bool think = ShouldThink(c, (title ?? "") + "\n" + (summary ?? "") + "\n" + (body ?? ""));
		string output = await RunAsync(c, system, user, think, onText).ConfigureAwait(false);
		JObject result = SplitPost(output);
		// 什么都没拿到时算失败（不当成译文、也不会进缓存），页面上能重试
		if (IsEmptyTranslation(result))
		{
			throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "接口没有返回译文" : "返回的译文格式不对，没找到分隔行");
		}
		return result;
	}

	public static bool IsEmptyTranslation(JObject t)
	{
		return t == null || string.IsNullOrWhiteSpace(t.Value<string>("title")) && string.IsNullOrWhiteSpace(t.Value<string>("summary")) && string.IsNullOrWhiteSpace(t.Value<string>("body"));
	}

	public static JObject SplitPost(string output)
	{
		string Part(string name)
		{
			Match m = Regex.Match(output ?? "", "===" + name + "===\\s*\\n?([\\s\\S]*?)(?=\\n?===(TITLE|SUMMARY|BODY)===|$)");
			return m.Success ? m.Groups[1].Value.Trim() : "";
		}
		return new JObject { ["title"] = Part("TITLE"), ["summary"] = Part("SUMMARY"), ["body"] = Part("BODY") };
	}

	/// <summary>一次翻译几条评论（纯文字），按原来的顺序返回。</summary>
	public static async Task<JArray> TranslateTextsAsync(IList<string> texts)
	{
		Config c = Snapshot();
		string system =
			"你是专业的翻译。把用户给出的几段 Steam 玩家评论分别翻译成" + Target(c) + "，语气口语化、自然。\n" +
			"保留 ===1===、===2=== 这样的分隔行，每段译文放在对应分隔行下面；原文已经是目标语言的照抄。只输出译文，不要解释。";
		var sb = new StringBuilder();
		for (int i = 0; i < texts.Count; i++)
		{
			sb.Append("===").Append(i + 1).Append("===\n").Append(texts[i] ?? "").Append('\n');
		}
		bool think = ShouldThink(c, sb.ToString());
		string output = await RunAsync(c, system, sb.ToString(), think, null).ConfigureAwait(false);
		var result = new JArray();
		for (int i = 0; i < texts.Count; i++)
		{
			Match m = Regex.Match(output, "===" + (i + 1) + "===\\s*\\n?([\\s\\S]*?)(?=\\n?===\\d+===|$)");
			result.Add(m.Success ? m.Groups[1].Value.Trim() : "");
		}
		return result;
	}

	/// <summary>设置页的「测试」：翻一句话，返回 (译文, 用时说明)。</summary>
	public static async Task<string> TestAsync()
	{
		Config c = Snapshot();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		string text = await RunAsync(c, "把用户的话翻译成" + Target(c) + "，只输出译文。", "The new update is out now! Thanks for playing.", ShouldThink(c, "short"), null).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("接口连通了，但没有返回译文（模型名是否正确？思考模型可能只返回了思考过程）");
		}
		return text.Trim() + "（" + (c.AiFormat == "anthropic" ? "Claude" : "OpenAI 兼容") + " · " + Model(c) + " · " + sw.Elapsed.TotalSeconds.ToString("0.0") + " 秒）";
	}

	private static Config Snapshot()
	{
		lock (Store.Lock)
		{
			return JsonConvert.DeserializeObject<Config>(JsonConvert.SerializeObject(Store.Cfg));
		}
	}

	private static async Task<string> RunAsync(Config c, string system, string user, bool think, Action<string> onText)
	{
		string key = Unprotect(c.AiKeyEnc);
		if (key == "")
		{
			throw new InvalidOperationException("还没有填 API 密钥（设置 → 翻译）");
		}
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			string text = c.AiFormat == "anthropic"
				? await ClaudeAsync(c, key, system, user, think, onText).ConfigureAwait(false)
				: await OpenAiAsync(c, key, system, user, think, onText).ConfigureAwait(false);
			return StripThink(text);
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>有的推理模型把思考过程用 &lt;think&gt; 包着放在正文里，去掉。</summary>
	private static string StripThink(string s) => Regex.Replace(s ?? "", @"^\s*<think>[\s\S]*?</think>\s*", "");

	// ---- OpenAI 兼容 ----

	private static async Task<string> OpenAiAsync(Config c, string key, string system, string user, bool think, Action<string> onText)
	{
		string baseUrl = BaseUrl(c);
		string host = Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri u) ? u.Host.ToLowerInvariant() : "";
		string model = think && !string.IsNullOrWhiteSpace(c.AiThinkModel) ? c.AiThinkModel.Trim() : Model(c);
		var body = new JObject
		{
			["model"] = model,
			["messages"] = new JArray
			{
				new JObject { ["role"] = "system", ["content"] = system },
				new JObject { ["role"] = "user", ["content"] = user },
			},
			["stream"] = c.AiStream,
		};
		// 各家开关思考的参数不一样：按地址判断（没有对应参数的，靠「思考用的模型」切换）
		if (host.Contains("dashscope") || host.Contains("siliconflow") || host.Contains("modelscope"))
		{
			body["enable_thinking"] = think;
		}
		else if (host.Contains("bigmodel") || host.EndsWith("z.ai") || host.Contains("volces") || host.Contains("ark."))
		{
			body["thinking"] = new JObject { ["type"] = think ? "enabled" : "disabled" };
		}
		else if (host.Contains("openrouter"))
		{
			body["reasoning"] = new JObject { ["enabled"] = think };
		}
		else if (host.Contains("api.openai.com") && Regex.IsMatch(model, @"^(o\d|gpt-5)", RegexOptions.IgnoreCase))
		{
			body["reasoning_effort"] = think ? "medium" : "low";
		}
		using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
		{
			Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
		};
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
			throw new InvalidOperationException("翻译接口出错（" + (int)resp.StatusCode + "）：" + ErrorText(err));
		}
		if (!c.AiStream)
		{
			JObject j = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
			// 用 SelectToken 取：choices 可能是空数组，直接 [0] 会越界
			string text = j.SelectToken("choices[0].message.content")?.Value<string>() ?? "";
			onText?.Invoke(text);
			return text;
		}
		var sb = new StringBuilder();
		using Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
		using var reader = new StreamReader(s, Encoding.UTF8);
		string line;
		while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
		{
			if (!line.StartsWith("data:", StringComparison.Ordinal))
			{
				continue;
			}
			string data = line.Substring(5).Trim();
			if (data == "[DONE]")
			{
				break;
			}
			JObject j;
			try
			{
				j = JObject.Parse(data);
			}
			catch
			{
				continue;
			}
			if (j["error"] != null)
			{
				throw new InvalidOperationException("翻译接口出错：" + ErrorText(data));
			}
			// 有的接口会发 choices 为空的块（开头的内容审核结果、结尾的用量统计），跳过即可
			string delta = j.SelectToken("choices[0].delta.content")?.Value<string>();
			if (!string.IsNullOrEmpty(delta))
			{
				sb.Append(delta);
				onText?.Invoke(sb.ToString());
			}
		}
		return sb.ToString();
	}

	private static string ErrorText(string raw)
	{
		try
		{
			JToken j = JToken.Parse(raw);
			string m = j["error"]?.Type == JTokenType.String ? j.Value<string>("error") : j["error"]?.Value<string>("message") ?? j.Value<string>("message");
			if (!string.IsNullOrEmpty(m))
			{
				return m;
			}
		}
		catch
		{
		}
		raw = (raw ?? "").Trim();
		return raw.Length > 200 ? raw.Substring(0, 200) + "…" : raw;
	}

	// ---- Anthropic Claude（官方 SDK） ----

	private static readonly HashSet<string> FallbackModels = new HashSet<string> { "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5" };

	private static async Task<string> ClaudeAsync(Config c, string key, string system, string user, bool think, Action<string> onText)
	{
		string baseUrl = BaseUrl(c);
		bool official = baseUrl.StartsWith("https://api.anthropic.com", StringComparison.OrdinalIgnoreCase);
		AnthropicClient client = new() { ApiKey = key, BaseUrl = baseUrl };
		string model = think && !string.IsNullOrWhiteSpace(c.AiThinkModel) ? c.AiThinkModel.Trim() : Model(c);
		var p = new MessageCreateParams
		{
			Model = model,
			MaxTokens = c.AiStream ? 64000 : 16000,
			System = system,
			Messages = [new BetaUserMessageParam { Content = user }],
			// 思考：自适应；不思考：不开思考、effort 调到最低（新模型思考关不掉，只能让它少想）
			OutputConfig = new BetaOutputConfig { Effort = think ? Effort.High : Effort.Low },
		};
		if (think)
		{
			p = p with { Thinking = new BetaThinkingConfigAdaptive() };
		}
		if (official && FallbackModels.Contains(model))
		{
			// 官方接口上被安全分类器拒绝时，服务端自动换模型重试
			p = p with { Betas = [AnthropicBeta.ServerSideFallback2026_06_01], Fallbacks = new List<BetaFallbackParam> { new BetaFallbackParam { Model = "claude-opus-4-8" } } };
		}
		if (!c.AiStream)
		{
			BetaMessage msg = await client.Beta.Messages.Create(p).ConfigureAwait(false);
			if (msg.StopReason == BetaStopReason.Refusal)
			{
				throw new InvalidOperationException("模型拒绝翻译这条内容");
			}
			string text = string.Concat(msg.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
			onText?.Invoke(text);
			return text;
		}
		var sb = new StringBuilder();
		bool refused = false;
		await foreach (BetaRawMessageStreamEvent ev in client.Beta.Messages.CreateStreaming(p).ConfigureAwait(false))
		{
			if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t))
			{
				sb.Append(t.Text);
				onText?.Invoke(sb.ToString());
			}
			else if (ev.TryPickDelta(out var md) && md.Delta.StopReason == BetaStopReason.Refusal)
			{
				refused = true;
			}
		}
		if (refused && sb.Length == 0)
		{
			throw new InvalidOperationException("模型拒绝翻译这条内容");
		}
		return sb.ToString();
	}
}
