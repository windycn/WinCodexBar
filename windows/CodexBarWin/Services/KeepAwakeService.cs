using CodexBarWin.Models;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexBarWin.Services;

public sealed class KeepAwakeService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const int InputMouse = 0;
    private const uint MouseEventMove = 0x0001;
    private const int MonitorIntervalMs = 500;

    private readonly CodexBarConfigStore _store;
    private readonly object _gate = new();
    private System.Threading.Timer? _advancedTimer;
    private DateTimeOffset _lastSyntheticInput = DateTimeOffset.MinValue;
    private bool _moveRight = true;

    public KeepAwakeService(CodexBarConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool IsEnabled { get; private set; }

    public bool IsAdvancedEnabled { get; private set; }

    public void Load()
    {
        IsEnabled = _store.Config.KeepAwakeEnabled;
        IsAdvancedEnabled = _store.Config.AdvancedKeepAwakeEnabled;
        Apply();
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        Apply();
        Save();
    }

    public void SetAdvancedEnabled(bool enabled)
    {
        IsAdvancedEnabled = enabled;
        Apply();
        Save();
    }

    public void ClearForProcessExit()
    {
        StopAdvancedTimer();
        ClearExecutionState();
    }

    private void Apply()
    {
        if (IsEnabled || IsAdvancedEnabled)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        }
        else
        {
            ClearExecutionState();
        }

        if (IsAdvancedEnabled)
        {
            StartAdvancedTimer();
        }
        else
        {
            StopAdvancedTimer();
        }
    }

    private void StartAdvancedTimer()
    {
        lock (_gate)
        {
            _advancedTimer ??= new System.Threading.Timer(_ => AdvancedTick(), null, MonitorIntervalMs, MonitorIntervalMs);
        }
    }

    private void StopAdvancedTimer()
    {
        lock (_gate)
        {
            _advancedTimer?.Dispose();
            _advancedTimer = null;
            _lastSyntheticInput = DateTimeOffset.MinValue;
        }
    }

    private void AdvancedTick()
    {
        try
        {
            var settings = AdvancedSettings.From(_store.Config);
            if (!IsAdvancedEnabled || (settings.PauseOnFullscreen && IsFullscreenForeground()))
            {
                return;
            }

            if (GetIdleMilliseconds() < settings.IdleThresholdMs)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var interval = settings.IntervalMs + (settings.JitterMs <= 0 ? 0 : Random.Shared.Next(0, settings.JitterMs + 1));
            if (_lastSyntheticInput != DateTimeOffset.MinValue
                && (now - _lastSyntheticInput).TotalMilliseconds < interval)
            {
                return;
            }

            DoAdvancedMove(settings.MovePattern);
            _lastSyntheticInput = now;
        }
        catch
        {
            // Advanced keep-awake is best-effort. Failures should never interrupt the tray app.
        }
    }

    private void Save()
    {
        _store.Update(config =>
        {
            config.KeepAwakeEnabled = IsEnabled;
            config.AdvancedKeepAwakeEnabled = IsAdvancedEnabled;
        });
    }

    private void DoAdvancedMove(string pattern)
    {
        switch (pattern)
        {
            case "micro_jitter":
            {
                var dx = Random.Shared.Next(-2, 3);
                var dy = Random.Shared.Next(-2, 3);
                if (dx == 0 && dy == 0)
                {
                    dx = 1;
                }

                SendRelativeMouseMove(dx, dy);
                Thread.Sleep(30);
                SendRelativeMouseMove(-dx, -dy);
                break;
            }

            case "random_walk_box":
            {
                var origin = GetCursorPosition();
                for (var i = 0; i < 4; i++)
                {
                    SendRelativeMouseMove(Random.Shared.Next(-3, 4), Random.Shared.Next(-3, 4));
                    Thread.Sleep(20);
                }

                var now = GetCursorPosition();
                SendRelativeMouseMove(origin.X - now.X, origin.Y - now.Y);
                break;
            }

            default:
            {
                var delta = _moveRight ? 1 : -1;
                _moveRight = !_moveRight;
                SendRelativeMouseMove(delta, 0);
                Thread.Sleep(50);
                SendRelativeMouseMove(-delta, 0);
                break;
            }
        }
    }

    private static void ClearExecutionState()
    {
        SetThreadExecutionState(EsContinuous);
    }

    private static int GetIdleMilliseconds()
    {
        var info = new LastInputInfo
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        return unchecked(Environment.TickCount - (int)info.DwTime);
    }

    private static bool IsFullscreenForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        return rect.Left <= 0
               && rect.Top <= 0
               && rect.Right >= GetSystemMetrics(0)
               && rect.Bottom >= GetSystemMetrics(1);
    }

    private static CursorPoint GetCursorPosition()
    {
        return GetCursorPos(out var point) ? point : default;
    }

    private static void SendRelativeMouseMove(int dx, int dy)
    {
        var input = new Input
        {
            Type = InputMouse,
            MouseInput = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = 0,
                DwFlags = MouseEventMove,
                Time = 0,
                DwExtraInfo = IntPtr.Zero,
            },
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public MouseInput MouseInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private readonly record struct AdvancedSettings(
        int IdleThresholdMs,
        int IntervalMs,
        int JitterMs,
        string MovePattern,
        bool PauseOnFullscreen)
    {
        public static AdvancedSettings From(CodexBarConfig config)
        {
            return new AdvancedSettings(
                Math.Clamp(config.AdvancedKeepAwakeIdleThresholdMs, 5_000, 3_600_000),
                Math.Clamp(config.AdvancedKeepAwakeIntervalMs, 1_000, 600_000),
                Math.Clamp(config.AdvancedKeepAwakeJitterMs, 0, 120_000),
                NormalizePattern(config.AdvancedKeepAwakeMovePattern),
                config.AdvancedKeepAwakePauseOnFullscreen);
        }

        private static string NormalizePattern(string? pattern)
        {
            return (pattern ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "micro_jitter" => "micro_jitter",
                "random_walk_box" => "random_walk_box",
                _ => "ping_pong",
            };
        }
    }
}
