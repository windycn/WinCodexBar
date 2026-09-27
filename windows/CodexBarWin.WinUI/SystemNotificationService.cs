using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexBarWin.WinUI;

/// <summary>Windows 通知中心提示；注册失败时不会影响托盘或工作台运行。</summary>
internal sealed class SystemNotificationService : IDisposable
{
    private readonly Action<string> _openPage;
    private bool _registered;
    private string? _registrationError;

    public SystemNotificationService(Action<string> openPage)
    {
        _openPage = openPage;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _registrationError = $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
        }
    }

    public string Status
    {
        get
        {
            if (!_registered) return "系统通知注册失败：" + _registrationError;
            try { return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled
                ? "系统通知已就绪"
                : "Windows 已关闭此应用的系统通知；窗口内提示仍可使用。"; }
            catch { return "无法读取 Windows 通知状态；窗口内提示仍可使用。"; }
        }
    }

    public bool Show(string title, string message, string page)
    {
        if (!_registered) return false;
        try
        {
            if (AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled) return false;
            var notification = new AppNotificationBuilder()
                .AddArgument("page", page)
                .AddText(title)
                .AddText(message.Length > 180 ? message[..180] + "…" : message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch { return false; /* 系统禁用通知时保留窗口内状态信息。 */ }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var page = args.Argument.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "page")?[1];
        if (page is "studio" or "quality" or "settings" or "accounts" or "home")
            _openPage(page);
    }

    public void Dispose()
    {
        if (!_registered) return;
        AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
        try { AppNotificationManager.Default.Unregister(); }
        catch { }
        _registered = false;
    }
}
