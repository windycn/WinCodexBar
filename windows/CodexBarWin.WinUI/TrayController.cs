using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using CodexBarWin.Models;
using Windows.Graphics;
using WinUIEx;

namespace CodexBarWin.WinUI;

public sealed class TrayController : IDisposable
{
    private readonly AppState _state;
    private readonly MainWindow _main;
    private readonly TrayIcon _icon;
    private QuickWindow? _quick;
    private CancellationTokenSource? _singleClick;
    private readonly string _generatedIconDirectory = Path.Combine(Path.GetTempPath(), $"WinCodexBar-tray-{Environment.ProcessId}");
    private string? _lastIconKey;

    public TrayController(AppState state, MainWindow main)
    {
        _state = state;
        _main = main;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico");
        _icon = new TrayIcon(1, iconPath, "WinCodexBar");
        _icon.Selected += (_, _) => ScheduleQuickWindow();
        _icon.LeftDoubleClick += (_, _) =>
        {
            CancelSingleClick();
            _quick?.Hide();
            _main.ShowDashboard();
        };
        _icon.ContextMenu += (_, e) => e.Flyout = CreateMenu();
        _state.Changed += StateChanged;
        UpdateTrayIcon();
        _icon.IsVisible = true;
    }

    private void StateChanged(object? sender, EventArgs e) => _main.DispatcherQueue.TryEnqueue(UpdateTrayIcon);

    private void UpdateTrayIcon()
    {
        var active = _state.ActiveAccount;
        var style = _state.Settings.Config.TrayIconStyle;
        IReadOnlyList<TokenAccount> source = active is null ? _state.Registry.Accounts : [active];
        var primary = source.SelectMany(AccountUsageHelpers.Windows).Where(w => w.Label == "5h").Select(w => (double?)w.UsedPercent).ToArray();
        var secondary = source.SelectMany(AccountUsageHelpers.Windows).Where(w => w.Label == "7d").Select(w => (double?)w.UsedPercent).ToArray();
        var first = primary.Length == 0 ? null : primary.Average();
        var second = secondary.Length == 0 ? null : secondary.Average();
        var key = TrayIconRenderer.CacheKey(style, first, second, active is not null);
        if (_lastIconKey != key)
        {
            _lastIconKey = key;
            _icon.SetIcon(ResolveIconPath(style, key, first, second, active is not null));
        }
        if (active is null)
        {
            _icon.Tooltip = "尚未添加账号\n左键查看速览 · 双击打开工作台";
        }
        else
        {
            var label = AccountUsageHelpers.DisplayName(active);
            if (label.Length > 48) label = label[..45] + "…";
            var mode = _state.Settings.Config.OpenAI.UsageDisplayMode;
            var usage = AccountUsageHelpers.UsageText(active, mode);
            var refreshed = AccountUsageHelpers.FormatLastChecked(active.LastChecked);
            _icon.Tooltip = $"当前：{label}\n{usage}\n刷新：{refreshed}";
        }
    }

    private string ResolveIconPath(string style, string key, double? primary, double? secondary, bool active)
    {
        var classic = Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico");
        if (style == "classic") return classic;
        Directory.CreateDirectory(_generatedIconDirectory);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(_generatedIconDirectory, id + ".ico");
        if (File.Exists(path)) return path;
        using var stream = File.Create(path);
        TrayIconRenderer.WriteIcon(stream, style, primary, secondary, active);
        return path;
    }

    private void ScheduleQuickWindow()
    {
        CancelSingleClick();
        if (_quick is not null &&
            (_quick.AppWindow.IsVisible || DateTimeOffset.UtcNow - _quick.LastHiddenAt < TimeSpan.FromMilliseconds(350)))
        {
            _quick.Hide();
            return;
        }
        _singleClick = new CancellationTokenSource();
        _ = OpenAfterDoubleClickWindowAsync(_singleClick.Token);
    }

    public void ShowQuickPreview()
    {
        _quick ??= new QuickWindow(_state, _main);
        _quick.Render();
        PositionNearTray(_quick);
        _quick.Activate();
        _quick.RefreshLocalTokenSummary();
        _quick.RefreshRadar();
    }

    public bool IsQuickVisible => _quick?.AppWindow.IsVisible == true;

    private async Task OpenAfterDoubleClickWindowAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(220, cancellationToken); }
        catch (OperationCanceledException) { return; }
        if (cancellationToken.IsCancellationRequested) return;
        _quick ??= new QuickWindow(_state, _main);
        _quick.Render();
        PositionNearTray(_quick);
        _quick.Activate();
        _quick.RefreshLocalTokenSummary();
        _quick.RefreshRadar();
    }

    private void PositionNearTray(QuickWindow window)
    {
        if (!GetCursorPos(out var point)) return;
        var monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var width = Math.Min((int)Math.Round(468 * scale), info.Work.Right - info.Work.Left - 16);
        var logicalHeight = Math.Min(744, 604 + Math.Min(2, _state.Registry.Accounts.Count) * 70);
        var height = Math.Min((int)Math.Round(logicalHeight * scale), info.Work.Bottom - info.Work.Top - 16);
        var x = Math.Clamp(point.X - width / 2, info.Work.Left + 8, Math.Max(info.Work.Left + 8, info.Work.Right - width - 8));
        var y = point.Y > (info.Work.Top + info.Work.Bottom) / 2
            ? point.Y - height - 12 : point.Y + 12;
        y = Math.Clamp(y, info.Work.Top + 8, Math.Max(info.Work.Top + 8, info.Work.Bottom - height - 8));
        window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private MenuFlyout CreateMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("打开快捷面板", () =>
        {
            _quick ??= new QuickWindow(_state, _main);
            PositionNearTray(_quick);
            _quick.Activate();
            _quick.RefreshLocalTokenSummary();
            _quick.RefreshRadar();
        }));
        menu.Items.Add(Item("打开工作台", () => { _quick?.Hide(); _main.ShowDashboard(); }));
        menu.Items.Add(Item(((App)Microsoft.UI.Xaml.Application.Current).ImageStudioVisible ? "关闭生图窗口" : "打开生图工作台",
            () => _ = ((App)Microsoft.UI.Xaml.Application.Current).RequestToggleImageStudioAsync()));
        menu.Items.Add(Item(((App)Microsoft.UI.Xaml.Application.Current).QualityCheckVisible ? "关闭检测窗口" : "打开模型质量检测",
            () => _ = ((App)Microsoft.UI.Xaml.Application.Current).RequestToggleQualityCheckAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("刷新所有账号", () => _ = _state.RefreshNowAsync()));
        menu.Items.Add(Item(_state.KeepAwakeEnabled ? "关闭保持唤醒" : "开启保持唤醒",
            () => _state.SetKeepAwake(!_state.KeepAwakeEnabled)));
        menu.Items.Add(Item($"黑屏离开（{_state.Settings.Config.AwayModeDelaySeconds} 秒后）", () => _main.StartAwayMode()));
        var delays = new MenuFlyoutSubItem { Text = "黑屏前等待" };
        foreach (var seconds in new[] { 0, 5, 15, 30, 60 })
            delays.Items.Add(Item($"{seconds} 秒" + (seconds == _state.Settings.Config.AwayModeDelaySeconds ? " ✓" : string.Empty),
                () => _state.SetAwayDelay(seconds)));
        delays.Items.Add(Item("自定义时间…", () => _main.ShowSettingsSection("behavior")));
        menu.Items.Add(delays);
        menu.Items.Add(new MenuFlyoutSeparator());
        if (_state.AvailableUpdate is { } update)
            menu.Items.Add(Item($"发现新版 {update.Version} · 查看更新", _main.ShowUpdateDialog));
        else
            menu.Items.Add(Item("检查更新", _main.ShowUpdateDialog));
        menu.Items.Add(Item("设置", _main.ShowSettings));
        menu.Items.Add(Item("退出 WinCodexBar", () => ((App)Microsoft.UI.Xaml.Application.Current).ExitApplication()));
        return menu;
    }

    private static MenuFlyoutItem Item(string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) => action();
        return item;
    }

    private void CancelSingleClick()
    {
        _singleClick?.Cancel();
        _singleClick?.Dispose();
        _singleClick = null;
    }

    public void Dispose()
    {
        CancelSingleClick();
        _state.Changed -= StateChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
        _quick?.Close();
        try
        {
            if (Directory.Exists(_generatedIconDirectory)) Directory.Delete(_generatedIconDirectory, true);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
