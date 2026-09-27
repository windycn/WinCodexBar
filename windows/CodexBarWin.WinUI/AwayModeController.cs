using System.Runtime.InteropServices;

namespace CodexBarWin.WinUI;

/// <summary>Shows a black desktop overlay without changing the Windows power state.</summary>
public sealed class AwayModeController : IDisposable
{
    private readonly BlackScreenOverlay _overlay = new();
    private CancellationTokenSource? _pending;
    private bool _active;

    public event EventHandler<int>? CountdownStarted;
    public event EventHandler? BlackScreenStarted;
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
            // Let the tray button's final mouse-up finish before arming input detection.
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(350), token);
            if (!GetLastInputInfo(out var before))
                throw new InvalidOperationException("无法读取输入状态，已取消黑屏。");

            _active = true;
            BlackScreenStarted?.Invoke(this, EventArgs.Empty);
            _overlay.Show();

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(75, token);
                if (!GetLastInputInfo(out var current))
                    throw new InvalidOperationException("无法读取输入状态，已退出黑屏。");
                if (current.Time == before.Time) continue;
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
        _overlay.Hide();
        Woke?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Stop();
        _overlay.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    private static bool GetLastInputInfo(out LastInputInfo info)
    {
        info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfoNative(ref info);
    }

    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    private static extern bool GetLastInputInfoNative(ref LastInputInfo info);
}
