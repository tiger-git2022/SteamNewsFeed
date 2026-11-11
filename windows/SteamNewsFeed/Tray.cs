using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace SteamNewsFeed;

/// <summary>托盘图标：点关闭时缩到右下角的通知区域，后台继续刷新；单击图标打开，右键菜单和 WE 加权轮播、N网 Mod 更新一样。</summary>
public partial class MainWindow
{
	private Forms.NotifyIcon _tray;

	/// <summary>真的要退出（托盘菜单的「退出」、程序更新），不再缩到托盘。</summary>
	private bool _exiting;

	private const string TrayTitle = "Steam 新闻聚合";

	private readonly TrayMenu _trayMenu = new TrayMenu();

	private void InitTray()
	{
		_tray = new Forms.NotifyIcon { Text = TrayTitle, Icon = TrayIcon() };
		_tray.MouseClick += (s, e) =>
		{
			if (e.Button == Forms.MouseButtons.Left)
			{
				ShowTimeline();
			}
		};
		_tray.MouseUp += (s, e) =>
		{
			if (e.Button == Forms.MouseButtons.Right)
			{
				ShowTrayMenu();
			}
		};
		_tray.BalloonTipClicked += delegate { ShowTimeline(); };
		// Windows 通知：弹出时点、或者之后在通知中心里点，都打开时间线（程序没开着时会启动它）
		ToastNotificationManagerCompat.OnActivated += e => Dispatcher.BeginInvoke((Action)ShowTimeline);
		UpdateTrayVisible();
		Closed += delegate
		{
			_tray.Visible = false;
			_tray.Dispose();
		};
	}

	private void ShowTimeline()
	{
		BringToFront();
		SelectPage(0);
	}

	private void ShowTrayMenu()
	{
		try
		{
			var entries = new List<TrayMenu.Entry>
			{
				new TrayMenu.Entry { Text = _unread > 0 ? _unread + " 条未读" : "没有未读的", Enabled = false, Icon = SymbolRegular.News24 },
				new TrayMenu.Entry { Separator = true },
				new TrayMenu.Entry { Text = "打开时间线", Bold = true, Icon = SymbolRegular.Home24, Click = ShowTimeline },
				new TrayMenu.Entry
				{
					Text = Feed.Refreshing ? "正在刷新…" : "刷新",
					Enabled = !Feed.Refreshing && Steam.LoggedIn,
					Icon = SymbolRegular.ArrowSync24,
					Click = () => _ = RefreshAsync(true),
				},
				new TrayMenu.Entry { Text = "Steam 新闻中心", Icon = SymbolRegular.Globe24, Click = () => FeedView.OpenExternal(Steam.StoreUrl + "/news/") },
				new TrayMenu.Entry { Text = "设置…", Icon = SymbolRegular.Settings24, Click = () => { BringToFront(); SelectPage(1); } },
				new TrayMenu.Entry { Separator = true },
				new TrayMenu.Entry { Text = "退出", Icon = SymbolRegular.Power24, Click = ExitApp },
			};
			_trayMenu.Show(entries);
		}
		catch (Exception ex)
		{
			Logger.Log("托盘菜单打开失败：" + ex);
		}
	}

	private static System.Drawing.Icon TrayIcon()
	{
		using Stream s = typeof(MainWindow).Assembly.GetManifestResourceStream("SteamNewsFeed.app.ico");
		return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
	}

	/// <summary>开了「关闭时最小化到托盘」才常驻托盘图标。</summary>
	private void UpdateTrayVisible()
	{
		if (_tray != null)
		{
			_tray.Visible = Store.Cfg.CloseToTray;
		}
	}

	/// <summary>托盘图标上的提示：有未读时显示数量。</summary>
	private void UpdateTrayText()
	{
		if (_tray != null)
		{
			_tray.Text = _unread > 0 ? TrayTitle + " · " + _unread + " 条未读" : TrayTitle;
		}
	}

	/// <summary>窗口缩在托盘里时，发现新发售的游戏弹个通知。</summary>
	private void NotifyReleases(List<string> names)
	{
		if (IsVisible && IsActive || !Store.Cfg.Notify || names.Count == 0)
		{
			return;
		}
		string text = names.Count == 1 ? "《" + names[0] + "》" : "《" + names[0] + "》等 " + names.Count + " 款";
		try
		{
			new ToastContentBuilder()
				.AddArgument("action", "timeline")
				.AddText("关注的发行商有新游戏发售")
				.AddText(text + "，点这里查看")
				.Show();
		}
		catch (Exception ex)
		{
			Logger.Log("系统通知发送失败，改用托盘气泡：" + ex.Message);
			if (_tray != null && _tray.Visible)
			{
				_tray.ShowBalloonTip(6000, "关注的发行商有新游戏发售", text + "，点这里查看", Forms.ToolTipIcon.Info);
			}
		}
	}

	/// <summary>点关闭：缩到托盘。返回 true 表示已经缩到托盘（关闭要取消）。</summary>
	private bool CloseToTray()
	{
		if (_exiting || !Store.Cfg.CloseToTray || _tray == null)
		{
			return false;
		}
		Hide();
		_feed.PostAction("leave");
		if (!Store.Cfg.TrayHinted)
		{
			lock (Store.Lock)
			{
				Store.Cfg.TrayHinted = true;
			}
			Store.SaveConfig();
			_tray.ShowBalloonTip(5000, TrayTitle + " 还在后台运行", "会继续定时刷新。单击这里的图标重新打开，右键可以退出；在设置里可以改成点关闭直接退出。", Forms.ToolTipIcon.Info);
		}
		return true;
	}

	/// <summary>真的退出。</summary>
	public void ExitApp()
	{
		_exiting = true;
		Close();
	}
}
