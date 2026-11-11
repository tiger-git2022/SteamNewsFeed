using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace SteamNewsFeed;

/// <summary>
/// 程序是一个单独的 exe：.NET 依赖由 Costura 嵌在 exe 里；WebView2 的原生 WebView2Loader.dll 也嵌在里面，
/// 启动时释放到 webview2\loader 文件夹再告诉 WebView2 去那里找。
/// </summary>
public static class SingleFile
{
	public static void Prepare()
	{
		string dir = ExtractLoader(Path.Combine(Paths.WebData, "loader")) ?? ExtractLoader(Path.Combine(Path.GetTempPath(), "SteamNewsFeed-loader"));
		if (dir != null)
		{
			UseLoader(dir);
		}
	}

	// 单独一个方法：WebView2 的程序集在这之后才加载
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void UseLoader(string dir) => CoreWebView2Environment.SetLoaderDllFolderPath(dir);

	/// <summary>把 WebView2Loader.dll 释放到 dir，内容一样就不重写。失败返回 null。</summary>
	private static string ExtractLoader(string dir)
	{
		try
		{
			byte[] data;
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamNewsFeed.WebView2Loader.dll"))
			using (var ms = new MemoryStream())
			{
				s.CopyTo(ms);
				data = ms.ToArray();
			}
			string path = Path.Combine(dir, "WebView2Loader.dll");
			if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data))
			{
				return dir;
			}
			Directory.CreateDirectory(dir);
			string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllBytes(tmp, data);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(tmp, path);
			return dir;
		}
		catch (Exception e)
		{
			Logger.Log("释放 WebView2Loader.dll 到 " + dir + " 失败：" + e.Message);
			return null;
		}
	}
}
