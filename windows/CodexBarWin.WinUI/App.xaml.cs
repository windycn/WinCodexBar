using Microsoft.UI.Xaml;

namespace CodexBarWin.WinUI;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private AppState? _state;
    private TrayController? _tray;
    private UpdateCoordinator? _updates;
    private ImageStudioWindow? _imageStudio;
    private VectorStudioWindow? _vectorStudio;
    private QualityCheckWindow? _qualityCheck;
    private SystemNotificationService? _notifications;
    private string? _notifiedUpdateVersion;
    private bool _featurePromptOpen;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _state = new AppState();
        _mainWindow = new MainWindow(_state);
        var background = args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (!background) _mainWindow.Activate();
        var previewQuick = Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_QUICK") == "1";
        var previewPage = Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_PAGE");
        // Screenshot preview pages are isolated app instances. Only the quick-panel preview
        // needs a tray controller; extra tray icons would otherwise outlive its windows.
        if (previewQuick || previewPage is null)
            _tray = new TrayController(_state, _mainWindow);
        if (previewPage is null && !previewQuick || Environment.GetEnvironmentVariable("CODEXBAR_PREVIEW_NOTIFICATIONS") == "1")
            _notifications = new SystemNotificationService(OpenFromNotification);
        _state.Changed += StateChangedForNotifications;
        _state.RefreshFinished += RefreshFinishedForNotifications;
        _state.UserActionCompleted += UserActionCompletedForNotifications;
        _state.NotifyChanged();
        if (!previewQuick && previewPage is null) _state.Start();
        if (!previewQuick && previewPage is null && string.Equals(Path.GetFileName(Environment.ProcessPath), "WinCodexBar.Next.exe", StringComparison.OrdinalIgnoreCase))
        {
            _updates = new UpdateCoordinator(_state, _mainWindow, _tray!, ExitApplication);
            _updates.Start();
        }
        if (previewQuick)
        {
            _mainWindow.AppWindow.Hide();
            _tray!.ShowQuickPreview();
        }
        else if (previewPage is not null)
        {
            switch (previewPage)
            {
                case "settings": _mainWindow.ShowSettings(); break;
                case "accounts": _mainWindow.ShowAccounts(); break;
                case "activity": _mainWindow.ShowTokenActivity(); break;
                case "sessions": _mainWindow.ShowSessionAnalysis(); break;
                case "studio": _mainWindow.AppWindow.Hide(); ToggleImageStudio(); break;
                case "vector": _mainWindow.AppWindow.Hide(); ShowVectorStudio(); break;
                case "quality": _mainWindow.AppWindow.Hide(); ToggleQualityCheck(); break;
                default: _mainWindow.ShowHome(); break;
            }
        }
        if (!previewQuick && previewPage is null)
        {
            try { ShortcutService.EnsureOnFirstLaunch(); }
            catch { /* 快捷方式失败不影响程序启动；设置页可重试。 */ }
        }
    }

    public void ExitApplication()
    {
        if (_state is not null)
        {
            _state.Changed -= StateChangedForNotifications;
            _state.RefreshFinished -= RefreshFinishedForNotifications;
            _state.UserActionCompleted -= UserActionCompletedForNotifications;
        }
        _notifications?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _imageStudio?.CloseForExit();
        _vectorStudio?.CloseForExit();
        _qualityCheck?.CloseForExit();
        _mainWindow?.CloseForExit();
        _state?.Dispose();
        Exit();
    }

    public bool ImageStudioVisible => _imageStudio?.AppWindow.IsVisible == true;

    private void ShowVectorStudio(string? selectedPath = null)
    {
        if (_state is null) return;
        _vectorStudio ??= new VectorStudioWindow(_state);
        _vectorStudio.ShowStudio(selectedPath);
    }

    public async Task OpenVectorStudioAsync(string? selectedPath = null)
    {
        if (_state is null || _mainWindow is null) return;
        if (!_state.Settings.Config.VectorStudioEnabled)
        {
            if (_featurePromptOpen) return;
            _featurePromptOpen = true;
            try
            {
                if (!await _mainWindow.ConfirmEnableFeatureAsync("转可编辑 SVG",
                    "此功能默认关闭。开启后可从生图图库、上传文件或剪贴板选择图片，转换结果单独保存在本机 SVG 图库。")) return;
                SetVectorStudioEnabled(true);
            }
            finally { _featurePromptOpen = false; }
        }
        ShowVectorStudio(selectedPath);
    }

    public void SetVectorStudioEnabled(bool enabled)
    {
        if (_state is null || _state.Settings.Config.VectorStudioEnabled == enabled) return;
        if (!enabled) _vectorStudio?.StopAndHide();
        _state.UpdateSettings(config => config.VectorStudioEnabled = enabled);
    }

    public void SetImageStudioEnabled(bool enabled)
    {
        if (_state is null || _state.Settings.Config.ImageStudioEnabled == enabled) return;
        if (!enabled) _imageStudio?.StopAndHide();
        _state.UpdateSettings(config => config.ImageStudioEnabled = enabled);
    }

    public void SetQualityCheckEnabled(bool enabled)
    {
        if (_state is null || _state.Settings.Config.QualityCheckEnabled == enabled) return;
        if (!enabled) _qualityCheck?.StopAndHide();
        _state.UpdateSettings(config => config.QualityCheckEnabled = enabled);
    }

    public async Task OpenImageStudioAsync()
    {
        if (_state is null || _mainWindow is null) return;
        if (!_state.Settings.Config.ImageStudioEnabled)
        {
            if (_featurePromptOpen) return;
            _featurePromptOpen = true;
            try
            {
                if (!await _mainWindow.ConfirmEnableFeatureAsync("生图工作台",
                    "此功能默认关闭。开启后可选择一个 Codex 账号生成图片、查看本机图库并转换可编辑 SVG。")) return;
                SetImageStudioEnabled(true);
            }
            finally { _featurePromptOpen = false; }
        }
        if (!ImageStudioVisible) ToggleImageStudio();
        else _imageStudio?.Activate();
    }

    public async Task OpenQualityCheckAsync()
    {
        if (_state is null || _mainWindow is null) return;
        if (!_state.Settings.Config.QualityCheckEnabled)
        {
            if (_featurePromptOpen) return;
            _featurePromptOpen = true;
            try
            {
                if (!await _mainWindow.ConfirmEnableFeatureAsync("降智检测",
                    "此功能默认关闭。开启后可选择账号和模型运行检测题目，结果保存在本机。")) return;
                SetQualityCheckEnabled(true);
            }
            finally { _featurePromptOpen = false; }
        }
        if (!QualityCheckVisible) ToggleQualityCheck();
        else _qualityCheck?.Activate();
    }

    public async Task RequestToggleImageStudioAsync()
    {
        if (ImageStudioVisible) ToggleImageStudio();
        else await OpenImageStudioAsync();
    }

    public async Task RequestToggleQualityCheckAsync()
    {
        if (QualityCheckVisible) ToggleQualityCheck();
        else await OpenQualityCheckAsync();
    }

    public string NotificationStatus => _notifications?.Status ?? "预览模式未启用系统通知。";

    public bool Notify(string title, string message, string page)
    {
        if (_state?.Settings.Config.SystemNotificationsEnabled == true)
            return _notifications?.Show(title, message, page) == true;
        return false;
    }

    private void StateChangedForNotifications(object? sender, EventArgs e)
    {
        _mainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            var version = _state?.AvailableUpdate?.Version.ToString();
            if (version is null || version == _notifiedUpdateVersion) return;
            _notifiedUpdateVersion = version;
            Notify("发现 WinCodexBar 更新", $"版本 {version} 已可用，可在设置中检查并安装。", "settings");
        });
    }

    private void RefreshFinishedForNotifications(object? sender, string message)
    {
        if (!message.Contains("失败") && !message.Contains("错误") && !message.Contains("未完成") && !message.Contains("无法刷新")) return;
        _mainWindow?.DispatcherQueue.TryEnqueue(() => Notify("WinCodexBar 需要关注", message, "home"));
    }

    private void UserActionCompletedForNotifications(string title, string message, string page) =>
        _mainWindow?.DispatcherQueue.TryEnqueue(() => Notify(title, message, page));

    private void OpenFromNotification(string page)
    {
        _mainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            switch (page)
            {
                case "studio": _ = OpenImageStudioAsync(); break;
                case "vector": _ = OpenVectorStudioAsync(); break;
                case "quality": _ = OpenQualityCheckAsync(); break;
                case "settings": _mainWindow.ShowSettings(); break;
                case "accounts": _mainWindow.ShowAccounts(); break;
                default: _mainWindow.ShowDashboard(); break;
            }
        });
    }

    public void ToggleImageStudio()
    {
        if (_state is null) return;
        _imageStudio ??= new ImageStudioWindow(_state);
        if (_imageStudio.AppWindow.IsVisible) _imageStudio.Hide();
        else _imageStudio.ShowStudio();
        _state.NotifyChanged();
    }

    public bool QualityCheckVisible => _qualityCheck?.AppWindow.IsVisible == true;

    public void ToggleQualityCheck()
    {
        if (_state is null) return;
        _qualityCheck ??= new QualityCheckWindow(_state);
        if (_qualityCheck.AppWindow.IsVisible) _qualityCheck.Hide();
        else _qualityCheck.ShowCheck();
        _state.NotifyChanged();
    }
}
