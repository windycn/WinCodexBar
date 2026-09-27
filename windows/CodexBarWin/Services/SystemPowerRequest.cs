using System.Runtime.InteropServices;

namespace CodexBarWin.Services;

/// <summary>A process-scoped power request; unlike SetThreadExecutionState it survives async thread hops.</summary>
internal sealed class SystemPowerRequest : IDisposable
{
    private const int SystemRequired = 0;
    private const int DisplayRequired = 1;
    private readonly nint _handle;
    private bool _system;
    private bool _display;
    private bool _disposed;

    public SystemPowerRequest()
    {
        var reason = Marshal.StringToHGlobalUni("WinCodexBar 正在保持后台任务运行");
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
            _handle = PowerCreateRequest(ref context);
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    public void Set(bool system, bool display)
    {
        if (_disposed || _handle == 0 || _handle == new nint(-1)) return;
        Update(SystemRequired, system, ref _system);
        Update(DisplayRequired, display, ref _display);
    }

    private void Update(int type, bool requested, ref bool current)
    {
        if (requested == current) return;
        if (requested ? PowerSetRequest(_handle, type) : PowerClearRequest(_handle, type))
            current = requested;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Set(false, false);
        _disposed = true;
        if (_handle != 0 && _handle != new nint(-1)) CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint handle, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint handle, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
