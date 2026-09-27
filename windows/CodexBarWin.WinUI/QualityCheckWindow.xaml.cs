using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace CodexBarWin.WinUI;

public sealed partial class QualityCheckWindow : Window
{
    private readonly AppState _state;
    private readonly QualityCheckService _checks;
    private bool _previewHandlersAttached;
    private bool _allowPreviewNavigation;
    private CancellationTokenSource? _running;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _openingScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly string _presetPath = Path.Combine(CodexPaths.CodexBarRoot, "quality-presets.json");
    private static readonly QualityPromptPreset[] BuiltInPresets =
    [
        new("鹈鹕骑自行车", "创建一个完整的 HTML 文件，用 SVG 绘制鹈鹕骑自行车的 2D 动画。所有 CSS 和 JavaScript 都写在文件中，不使用外部资源。", true),
        new("模拟时钟", "创建一个完整的 HTML 文件，绘制准确显示当前本地时间的 SVG 模拟时钟。秒针平滑运动，时区与数字刻度正确。所有资源内联。", true),
        new("太阳系轨道", "创建一个自包含 HTML 太阳系动画。用 SVG 展示太阳与至少八颗行星的不同公转速度、轨道和标签，提供暂停/继续按钮。", true),
        new("弹跳小球物理", "创建一个自包含 HTML 动画：三个质量不同的小球在矩形边界内弹跳、碰撞，运动符合重力和能量损失，带暂停与重置。", true),
        new("齿轮传动", "创建一个自包含 HTML SVG 动画，展示三个不同大小的齿轮相互啮合并按正确方向与角速度转动，带速度滑块。", true),
        new("城市夜景视差", "创建一个自包含 HTML SVG 动画，展示夜晚城市的三层视差景深、移动云朵、窗灯和驶过的车辆，配色协调。", true),
        new("汉字笔顺", "创建一个自包含 HTML SVG 动画，展示汉字“永”的逐笔书写顺序；每笔有清晰编号、轨迹和重播按钮。", true),
        new("响应式仪表盘", "创建一个自包含 HTML 响应式仪表盘，含四张指标卡、一张 SVG 折线图、一张柱状图和可切换的亮色/暗色主题；窄屏不横向溢出。", true),
        new("交互式流程图", "创建一个自包含 HTML SVG 流程图，展示登录、验证、授权、失败重试四种状态；点击节点能显示说明，连线和箭头清楚。", true),
        new("中文排版卡片", "创建一个自包含 HTML 页面，展示一篇包含中文标题、摘要、三段正文和脚注的文章；在 320px 到 1200px 宽度下保持清晰排版和适当行距。", true),
        new("排序算法演示", "创建一个自包含 HTML 动画，用 20 根柱子演示冒泡排序，显示比较次数与交换次数，支持暂停、继续、重新开始及速度调整。", true),
        new("地图与路线", "创建一个自包含 HTML SVG 示意地图，标出五个地点和两条不同颜色的路线；点击路线可查看途经地点与距离，文字不重叠。", true)
    ];

