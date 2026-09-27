using CodexBarWin.Interop;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace CodexBarWin.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly Dictionary<string, AccountViewModel> _accountViews = new(StringComparer.Ordinal);
    private bool _updatingSettings;
    private bool _allowClose;
    private readonly AwayModeController _awayMode = new();
    private bool _activityScanning;
    private bool _sessionScanning;
    private DateTimeOffset _activityScannedAt;
    private DateTimeOffset _sessionScannedAt;
    private DispatcherTimer? _settingsNavigationTimer;
    private NavigationViewItem? _lastContentNavigationItem;
    private bool _updatingFeatureToggles;

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public AppState State => _state;

    public MainWindow(AppState state)
    {
        _state = state;
        _awayMode.DisplayOffStarted += (_, _) => _state.SetDisplayOffRequested(true);
        _awayMode.Woke += (_, _) => _state.SetDisplayOffRequested(false);
        _awayMode.Failed += (_, message) => _state.ReportBackgroundError("黑屏离开失败：" + message);
        _updatingSettings = true;
        InitializeComponent();
        InitializeSettingsOptions();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AccountList.DataContext = this;
        SizeBeforeFirstFrame();
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        Navigation.SelectedItem = Navigation.MenuItems[0];
        _state.Changed += State_Changed;
        _state.RefreshFinished += State_RefreshFinished;
        Render();
    }

    private void SizeBeforeFirstFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        AppWindow.Resize(new SizeInt32((int)Math.Round(1080 * scale), (int)Math.Round(740 * scale)));
    }

    private void ResponsivePage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.Content is not FrameworkElement content)
            return;

        // ScrollViewer measures its content with unbounded width. Give the page a real
        // viewport width so star columns and cards cannot drift or clip when maximized.
        var available = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : e.NewSize.Width;
        if (available <= 0) return;
        var width = Math.Min(content.MaxWidth, Math.Max(0, available - 4));
        if (double.IsNaN(content.Width) || Math.Abs(content.Width - width) > 0.5)
            content.Width = width;
        if (scroll.Visibility == Visibility.Visible)
        {
            PageHeader.Width = width;
            AccountsPage.Width = width;
        }
    }

    private void State_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Render);
    private void State_RefreshFinished(object? sender, string message) =>
        DispatcherQueue.TryEnqueue(() => StatusText.Text = message);

    public void Render()
    {
        var mode = _state.Settings.Config.OpenAI.UsageDisplayMode;
        var warning = _state.Settings.Config.OpenAI.WarningThresholdPercent;
        var danger = _state.Settings.Config.OpenAI.DangerThresholdPercent;
        var snapshot = _state.Registry.Accounts.ToArray();
        var ids = snapshot.Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
        for (var i = Accounts.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(Accounts[i].Account.AccountId)) continue;
            _accountViews.Remove(Accounts[i].Account.AccountId);
            Accounts.RemoveAt(i);
        }
        foreach (var account in snapshot)
        {
            if (_accountViews.TryGetValue(account.AccountId, out var view))
                view.Update(account, mode, account.AccountId == _state.Registry.ActiveAccountId, warning, danger);
            else
            {
                view = new AccountViewModel(account, mode, account.AccountId == _state.Registry.ActiveAccountId, warning, danger);
                _accountViews.Add(account.AccountId, view);
                Accounts.Add(view);
            }
        }
        var active = _state.ActiveAccount;
        ActiveAccountText.Text = active is null ? "尚未添加账号" : AccountUsageHelpers.DisplayName(active);
        ActiveUsageText.Text = active is null ? "添加账号后显示额度" : AccountUsageHelpers.UsageText(active, mode);
        ActiveResetText.Text = active is null ? string.Empty : AccountUsageHelpers.ResetText(active);
        var colors = UsageColors.For(active is null ? AccountHealthStatus.Unknown : AccountUsageHelpers.Health(active, warning, danger));
        ActiveUsageText.Foreground = colors.Accent;
        ActiveUsageCard.Background = colors.Surface;
        ActiveUsageCard.BorderBrush = colors.Border;
        AccountCountText.Text = snapshot.Length.ToString();
        KeepAwakeOverviewButton.Content = _state.KeepAwakeEnabled ? "关闭保持唤醒" : "开启保持唤醒";
        KeepAwakeOverviewButton.Style = _state.KeepAwakeEnabled
            ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        KeepAwakeOverviewButton.CornerRadius = new CornerRadius(9);
        _updatingFeatureToggles = true;
        ImageStudioOverviewToggle.IsOn = _state.Settings.Config.ImageStudioEnabled;
        VectorStudioOverviewToggle.IsOn = _state.Settings.Config.VectorStudioEnabled;
        QualityCheckOverviewToggle.IsOn = _state.Settings.Config.QualityCheckEnabled;
        _updatingFeatureToggles = false;
        SyncSettingsControls();
    }

    private void SyncSettingsControls()
    {
        _updatingSettings = true;
        try
        {
            KeepAwakeToggle.IsOn = _state.KeepAwakeEnabled;
            var delay = _state.Settings.Config.AwayModeDelaySeconds;
            SelectByTag(AwayDelayCombo, delay is 0 or 5 or 15 or 30 or 60 ? delay.ToString() : "custom");
            AwayCustomDelayBox.Value = delay;
            AwayCustomDelayBox.Visibility = delay is 0 or 5 or 15 or 30 or 60 ? Visibility.Collapsed : Visibility.Visible;
            SelectByTag(UsageModeCombo, _state.Settings.Config.OpenAI.UsageDisplayMode.ToString());
            SelectByTag(AccountModeCombo, _state.Settings.Config.OpenAI.AccountUsageMode.ToString());
            SyncExtendedSettingsControls();
        }
        finally { _updatingSettings = false; }
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == tag)
            {
                if (!ReferenceEquals(box.SelectedItem, item)) box.SelectedItem = item;
                return;
            }
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        if (page is "studio" or "quality" or "vector")
        {
            var app = (App)Application.Current;
            if (page == "studio") _ = app.OpenImageStudioAsync();
            else if (page == "vector") _ = app.OpenVectorStudioAsync();
            else _ = app.OpenQualityCheckAsync();
            Navigation.SelectedItem = _lastContentNavigationItem ?? Navigation.MenuItems[0];
            return;
        }
        if (args.SelectedItem is NavigationViewItem selected) _lastContentNavigationItem = selected;
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        SessionsPage.Visibility = page == "sessions" ? Visibility.Visible : Visibility.Collapsed;
        var inSettings = page?.StartsWith("settings", StringComparison.Ordinal) == true;
        SettingsPage.Visibility = inSettings ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "accounts" => "账号", "activity" => "Token 活动", "sessions" => "会话分析", _ when inSettings => "设置", _ => "WinCodexBar" };
        PageSubtitle.Text = page switch
        {
            "accounts" => "切换、刷新和管理你的 OpenAI 账号。",
            "activity" => "查看每日 Token 使用、连续活跃和费用估算。",
            "sessions" => "查看会话数量、模型分布和最近的本地会话。",
            _ when inSettings => "调整用量、托盘和系统集成。",
            _ => "你的 Codex 账号与额度，一眼看清。"
        };
        if (page == "activity") _ = LoadActivityAsync();
        if (page == "sessions") _ = LoadSessionsAsync();
        if (inSettings) ScheduleSettingsNavigation(page!);
    }

    private void ScheduleSettingsNavigation(string page)
    {
        _settingsNavigationTimer?.Stop();
        _settingsNavigationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _settingsNavigationTimer.Tick += (_, _) =>
        {
            _settingsNavigationTimer?.Stop();
            ScrollToSettingsSection(page);
        };
        _settingsNavigationTimer.Start();
    }

    private void ScrollToSettingsSection(string page)
    {
        FrameworkElement target = page switch
        {
            "settings:refresh" => RefreshSettingsHeading,
            "settings:pricing" => PricingSettingsHeading,
            "settings:models" => ModelSettingsHeading,
            "settings:appearance" => AppearanceSettingsHeading,
            "settings:system" => SystemSettingsHeading,
            "settings:shortcuts" => ShortcutSettingsHeading,
            _ => BehaviorSettingsHeading
        };
        if (SettingsPage.Content is not UIElement content || target.ActualHeight <= 0) return;
        var top = target.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point()).Y;
        SettingsPage.ChangeView(null, Math.Max(0, top - 8), null, true);
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void OpenAccountsClicked(object sender, RoutedEventArgs e) => Navigation.SelectedItem = Navigation.MenuItems[1];

    private void ImageStudioClicked(object sender, RoutedEventArgs e) =>
        _ = ((App)Microsoft.UI.Xaml.Application.Current).RequestToggleImageStudioAsync();

    private void QualityCheckClicked(object sender, RoutedEventArgs e) =>
        _ = ((App)Microsoft.UI.Xaml.Application.Current).RequestToggleQualityCheckAsync();

    private void ImageStudioOverviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingFeatureToggles)
            ((App)Microsoft.UI.Xaml.Application.Current).SetImageStudioEnabled(ImageStudioOverviewToggle.IsOn);
    }

    private void QualityCheckOverviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingFeatureToggles)
            ((App)Microsoft.UI.Xaml.Application.Current).SetQualityCheckEnabled(QualityCheckOverviewToggle.IsOn);
    }

    private void VectorStudioOverviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingFeatureToggles)
            ((App)Microsoft.UI.Xaml.Application.Current).SetVectorStudioEnabled(VectorStudioOverviewToggle.IsOn);
    }

    public async Task<bool> ConfirmEnableFeatureAsync(string name, string description)
    {
        ShowDashboard();
        return await new ContentDialog
        {
            Title = $"开启{name}？",
            Content = description,
            PrimaryButtonText = "开启并打开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        }.ShowAsync() == ContentDialogResult.Primary;
    }

    public void ShowDashboard()
    {
        AppWindow.Show();
        Activate();
    }

    public void ShowHome()
    {
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowDashboard();
    }

    public void ShowAccounts()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowDashboard();
    }

    public void ShowImport()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowDashboard();
        ImportClicked(this, new RoutedEventArgs());
    }

    public void ShowExport()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowDashboard();
        ExportClicked(this, new RoutedEventArgs());
    }

    public void ShowSettings()
    {
        SettingsNavigationItem.IsExpanded = true;
        Navigation.SelectedItem = SettingsNavigationItem;
        ShowDashboard();
    }

    public void ShowSettingsSection(string section)
    {
        var settingsItem = SettingsNavigationItem;
        settingsItem.IsExpanded = true;
        var item = settingsItem.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => candidate.Tag?.ToString() == "settings:" + section);
        if (item is null) { ShowSettings(); return; }
        Navigation.SelectedItem = item;
        ShowDashboard();
    }

    public void ShowUpdateDialog()
    {
        ShowSettings();
        CheckUpdateClicked(this, new RoutedEventArgs());
    }

    public void ShowTokenActivity()
    {
        Navigation.SelectedItem = Navigation.MenuItems[2];
        ShowDashboard();
    }

    public void ShowSessionAnalysis()
    {
        Navigation.SelectedItem = Navigation.MenuItems[3];
        ShowDashboard();
    }

    public void ShowAddAccount()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowDashboard();
        AddAccountClicked(this, new RoutedEventArgs());
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _awayMode.Dispose();
        Close();
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        var page = (Navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        if (page == "activity") { await LoadActivityAsync(force: true); return; }
        if (page == "sessions") { await LoadSessionsAsync(force: true); return; }
        StatusText.Text = "正在刷新账号用量…";
        await _state.RefreshNowAsync();
    }

    private async Task LoadActivityAsync(bool force = false)
    {
        if (_activityScanning || !force && DateTimeOffset.UtcNow - _activityScannedAt < TimeSpan.FromSeconds(30)) return;
        _activityScanning = true;
        StatusText.Text = "正在读取 Token 活动…";
        try
        {
            var (activity, direct) = await Task.Run(() =>
                (TokenUsageScanService.ScanActivity(), AppUsageService.Load()));
            var dashboard = TokenActivityDashboard.Build(activity, direct, DateTime.Now);
            var unit = _state.Settings.Config.OpenAI.TokenUnitDisplayMode;
            TodayActivityText.Text = AccountUsageHelpers.FormatTokenCount(dashboard.TodayTokens, unit);
            WeekActivityText.Text = AccountUsageHelpers.FormatTokenCount(dashboard.WeekTokens, unit);
            MonthActivityText.Text = AccountUsageHelpers.FormatTokenCount(dashboard.MonthTokens, unit);
            TotalActivityText.Text = AccountUsageHelpers.FormatTokenCount(dashboard.TotalTokens, unit);
            ActivityStreakText.Text = $"Codex 连续活跃 {activity.CurrentStreakDays} 天 · 最长 {activity.LongestStreakDays} 天 · 单日峰值 {AccountUsageHelpers.FormatTokenCount(activity.PeakDailyTokens, unit)} · 今日高峰时段 {dashboard.PeakHour}";
            var pricing = _state.Settings.Config.OpenAI.GetOrCreateTokenPricePreset();
            var cost = pricing.EstimateUsd(activity.TotalCostTokens);
            ActivityCostText.Text = $"Codex 会话按 {_state.Settings.Config.OpenAI.TokenPricingModel} 的配置估算：US$ {cost:0.00}。生图、SVG、检测的模型计费不同，暂不混入这项估算。";
            DirectUsageText.Text = $"直连任务 {dashboard.DirectTaskCount} 次 · 上游提供 Token 用量 {dashboard.ReportedTaskCount} 次 · 未提供 {dashboard.DirectTaskCount - dashboard.ReportedTaskCount} 次。仅把上游报告的 Token 计入合计。";
            static TokenDayRow[] Rows(IEnumerable<ActivityChartPoint> points, CodexBarWin.Models.TokenUnitDisplayMode unit)
            {
                var array = points.ToArray();
                var peak = Math.Max(1, array.Max(item => item.Tokens));
                return array.Select(item => new TokenDayRow(item.Label,
                    AccountUsageHelpers.FormatTokenCount(item.Tokens, unit), 100d * item.Tokens / peak)).ToArray();
            }
            ActivityDaysList.ItemsSource = Rows(dashboard.Daily.Reverse(), unit);
            ActivityWeeksList.ItemsSource = Rows(dashboard.Weekly.Reverse(), unit);
            ActivityMonthsList.ItemsSource = Rows(dashboard.Monthly.Reverse(), unit);
            ActivityHoursList.ItemsSource = Rows(dashboard.Hourly, unit);
            ActivitySourcesList.ItemsSource = dashboard.Sources.Select(item => new TokenBreakdownRow(item.Name,
                item.Name == "Codex 会话" ? "本地会话" : $"{item.ReportedCount}/{item.TaskCount} 次有用量",
                AccountUsageHelpers.FormatTokenCount(item.Tokens, unit))).ToArray();
            ActivityModelsList.ItemsSource = dashboard.Models.Select(item => new TokenBreakdownRow(item.Name,
                $"{item.ReportedCount}/{item.TaskCount} 次有用量",
                AccountUsageHelpers.FormatTokenCount(item.Tokens, unit))).ToArray();
            _activityScannedAt = DateTimeOffset.UtcNow;
            StatusText.Text = "Token 活动已更新";
        }
        catch (Exception ex) { StatusText.Text = "Token 活动加载失败：" + ex.Message; }
        finally { _activityScanning = false; }
    }

    private async Task LoadSessionsAsync(bool force = false)
    {
        if (_sessionScanning || !force && DateTimeOffset.UtcNow - _sessionScannedAt < TimeSpan.FromSeconds(30)) return;
        _sessionScanning = true;
        StatusText.Text = "正在读取会话分析…";
        try
        {
            var analysis = await Task.Run(TokenUsageScanService.ScanSessions);
            var unit = _state.Settings.Config.OpenAI.TokenUnitDisplayMode;
            SessionCountValue.Text = analysis.SessionCount.ToString();
            SessionSplitValue.Text = $"{analysis.ActiveSessionCount} / {analysis.ArchivedSessionCount}";
            SessionAverageValue.Text = AccountUsageHelpers.FormatTokenCount(analysis.AverageTokensPerSession, unit);
            SessionLongestValue.Text = FormatDuration(analysis.LongestSessionDuration);
            ModelUsageList.ItemsSource = analysis.ModelTokens
                .Select(model => new ModelUsageRow(model.Model, model.SessionCount + " 会话", AccountUsageHelpers.FormatTokenCount(model.Tokens, unit))).ToArray();
            RecentSessionList.ItemsSource = analysis.RecentSessions
                .Select(session => new RecentSessionRow(session.Model,
                    session.StartedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "时间未知",
                    FormatDuration(session.Duration), AccountUsageHelpers.FormatTokenCount(session.TotalTokens, unit))).ToArray();
            _sessionScannedAt = DateTimeOffset.UtcNow;
            StatusText.Text = "会话分析已更新";
        }
        catch (Exception ex) { StatusText.Text = "会话分析加载失败：" + ex.Message; }
        finally { _sessionScanning = false; }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? $"{(int)value.TotalHours} 时 {value.Minutes} 分" : $"{Math.Max(0, (int)value.TotalMinutes)} 分";

    private void AccountSwitchClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountViewModel vm) return;
        _state.Activate(vm.Account);
        StatusText.Text = "已切换当前账号。已运行的 Codex 通常需要重启后才会读取新登录态。";
    }

    private async void AccountRefreshClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountViewModel vm) return;
        StatusText.Text = $"正在刷新 {vm.Name}…";
        await _state.RefreshNowAsync(vm.Account);
    }

    private async void AccountDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountViewModel vm) return;
        await ConfirmDeleteAsync(vm.Account);
    }

    private async void AccountDetailsClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccountViewModel vm)
            await ShowAccountDetailsAsync(vm.Account);
    }

    public async Task ShowAccountDetailsAsync(TokenAccount account)
    {
        ShowAccounts();
        var summary = new TextBlock
        {
            Text = AccountUsageHelpers.DetailsText(account, _state.Settings.Config.OpenAI.UsageDisplayMode),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var scroll = new ScrollViewer
        {
            Content = summary,
            MaxHeight = 460,
            MinWidth = 300,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        await new ContentDialog
        {
            Title = "账号额度与重置卡",
            Content = scroll,
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    public async Task ConfirmDeleteAsync(TokenAccount account)
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ShowDashboard();
        // 对话框始终属于此窗口的 XamlRoot。若从托盘面板触发，应先关闭面板再打开工作台。
        var choice = await new ContentDialog
        {
            Title = "删除账号？",
            Content = $"将从 WinCodexBar 移除 {AccountUsageHelpers.DisplayName(account)}。此操作不会删除 Codex 的会话记录。",
            PrimaryButtonText = "删除账号",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
        if (choice != ContentDialogResult.Primary) return;
        _state.Delete(account);
        StatusText.Text = "账号已删除";
    }

    private async void AddAccountClicked(object sender, RoutedEventArgs e)
    {
        var callbackBox = new TextBox { Header = "登录回调链接", PlaceholderText = "浏览器返回后会自动填入，也可以粘贴回调链接" };
        var instructions = new TextBlock
        {
            Text = "浏览器将打开 OpenAI 登录页。完成登录后回到这里继续。",
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(instructions);
        var flow = _state.Login.StartFlow();
        var authUrl = new TextBox { Header = "OpenAI 官方授权链接", Text = flow.AuthUrl, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var copyUrl = new Button { Content = "复制授权链接", HorizontalAlignment = HorizontalAlignment.Left };
        copyUrl.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(flow.AuthUrl);
            Clipboard.SetContent(data);
            instructions.Text = "授权链接已复制。可在其他设备浏览器打开；完成后将回调链接粘贴到下方。";
        };
        content.Children.Add(authUrl);
        content.Children.Add(copyUrl);
        content.Children.Add(callbackBox);
        using var server = _state.Login.CreateCallbackServer(url => DispatcherQueue.TryEnqueue(() => callbackBox.Text = url));
        try { server.Start(); }
        catch (Exception ex) { instructions.Text = $"本地回调未启动：{ex.Message}。登录后请粘贴回调链接。"; }
        Process.Start(new ProcessStartInfo(flow.AuthUrl) { UseShellExecute = true });
        var dialog = new ContentDialog
        {
            Title = "添加 OpenAI 账号",
            Content = content,
            PrimaryButtonText = "完成登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _state.Login.CancelFlow(flow.FlowId);
            return;
        }
        try
        {
            var account = await _state.Login.CompleteFlowAsync(flow.FlowId, callbackBox.Text);
            _state.Add(account);
            StatusText.Text = "账号已添加";
        }
        catch (Exception ex)
        {
            _state.Login.CancelFlow(flow.FlowId);
            await ShowMessageAsync("添加账号失败", ex.Message);
        }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var parsed = OpenAIAccountCSVService.Parse(await File.ReadAllTextAsync(file.Path));
            _state.Registry.MergeImportedAccounts(parsed.Accounts, parsed.InteropContext);
            if (!string.IsNullOrWhiteSpace(parsed.ActiveAccountId))
                _state.Registry.SetActive(parsed.ActiveAccountId);
            _state.Registry.Save();
            _state.NotifyChanged();
            StatusText.Text = $"已导入 {parsed.RowCount} 个账号";
            ((App)Application.Current).Notify("账号导入完成", $"已导入 {parsed.RowCount} 个账号。", "accounts");
        }
        catch (Exception ex) { await ShowMessageAsync("导入失败", ex.Message); }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_state.Registry.Accounts.Count == 0) { await ShowMessageAsync("导出账号", "当前没有账号。"); return; }
        var format = new ComboBox { Header = "导出格式", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        format.Items.Add("codexbar JSON（推荐，保留完整账号信息）");
        format.Items.Add("Codex2API 扁平 JSON");
        format.Items.Add("codexbar CSV（旧版兼容）");
        var exportOptions = new StackPanel { Spacing = 12 };
        exportOptions.Children.Add(format);
        exportOptions.Children.Add(new TextBlock
        {
            Text = "导出文件包含可直接使用账号的凭据。请只保存在可信位置，不要公开分享或上传到公开仓库。",
            TextWrapping = TextWrapping.Wrap
        });
        var approved = await new ContentDialog
        {
            Title = "导出账号文件", Content = exportOptions,
            PrimaryButtonText = "选择保存位置", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot
        }.ShowAsync();
        if (approved != ContentDialogResult.Primary) return;
        var picker = new FileSavePicker { SuggestedFileName = "openai_accounts_export" };
        var selectedFormat = format.SelectedIndex;
        picker.FileTypeChoices.Add(selectedFormat == 2 ? "CSV" : "JSON",
            new List<string> { selectedFormat == 2 ? ".csv" : ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var text = selectedFormat switch
            {
                1 => OpenAIAccountCSVService.ExportFlatJSON(_state.Registry.Accounts),
                2 => OpenAIAccountCSVService.ExportLegacyCSV(_state.Registry.Accounts, _state.Registry.ActiveAccountId),
                _ => OpenAIAccountCSVService.ExportInteropBundle(
                    _state.Registry.Accounts, _state.Registry.MetadataByAccountId,
                    _state.Registry.ProxiesJSON, _state.Registry.ActiveAccountId)
            };
            await File.WriteAllTextAsync(file.Path, text);
            StatusText.Text = "账号已导出";
            ((App)Application.Current).Notify("账号导出完成", "JSON/CSV 文件包含账号凭据，请妥善保存，勿公开分享。", "accounts");
        }
        catch (Exception ex) { await ShowMessageAsync("导出失败", ex.Message); }
    }

    private void KeepAwakeClicked(object sender, RoutedEventArgs e) => _state.SetKeepAwake(!_state.KeepAwakeEnabled);
    private void KeepAwakeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.SetKeepAwake(KeepAwakeToggle.IsOn);
    }
    private void AwayDelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings) return;
        var tag = (AwayDelayCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (tag == "custom")
        {
            AwayCustomDelayBox.Visibility = Visibility.Visible;
            if (_state.Settings.Config.AwayModeDelaySeconds is 0 or 5 or 15 or 30 or 60)
                _state.SetAwayDelay(45);
            AwayCustomDelayBox.Value = _state.Settings.Config.AwayModeDelaySeconds;
        }
        else if (int.TryParse(tag, out var seconds))
        {
            AwayCustomDelayBox.Visibility = Visibility.Collapsed;
            _state.SetAwayDelay(seconds);
        }
    }
    private void AwayCustomDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_updatingSettings || sender.Visibility != Visibility.Visible || !double.IsFinite(sender.Value)) return;
        _state.SetAwayDelay(Math.Clamp((int)Math.Round(sender.Value), 1, 3600));
    }
    private void UsageModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSettings && Enum.TryParse<UsageDisplayMode>((UsageModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var mode))
            _state.SetUsageMode(mode);
    }
    private void AccountModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSettings && Enum.TryParse<AccountUsageMode>((AccountModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var mode))
            _state.SetAccountMode(mode);
    }
    private void AwayModeClicked(object sender, RoutedEventArgs e) => StartAwayMode();
    public void StartAwayMode() => _awayMode.Start(_state.Settings.Config.AwayModeDelaySeconds);

    private async void CreateShortcutsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ShortcutService.CreateShortcuts();
            await ShowMessageAsync("快捷方式", "桌面和开始菜单快捷方式已创建。");
        }
        catch (Exception ex) { await ShowMessageAsync("创建失败", ex.Message); }
    }

    private async Task ShowMessageAsync(string title, string message) =>
        await new ContentDialog { Title = title, Content = message, CloseButtonText = "知道了", XamlRoot = Content.XamlRoot }.ShowAsync();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
