using CodexBarWin.Models;
using CodexBarWin.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;

namespace CodexBarWin.WinUI;

public sealed partial class QuickWindow : Window
{
    private readonly AppState _state;
    private readonly MainWindow _main;
    private bool _tokenScanStarted;
    private TokenActivityDashboard? _tokenDashboard;
    private DateTimeOffset _tokenScanAt;
    private int _manualRefreshes;
    private bool _confirmingDelete;
    private readonly CodexRadarService _radar = new();
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedModeBrush =
        new(Windows.UI.Color.FromArgb(255, 0, 103, 192));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush IdleModeBrush =
        new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedTextBrush =
        new(Windows.UI.Color.FromArgb(255, 255, 255, 255));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush IdleTextBrush =
        new(Windows.UI.Color.FromArgb(255, 45, 57, 72));
    public DateTimeOffset LastHiddenAt { get; private set; } = DateTimeOffset.MinValue;

    public QuickWindow(AppState state, MainWindow main)
    {
        _state = state;
        _main = main;
        InitializeComponent();
        var corners = 2;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, ref corners, sizeof(int));
        AccountList.ItemsSource = main.Accounts;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }
        if (Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_QUICK") != "1")
            Activated += (_, args) =>
            {
                if (!_confirmingDelete && args.WindowActivationState == WindowActivationState.Deactivated)
                    Hide();
            };
        _state.Changed += (_, _) => DispatcherQueue.TryEnqueue(Render);
        Render();
    }

    public void Render()
    {
        var active = _state.ActiveAccount;
        var accounts = _state.Registry.Accounts;
        var displayMode = _state.Settings.Config.OpenAI.UsageDisplayMode;
        var aggregate = _state.Settings.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway;
        CurrentAccountText.Text = active is null ? "尚未添加账号" : $"当前账号 · {AccountUsageHelpers.DisplayName(active)}";
        CurrentUsageText.Text = active is null ? "添加账号后显示额度" :
            aggregate ? AccountUsageHelpers.AverageText(accounts, displayMode) : AccountUsageHelpers.UsageText(active, displayMode);
        var usageColors = UsageColors.For(active is null ? AccountHealthStatus.Unknown :
            AccountUsageHelpers.Health(active, _state.Settings.Config.OpenAI.WarningThresholdPercent,
                _state.Settings.Config.OpenAI.DangerThresholdPercent));
        CurrentUsageText.Foreground = usageColors.Accent;
        HeroCard.BorderBrush = usageColors.Border;
        HeroCard.Background = usageColors.Surface;
        CurrentPlanText.Text = active is null ? "订阅" : AccountUsageHelpers.PlanLabel(active) + " 订阅";
        CurrentResetText.Text = active is null ? "登录后可查看额度窗口和重置时间" :
            string.Join(" · ", AccountUsageHelpers.Windows(active).Select(w =>
                $"{w.Label} 重置 {w.ResetAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未提供"}"));
        CreditDetailsButton.Content = active is null ? "重置卡 · 暂无账号" :
            AccountUsageHelpers.CreditSummary(active) + " · 查看详情";
        CreditDetailsButton.IsEnabled = active is not null;
        AccountCountText.Text = $"{accounts.Count} 个";
        var statuses = accounts.Select(a => AccountUsageHelpers.Health(
            a, _state.Settings.Config.OpenAI.WarningThresholdPercent,
            _state.Settings.Config.OpenAI.DangerThresholdPercent)).ToArray();
        AccountHealthText.Text = $"健康 {statuses.Count(s => s == AccountHealthStatus.Healthy)} · 警戒 {statuses.Count(s => s == AccountHealthStatus.Warning)} · 高负载 {statuses.Count(s => s == AccountHealthStatus.Exhausted)}";
        EmptyState.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IReadOnlyList<AccountUsageWindow> windows = active is null ? [] : AccountUsageHelpers.Windows(active);
        UpdateQuotaBar(windows.FirstOrDefault(w => w.Label == "5h"), displayMode,
            PrimaryFillColumn, PrimaryRestColumn, PrimaryProgressFill);
        UpdateQuotaBar(windows.FirstOrDefault(w => w.Label == "7d"), displayMode,
            SecondaryFillColumn, SecondaryRestColumn, SecondaryProgressFill);
        var unit = _state.Settings.Config.OpenAI.TokenUnitDisplayMode;
        TodayTokensText.Text = _tokenDashboard is null ? "读取中…" : AccountUsageHelpers.FormatTokenCount(_tokenDashboard.TodayTokens, unit);
        WeekTokensText.Text = _tokenDashboard is null ? "读取中…" : AccountUsageHelpers.FormatTokenCount(_tokenDashboard.WeekTokens, unit);
        MonthTokensText.Text = _tokenDashboard is null ? "读取中…" : AccountUsageHelpers.FormatTokenCount(_tokenDashboard.MonthTokens, unit);
        KeepAwakeLabel.Text = _state.KeepAwakeEnabled ? "关闭唤醒" : "开启唤醒";
        KeepAwakeButton.Style = _state.KeepAwakeEnabled
            ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        KeepAwakeButton.CornerRadius = new CornerRadius(9);
        ImageStudioQuickButton.Content = ((App)Microsoft.UI.Xaml.Application.Current).ImageStudioVisible ? "关闭生图窗口" : "打开生图工作台";
        ManualModeButton.Background = aggregate ? IdleModeBrush : SelectedModeBrush;
        ManualModeButton.Foreground = aggregate ? IdleTextBrush : SelectedTextBrush;
        ManualModeLabel.Foreground = aggregate ? IdleTextBrush : SelectedTextBrush;
        AggregateModeButton.Background = aggregate ? SelectedModeBrush : IdleModeBrush;
        AggregateModeButton.Foreground = aggregate ? SelectedTextBrush : IdleTextBrush;
        AggregateModeLabel.Foreground = aggregate ? SelectedTextBrush : IdleTextBrush;
        Delay0Button.Style = _state.Settings.Config.AwayModeDelaySeconds == 0 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        Delay5Button.Style = _state.Settings.Config.AwayModeDelaySeconds == 5 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        Delay15Button.Style = _state.Settings.Config.AwayModeDelaySeconds == 15 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        Delay30Button.Style = _state.Settings.Config.AwayModeDelaySeconds == 30 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        Delay60Button.Style = _state.Settings.Config.AwayModeDelaySeconds == 60 ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        var custom = _state.Settings.Config.AwayModeDelaySeconds is not (0 or 5 or 15 or 30 or 60);
        CustomDelayButton.Style = custom ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] : null;
        CustomDelayButton.Content = custom ? $"{_state.Settings.Config.AwayModeDelaySeconds} 秒 · 自定" : "自定义…";
    }

    private void UpdateQuotaBar(AccountUsageWindow? window, UsageDisplayMode mode,
        ColumnDefinition fill, ColumnDefinition rest, Border foreground)
    {
        var amount = window is null ? 0 : mode == UsageDisplayMode.Remaining
            ? 100 - window.UsedPercent : window.UsedPercent;
        amount = Math.Clamp(amount, 0, 100);
        fill.Width = new GridLength(amount, GridUnitType.Star);
        rest.Width = new GridLength(100 - amount, GridUnitType.Star);
        foreground.Background = window is null ? UsageColors.Neutral : UsageColors.ForQuota(window.UsedPercent,
            _state.Settings.Config.OpenAI.WarningThresholdPercent, _state.Settings.Config.OpenAI.DangerThresholdPercent);
    }

    public async void RefreshLocalTokenSummary()
    {
        if (_tokenScanStarted || DateTimeOffset.UtcNow - _tokenScanAt < TimeSpan.FromSeconds(30)) return;
        _tokenScanStarted = true;
        try
        {
            _tokenDashboard = await Task.Run(() => TokenActivityDashboard.Build(
                TokenUsageScanService.ScanActivity(), AppUsageService.Load(), DateTime.Now));
            _tokenScanAt = DateTimeOffset.UtcNow;
            Render();
        }
        catch
        {
            TodayTokensText.Text = "—";
            WeekTokensText.Text = "—";
            MonthTokensText.Text = "—";
        }
        finally { _tokenScanStarted = false; }
    }

    public async void RefreshRadar()
    {
        RadarText.Text = _radar.LastPrediction.DisplayText;
        var prediction = await _radar.GetCurrentAsync();
        RadarText.Text = prediction.DisplayText;
        ToolTipService.SetToolTip(RadarButton, prediction.DisplayText + " · 点击打开 Codex Radar");
    }

    private void RadarClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://codexradar.com/") { UseShellExecute = true }); }
        catch { }
    }

    public void Hide()
    {
        if (AppWindow.IsVisible) LastHiddenAt = DateTimeOffset.UtcNow;
        AppWindow.Hide();
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Hide();
    private void DashboardClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowDashboard(); }
    private void SettingsClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowSettings(); }
    private void DetailsClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowSessionAnalysis(); }
    private void ImageStudioClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _ = ((App)Microsoft.UI.Xaml.Application.Current).RequestToggleImageStudioAsync();
    }
    private async void CreditDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_state.ActiveAccount is not { } account) return;
        Hide();
        await _main.ShowAccountDetailsAsync(account);
    }
    private void AddClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowAddAccount(); }
    private void HomeClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowHome(); }
    private void ImportClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowImport(); }
    private void ExportClicked(object sender, RoutedEventArgs e) { Hide(); _main.ShowExport(); }
    private void ExitClicked(object sender, RoutedEventArgs e) =>
        ((App)Microsoft.UI.Xaml.Application.Current).ExitApplication();
    private void SwitchClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountViewModel vm) return;
        _state.Activate(vm.Account);
    }
    private void ManualModeClicked(object sender, RoutedEventArgs e) => _state.SetAccountMode(AccountUsageMode.Switch);
    private void AggregateModeClicked(object sender, RoutedEventArgs e) => _state.SetAccountMode(AccountUsageMode.AggregateGateway);
    private async void AccountRefreshClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccountViewModel vm)
            await RunWithRefreshAnimationAsync(() => _state.RefreshNowAsync(vm.Account));
    }
    private async void AccountDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountViewModel vm) return;
        _confirmingDelete = true;
        try
        {
            var choice = await new ContentDialog
            {
                Title = "删除账号？",
                Content = $"将从 WinCodexBar 移除 {AccountUsageHelpers.DisplayName(vm.Account)}。Codex 会话记录仍保留。",
                PrimaryButtonText = "删除账号",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (Content as FrameworkElement)?.XamlRoot
            }.ShowAsync();
            if (choice == ContentDialogResult.Primary) _state.Delete(vm.Account);
        }
        finally { _confirmingDelete = false; }
    }
    private void KeepAwakeClicked(object sender, RoutedEventArgs e) => _state.SetKeepAwake(!_state.KeepAwakeEnabled);
    private void AwayClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        _main.StartAwayMode();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) =>
        await RunWithRefreshAnimationAsync(() => _state.RefreshNowAsync());
    private async Task RunWithRefreshAnimationAsync(Func<Task> refresh)
    {
        if (_manualRefreshes++ == 0)
        {
            var visual = ElementCompositionPreview.GetElementVisual(RefreshGlyph);
            visual.CenterPoint = new Vector3((float)RefreshGlyph.ActualWidth / 2,
                (float)RefreshGlyph.ActualHeight / 2, 0);
            var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
            rotation.InsertKeyFrame(0, 0);
            rotation.InsertKeyFrame(1, 360);
            rotation.Duration = TimeSpan.FromMilliseconds(850);
            rotation.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngleInDegrees", rotation);
            RefreshButton.IsEnabled = false;
        }
        try { await refresh(); }
        finally
        {
            if (--_manualRefreshes == 0)
            {
                var visual = ElementCompositionPreview.GetElementVisual(RefreshGlyph);
                visual.StopAnimation("RotationAngleInDegrees");
                visual.RotationAngleInDegrees = 0;
                RefreshButton.IsEnabled = true;
            }
        }
    }
    private void Delay0Clicked(object sender, RoutedEventArgs e) => _state.SetAwayDelay(0);
    private void Delay5Clicked(object sender, RoutedEventArgs e) => _state.SetAwayDelay(5);
    private void Delay15Clicked(object sender, RoutedEventArgs e) => _state.SetAwayDelay(15);
    private void Delay30Clicked(object sender, RoutedEventArgs e) => _state.SetAwayDelay(30);
    private void Delay60Clicked(object sender, RoutedEventArgs e) => _state.SetAwayDelay(60);
    private async void CustomDelayClicked(object sender, RoutedEventArgs e)
    {
        var input = new NumberBox
        {
            Header = "等待秒数（1—3600）",
            Minimum = 1,
            Maximum = 3600,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = _state.Settings.Config.AwayModeDelaySeconds
        };
        var result = await new ContentDialog
        {
            Title = "自定义黑屏前等待",
            Content = input,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
        if (result == ContentDialogResult.Primary && double.IsFinite(input.Value))
            _state.SetAwayDelay(Math.Clamp((int)Math.Round(input.Value), 1, 3600));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
