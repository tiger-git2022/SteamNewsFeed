using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SteamNewsFeed;

/// <summary>GitHub 上的一个新版本。</summary>
public sealed class UpdateInfo
{
	public string Version;
	public string Notes;
	public string PageUrl;
	public string ExeUrl;
	public string Sha256;
}

/// <summary>
/// 检查 GitHub Releases 上的新版本，下载新的 exe、校验 SHA-256，退出后用一个隐藏的 PowerShell 脚本把它换上再重新打开。
/// 程序文件夹里的其他文件（设置、记录、日志等）不会动。
/// </summary>
public static class Updater
{
	public const string Repo = "tiger-git2022/SteamNewsFeed";
	public const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
	const string AppName = "SteamNewsFeed";

	/// <summary>发布里的程序：一个单独的 SteamNewsFeed.exe。</summary>
	static bool IsAsset(string name) => name.Equals(AppName + ".exe", StringComparison.OrdinalIgnoreCase);

	static readonly HttpClient Http = CreateClient();

	static HttpClient CreateClient()
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(AppName + "/" + CurrentVersion);
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	public static string CurrentVersion => string.Join(".", (System.Windows.Forms.Application.ProductVersion ?? "0.0.0").Split('.', '+', '-').Take(3));

	public static string AppDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

	/// <summary>
	/// 发版时附带的 update.json（和接口返回的发布信息格式相同）。走的是普通下载地址，不占 GitHub 接口
	/// 每个 IP 每小时 60 次的额度。
	/// </summary>
	public const string ManifestUrl = ReleasesUrl + "/download/update.json";

	/// <summary>先读 update.json；读不到（比如旧版本发布时还没有这个文件）再问 GitHub 接口。</summary>
	static async Task<string> LatestReleaseJsonAsync()
	{
		try
		{
			return await Http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
		}
		catch (Exception manifestError) when (manifestError is HttpRequestException || manifestError is TaskCanceledException)
		{
			try
			{
				return await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false);
			}
			catch (HttpRequestException apiError)
			{
				throw new HttpRequestException("update.json：" + manifestError.Message + "；GitHub 接口：" + apiError.Message);
			}
		}
	}

	/// <summary>有比当前新的版本就返回它，否则返回 null。</summary>
	public static async Task<UpdateInfo> CheckAsync()
	{
		string json = await LatestReleaseJsonAsync().ConfigureAwait(false);
		JObject r = JObject.Parse(json.TrimStart('﻿'));
		string tag = r.Value<string>("tag_name") ?? "";
		int[] latest = Parse(tag);
		if (latest == null || Compare(latest, Parse(CurrentVersion)) <= 0)
		{
			return null;
		}
		var info = new UpdateInfo { Version = tag.TrimStart('v', 'V'), Notes = r.Value<string>("body") ?? "", PageUrl = r.Value<string>("html_url") ?? ReleasesUrl };
		if (r["assets"] is JArray list)
		{
			foreach (JObject a in list.OfType<JObject>())
			{
				if (IsAsset(a.Value<string>("name") ?? ""))
				{
					info.ExeUrl = a.Value<string>("browser_download_url");
					string digest = a.Value<string>("digest") ?? "";
					info.Sha256 = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest.Substring(7).ToLowerInvariant() : null;
					break;
				}
			}
		}
		return info;
	}

	/// <summary>下载并校验新的 exe，返回本地路径。</summary>
	public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> progress)
	{
		if (info.ExeUrl == null)
		{
			throw new InvalidDataException("这个版本没有可下载的程序");
		}
		var uri = new Uri(info.ExeUrl);
		if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("下载地址不属于项目仓库");
		}
		if (info.Sha256 == null)
		{
			throw new InvalidDataException("缺少 SHA-256 校验值");
		}
		string work = WorkDir(info.Version);
		string path = Path.Combine(work, AppName + ".exe");
		using (HttpResponseMessage response = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
		{
			response.EnsureSuccessStatusCode();
			long total = response.Content.Headers.ContentLength ?? -1;
			using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using FileStream output = File.Create(path);
			var buffer = new byte[81920];
			long read = 0;
			int n;
			while ((n = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
			{
				await output.WriteAsync(buffer, 0, n).ConfigureAwait(false);
				read += n;
				if (total > 0)
				{
					progress?.Report((int)(read * 100 / total));
				}
			}
		}
		string actual;
		using (var sha = SHA256.Create())
		using (FileStream file = File.OpenRead(path))
		{
			actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
		}
		if (actual != info.Sha256)
		{
			File.Delete(path);
			throw new InvalidDataException("SHA-256 校验失败，下载的文件可能不完整");
		}
		return path;
	}

	/// <summary>程序文件夹能不能写（放在 Program Files 里时不能自动更新）。</summary>
	public static bool CanWriteAppDir()
	{
		try
		{
			string probe = Path.Combine(AppDir, ".update-probe-" + Guid.NewGuid().ToString("N"));
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>启动更新脚本。调用后程序要马上退出，脚本等进程结束后换上新的 exe 并重新打开程序。</summary>
	public static void StartInstall(string newExe)
	{
		string work = Path.GetDirectoryName(newExe);
		string exe = Process.GetCurrentProcess().MainModule.FileName;
		string script = Path.Combine(work, "update.ps1");
		File.WriteAllText(script, Script, new UTF8Encoding(true));
		string args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script)
			+ " -ProcId " + Process.GetCurrentProcess().Id + " -Source " + Quote(newExe) + " -Exe " + Quote(exe) + " -Log " + Quote(Path.Combine(work, "update.log"));
		Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work });
	}

	static string WorkDir(string version)
	{
		string dir = Path.Combine(Path.GetTempPath(), AppName + "-update-" + version);
		Directory.CreateDirectory(dir);
		return dir;
	}

	static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";


	static int[] Parse(string v)
	{
		string[] parts = (v ?? "").Trim().TrimStart('v', 'V').Split('.');
		if (parts.Length < 2)
		{
			return null;
		}
		var nums = new int[3];
		for (int i = 0; i < Math.Min(3, parts.Length); i++)
		{
			if (!int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out nums[i]))
			{
				return null;
			}
		}
		return nums;
	}

	static int Compare(int[] a, int[] b)
	{
		b ??= new int[3];
		for (int i = 0; i < 3; i++)
		{
			if (a[i] != b[i])
			{
				return a[i].CompareTo(b[i]);
			}
		}
		return 0;
	}

	// 等旧进程退出 → 用新的 exe 覆盖旧的（被占用时重试）→ 重新打开程序。出错写进 update.log，仍然尝试打开程序。
	const string Script = @"param([int]$ProcId, [string]$Source, [string]$Exe, [string]$Log)
$ErrorActionPreference = 'Stop'
try {
  try { Wait-Process -Id $ProcId -Timeout 120 } catch { }
  Start-Sleep -Milliseconds 500
  for ($i = 0; ; $i++) {
    try { Copy-Item -LiteralPath $Source -Destination $Exe -Force; break } catch { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 500 }
  }
  'ok ' + (Get-Date) | Out-File -LiteralPath $Log -Encoding utf8
} catch {
  ('failed ' + (Get-Date) + ': ' + $_) | Out-File -LiteralPath $Log -Encoding utf8
}
Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe)
";
}
