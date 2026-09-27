using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Drawing.Imaging;

namespace CodexBarWin.WinUI;

public sealed partial class MainWindow
{
    private string? _previewStyle;
    private bool _checkingUpdate;

    private void InitializeSettingsOptions()
    {
        var models = CodexModelCatalog.Models();
        DefaultModelCombo.ItemsSource = models;
        ReviewModelCombo.ItemsSource = models;
        PricingModelCombo.ItemsSource = _state.Settings.Config.OpenAI.TokenPricePresets.Keys.OrderBy(x => x).ToArray();
        ReasoningCombo.ItemsSource = CodexModelCatalog.Efforts(_state.Settings.Config.Global.DefaultModel);
        ServiceTierCombo.ItemsSource = CodexModelCatalog.Tiers(_state.Settings.Config.Global.DefaultModel);
    }

    private void SyncExtendedSettingsControls()
    {
        var config = _state.Settings.Config;
        var priceModels = config.OpenAI.TokenPricePresets.Keys.OrderBy(x => x).ToArray();
        if (!PricingModelCombo.Items.OfType<string>().SequenceEqual(priceModels)) PricingModelCombo.ItemsSource = priceModels;
        AutoPricingToggle.IsOn = config.OpenAI.AutoSyncOfficialPricing;
        PricingIntervalBox.Value = config.OpenAI.OfficialPricingSyncIntervalHours;
        PricingSyncStatusText.Text = _state.PricingSyncStatus;
        AutoRefreshToggle.IsOn = config.OpenAI.AutoRefreshEnabled;
        RefreshIntervalBox.Value = config.OpenAI.AutoRefreshIntervalSeconds;
        SelectByTag(TokenUnitCombo, config.OpenAI.TokenUnitDisplayMode.ToString());
        WarningThresholdBox.Value = config.OpenAI.WarningThresholdPercent;
        DangerThresholdBox.Value = config.OpenAI.DangerThresholdPercent;
        PricingModelCombo.SelectedItem = config.OpenAI.TokenPricingModel;
        var preset = config.OpenAI.GetOrCreateTokenPricePreset();
        PricingSourceText.Text = preset.Source switch
        {
            "custom" => "价格来源：手动设置（自动同步会保留）",
            "official" => "价格来源：OpenAI 官方 · " + (preset.SyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "已同步"),
            _ => "价格来源：内置估算"
        };
        InputPriceBox.Value = preset.InputUsdPerMillion;
        CachedPriceBox.Value = preset.CachedInputUsdPerMillion;
        OutputPriceBox.Value = preset.OutputUsdPerMillion;
        UsdCnyBox.Value = config.OpenAI.UsdToCnyRate;
        DefaultModelCombo.SelectedItem = config.Global.DefaultModel;
        ReviewModelCombo.SelectedItem = config.Global.ReviewModel;
        ReasoningCombo.SelectedItem = config.Global.ReasoningEffort;
        ServiceTierCombo.SelectedItem = config.Global.ServiceTier;
        SelectByTag(TrayStyleCombo, config.TrayIconStyle);
        SelectByTag(UiScaleCombo, config.UiScalePercent.ToString());
        KeepAwakeToggle.IsOn = _state.KeepAwakeEnabled;
        AdvancedAwakeToggle.IsOn = config.AdvancedKeepAwakeEnabled;
        AwakeIdleBox.Value = config.AdvancedKeepAwakeIdleThresholdMs / 1000d;
        AwakeIntervalBox.Value = config.AdvancedKeepAwakeIntervalMs / 1000d;
        AwakeJitterBox.Value = config.AdvancedKeepAwakeJitterMs / 1000d;
        PauseOnFullscreenToggle.IsOn = config.AdvancedKeepAwakePauseOnFullscreen;
        SelectByTag(AwakePatternCombo, config.AdvancedKeepAwakeMovePattern);
        StartupToggle.IsOn = config.StartWithWindows;
        SystemNotificationsToggle.IsOn = config.SystemNotificationsEnabled;
        SystemNotificationStatusText.Text = ((App)Application.Current).NotificationStatus;
        AutoUpdateToggle.IsOn = config.AutoCheckUpdates;
        SilentUpdateToggle.IsOn = config.SilentUpdates;
        InstalledVersionText.Text = "当前版本：" + AppUpdateService.DisplayVersion;
        UpdateTrayStylePreview(config.TrayIconStyle);
    }

