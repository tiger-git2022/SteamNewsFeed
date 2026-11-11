using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace SteamNewsFeed;

public partial class MainWindow : FluentWindow
{
	private readonly FrameworkElement[] _pages;

	private readonly FeedView _feed;

	private bool _ready;

	private bool _loading;

	private int _page;

	private UpdateInfo _appUpdate;

	private readonly DispatcherTimer _tintSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

	/// <summary>每分钟看一次要不要自动刷新。</summary>
	private readonly DispatcherTimer _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };

	private readonly DispatcherTimer _unreadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

	/// <summary>这次运行已经刷新过一次（页面等它完成才加载，免得先显示一个空的时间线）。</summary>
	public static bool FirstRefreshDone;

	private bool _feedStarted;

	private int _unread;

	private BitmapFrame[] _iconFrames = new BitmapFrame[0];

	/// <summary>
	/// 窗口 / 任务栏图标用多尺寸的 app.ico（Windows 按需要的大小挑一档），标题栏的小图标按屏幕缩放挑最接近的一档，
	/// 不把 256 像素的大图缩小显示（那样会糊）。
	/// </summary>
	private void LoadIcons()
	{
		try
		{
			using Stream s = typeof(MainWindow).Assembly.GetManifestResourceStream("SteamNewsFeed.app.ico");
			var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			_iconFrames = decoder.Frames.OrderBy(f => f.PixelWidth).ToArray();
			Icon = decoder.Frames[0];
			UpdateTitleIcon();
			DpiChanged += delegate { UpdateTitleIcon(); };
			Loaded += delegate { UpdateTitleIcon(); };
		}
		catch (Exception ex)
		{
			Logger.Log("读取图标失败：" + ex.Message);
		}
	}

	private void UpdateTitleIcon()
	{
		if (_iconFrames.Length == 0)
		{
			return;
		}
		double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
		int want = (int)Math.Ceiling(16 * scale);
		TitleIcon.Source = _iconFrames.FirstOrDefault(f => f.PixelWidth >= want) ?? _iconFrames[_iconFrames.Length - 1];
	}

	public MainWindow()
	{
		InitializeComponent();
		LoadIcons();
		_pages = new FrameworkElement[] { PageFeed, PageSettings };
		_feed = new FeedView(this);
		PageFeed.Child = _feed;
		LoadSettings();
		_tintSave.Tick += delegate
		{
			_tintSave.Stop();
			Store.SaveConfig();
		};
		ApplicationThemeManager.Changed += OnThemeChanged;
		Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
		Closed += delegate
		{
			ApplicationThemeManager.Changed -= OnThemeChanged;
			Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		};
		ApplyAppearance();
		_ready = true;
		SelectPage(0);

		// 窗口还没显示（开机自启缩在托盘）也要有句柄：后台读 cookie、续期登录的隐藏浏览器挂在它下面
		WebEnv.HostHwnd = new WindowInteropHelper(this).EnsureHandle();
		Feed.Changed += () => Dispatcher.BeginInvoke((Action)OnFeedChanged);
		Feed.NewReleases += names => Dispatcher.BeginInvoke((Action)(() => NotifyReleases(names)));
		Steam.AuthChanged += () => Dispatcher.BeginInvoke((Action)UpdateSide);
		_unreadTimer.Tick += async delegate
		{
			_unreadTimer.Stop();
			int n = await Task.Run(Feed.CountUnread);
			SetUnread(n);
		};
		_refreshTimer.Tick += delegate
		{
			int minutes = Store.Cfg.RefreshMinutes;
			if (minutes > 0 && Steam.LoggedIn && !Feed.Refreshing && Store.Now - Store.St.LastRefresh >= minutes * 60)
			{
				_ = RefreshAsync();
			}
		};
		Loaded += delegate { StartFeed(); };
		// 最小化也算离开：记下「上次看到这里」
		StateChanged += delegate
		{
			if (WindowState == WindowState.Minimized)
			{
				_feed.PostAction("leave");
			}
		};
		InitTray();
		Closing += (s, e) =>
		{
			// 点关闭：缩到托盘（设置里可以关掉）
			if (CloseToTray())
			{
				e.Cancel = true;
				return;
			}
			// 真的退出：先把页面里的阅读进度存下来再关
			if (!_stateFlushed)
			{
				e.Cancel = true;
				_ = FlushAndCloseAsync();
			}
		};
		Dispatcher.BeginInvoke((Action)(async () => await StartupAsync()), DispatcherPriority.Background);
	}

	private bool _stateFlushed;

	private async Task FlushAndCloseAsync()
	{
		_exiting = true;
		await _feed.FlushStateAsync();
		Store.SaveNow();
		_stateFlushed = true;
		Close();
	}

	/// <summary>启动：读登录状态，刷新一次，检查程序更新。</summary>
	private async Task StartupAsync()
	{
		await Steam.SyncCookiesHiddenAsync();
		UpdateSide();
		await RefreshAsync();
		_refreshTimer.Start();
		if (Store.Cfg.CheckAppUpdate)
		{
			await CheckAppUpdateAsync(false);
		}
	}

	/// <summary>刷新。manual：用户点的刷新（页面里的按钮、托盘菜单），「上次看到这里」分界线跟着挪。</summary>
	public async Task RefreshAsync(bool manual = false)
	{
		await Task.Run(Feed.RefreshAsync);
		FirstRefreshDone = true;
		_feed.PostStatus();
		_feed.PostUpdated(manual);
		UpdateSide();
		UpdateUnreadSoon();
	}

	private void StartFeed()
	{
		if (!_feedStarted)
		{
			_feedStarted = true;
			_feed.Start();
		}
	}

	/// <summary>窗口缩在托盘里启动（开机自启）。</summary>
	public void StartHidden()
	{
		// 不显示窗口；托盘图标在 InitTray 里已经出来了
	}

	public void BringToFront()
	{
		if (WindowState == WindowState.Minimized)
		{
			WindowState = WindowState.Normal;
		}
		Show();
		Activate();
		Topmost = true;
		Topmost = false;
		StartFeed();
	}

	private void OnFeedChanged()
	{
		_feed.PostStatus();
		UpdateSide();
	}

	// ---- 翻译设置 ----

	private bool _aiLoading;

	private DispatcherTimer _aiSaveTimer;

	private void LoadAiSettings()
	{
		_aiLoading = true;
		Config c = Store.Cfg;
		SelectByTagString(AiFormatBox, c.AiFormat, "openai");
		AiBaseBox.Text = c.AiBaseUrl;
		AiKeyBox.Password = Translator.Unprotect(c.AiKeyEnc);
		AiModelBox.Text = c.AiModel;
		AiThinkModelBox.Text = c.AiThinkModel;
		SelectByTagString(AiThinkBox, c.AiThink, "auto");
		AiThinkWordsBox.Text = c.AiThinkWords.ToString(CultureInfo.InvariantCulture);
		AiStreamBox.IsChecked = c.AiStream;
		AutoTranslateBox.IsChecked = c.AutoTranslate;
		SelectByTagString(TranslateToBox, c.TranslateTo, "简体中文");
		UpdateAiHints();
		_aiLoading = false;
	}

	private static void SelectByTagString(ComboBox box, string value, string fallback)
	{
		box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value)
			?? box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == fallback);
	}

	private static string TagString(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

	private void UpdateAiHints()
	{
		bool claude = TagString(AiFormatBox, "openai") == "anthropic";
		AiBaseBox.PlaceholderText = claude ? Translator.DefaultClaudeBase : Translator.DefaultOpenAiBase;
		AiModelBox.PlaceholderText = claude ? Translator.DefaultClaudeModel : Translator.DefaultOpenAiModel;
		AiBaseHint.Text = claude
			? "留空用官方接口 " + Translator.DefaultClaudeBase + "；用中转的话填中转地址"
			: "填到 /v1 为止，例如 https://api.deepseek.com/v1、https://dashscope.aliyuncs.com/compatible-mode/v1";
		AiModelHint.Text = claude
			? "留空用 " + Translator.DefaultClaudeModel
			: "留空用 " + Translator.DefaultOpenAiModel + "（DeepSeek）；其他服务商填它的模型名";
		AiThinkHint.Text = claude
			? "思考：开自适应思考；不思考：把 effort 调到最低（新的 Claude 模型思考关不掉，只能让它少想）"
			: "按接口地址自动用对应的开关（通义 / 硅基流动 enable_thinking，智谱 / 火山 thinking，OpenRouter reasoning）；其他服务商用下面的「思考时换用的模型」";
		bool auto = TagString(AiThinkBox, "auto") == "auto";
		AiThinkWordsBox.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
		AiThinkWordsUnit.Visibility = AiThinkWordsBox.Visibility;
	}

	private void AiFormat_Changed(object sender, RoutedEventArgs e)
	{
		if (_aiLoading || !_ready)
		{
			return;
		}
		// 换了接口格式：地址、模型是另一家的，清空用默认
		AiBaseBox.Text = "";
		AiModelBox.Text = "";
		AiThinkModelBox.Text = "";
		Ai_Changed(sender, e);
	}

	private void Ai_Changed(object sender, RoutedEventArgs e)
	{
		if (_aiLoading || !_ready)
		{
			return;
		}
		UpdateAiHints();
		if (_aiSaveTimer == null)
		{
			_aiSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
			_aiSaveTimer.Tick += delegate
			{
				_aiSaveTimer.Stop();
				SaveAiSettings();
			};
		}
		_aiSaveTimer.Stop();
		_aiSaveTimer.Start();
	}

	private void SaveAiSettings()
	{
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			c.AiFormat = TagString(AiFormatBox, "openai");
			c.AiBaseUrl = AiBaseBox.Text.Trim();
			c.AiKeyEnc = Translator.Protect(AiKeyBox.Password.Trim());
			c.AiModel = AiModelBox.Text.Trim();
			c.AiThinkModel = AiThinkModelBox.Text.Trim();
			c.AiThink = TagString(AiThinkBox, "auto");
			if (int.TryParse(AiThinkWordsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w >= 0)
			{
				c.AiThinkWords = w;
			}
			c.AiStream = AiStreamBox.IsChecked == true;
			c.AutoTranslate = AutoTranslateBox.IsChecked == true;
			c.TranslateTo = TagString(TranslateToBox, "简体中文");
		}
		Store.SaveConfig();
		_feed.PostStatus();
	}

	private async void AiTest_Click(object sender, RoutedEventArgs e)
	{
		_aiSaveTimer?.Stop();
		SaveAiSettings();
		AiTestButton.IsEnabled = false;
		AiTestText.Text = "正在测试…";
		try
		{
			AiTestText.Text = "可以用：" + await Task.Run(Translator.TestAsync);
		}
		catch (Exception ex)
		{
			AiTestText.Text = "失败：" + ex.Message;
		}
		AiTestButton.IsEnabled = true;
	}

	/// <summary>屏蔽的列表变了。fromSettings：在设置里取消屏蔽的，时间线要重新加载（在时间线里屏蔽的，页面自己把那几条拿掉）。</summary>
	public void BlockedChanged(bool fromSettings)
	{
		FillBlockedList();
		UpdateUnreadSoon();
		if (fromSettings)
		{
			_feed.PostUpdated();
		}
	}

	private void FillBlockedList()
	{
		var rows = new System.Collections.Generic.List<(string Kind, int Id, string Name)>();
		lock (Store.Lock)
		{
			rows.AddRange(Store.Cfg.BlockedApps.Select(kv => ("app", kv.Key, kv.Value)));
			rows.AddRange(Store.Cfg.BlockedClans.Select(kv => ("clan", kv.Key, kv.Value)));
		}
		BlockedHint.Text = rows.Count == 0
			? "还没有屏蔽任何东西。在时间线里点每条右上角的「⋯」可以屏蔽这个游戏或账号，之后它的新闻和新发售都不再显示"
			: "共 " + rows.Count + " 个。屏蔽后它们的新闻和新发售都不再显示、也不弹通知";
		BlockedList.Children.Clear();
		foreach (var r in rows.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCulture))
		{
			var grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			var text = new System.Windows.Controls.TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
			text.Inlines.Add(new System.Windows.Documents.Run(r.Name));
			var kind = new System.Windows.Documents.Run("　" + (r.Kind == "app" ? "游戏" : "账号"));
			kind.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
			text.Inlines.Add(kind);
			grid.Children.Add(text);
			var button = new Button { Content = "取消屏蔽", Margin = new Thickness(12, 0, 0, 0) };
			Grid.SetColumn(button, 1);
			var row = r;
			button.Click += (s, e) =>
			{
				Feed.SetBlocked(row.Kind, row.Id, row.Name, false);
				BlockedChanged(true);
			};
			grid.Children.Add(button);
			var border = new Border { Child = grid, Padding = new Thickness(16, 8, 12, 8), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(4) };
			border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			BlockedList.Children.Add(border);
		}
	}

	public void UpdateUnreadSoon()
	{
		_unreadTimer.Stop();
		_unreadTimer.Start();
	}

	private void SetUnread(int n)
	{
		_unread = n;
		NavBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
		NavCount.Text = n > 99 ? "99+" : n.ToString();
		UpdateTrayText();
		_feed.PostUnread(n);
	}

	// ---- 页面 ----

	private void Nav_Click(object sender, RoutedEventArgs e)
	{
		if (int.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out int page))
		{
			SelectPage(page);
		}
	}

	public void SelectPage(int page)
	{
		_page = page;
		for (int i = 0; i < _pages.Length; i++)
		{
			_pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
		}
		foreach (object child in Nav.Children)
		{
			if (child is Button b)
			{
				b.Appearance = b.Tag?.ToString() == page.ToString() ? ControlAppearance.Secondary : ControlAppearance.Transparent;
			}
		}
		if (page == 1)
		{
			UpdateLoginState();
		}
	}

	private void UpdateSide()
	{
		SideUser.Text = Steam.LoggedIn ? (Steam.LoginFresh ? "已登录 Steam" : "Steam 登录已过期") : "未登录 Steam";
		long last = Store.St.LastRefresh;
		SideRefresh.Text = Feed.Refreshing ? (Feed.Progress != "" ? Feed.Progress : "正在刷新…")
			: Feed.Error != "" ? Feed.Error
			: last > 0 ? "上次刷新 " + DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime.ToString("HH:mm") : "";
		if (_page == 1)
		{
			UpdateLoginState();
		}
	}

	private void UpdateLoginState()
	{
		Newtonsoft.Json.Linq.JObject s = Feed.StatusJson();
		if (Steam.LoggedIn)
		{
			LoginState.Text = "已登录（Steam ID " + Steam.SteamId + "）" + (s.Value<int>("followed") > 0 ? "，关注了 " + s.Value<int>("followed") + " 个发行商 / 鉴赏家" : "")
				+ (Steam.LoginFresh ? "" : "。登录票据已过期，刷新时会自动续期；续期失败请重新登录");
			LoginButton.Content = "重新登录";
			LogoutButton.Visibility = Visibility.Visible;
		}
		else
		{
			LoginState.Text = "用你的 Steam 账号读取新闻流、关注列表和愿望单（登录状态保存在程序文件夹里）";
			LoginButton.Content = "登录";
			LogoutButton.Visibility = Visibility.Collapsed;
		}
		string cur = Store.St.Currency;
		RateText.Text = cur == "" ? "按当天汇率换算，半天更新一次"
			: cur == "CNY" ? "你的商店价格已经是人民币"
			: Store.St.CnyRate > 0 ? "商店货币 " + cur + "，1 " + cur + " ≈ " + Store.St.CnyRate.ToString("0.####", CultureInfo.InvariantCulture) + " 元（" + DateTimeOffset.FromUnixTimeSeconds(Store.St.CnyRateAt).LocalDateTime.ToString("MM-dd HH:mm") + " 更新）"
			: "商店货币 " + cur + "，汇率还没读到";
	}

	public async Task LoginAsync()
	{
		string before = Steam.SteamId;
		await Steam.ShowLoginAsync(this);
		if (Steam.SteamId != before)
		{
			Feed.Reset();
		}
		UpdateSide();
		if (Steam.LoggedIn)
		{
			await RefreshAsync();
		}
	}

	private async void Login_Click(object sender, RoutedEventArgs e)
	{
		await LoginAsync();
	}

	private async void Logout_Click(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show("退出 Steam 登录？", "Steam 新闻聚合", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		await Steam.LogoutAsync();
		Feed.Reset();
		UpdateSide();
		_feed.PostStatus();
		_feed.PostUpdated();
	}

	// ---- 设置 ----

	private static void SelectByTag(ComboBox box, int value, int fallback)
	{
		box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value.ToString())
			?? box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == fallback.ToString());
	}

	private static int TagOf(ComboBox box, int fallback) => int.TryParse((box.SelectedItem as ComboBoxItem)?.Tag as string, out int v) ? v : fallback;

	private void LoadSettings()
	{
		_loading = true;
		Config c = Store.Cfg;
		IncludeDlc.IsChecked = c.IncludeDlc;
		OnlyOwnGames.IsChecked = c.OnlyOwnGames;
		ShowCny.IsChecked = c.ShowCny;
		ShowFeatured.IsChecked = c.ShowFeatured;
		FillBlockedList();
		LoadAiSettings();
		NotifyBox.IsChecked = c.Notify;
		CloseToTrayBox.IsChecked = c.CloseToTray;
		CheckAppUpdate.IsChecked = c.CheckAppUpdate;
		AutoStartBox.IsChecked = AutoStart.Enabled;
		SelectByTag(LookbackBox, c.LookbackDays, 30);
		SelectByTag(RefreshBox, c.RefreshMinutes, 30);
		ThemeBox.SelectedIndex = c.Theme == "light" ? 1 : (c.Theme == "dark" ? 2 : 0);
		BackdropBox.SelectedIndex = c.Backdrop == "mica" ? 1 : (c.Backdrop == "none" ? 2 : 0);
		TintSlider.Value = c.TintPercent;
		VersionText.Text = "Steam 新闻聚合 v" + Updater.CurrentVersion;
		_loading = false;
	}

	private void Option_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading || !_ready)
		{
			return;
		}
		bool feedChanged, rateChanged;
		lock (Store.Lock)
		{
			Config c = Store.Cfg;
			feedChanged = c.IncludeDlc != (IncludeDlc.IsChecked == true) || c.OnlyOwnGames != (OnlyOwnGames.IsChecked == true) || c.ShowCny != (ShowCny.IsChecked == true) || c.ShowFeatured != (ShowFeatured.IsChecked == true);
			rateChanged = !c.ShowCny && ShowCny.IsChecked == true;
			int lookback = TagOf(LookbackBox, 30);
			bool lookbackGrew = lookback > c.LookbackDays;
			c.IncludeDlc = IncludeDlc.IsChecked == true;
			c.OnlyOwnGames = OnlyOwnGames.IsChecked == true;
			c.ShowCny = ShowCny.IsChecked == true;
			c.ShowFeatured = ShowFeatured.IsChecked == true;
			c.Notify = NotifyBox.IsChecked == true;
			c.CloseToTray = CloseToTrayBox.IsChecked == true;
			c.CheckAppUpdate = CheckAppUpdate.IsChecked == true;
			c.LookbackDays = lookback;
			c.RefreshMinutes = TagOf(RefreshBox, 30);
			if (lookbackGrew)
			{
				// 往回找得更远：已经查过的游戏重新判断一遍
				foreach (AppRec a in Store.St.Apps.Values)
				{
					a.CheckedAt = 0;
				}
				rateChanged = true;
			}
		}
		Store.SaveConfig();
		UpdateTrayVisible();
		if (rateChanged)
		{
			_ = RefreshAsync();
		}
		else if (feedChanged)
		{
			_feed.PostUpdated();
			UpdateUnreadSoon();
		}
	}

	private void AutoStart_Click(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		AutoStart.Enabled = AutoStartBox.IsChecked == true;
		AutoStartBox.IsChecked = AutoStart.Enabled;
	}

	// ---- 外观（和 WE 加权轮播、N网 Mod 更新一样）----

	private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready || _loading)
		{
			return;
		}
		lock (Store.Lock)
		{
			Store.Cfg.Theme = ThemeBox.SelectedIndex == 1 ? "light" : (ThemeBox.SelectedIndex == 2 ? "dark" : "system");
			Store.Cfg.Backdrop = BackdropBox.SelectedIndex == 1 ? "mica" : (BackdropBox.SelectedIndex == 2 ? "none" : "acrylic");
		}
		ApplyAppearance();
		Store.SaveConfig();
	}

	private void ApplyAppearance()
	{
		Config cfg = Store.Cfg;
		WindowBackdropType backdrop = cfg.Backdrop == "mica" ? WindowBackdropType.Mica : (cfg.Backdrop == "none" ? WindowBackdropType.None : WindowBackdropType.Acrylic);
		// 跟随系统时自己读 Windows 的「应用使用浅色主题」开关，不用 WPF-UI 的监听器：
		// 它在壁纸、强调色变化时也会重新判断，偶尔会把深色系统判成浅色
		ApplicationThemeManager.Apply(Program.WantDark() ? ApplicationTheme.Dark : ApplicationTheme.Light, backdrop, updateAccent: true);
		WindowBackdropType = backdrop;
		UpdateTint();
	}

	private void UpdateTint()
	{
		Config cfg = Store.Cfg;
		bool acrylic = cfg.Backdrop == "acrylic";
		bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
		Tint.Fill = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
		// 和网页里的徽标一样：深色时浅橙底黑字，浅色时深橙底白字
		NavBadge.Background = new SolidColorBrush(dark ? Color.FromRgb(0xF0, 0xA0, 0x50) : Color.FromRgb(0xC2, 0x5E, 0x00));
		NavCount.Foreground = dark ? new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)) : Brushes.White;
		// 浅色时卡片、按钮、输入框用半透明的白（和网页一样），不然 WPF-UI 默认 70% 的白底压在亚克力上很突兀；深色用 WPF-UI 自己的
		var lightFills = new (string Key, byte Alpha)[]
		{
			("CardBackgroundFillColorDefaultBrush", 0x33), ("CardBackgroundFillColorSecondaryBrush", 0x24),
			("ControlFillColorDefaultBrush", 0x4D), ("ControlFillColorSecondaryBrush", 0x6B), ("ControlFillColorTertiaryBrush", 0x33)
		};
		foreach (var f in lightFills)
		{
			if (!dark && acrylic)
			{
				Resources[f.Key] = new SolidColorBrush(Color.FromArgb(f.Alpha, 0xFF, 0xFF, 0xFF));
			}
			else
			{
				Resources.Remove(f.Key);
			}
		}
		Tint.Opacity = acrylic ? cfg.TintPercent / 100.0 : 0.0;
		PageLayer.Background = acrylic ? Brushes.Transparent : (Brush)FindResource("LayerFillColorDefaultBrush");
		TintCard.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
		TintText.Text = cfg.TintPercent + "%";
	}

	/// <summary>系统设置变了（深浅色、强调色、壁纸等）：只有深浅色或强调色真的变了才重新套用。</summary>
	private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
			if (Program.WantDark() != dark)
			{
				ApplyAppearance();
				return;
			}
			Color before = ApplicationAccentColorManager.PrimaryAccent;
			ApplicationAccentColorManager.ApplySystemAccent();
			if (ApplicationAccentColorManager.PrimaryAccent != before)
			{
				_feed.PostTheme();
			}
		});
	}

	private void OnThemeChanged(ApplicationTheme theme, Color accent)
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			UpdateTint();
			_feed.PostTheme();
		});
	}

	private void Tint_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready)
		{
			return;
		}
		Store.Cfg.TintPercent = (int)Math.Round(TintSlider.Value);
		UpdateTint();
		_tintSave.Stop();
		_tintSave.Start();
	}

	// ---- 程序更新 ----

	private async Task CheckAppUpdateAsync(bool manual)
	{
		UpdateState.Text = "正在检查…";
		try
		{
			_appUpdate = await Updater.CheckAsync();
			if (_appUpdate == null)
			{
				UpdateState.Text = "已经是最新版本";
				AppUpdateBox.Visibility = Visibility.Collapsed;
				return;
			}
			UpdateState.Text = "有新版本 v" + _appUpdate.Version;
			AppUpdateText.Text = "新版本 v" + _appUpdate.Version + " 可以更新";
			AppUpdateBox.Visibility = Visibility.Visible;
			if (manual)
			{
				AppUpdate_Click(null, null);
			}
		}
		catch (Exception ex)
		{
			UpdateState.Text = "检查更新失败：" + ex.Message;
			Logger.Log("检查更新失败：" + ex.Message);
		}
	}

	private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
	{
		await CheckAppUpdateAsync(true);
	}

	private async void AppUpdate_Click(object sender, RoutedEventArgs e)
	{
		if (_appUpdate == null)
		{
			return;
		}
		string notes = _appUpdate.Notes.Length > 800 ? _appUpdate.Notes.Substring(0, 800) + "…" : _appUpdate.Notes;
		if (MessageBox.Show("更新到 v" + _appUpdate.Version + "？\n\n" + notes + "\n\n设置和阅读进度不会丢失，更新完成后会自动重新打开。", "Steam 新闻聚合", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		if (!Updater.CanWriteAppDir())
		{
			MessageBox.Show("程序文件夹不能写入（可能放在了 Program Files 里），请手动下载新版本。", "Steam 新闻聚合");
			Process.Start(_appUpdate.PageUrl);
			return;
		}
		AppUpdateButton.IsEnabled = false;
		try
		{
			string newExe = await Updater.DownloadAsync(_appUpdate, new Progress<int>(p =>
			{
				AppUpdateText.Text = "正在下载 v" + _appUpdate.Version + "… " + p + "%";
				UpdateState.Text = AppUpdateText.Text;
			}));
			Store.SaveNow();
			Updater.StartInstall(newExe);
			ExitApp();
		}
		catch (Exception ex)
		{
			AppUpdateButton.IsEnabled = true;
			AppUpdateText.Text = "更新失败：" + ex.Message;
			Logger.Log("更新失败：" + ex);
		}
	}

	// ---- 其他 ----

	private void OpenLog_Click(object sender, RoutedEventArgs e)
	{
		if (File.Exists(Paths.Log))
		{
			Process.Start("notepad.exe", "\"" + Paths.Log + "\"");
		}
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		Process.Start("explorer.exe", "\"" + Paths.AppDir + "\"");
	}

	private void OpenRepo_Click(object sender, RoutedEventArgs e)
	{
		FeedView.OpenExternal("https://github.com/" + Updater.Repo);
	}
}

/// <summary>开机自启：HKCU\...\Run 里的一项，带 --tray 参数（启动后缩在托盘）。</summary>
public static class AutoStart
{
	private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

	private const string Name = "SteamNewsFeed";

	private static string Command => "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" " + Program.TrayArg;

	public static bool Enabled
	{
		get
		{
			try
			{
				using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
				return k?.GetValue(Name) is string;
			}
			catch
			{
				return false;
			}
		}
		set
		{
			try
			{
				using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
				if (value)
				{
					k.SetValue(Name, Command);
				}
				else
				{
					k.DeleteValue(Name, false);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("设置开机自启失败：" + ex.Message);
			}
		}
	}
}
