using System;
using System.IO;
using System.Text;

namespace SteamNewsFeed;

public static class Paths
{
	public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

	public static string Config => Path.Combine(AppDir, "snf.config.json");

	public static string State => Path.Combine(AppDir, "snf.state.json");

	public static string Log => Path.Combine(AppDir, "snf.log");

	public static string WebData => Path.Combine(AppDir, "webview2");
}

/// <summary>日志：snf.log，超过 2 MB 时把旧的改名为 snf.log.old。</summary>
public static class Logger
{
	private static readonly object Lock = new object();

	public static void Log(string message)
	{
		try
		{
			lock (Lock)
			{
				var fi = new FileInfo(Paths.Log);
				if (fi.Exists && fi.Length > 2 * 1024 * 1024)
				{
					string old = Paths.Log + ".old";
					if (File.Exists(old))
					{
						File.Delete(old);
					}
					File.Move(Paths.Log, old);
				}
				File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch
		{
		}
	}
}
