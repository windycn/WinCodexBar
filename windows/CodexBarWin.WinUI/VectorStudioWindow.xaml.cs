using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CodexBarWin.WinUI;

public sealed partial class VectorStudioWindow : Window
{
    private readonly AppState _state;
    private readonly ImageStudioService _studio;
    private readonly VectorSvgService _vectors;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _conversion;
    private StudioImage? _source;
    private VectorGalleryItem? _selectedVector;
    private string? _previewPath;
    private bool _previewSvg;
    private bool _changingSelection;
    private int _previewRevision;
    private bool _svgPreviewHandlersAttached;

    public VectorStudioWindow(AppState state)
    {
        _state = state;
        _studio = new ImageStudioService(state);
        _vectors = new VectorSvgService(state);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        AppWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(750 * scale)));
        AppWindow.Closing += (_, args) => { args.Cancel = true; Hide(); };
        RefreshAccounts();
        RestorePreferences();
        RefreshLibraries();
        GalleryPathText.Text = "当前目录：" + _studio.VectorGalleryPath;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
        AccountCombo.SelectionChanged += (_, _) => ScheduleSave();
        ModelCombo.SelectionChanged += (_, _) => ScheduleSave();
        EffortCombo.SelectionChanged += (_, _) => ScheduleSave();
        GuidanceBox.TextChanged += (_, _) => ScheduleSave();
        CreativeTaskQueue.Shared.Changed += QueueChanged;
        QueueChanged();
        _state.Changed += StateChanged;
    }

    public void ShowStudio(string? selectedPath)
    {
        RefreshAccounts();
        RefreshLibraries();
        GalleryPathText.Text = "当前目录：" + _studio.VectorGalleryPath;
        if (!string.IsNullOrWhiteSpace(selectedPath)) SelectSource(selectedPath);
        AppWindow.Show();
        Activate();
    }

    public void Hide()
    {
        _saveTimer.Stop();
        SavePreferences();
        AppWindow.Hide();
    }

    public void StopAndHide()
    {
        _conversion?.Cancel();
        Hide();
    }

    private void RestorePreferences()
    {
        var prefs = _state.Settings.Config.VectorStudio;
        SelectTag(ModelCombo, prefs.Model, "gpt-6-luna");
        SelectTag(EffortCombo, prefs.Effort, "low");
        GuidanceBox.Text = prefs.Guidance;
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<AccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == prefs.SelectedAccountId)
            ?? AccountCombo.SelectedItem;
    }

    private static void SelectTag(ComboBox combo, string? tag, string fallback) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == tag)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == fallback);

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SavePreferences()
    {
        if (AccountCombo is null) return;
        var prefs = new VectorStudioPreferences
        {
            SelectedAccountId = (AccountCombo.SelectedItem as AccountChoice)?.Account.AccountId ?? string.Empty,
            Model = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna",
            Effort = (EffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "low",
            Guidance = GuidanceBox.Text
        };
        _state.Settings.Update(config => config.VectorStudio = prefs);
    }

    private void StateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshAccounts);

    private void QueueChanged() => DispatcherQueue.TryEnqueue(() =>
        CreativeQueueList.ItemsSource = CreativeTaskQueue.Shared.Snapshot());

    private void RefreshAccounts()
    {
        var selected = (AccountCombo.SelectedItem as AccountChoice)?.Account.AccountId
            ?? _state.Settings.Config.VectorStudio.SelectedAccountId;
        AccountCombo.ItemsSource = _state.Registry.Accounts.Select(account => new AccountChoice(account)).ToArray();
        AccountCombo.DisplayMemberPath = nameof(AccountChoice.Name);
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<AccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == selected)
            ?? AccountCombo.Items.OfType<AccountChoice>()
                .FirstOrDefault(choice => choice.Account.AccountId == _state.Registry.ActiveAccountId);
    }

    private void RefreshLibraries()
    {
        var sourcePaths = SourceList.SelectedItems.OfType<StudioImage>()
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectorPath = _selectedVector?.Path;
        _changingSelection = true;
        try
        {
            SourceList.ItemsSource = _studio.LoadGallery();
            VectorList.ItemsSource = _studio.LoadVectorGallery();
            foreach (var item in SourceList.Items.OfType<StudioImage>().Where(item => sourcePaths.Contains(item.Path)))
                SourceList.SelectedItems.Add(item);
            if (SourceList.SelectedItems.Count == 0)
                VectorList.SelectedItem = VectorList.Items.OfType<VectorGalleryItem>()
                    .FirstOrDefault(item => item.Path == vectorPath);
        }
        finally { _changingSelection = false; }
        _source = SourceList.SelectedItems.OfType<StudioImage>().FirstOrDefault();
        _selectedVector = VectorList.SelectedItem as VectorGalleryItem;
        if (_selectedVector is not null) ShowVector(_selectedVector);
        else if (_source is not null) ShowSource(_source);
        else ClearPreview();
    }

    private void SelectSource(string path)
    {
        var item = SourceList.Items.OfType<StudioImage>()
            .FirstOrDefault(image => string.Equals(image.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        SourceList.SelectedItem = item;
        SourceList.ScrollIntoView(item);
        ShowSource(item);
    }

    private void SourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection) return;
        var image = SourceList.SelectedItems.OfType<StudioImage>().FirstOrDefault();
        if (image is null)
        {
            _source = null;
            SourceText.Text = "尚未选择图片";
            if (_selectedVector is null) ClearPreview();
            return;
        }
        _changingSelection = true;
        try { VectorList.SelectedItem = null; }
        finally { _changingSelection = false; }
        _selectedVector = null;
        ShowSource(image);
    }

    private void ShowSource(StudioImage image)
    {
        _source = image;
        SourceText.Text = image.Prompt;
        ShowPreview(image.Path, false);
        StatusText.Text = "已选择图片 · 可点击“转可编辑SVG”。";
    }

    private void VectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection) return;
        if (VectorList.SelectedItem is not VectorGalleryItem item)
        {
            _selectedVector = null;
            if (_source is null) ClearPreview();
            return;
        }
        _changingSelection = true;
        try { SourceList.SelectedItems.Clear(); }
        finally { _changingSelection = false; }
        _source = null;
        SourceText.Text = "尚未选择图片";
        _selectedVector = item;
        ShowVector(item);
    }

    private void ShowVector(VectorGalleryItem item)
    {
        ShowPreview(item.Path, true);
        StatusText.Text = $"SVG 图库 · {item.DetailLabel}";
    }

    private void ClearPreview()
    {
        _previewRevision++;
        _previewPath = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        SvgPreviewWebView.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        StatusText.Text = "尚未选择图片或 SVG。";
    }

    private void ClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        _changingSelection = true;
        try
        {
            SourceList.SelectedItems.Clear();
            VectorList.SelectedItem = null;
        }
        finally { _changingSelection = false; }
        _source = null;
        _selectedVector = null;
        SourceText.Text = "尚未选择图片";
        ClearPreview();
    }

    private void ShowPreview(string path, bool svg)
    {
        if (!File.Exists(path)) { StatusText.Text = "预览文件不存在。"; return; }
        var revision = ++_previewRevision;
        _previewPath = path;
        _previewSvg = svg;
        EmptyPreview.Visibility = Visibility.Collapsed;
        if (svg)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            SvgPreviewWebView.Visibility = Visibility.Visible;
            _ = ShowSvgPreviewAsync(path, revision);
        }
        else
        {
            SvgPreviewWebView.Visibility = Visibility.Collapsed;
            PreviewImage.Source = new BitmapImage(new Uri(path));
            PreviewImage.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowSvgPreviewAsync(string path, int revision)
    {
        try
        {
            var html = await Task.Run(() => SvgPreview.Html(path));
            if (revision != _previewRevision) return;
            await SvgPreviewWebView.EnsureCoreWebView2Async();
            if (revision != _previewRevision) return;
            SvgPreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            SvgPreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            if (!_svgPreviewHandlersAttached)
            {
                SvgPreviewWebView.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (args.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                        args.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                        args.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
                };
                SvgPreviewWebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
                _svgPreviewHandlersAttached = true;
            }
            SvgPreviewWebView.NavigateToString(html);
        }
        catch (Exception ex) { if (revision == _previewRevision) StatusText.Text = "无法预览 SVG：" + ex.Message; }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files) await ImportImageAsync(file.Path);
    }

    private async void PasteClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var files = await content.GetStorageItemsAsync();
                foreach (var file in files.OfType<StorageFile>()) await ImportImageAsync(file.Path);
                return;
            }
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                StatusText.Text = "剪贴板里没有图片。";
                return;
            }
            var source = await content.GetBitmapAsync();
            using var stream = await source.OpenReadAsync();
            var directory = Path.Combine(Path.GetTempPath(), "WinCodexBar", "vector-import");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
            await using (var output = File.Create(path)) await stream.AsStreamForRead().CopyToAsync(output);
            try { await ImportImageAsync(path); }
            finally { File.Delete(path); }
        }
        catch (Exception ex) { StatusText.Text = "无法粘贴图片：" + ex.Message; }
    }

    private async Task ImportImageAsync(string path)
    {
        try
        {
            var item = await _studio.ImportImageAsync(path);
            RefreshLibraries();
            SelectSource(item.Path);
            StatusText.Text = "图片已导入生图图库，可开始转换。";
        }
        catch (Exception ex) { StatusText.Text = "无法导入图片：" + ex.Message; }
    }

    private async void ConvertClicked(object sender, RoutedEventArgs e)
    {
        var sources = SourceList.SelectedItems.OfType<StudioImage>().ToArray();
        if (sources.Length == 0 && _source is not null) sources = [_source];
        if (sources.Length == 0) { StatusText.Text = "请先从图库选择或导入图片。"; return; }
        if ((AccountCombo.SelectedItem as AccountChoice)?.Account is not { } account)
        { StatusText.Text = "请先选择一个已登录的 Codex 账号。"; return; }
        _saveTimer.Stop();
        SavePreferences();
        _conversion = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        var model = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna";
        var effort = (EffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "low";
        var guidance = GuidanceBox.Text;
        var completed = 0;
        var errors = new List<string>();
        try
        {
            StatusText.Text = $"已将 {sources.Length} 张图片加入转换队列 · 最多同时运行 6 项。";
            var jobs = sources.Select(async (source, index) =>
            {
                try
                {
                    var result = await CreativeTaskQueue.Shared.EnqueueAsync("SVG", $"{index + 1}/{sources.Length} · {model}",
                        token => _vectors.ConvertAsync(account, source, model, effort, guidance, token), _conversion.Token);
                    _studio.SaveVectorResult(source, result.Path, model, effort, AccountUsageHelpers.DisplayName(account),
                        result.DurationMs, result.Usage);
                    completed++;
                    RefreshLibraries();
                    VectorList.SelectedItem = VectorList.Items.OfType<VectorGalleryItem>()
                        .FirstOrDefault(item => item.Path == result.Path);
                    ShowPreview(result.Path, true);
                    StatusText.Text = $"已转换 {completed}/{sources.Length} 张，结果保存在 SVG 图库。";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add(ex.Message); }
            });
            await Task.WhenAll(jobs);
            StatusText.Text = errors.Count > 0 ? $"完成 {completed}/{sources.Length} 张；失败 {errors.Count} 张：{errors[0]}"
                : _conversion.IsCancellationRequested ? $"已取消；保留已完成的 {completed} 张。"
                : $"已完成 {completed} 张，全部保存到 SVG 图库。";
            if (completed > 0) ((App)Application.Current).Notify("SVG 转换完成", $"{completed} 个结果已保存到 SVG 图库。", "vector");
            if (errors.Count > 0) ((App)Application.Current).Notify("部分 SVG 转换未完成", errors[0], "vector");
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消转换。"; }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ((App)Application.Current).Notify("SVG 转换未完成", ex.Message, "vector");
        }
        finally
        {
            _conversion.Dispose();
            _conversion = null;
            ConvertButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ConversionProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _conversion?.Cancel();
    private void CloseClicked(object sender, RoutedEventArgs e) => Hide();
    private void FullScreenClicked(object sender, RoutedEventArgs e) => OpenFullScreen();
    private void PreviewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenFullScreen();

    private void OpenFullScreen()
    {
        if (_previewPath is null) { StatusText.Text = "请先选择图片或 SVG。"; return; }
        new FullScreenPreviewWindow(_previewPath, _previewSvg,
            (_previewSvg ? "可编辑 SVG · " : "图片 · ") + Path.GetFileName(_previewPath)).ShowPreview();
    }

    private void OpenSvgClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedVector is not { } item || !File.Exists(item.Path)) { StatusText.Text = "请先从 SVG 图库选择结果。"; return; }
        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private void CopySvgClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedVector is not { } item || !File.Exists(item.Path)) { StatusText.Text = "请先从 SVG 图库选择结果。"; return; }
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(File.ReadAllText(item.Path));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        StatusText.Text = "SVG 代码已复制。";
    }

    private void OpenVectorGalleryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_studio.VectorGalleryPath);
        Process.Start(new ProcessStartInfo(_studio.VectorGalleryPath) { UseShellExecute = true });
    }

    private async void DeleteSvgClicked(object sender, RoutedEventArgs e)
    {
        if (VectorList.SelectedItem is not VectorGalleryItem item)
        { StatusText.Text = "请先从 SVG 图库选择一个结果。"; return; }
        var decision = await new ContentDialog
        {
            Title = "移入 SVG 回收站？", Content = item.Prompt,
            PrimaryButtonText = "移入回收站", CloseButtonText = "取消",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (decision != ContentDialogResult.Primary) return;
        GalleryStorage.TrashMedia(_studio.VectorGalleryPath, item.Path);
        SvgPreview.Invalidate(item.Path);
        _selectedVector = null;
        ClearPreview();
        RefreshLibraries();
        StatusText.Text = "SVG 已移入回收站。";
    }

    private async void ChangeGalleryPathClicked(object sender, RoutedEventArgs e)
    {
        if (CreativeTaskQueue.Shared.IsBusy) { StatusText.Text = "请等待生图和 SVG 队列结束后再更改图库目录。"; return; }
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var destination = Path.Combine(folder.Path, "WinCodexBar-SVG");
        var previous = _studio.VectorGalleryPath;
        try
        {
            StatusText.Text = "正在复制现有 SVG 和索引…";
            await Task.Run(() => GalleryStorage.CopyLibrary<VectorGalleryItem>(previous, destination, ".svg",
                item => item.Path, (item, path) => item with { Path = path }));
            _state.UpdateSettings(config => config.SvgGalleryPath = destination);
            GalleryPathText.Text = "当前目录：" + destination;
            RefreshLibraries();
            StatusText.Text = "SVG 图库已切换；原目录保留为备份。";
        }
        catch (Exception ex) { StatusText.Text = "更改 SVG 图库目录失败：" + ex.Message; }
    }

    private void TrashRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomTrashDays is not null)
            CustomTrashDays.Visibility = (TrashRetentionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom"
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CleanTrashClicked(object sender, RoutedEventArgs e)
    {
        var tag = (TrashRetentionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "7";
        var days = tag == "custom" ? Math.Clamp((int)Math.Round(CustomTrashDays.Value), 1, 3650) : int.Parse(tag);
        var label = days == 0 ? "全部" : $"超过 {days} 天的";
        var decision = await new ContentDialog
        {
            Title = "清理 SVG 回收站？", Content = $"将永久删除{label}回收站文件。",
            PrimaryButtonText = "清理", CloseButtonText = "取消", XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (decision != ContentDialogResult.Primary) return;
        try
        {
            var count = await Task.Run(() => GalleryStorage.EmptyTrash(_studio.VectorGalleryPath, days));
            StatusText.Text = $"已清理 {count} 个回收站文件。";
        }
        catch (Exception ex) { StatusText.Text = "清理失败：" + ex.Message; }
    }

    public void CloseForExit()
    {
        _saveTimer.Stop();
        SavePreferences();
        _state.Changed -= StateChanged;
        CreativeTaskQueue.Shared.Changed -= QueueChanged;
        _conversion?.Cancel();
        _studio.Dispose();
        _vectors.Dispose();
        AppWindow.Destroy();
    }

    private void EditGuidanceWithNativeImeClicked(object sender, RoutedEventArgs e) =>
        ImeCompatibilityEditor.Open(this, GuidanceBox, "编辑矢量转换要求");

    private sealed class AccountChoice(TokenAccount account)
    {
        public TokenAccount Account { get; } = account;
        public string Name { get; } = AccountUsageHelpers.DisplayName(account);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