    private void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        _state.UpdateSettings(config => config.OpenAI.AutoRefreshEnabled = AutoRefreshToggle.IsOn);
    }

    private void RefreshIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSettings || !double.IsFinite(sender.Value)) return;
        _state.UpdateSettings(config => config.OpenAI.AutoRefreshIntervalSeconds = Math.Clamp((int)sender.Value, 60, 7200));
    }

    private void TokenUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || !Enum.TryParse<TokenUnitDisplayMode>((TokenUnitCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var unit)) return;
        _state.UpdateSettings(config => config.OpenAI.TokenUnitDisplayMode = unit);
        _activityScannedAt = DateTimeOffset.MinValue;
        _sessionScannedAt = DateTimeOffset.MinValue;
    }

    private void WarningThresholdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveThresholds();
    private void DangerThresholdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveThresholds();
    private void SaveThresholds()
    {
        if (_updatingSettings || !double.IsFinite(WarningThresholdBox.Value) || !double.IsFinite(DangerThresholdBox.Value)) return;
        _state.UpdateSettings(config =>
        {
            config.OpenAI.WarningThresholdPercent = Math.Clamp(WarningThresholdBox.Value, 1, 99);
            config.OpenAI.DangerThresholdPercent = Math.Clamp(DangerThresholdBox.Value, config.OpenAI.WarningThresholdPercent, 100);
        });
    }

    private void PricingModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || PricingModelCombo.SelectedItem is not string model) return;
        _state.UpdateSettings(config => config.OpenAI.TokenPricingModel = model);
        _activityScannedAt = DateTimeOffset.MinValue;
    }

    private void PriceBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSettings || !double.IsFinite(InputPriceBox.Value) || !double.IsFinite(CachedPriceBox.Value) || !double.IsFinite(OutputPriceBox.Value)) return;
        _state.UpdateSettings(config =>
        {
            var preset = config.OpenAI.GetOrCreateTokenPricePreset();
            preset.InputUsdPerMillion = Math.Clamp(InputPriceBox.Value, 0, 10000);
            preset.CachedInputUsdPerMillion = Math.Clamp(CachedPriceBox.Value, 0, 10000);
            preset.OutputUsdPerMillion = Math.Clamp(OutputPriceBox.Value, 0, 10000);
            preset.Source = "custom";
            preset.SyncedAt = null;
        });
        _activityScannedAt = DateTimeOffset.MinValue;
    }

    private void AutoPricingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        _state.UpdateSettings(config => config.OpenAI.AutoSyncOfficialPricing = AutoPricingToggle.IsOn);
        if (AutoPricingToggle.IsOn) _ = _state.SyncOfficialPricingIfDueAsync();
    }

    private void PricingIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSettings || !double.IsFinite(sender.Value)) return;
        _state.UpdateSettings(config => config.OpenAI.OfficialPricingSyncIntervalHours = Math.Clamp((int)sender.Value, 1, 168));
    }

    private async void SyncOfficialPricingClicked(object sender, RoutedEventArgs e) =>
        await _state.SyncOfficialPricingIfDueAsync(force: true);

    private void UsdCnyBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSettings || !double.IsFinite(sender.Value)) return;
        _state.UpdateSettings(config => config.OpenAI.UsdToCnyRate = Math.Clamp(sender.Value, 0.1, 50));
    }

    private void ModelSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || DefaultModelCombo.SelectedItem is not string defaultModel) return;
        if (ReferenceEquals(sender, DefaultModelCombo))
        {
            _updatingSettings = true;
            try
            {
                ReasoningCombo.ItemsSource = CodexModelCatalog.Efforts(defaultModel);
                ServiceTierCombo.ItemsSource = CodexModelCatalog.Tiers(defaultModel);
                ReasoningCombo.SelectedItem = _state.Settings.Config.Global.ReasoningEffort;
                ServiceTierCombo.SelectedItem = _state.Settings.Config.Global.ServiceTier;
            }
            finally { _updatingSettings = false; }
        }
        _state.UpdateSettings(config =>
        {
            config.Global.DefaultModel = defaultModel;
            config.Global.ReviewModel = ReviewModelCombo.SelectedItem as string ?? defaultModel;
            config.Global.ReasoningEffort = ReasoningCombo.SelectedItem as string ?? "medium";
            config.Global.ServiceTier = ServiceTierCombo.SelectedItem as string ?? "standard";
        }, syncCodex: true);
    }

    private void TrayStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || (TrayStyleCombo.SelectedItem as ComboBoxItem)?.Tag is not string style) return;
        _state.SetTrayStyle(style);
        UpdateTrayStylePreview(style);
    }

    private void UpdateTrayStylePreview(string style)
    {
        var selected = TrayIconRenderer.StyleOptions.FirstOrDefault(option => option.Id == style);
        TrayStyleDescription.Text = selected.Description ?? "";
        if (_previewStyle == style) return;
        try
        {
            var previewPath = Path.Combine(Path.GetTempPath(), $"WinCodexBar-tray-preview-{style}.png");
            using (var bitmap = TrayIconRenderer.RenderPreview(style))
                bitmap.Save(previewPath, ImageFormat.Png);
            TrayStylePreview.Source = new BitmapImage(new Uri(previewPath));
            _previewStyle = style;
        }
        catch { TrayStylePreview.Source = null; }
    }

    private void UiScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || !int.TryParse((UiScaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var scale)) return;
        _state.UpdateSettings(config => config.UiScalePercent = scale);
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        try { _state.SetStartup(StartupToggle.IsOn); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.UpdateSettings(config => config.AutoCheckUpdates = AutoUpdateToggle.IsOn);
    }

    private void SystemNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.UpdateSettings(config => config.SystemNotificationsEnabled = SystemNotificationsToggle.IsOn);
    }

    private void SilentUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.UpdateSettings(config => config.SilentUpdates = SilentUpdateToggle.IsOn);
    }

    private void AdvancedAwakeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.SetAdvancedKeepAwake(AdvancedAwakeToggle.IsOn);
    }

    private void AdvancedAwakeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSettings || !double.IsFinite(AwakeIdleBox.Value) || !double.IsFinite(AwakeIntervalBox.Value) || !double.IsFinite(AwakeJitterBox.Value)) return;
        _state.UpdateSettings(config =>
        {
            config.AdvancedKeepAwakeIdleThresholdMs = Math.Clamp((int)AwakeIdleBox.Value, 5, 3600) * 1000;
            config.AdvancedKeepAwakeIntervalMs = Math.Clamp((int)AwakeIntervalBox.Value, 1, 600) * 1000;
            config.AdvancedKeepAwakeJitterMs = Math.Clamp((int)AwakeJitterBox.Value, 0, 120) * 1000;
        });
    }

    private void PauseOnFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingSettings) _state.UpdateSettings(config => config.AdvancedKeepAwakePauseOnFullscreen = PauseOnFullscreenToggle.IsOn);
    }

    private void AwakePatternCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || (AwakePatternCombo.SelectedItem as ComboBoxItem)?.Tag is not string pattern) return;
        _state.UpdateSettings(config => config.AdvancedKeepAwakeMovePattern = pattern);
    }

    private void OpenConfigClicked(object sender, RoutedEventArgs e)
    {
        CodexPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo(CodexPaths.CodexBarRoot) { UseShellExecute = true });
    }

    private async void CheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        StatusText.Text = "正在检查正式版更新…";
        try
        {
            var updater = new AppUpdateService();
            var update = await updater.CheckAsync();
            if (update is null) { await ShowMessageAsync("检查更新", $"当前版本 {AppUpdateService.DisplayVersion} 已是最新正式版。"); return; }
            var choice = await new ContentDialog
            {
                Title = $"发现新版本 {update.Version}",
                Content = "将下载并校验安装包，备份账号和设置，再覆盖程序并重启。",
                PrimaryButtonText = "更新并重启",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
            if (choice != ContentDialogResult.Primary) return;
            StatusText.Text = "正在下载并校验更新包…";
            var staged = await updater.DownloadAndVerifyAsync(update);
            updater.StartInstaller(staged);
            ((App)Application.Current).ExitApplication();
        }
        catch (Exception ex) { await ShowMessageAsync("更新未完成", ex.Message); }
        finally { _checkingUpdate = false; }
    }
}
