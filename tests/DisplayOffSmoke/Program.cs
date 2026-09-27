using CodexBarWin.WinUI;
using System.Runtime.InteropServices;

if (args.Length != 1 || args[0] != "--display")
{
    Console.WriteLine("Run with --display to perform one real native display-off and wake cycle.");
    return 0;
}

var blanker = new NativeIdleDisplayOff();
if (blanker.RecoveryError is { } recoveryError)
    throw new InvalidOperationException(recoveryError);
var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
if (!Native.GetLastInputInfo(ref input)) throw new InvalidOperationException("GetLastInputInfo failed");
Console.WriteLine("Starting a single native display-off cycle. The test will restore the display timeout and turn the display on.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
var ran = await blanker.BlankAsync(input.Time, timeout.Token);
if (!ran) throw new InvalidOperationException("Display-off mutex was busy");
Native.SendMessageTimeoutW(new nint(0xFFFF), 0x112, 0xF170, new nint(-1), 2, 2000, out _);
Console.WriteLine("Native display-off cycle finished and wake command sent.");
return 0;

[StructLayout(LayoutKind.Sequential)]
struct LastInputInfo { public uint Size; public uint Time; }

static partial class Native
{
    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeoutMilliseconds, out nuint result);
}
