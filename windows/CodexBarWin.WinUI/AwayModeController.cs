using System.Runtime.InteropServices;

namespace CodexBarWin.WinUI;

/// <summary>Turns displays off through Windows' idle path while leaving applications running.</summary>
public sealed class AwayModeController : IDisposable
{
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMonitorPower = 0xF170;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly nint HwndBroadcast = new(0xFFFF);
    private readonly NativeIdleDisplayOff _display = new();
    private CancellationTokenSource? _pending;
    private Task<bool>? _blankTask;
    private bool _active;

    public event EventHandler<int>? CountdownStarted;
    public event EventHandler? DisplayOffStarted;
    public event EventHandler? Woke;
    public event EventHandler<string>? Failed;
    public bool IsActive => _active;

    public void Start(int delaySeconds)
    {
        Stop();
        delaySeconds = Math.Clamp(delaySeconds, 0, 3600);
        _pending = new CancellationTokenSource();
        CountdownStarted?.Invoke(this, delaySeconds);
        _ = RunAsync(delaySeconds, _pending.Token);
    }

    private async Task RunAsync(int delaySeconds, CancellationToken token)
    {
        try
        {
            // Let the triggering mouse click finish before measuring idle time.
            var end = DateTimeOffset.UtcNow.AddSeconds(delaySeconds).AddMilliseconds(150);
            while (DateTimeOffset.UtcNow < end)
            {
                token.ThrowIfCancellationRequested();
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) return;
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
            if (_display.RecoveryError is { } recoveryError)
                throw new InvalidOperationException("显示器设置恢复失败：" + recoveryError);
            if (!GetLastInputInfo(out var before))
                throw new InvalidOperationException("无法读取输入状态，已取消关屏。");
            _active = true;
            DisplayOffStarted?.Invoke(this, EventArgs.Empty);
            _blankTask = _display.BlankAsync(before.Time, token);
            if (!await _blankTask) { Stop(); return; }
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(150, token);
                if (!GetLastInputInfo(out var now) || now.Time == before.Time) continue;
                Stop();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Stop();
            Failed?.Invoke(this, error.Message);
        }
    }

    public void Stop()
    {
        var pending = _pending;
        _pending = null;
        pending?.Cancel();
        pending?.Dispose();
        if (!_active) return;
        _active = false;
        Woke?.Invoke(this, EventArgs.Empty);
        var blankTask = _blankTask;
        _blankTask = null;
        _ = Task.Run(async () =>
        {
            if (blankTask is not null)
            {
                try { await blankTask; }
                catch { /* The restore path reports its own error. */ }
            }
            // Only an ON request. SC_MONITORPOWER=2 (OFF) is never used.
            SendMessageTimeoutW(HwndBroadcast, WmSysCommand, ScMonitorPower, new nint(-1),
                SmtoAbortIfHung, 2000, out _);
        });
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    private static bool GetLastInputInfo(out LastInputInfo info)
    {
        info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfoNative(ref info);
    }

    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    private static extern bool GetLastInputInfoNative(ref LastInputInfo info);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeoutMilliseconds, out nuint result);
}