    public QualityCheckWindow(AppState state)
    {
        _state = state;
        _checks = new QualityCheckService(state);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        AppWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(760 * scale)));
        AppWindow.Closing += (_, args) => { args.Cancel = true; Hide(); };
        _state.Changed += StateChanged;
        RefreshAccounts();
        RefreshPresets();
        RestorePreferences();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
        _openingScrollTimer.Tick += (_, _) =>
        {
            _openingScrollTimer.Stop();
            ComposerScroll.ChangeView(null, 0, null, true);
        };
        AccountCombo.SelectionChanged += (_, _) => ScheduleSave();
        ModelCombo.SelectionChanged += (_, _) => ScheduleSave();
        EffortCombo.SelectionChanged += (_, _) => ScheduleSave();
        PromptBox.TextChanged += (_, _) => ScheduleSave();
        RefreshHistory();
        DispatcherQueue.TryEnqueue(() => ComposerScroll.ChangeView(null, 0, null, true));
    }

    public void ShowCheck()
    {
        RefreshAccounts();
        AppWindow.Show();
        Activate();
        _openingScrollTimer.Stop();
        _openingScrollTimer.Start();
    }

    public void Hide()
    {
        _saveTimer.Stop();
        _openingScrollTimer.Stop();
        SavePreferences();
        AppWindow.Hide();
    }

    public void StopAndHide()
    {
        _running?.Cancel();
        Hide();
    }

    private void RestorePreferences()
    {
        var prefs = _state.Settings.Config.QualityCheck;
        SelectTag(ModelCombo, prefs.Model);
        SelectTag(EffortCombo, prefs.Effort);
        if (!string.IsNullOrWhiteSpace(prefs.Prompt)) PromptBox.Text = prefs.Prompt;
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<AccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == prefs.SelectedAccountId)
            ?? AccountCombo.SelectedItem;
    }

    private static void SelectTag(ComboBox combo, string? tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == tag)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "medium")
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SavePreferences()
    {
        if (AccountCombo is null) return;
        var prefs = new QualityCheckPreferences
        {
            SelectedAccountId = (AccountCombo.SelectedItem as AccountChoice)?.Account.AccountId ?? string.Empty,
            Model = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna",
            Effort = (EffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium",
            Prompt = PromptBox.Text,
        };
        _state.Settings.Update(config => config.QualityCheck = prefs);
    }

    private void StateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshAccounts);

    private void RefreshAccounts()
    {
        var selected = (AccountCombo.SelectedItem as AccountChoice)?.Account.AccountId;
        AccountCombo.ItemsSource = _state.Registry.Accounts.Select(account => new AccountChoice(account)).ToArray();
        AccountCombo.DisplayMemberPath = nameof(AccountChoice.Name);
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<AccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == selected)
            ?? AccountCombo.Items.OfType<AccountChoice>()
                .FirstOrDefault(choice => choice.Account.AccountId == _state.Registry.ActiveAccountId);
    }

    private void RefreshHistory()
    {
        var selected = (HistoryList.SelectedItem as QualityCheckRecord)?.Id;
        var period = (PeriodCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var records = _checks.LoadRecords();
        if (period == "today") records = records.Where(record => record.CreatedAt.LocalDateTime.Date == DateTime.Today).ToArray();
        if (period == "7d") records = records.Where(record => record.CreatedAt >= DateTimeOffset.Now.AddDays(-7)).ToArray();
        HistoryList.ItemsSource = records;
        HistoryList.SelectedItem = HistoryList.Items.OfType<QualityCheckRecord>().FirstOrDefault(record => record.Id == selected);
    }

    private void RefreshPresets()
    {
        var selected = (PresetCombo.SelectedItem as QualityPromptPreset)?.Name;
        QualityPromptPreset[] custom;
        try { custom = File.Exists(_presetPath) ? JsonSerializer.Deserialize<QualityPromptPreset[]>(File.ReadAllText(_presetPath)) ?? [] : []; }
        catch { custom = []; }
        PresetCombo.ItemsSource = BuiltInPresets.Concat(custom).ToArray();
        PresetCombo.DisplayMemberPath = nameof(QualityPromptPreset.Name);
        PresetCombo.SelectedItem = PresetCombo.Items.OfType<QualityPromptPreset>().FirstOrDefault(item => item.Name == selected)
            ?? PresetCombo.Items.OfType<QualityPromptPreset>().FirstOrDefault();
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PromptBox is not null && PresetCombo.SelectedItem is QualityPromptPreset preset)
            PromptBox.Text = preset.Prompt;
    }

    private async void SavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) return;
        var nameBox = new TextBox { Header = "预设名称", MaxLength = 100 };
        var result = await new ContentDialog
        {
            Title = "保存检测题目", Content = nameBox,
            PrimaryButtonText = "保存", CloseButtonText = "取消",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text)) return;
        var custom = PresetCombo.Items.OfType<QualityPromptPreset>().Where(item => !item.IsBuiltIn).ToList();
        custom.RemoveAll(item => item.Name.Equals(nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        custom.Add(new QualityPromptPreset(nameBox.Text.Trim(), PromptBox.Text.Trim(), false));
        Directory.CreateDirectory(CodexPaths.CodexBarRoot);
        File.WriteAllText(_presetPath, JsonSerializer.Serialize(custom));
        RefreshPresets();
        PresetCombo.SelectedItem = PresetCombo.Items.OfType<QualityPromptPreset>().FirstOrDefault(item => item.Name == nameBox.Text.Trim());
        StatusText.Text = "检测题目已保存为本机预设。";
    }

    private async void DeletePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not QualityPromptPreset preset || preset.IsBuiltIn) { StatusText.Text = "内置预设可以复制修改，不能删除。"; return; }
        var result = await new ContentDialog
        {
            Title = "删除我的预设？", Content = preset.Name,
            PrimaryButtonText = "删除", CloseButtonText = "取消",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var remaining = PresetCombo.Items.OfType<QualityPromptPreset>().Where(item => !item.IsBuiltIn && item.Name != preset.Name).ToArray();
        File.WriteAllText(_presetPath, JsonSerializer.Serialize(remaining));
        RefreshPresets();
    }

    private void PeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList is not null) RefreshHistory();
    }

    private async void HistoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not QualityCheckRecord record) return;
        try
        {
            // WebView2 needs a visible XAML host before its composition surface is created.
            PreviewWebView.Visibility = Visibility.Visible;
            EmptyPreview.Visibility = Visibility.Collapsed;
            await PreviewWebView.EnsureCoreWebView2Async();
            PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            if (!_previewHandlersAttached)
            {
                PreviewWebView.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (_allowPreviewNavigation && args.Uri.StartsWith("data:text/html;charset=utf-8;base64,", StringComparison.OrdinalIgnoreCase))
                    {
                        _allowPreviewNavigation = false;
                        return;
                    }
                    if (!args.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
                };
                PreviewWebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
                _previewHandlersAttached = true;
            }
            _allowPreviewNavigation = true;
            PreviewWebView.NavigateToString(WithPreviewPolicy(record.Html));
            StatusText.Text = $"{record.CreatedAt:yyyy-MM-dd HH:mm:ss} · {record.AccountName} · {record.DurationMs / 1000d:0.0} 秒 · 输出 {record.OutputTokens} Token";
        }
        catch (Exception ex) { StatusText.Text = "无法预览记录：" + ex.Message; }
    }

    private static string WithPreviewPolicy(string html)
    {
        const string policy = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; " +
            "img-src data: blob:; media-src data: blob:; font-src data:; " +
            "style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
            "connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\">";
        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        var end = head < 0 ? -1 : html.IndexOf('>', head);
        if (end >= 0) return html.Insert(end + 1, policy);
        var document = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        end = document < 0 ? -1 : html.IndexOf('>', document);
        return end >= 0 ? html.Insert(end + 1, "<head>" + policy + "</head>") : "<head>" + policy + "</head>" + html;
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if ((AccountCombo.SelectedItem as AccountChoice)?.Account is not { } account)
        {
            StatusText.Text = "请先选择一个账号。";
            return;
        }
        _running = new CancellationTokenSource();
        _saveTimer.Stop();
        _openingScrollTimer.Stop();
        SavePreferences();
        StartButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "正在检测所选账号，结果将保存到本机…";
        try
        {
            var record = await _checks.RunAsync(account,
                (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna",
                (EffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium",
                PromptBox.Text, _running.Token);
            RefreshHistory();
            HistoryList.SelectedItem = HistoryList.Items.OfType<QualityCheckRecord>().FirstOrDefault(item => item.Id == record.Id);
            StatusText.Text = "检测已完成，已保存到本机记录。";
            ((App)Application.Current).Notify("模型质量检测完成", "结果已保存，可在检测记录中查看和预览。", "quality");
        }
        catch (OperationCanceledException) { StatusText.Text = "已停止检测。"; }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ((App)Application.Current).Notify("模型质量检测未完成", ex.Message, "quality");
        }
        finally
        {
            _running.Dispose();
            _running = null;
            StartButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _running?.Cancel();
    private void CloseClicked(object sender, RoutedEventArgs e) => Hide();

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not QualityCheckRecord record) return;
        var picker = new FileSavePicker { SuggestedFileName = "WinCodexBar-quality-" + record.CreatedAt.ToString("yyyyMMdd-HHmm") };
        picker.FileTypeChoices.Add("HTML", [".html"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path, record.Html);
        StatusText.Text = "已导出 HTML：" + file.Name;
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not QualityCheckRecord record) return;
        var result = await new ContentDialog
        {
            Title = "删除检测记录？", Content = record.Summary,
            PrimaryButtonText = "删除", CloseButtonText = "取消",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        _checks.Delete(record);
        PreviewWebView.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        RefreshHistory();
    }

    public void CloseForExit()
    {
        _saveTimer.Stop();
        SavePreferences();
        _state.Changed -= StateChanged;
        _running?.Cancel();
        _checks.Dispose();
        AppWindow.Destroy();
    }

    private void EditPromptWithNativeImeClicked(object sender, RoutedEventArgs e) =>
        ImeCompatibilityEditor.Open(this, PromptBox, "编辑检测题目");

    private sealed class AccountChoice(TokenAccount account)
    {
        public TokenAccount Account { get; } = account;
        public string Name { get; } = AccountUsageHelpers.DisplayName(account);
    }

    private sealed record QualityPromptPreset(string Name, string Prompt, bool IsBuiltIn);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
