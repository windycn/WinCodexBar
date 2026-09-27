using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.WinUI;

/// <summary>WinUI 窗口和托盘共享的数据入口。窗口只订阅状态，不执行网络请求。</summary>
public sealed class AppState : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly UsageRefreshCoordinator _refresh;
    private readonly CodexSyncService _sync;
    private readonly OpenAIAccountGatewayService _gateway;
    private readonly KeepAwakeService _keepAwake;
    private readonly OfficialPricingService _officialPricing = new();
    private readonly SemaphoreSlim _pricingGate = new(1, 1);
    private readonly QuotaAlertTracker _quotaAlerts = new();
    private readonly CodexRadarService _radar = new();
    private readonly CodexRadarAlertTracker _radarAlerts = new();
    private DateTimeOffset _lastPricingAttempt;
    private Task? _timerTask;
    private Task? _radarTask;

    public AccountRegistry Registry { get; } = new();
    public CodexBarConfigStore Settings { get; } = new();
    public OpenAIOAuthLoginService Login { get; } = new();
    public event EventHandler? Changed;
    public event EventHandler<string>? RefreshFinished;
    public event Action<string, string, string>? UserActionCompleted;
    public AppUpdate? AvailableUpdate { get; private set; }
    public string PricingSyncStatus { get; private set; } = "尚未同步官方价格";

    public AppState()
    {
        Settings.Load();
        Registry.Load();
        _sync = new CodexSyncService(Settings);
        var oauthRefresh = new OpenAIOAuthRefreshService();
        _refresh = new UsageRefreshCoordinator(new OpenAIUsageService(), oauthRefresh, Registry);
        _gateway = new OpenAIAccountGatewayService(Registry, Settings, oauthRefresh);
        _keepAwake = new KeepAwakeService(Settings);
        _keepAwake.Load();
        ApplyGatewayMode();
    }

    public TokenAccount? ActiveAccount => Registry.Accounts.FirstOrDefault(a => a.AccountId == Registry.ActiveAccountId);
    public bool KeepAwakeEnabled => _keepAwake.IsEnabled || _keepAwake.IsAdvancedEnabled || _keepAwake.IsBlackScreenActive;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void ReportBackgroundError(string message) => RefreshFinished?.Invoke(this, message);

    public void SetAvailableUpdate(AppUpdate? update)
    {
        AvailableUpdate = update;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        if (_timerTask is not null) return;
        _timerTask = PollAsync(_shutdown.Token);
        _radarTask = MonitorRadarAsync(_shutdown.Token);
        _ = RefreshDueAsync(includeInactive: true);
        _ = SyncOfficialPricingIfDueAsync();
    }

    public async Task RefreshNowAsync(TokenAccount? account = null)
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            var snapshot = account is null ? Registry.Accounts.ToArray() : [account];
            var activeBefore = ActiveAccount?.LastChecked;
            var report = await _refresh.RefreshAsync(snapshot, maxParallel: 3, _shutdown.Token);
            Changed?.Invoke(this, EventArgs.Empty);
            RefreshFinished?.Invoke(this, $"已更新 {report.Updated} 个账号" +
                (report.Failed > 0 ? $"，{report.Failed} 个账号暂时无法刷新" : string.Empty));
            NotifyLowQuotaIfFresh(snapshot, activeBefore);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task RefreshDueAsync(bool includeInactive = false)
    {
        if (!Settings.Config.OpenAI.AutoRefreshEnabled) return;
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var activeInterval = TimeSpan.FromSeconds(Math.Clamp(Settings.Config.OpenAI.AutoRefreshIntervalSeconds, 60, 86400));
            var accounts = Registry.Accounts.Where(account =>
            {
                var isActive = account.AccountId == Registry.ActiveAccountId;
                if (!isActive && !includeInactive) return false;
                var interval = isActive ? activeInterval : TimeSpan.FromMinutes(5);
                return !account.LastChecked.HasValue || now - account.LastChecked.Value >= interval;
            }).ToArray();
            if (accounts.Length == 0) return;
            var activeBefore = ActiveAccount?.LastChecked;
            var report = await _refresh.RefreshAsync(accounts, maxParallel: 3, _shutdown.Token);
            if (report.AnyChanged) Changed?.Invoke(this, EventArgs.Empty);
            NotifyLowQuotaIfFresh(accounts, activeBefore);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void NotifyLowQuotaIfFresh(IReadOnlyList<TokenAccount> refreshed, DateTimeOffset? activeBefore)
    {
        var active = ActiveAccount;
        if (active is null || !refreshed.Any(account => account.AccountId == active.AccountId) ||
            active.LastChecked is null || active.LastChecked == activeBefore) return;
        var config = Settings.Config.OpenAI;
        foreach (var alert in _quotaAlerts.Evaluate(active, config.WarningThresholdPercent, config.DangerThresholdPercent))
        {
            var window = alert.WindowLabel == "5h" ? "5 小时" : alert.WindowLabel == "7d" ? "7 天" : alert.WindowLabel;
            var reset = alert.ResetAt is { } time ? $"预计 {time.ToLocalTime():MM-dd HH:mm} 重置。" : "重置时间暂未提供。";
            UserActionCompleted?.Invoke(alert.Critical ? "额度接近耗尽" : "额度偏低",
                $"{AccountUsageHelpers.DisplayName(active)} 的 {window} 额度剩余 {alert.RemainingPercent:0.#}%。{reset}", "accounts");
        }
    }

    public void Activate(TokenAccount account)
    {
        if (Registry.ActiveAccountId == account.AccountId) return;
        Registry.SetActive(account.AccountId);
        Registry.Save();
        _sync.SyncForCurrentMode(account);
        Changed?.Invoke(this, EventArgs.Empty);
        UserActionCompleted?.Invoke("账号已切换 · 请重启 Codex", $"当前账号：{AccountUsageHelpers.DisplayName(account)}。请重启 Codex，切换后的账号才会应用。", "accounts");
        _ = RefreshNowAsync(account);
    }

    public bool Delete(TokenAccount account)
    {
        if (!Registry.RemoveAccount(account.AccountId)) return false;
        Registry.Save();
        if (ActiveAccount is { } active) _sync.SyncForCurrentMode(active);
        Changed?.Invoke(this, EventArgs.Empty);
        UserActionCompleted?.Invoke("账号已删除", $"已移除 {AccountUsageHelpers.DisplayName(account)}。", "accounts");
        return true;
    }

    public void Add(TokenAccount account)
    {
        Registry.UpsertAccount(account, activate: true);
        Registry.Save();
        _sync.SyncForCurrentMode(account);
        Changed?.Invoke(this, EventArgs.Empty);
        UserActionCompleted?.Invoke("账号已添加", $"当前账号：{AccountUsageHelpers.DisplayName(account)}。", "accounts");
        _ = RefreshNowAsync(account);
    }

    public void SetKeepAwake(bool enabled)
    {
        var wasEnabled = KeepAwakeEnabled;
        _keepAwake.SetEnabled(enabled);
        Changed?.Invoke(this, EventArgs.Empty);
        if (wasEnabled != KeepAwakeEnabled)
            UserActionCompleted?.Invoke(KeepAwakeEnabled ? "保持唤醒已开启" : "保持唤醒已关闭",
                KeepAwakeEnabled ? "电脑将按当前设置保持唤醒。" : "电脑将恢复 Windows 的正常休眠行为。", "settings");
    }

    public void SetAwayDelay(int seconds)
    {
        if (seconds is < 0 or > 3600) return;
        Settings.Update(config => config.AwayModeDelaySeconds = seconds);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetBlackScreenActive(bool active)
    {
        _keepAwake.SetBlackScreenActive(active);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public bool IsDisplayHoldActive => _keepAwake.IsDisplayHoldActive;

    public void SetTrayStyle(string style)
    {
        if (!TrayIconRenderer.IsSupported(style)) return;
        Settings.Update(config => config.TrayIconStyle = style);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetUsageMode(UsageDisplayMode mode)
    {
        Settings.Update(config => config.OpenAI.UsageDisplayMode = mode);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSettings(Action<CodexBarConfig> update, bool syncCodex = false)
    {
        Settings.Update(update);
        if (syncCodex && ActiveAccount is { } active)
        {
            try { _sync.SyncForCurrentMode(active); }
            catch (Exception ex) { RefreshFinished?.Invoke(this, "设置已保存，但 Codex 同步失败：" + ex.Message); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SyncOfficialPricingIfDueAsync(bool force = false)
    {
        var config = Settings.Config.OpenAI;
        if (!force && !config.AutoSyncOfficialPricing) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastPricingAttempt < TimeSpan.FromHours(1) ||
            config.OfficialPricingLastSuccessAt is { } success &&
            now - success < TimeSpan.FromHours(config.OfficialPricingSyncIntervalHours))) return;
        if (!await _pricingGate.WaitAsync(0)) return;
        try
        {
            _lastPricingAttempt = DateTimeOffset.UtcNow;
            PricingSyncStatus = "正在读取 OpenAI 官方价格…";
            Changed?.Invoke(this, EventArgs.Empty);
            var prices = await _officialPricing.FetchAsync(_shutdown.Token);
            var applied = 0;
            Settings.Update(settings =>
            {
                foreach (var (model, price) in prices)
                {
                    if (settings.OpenAI.TokenPricePresets.TryGetValue(model, out var existing) && existing.Source == "custom")
                        continue;
                    settings.OpenAI.TokenPricePresets[model] = price.Clone();
                    applied++;
                }
                settings.OpenAI.OfficialPricingLastSuccessAt = DateTimeOffset.UtcNow;
            });
            PricingSyncStatus = $"{DateTimeOffset.Now:MM-dd HH:mm} 已同步 {applied} 个模型 · 手动价格优先";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PricingSyncStatus = "官方价格同步失败：" + ex.Message;
        }
        finally
        {
            _pricingGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetAdvancedKeepAwake(bool enabled)
    {
        _keepAwake.SetAdvancedEnabled(enabled);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetStartup(bool enabled)
    {
        if (!StartupService.SetEnabled(enabled))
            throw new IOException("无法修改 Windows 开机启动设置。");
        UpdateSettings(config => config.StartWithWindows = enabled);
    }

    public void SetAccountMode(AccountUsageMode mode)
    {
        if (Settings.Config.OpenAI.AccountUsageMode == mode) return;
        Settings.Update(config => config.OpenAI.AccountUsageMode = mode);
        ApplyGatewayMode();
        if (ActiveAccount is { } active) _sync.SyncForCurrentMode(active);
        Changed?.Invoke(this, EventArgs.Empty);
        UserActionCompleted?.Invoke("账号模式已切换",
            mode == AccountUsageMode.Switch ? "已切换为手动模式。" : "已切换为聚合模式。", "settings");
    }

    private void ApplyGatewayMode()
    {
        if (Settings.Config.OpenAI.AccountUsageMode == AccountUsageMode.AggregateGateway)
            _gateway.EnsureStarted();
        else
            _gateway.Stop();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshDueAsync(includeInactive: true);
                await SyncOfficialPricingIfDueAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task MonitorRadarAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(3));
        try
        {
            do
            {
                if (!Settings.Config.SystemNotificationsEnabled) continue;
                var prediction = await _radar.GetCurrentAsync(cancellationToken: cancellationToken);
                if (_radarAlerts.Observe(prediction))
                {
                    var openedAt = prediction.WindowOpenedAt is { } time
                        ? $" · {time.ToLocalTime():M月d日 HH:mm} 开启" : string.Empty;
                    UserActionCompleted?.Invoke("Codex 速蹬窗口已开启",
                        $"Codex 雷达检测到速蹬窗口已开启{openedAt}。点击查看工作台。", "home");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _gateway.Dispose();
        _officialPricing.Dispose();
        _keepAwake.ClearForProcessExit();
        _shutdown.Dispose();
        _refreshGate.Dispose();
        _pricingGate.Dispose();
    }
}
