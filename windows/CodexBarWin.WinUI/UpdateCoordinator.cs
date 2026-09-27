using CodexBarWin.Services;

namespace CodexBarWin.WinUI;

/// <summary>Checks releases in the background and installs a verified package only while UI is idle.</summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private readonly AppState _state;
    private readonly MainWindow _main;
    private readonly TrayController _tray;
    private readonly Action _exit;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public UpdateCoordinator(AppState state, MainWindow main, TrayController tray, Action exit)
    {
        _state = state;
        _main = main;
        _tray = tray;
        _exit = exit;
    }

    public void Start() => _loop ??= RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_state.Settings.Config.AutoCheckUpdates)
                    await CheckOnceAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromHours(6), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updater = new AppUpdateService();
            var update = await updater.CheckAsync(cancellationToken);
            _state.SetAvailableUpdate(update);
            if (update is null || !_state.Settings.Config.SilentUpdates) return;

            var staged = await updater.DownloadAndVerifyAsync(update, cancellationToken);
            // Wait for both windows to be closed so the background update never interrupts a dialog.
            while (_main.AppWindow.IsVisible || _tray.IsQuickVisible)
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

            if (!_state.Settings.Config.AutoCheckUpdates || !_state.Settings.Config.SilentUpdates) return;
            updater.StartInstaller(staged, backgroundRestart: true);
            _exit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The current installation remains in place; a later check can retry.
            _state.ReportBackgroundError("更新暂未完成：" + ex.Message);
        }
    }

    public void Dispose() => _stop.Cancel();
}
