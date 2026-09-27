using System.Runtime.InteropServices;

namespace CodexBarWin.WinUI;

/// <summary>One opaque topmost window spanning the full virtual desktop.</summary>
internal sealed class BlackScreenOverlay : IDisposable
{
    private const string WindowClass = "WinCodexBarBlackScreenOverlay";
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;
    private const int SwShow = 5;
    private const int BlackBrush = 4;
    private const uint WmSetCursor = 0x0020;
    private const uint WmDisplayChange = 0x007E;
    private static readonly WindowProc Proc = WindowProcedure;
    private static bool _registered;
    private nint _window;
    private uint _threadId;

    public void Show()
    {
        if (_window != 0) return;
        Register();
        var bounds = VirtualDesktop();
        _threadId = GetCurrentThreadId();
        _window = CreateWindowExW(WsExTopmost | WsExToolWindow,
            WindowClass, string.Empty, WsPopup,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            0, 0, GetModuleHandleW(null), 0);
        if (_window == 0)
            throw new InvalidOperationException($"无法创建黑屏窗口（Windows 错误 {Marshal.GetLastWin32Error()}）。");
        ShowWindow(_window, SwShow);
        SetForegroundWindow(_window);
        if (!SetWindowPos(_window, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SwpShowWindow | SwpNoActivate))
        {
            Hide();
            throw new InvalidOperationException($"无法覆盖整个桌面（Windows 错误 {Marshal.GetLastWin32Error()}）。");
        }
    }

    public void Hide()
    {
        var window = _window;
        _window = 0;
        if (window == 0) return;
        if (_threadId == GetCurrentThreadId()) DestroyWindow(window);
        else PostMessageW(window, 0x0010, 0, 0); // WM_CLOSE on the owning UI thread.
    }

    public void Dispose() => Hide();

    private static void Register()
    {
        if (_registered) return;
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(Proc),
            Instance = GetModuleHandleW(null),
            Background = GetStockObject(BlackBrush),
            ClassName = WindowClass
        };
        if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException($"无法注册黑屏窗口（Windows 错误 {Marshal.GetLastWin32Error()}）。");
        _registered = true;
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmSetCursor)
        {
            SetCursor(0);
            return 1;
        }
        if (message == WmDisplayChange)
        {
            var bounds = VirtualDesktop();
            SetWindowPos(window, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SwpNoActivate);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static (int X, int Y, int Width, int Height) VirtualDesktop() =>
        (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint IconSmall;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string? moduleName);
    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectType);
}
