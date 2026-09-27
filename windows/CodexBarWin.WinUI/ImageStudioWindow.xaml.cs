using CodexBarWin.Models;
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

public sealed partial class ImageStudioWindow : Window
{
    private readonly AppState _state;
    private readonly ImageStudioService _studio;
    private readonly VectorSvgService _vectors;
    private CancellationTokenSource? _generation;
    private CancellationTokenSource? _vectorGeneration;
    private readonly List<string> _referencePaths = [];
    private StudioImage? _latestGeneratedImage;
    private bool _previewingSvg;
    private int _svgPreviewRevision;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _openingScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public ImageStudioWindow(AppState state)
    {
        _state = state;
        _studio = new ImageStudioService(state);
        _vectors = new VectorSvgService(state);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        AppWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(760 * scale)));
        AppWindow.Closing += (_, args) => { args.Cancel = true; Hide(); };
        RefreshAccounts();
        RestorePreferences();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePreferences(); };
        _openingScrollTimer.Tick += (_, _) =>
        {
            _openingScrollTimer.Stop();
            ComposerScroll.ChangeView(null, 0, null, true);
        };
        AccountCombo.SelectionChanged += (_, _) => ScheduleSave();
        RequestModelCombo.SelectionChanged += (_, _) => ScheduleSave();
        ModelCombo.SelectionChanged += (_, _) => ScheduleSave();
        ResolutionCombo.SelectionChanged += (_, _) => ScheduleSave();
        ImageEffortCombo.SelectionChanged += (_, _) => ScheduleSave();
        SizeCombo.SelectionChanged += (_, _) => ScheduleSave();
        QualityCombo.SelectionChanged += (_, _) => ScheduleSave();
        VectorModelCombo.SelectionChanged += (_, _) => ScheduleSave();
        VectorEffortCombo.SelectionChanged += (_, _) => ScheduleSave();
        ImageCountBox.ValueChanged += (_, _) => ScheduleSave();
        PromptBox.TextChanged += (_, _) => ScheduleSave();
        VectorGuidanceBox.TextChanged += (_, _) => ScheduleSave();
        CreativeTaskQueue.Shared.Changed += QueueChanged;
        QueueChanged();
        RefreshGallery();
        GalleryPathText.Text = "当前目录：" + _studio.GalleryPath;
        _state.Changed += StateChanged;
        DispatcherQueue.TryEnqueue(() => ComposerScroll.ChangeView(null, 0, null, true));
    }

    public void ShowStudio()
    {
        RefreshAccounts();
        RefreshGallery();
        GalleryPathText.Text = "当前目录：" + _studio.GalleryPath;
        AppWindow.Show();
        Activate();
        CloseStudioButton.Focus(FocusState.Programmatic);
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
        _generation?.Cancel();
        _vectorGeneration?.Cancel();
        Hide();
    }

    private void RestorePreferences()
    {
        var prefs = _state.Settings.Config.ImageStudio;
        SelectTag(RequestModelCombo, prefs.RequestModel);
        SelectTag(ModelCombo, prefs.ImageModel.Split("-2k")[0].Split("-4k")[0], "gpt-image-2.5-flare");
        SelectTag(ResolutionCombo, prefs.Resolution is "2k" or "4k" ? prefs.Resolution
            : prefs.ImageModel.EndsWith("-2k", StringComparison.Ordinal) ? "2k"
            : prefs.ImageModel.EndsWith("-4k", StringComparison.Ordinal) ? "4k" : "1k", "1k");
        SelectTag(ImageEffortCombo, prefs.ImageEffort);
        SelectTag(SizeCombo, prefs.Size);
        SelectTag(QualityCombo, prefs.Quality);
        SelectTag(VectorModelCombo, prefs.VectorModel);
        SelectTag(VectorEffortCombo, prefs.VectorEffort, "low");
        ImageCountBox.Value = Math.Clamp(prefs.Count, 1, 10);
        PromptBox.Text = prefs.Prompt ?? string.Empty;
        VectorGuidanceBox.Text = prefs.VectorGuidance ?? string.Empty;
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<StudioAccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == prefs.SelectedAccountId)
            ?? AccountCombo.SelectedItem;
    }

    private static void SelectTag(ComboBox combo, string? tag, string fallback = "medium")
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == tag)
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == fallback)
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
        var prefs = new ImageStudioPreferences
        {
            SelectedAccountId = (AccountCombo.SelectedItem as StudioAccountChoice)?.Account.AccountId ?? string.Empty,
            RequestModel = (RequestModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna",
            ImageModel = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-image-2.5-flare",
            Resolution = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1k",
            ImageEffort = (ImageEffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium",
            Size = (SizeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1024x1024",
            Quality = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium",
            Count = double.IsFinite(ImageCountBox.Value) ? Math.Clamp((int)Math.Round(ImageCountBox.Value), 1, 10) : 1,
            Prompt = PromptBox.Text,
            VectorModel = (VectorModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna",
            VectorEffort = (VectorEffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "low",
            VectorGuidance = VectorGuidanceBox.Text,
        };
        _state.Settings.Update(config => config.ImageStudio = prefs);
    }

    private void StateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshAccounts);

    private void QueueChanged() => DispatcherQueue.TryEnqueue(() =>
        CreativeQueueList.ItemsSource = CreativeTaskQueue.Shared.Snapshot());

    private void RefreshAccounts()
    {
        var selected = (AccountCombo.SelectedItem as StudioAccountChoice)?.Account.AccountId;
        AccountCombo.ItemsSource = _state.Registry.Accounts.Select(account => new StudioAccountChoice(account)).ToArray();
        AccountCombo.DisplayMemberPath = nameof(StudioAccountChoice.Name);
        AccountCombo.SelectedItem = AccountCombo.Items.OfType<StudioAccountChoice>()
            .FirstOrDefault(choice => choice.Account.AccountId == selected)
            ?? AccountCombo.Items.OfType<StudioAccountChoice>()
                .FirstOrDefault(choice => choice.Account.AccountId == _state.Registry.ActiveAccountId);
        AccountChanged(AccountCombo, null!);
    }

    private void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        var account = (AccountCombo.SelectedItem as StudioAccountChoice)?.Account;
        AccountUsageText.Text = account is null ? "请选择已登录账号" :
            AccountUsageHelpers.UsageText(account, _state.Settings.Config.OpenAI.UsageDisplayMode);
    }

    private void RefreshGallery()
    {
        var selected = (GalleryList.SelectedItem as StudioImage)?.Path;
        var period = (GalleryPeriodCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var images = _studio.LoadGallery();
        _latestGeneratedImage = images.FirstOrDefault();
        if (period == "today") images = images.Where(image => image.CreatedAt.LocalDateTime.Date == DateTime.Today).ToArray();
        if (period == "7d") images = images.Where(image => image.CreatedAt >= DateTimeOffset.Now.AddDays(-7)).ToArray();
        GalleryList.ItemsSource = images;
        GalleryList.SelectedItem = GalleryList.Items.OfType<StudioImage>().FirstOrDefault(item => item.Path == selected);
        UpdateSvgTarget();
    }

    private void GalleryPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryList is not null) RefreshGallery();
    }

    private void GallerySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSvgTarget();
        if (GalleryList.SelectedItem is not StudioImage item || !File.Exists(item.Path))
        {
            _svgPreviewRevision++;
            SvgResultWebView.Visibility = Visibility.Collapsed;
            ResultImage.Source = null;
            ResultImage.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Visible;
            ResultTitle.Text = "本机图库";
            SvgStatusText.Text = "尚未选择图片";
            return;
        }
        _previewingSvg = false;
        _svgPreviewRevision++;
        SvgResultWebView.Visibility = Visibility.Collapsed;
        ResultImage.Source = new BitmapImage(new Uri(item.Path));
        ResultImage.Visibility = Visibility.Visible;
        EmptyPreview.Visibility = Visibility.Collapsed;
        ResultTitle.Text = $"{item.AccountName} · {item.CreatedAt:MM-dd HH:mm}";
        SvgStatusText.Text = !string.IsNullOrWhiteSpace(item.SvgPath) && File.Exists(item.SvgPath)
            ? $"已有可编辑 SVG · {item.SvgModel} · {item.SvgCreatedAt:MM-dd HH:mm}"
            : "尚未转换";
    }

    private void ClearGallerySelectionClicked(object sender, RoutedEventArgs e)
    {
        GalleryList.SelectedItem = null;
        StatusText.Text = "已取消图库选择。";
    }

    private void UpdateSvgTarget()
    {
        if (SvgTargetText is null) return;
        var selected = GalleryList.SelectedItem as StudioImage;
        var target = selected ?? _latestGeneratedImage;
        SvgTargetText.Text = target is null ? "转换目标：尚无图片"
            : selected is null ? $"转换目标：最近生成 · {target.CreatedAt:MM-dd HH:mm}"
            : $"转换目标：已选图片 · {target.CreatedAt:MM-dd HH:mm}";
    }

    private void UseLatestForSvgClicked(object sender, RoutedEventArgs e)
    {
        if (_latestGeneratedImage is not { } latest) { SvgStatusText.Text = "还没有可转换的图片。"; return; }
        GalleryPeriodCombo.SelectedIndex = 0;
        RefreshGallery();
        GalleryList.SelectedItem = GalleryList.Items.OfType<StudioImage>().FirstOrDefault(item => item.Path == latest.Path);
        UpdateSvgTarget();
    }

    private async void ChooseReferenceClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files) AddReference(file.Path);
    }

    private void ClearReferenceClicked(object sender, RoutedEventArgs e)
    {
        _referencePaths.Clear();
        RenderReferences();
    }

    private async void PastePromptClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) { StatusText.Text = "剪贴板里没有文字。"; return; }
            PromptBox.Text = await content.GetTextAsync();
            PromptBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) { StatusText.Text = "无法粘贴提示词：" + ex.Message; }
    }

    private void ClearPromptClicked(object sender, RoutedEventArgs e) => PromptBox.Text = string.Empty;

    private async void PasteReferenceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                foreach (var item in items.OfType<StorageFile>()) AddReference(item.Path);
                return;
            }
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                StatusText.Text = "剪贴板里没有图片。";
                return;
            }
            var source = await content.GetBitmapAsync();
            using var stream = await source.OpenReadAsync();
            var directory = Path.Combine(Path.GetTempPath(), "WinCodexBar", "studio-references");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
            await using (var output = File.Create(path)) await stream.AsStreamForRead().CopyToAsync(output);
            AddReference(path);
        }
        catch (Exception ex) { StatusText.Text = "无法粘贴图片：" + ex.Message; }
    }

    private void AddReference(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            StatusText.Text = "仅支持 PNG、JPG 和 WebP 图片。";
            return;
        }
        if (_referencePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        if (_referencePaths.Count >= 8) { StatusText.Text = "最多添加 8 张参考图。"; return; }
        _referencePaths.Add(path);
        RenderReferences();
    }

    private void RenderReferences()
    {
        ReferenceText.Text = _referencePaths.Count == 0 ? "没有参考图 · 使用文生图" : $"已添加 {_referencePaths.Count} 张参考图 · 使用图生图";
        ReferenceList.ItemsSource = _referencePaths.Select(Path.GetFileName).ToArray();
        ReferenceList.Visibility = _referencePaths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearReferenceButton.Visibility = _referencePaths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void GenerateClicked(object sender, RoutedEventArgs e)
    {
        if ((AccountCombo.SelectedItem as StudioAccountChoice)?.Account is not { } account)
        {
            StatusText.Text = "请先选择一个已登录账号。";
            return;
        }
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            StatusText.Text = "请填写画面描述。";
            return;
        }
        _generation = new CancellationTokenSource();
        _saveTimer.Stop();
        _openingScrollTimer.Stop();
        SavePreferences();
        GenerateButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        GenerationProgress.Visibility = Visibility.Visible;
        var count = double.IsFinite(ImageCountBox.Value) ? Math.Clamp((int)Math.Round(ImageCountBox.Value), 1, 10) : 1;
        var prompt = PromptBox.Text;
        var requestModel = (RequestModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna";
        var imageModel = (ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-image-2.5-flare";
        var quality = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium";
        var effort = (ImageEffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium";
        var references = _referencePaths.ToArray();
        var completed = 0;
        var errors = new List<string>();
        try
        {
            var size = ImageStudioService.ResolveSize(
                (SizeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1024x1024",
                (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1k");
            StatusText.Text = $"已将 {count} 张图片加入队列 · 最多同时运行 6 项。";
            var jobs = Enumerable.Range(1, count).Select(async index =>
            {
                try
                {
                    var result = await CreativeTaskQueue.Shared.EnqueueAsync("生图", $"{index}/{count} · {imageModel} · {size}",
                        token => _studio.GenerateAsync(account, prompt, requestModel, imageModel, size, quality,
                            effort, references, token), _generation.Token);
                    completed++;
                    RefreshGallery();
                    GalleryList.SelectedItem = GalleryList.Items.OfType<StudioImage>()
                        .FirstOrDefault(item => item.Path == result.Path);
                    _latestGeneratedImage = result;
                    UpdateSvgTarget();
                    StatusText.Text = $"已完成 {completed}/{count} 张，结果已保存到本机图库。";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add(ex.Message); }
            });
            await Task.WhenAll(jobs);
            StatusText.Text = errors.Count > 0 ? $"完成 {completed}/{count} 张；失败 {errors.Count} 张：{errors[0]}"
                : _generation.IsCancellationRequested ? $"已取消；保留已完成的 {completed} 张。"
                : $"已完成 {completed} 张，全部保存到本机图库。";
            if (completed > 0) ((App)Application.Current).Notify("图片生成完成", $"已生成 {completed} 张图片，并保存到本机图库。", "studio");
            if (errors.Count > 0) ((App)Application.Current).Notify("部分图片生成未完成", errors[0], "studio");
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消生成。"; }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ((App)Application.Current).Notify("图片生成未完成", ex.Message, "studio");
        }
        finally
        {
            _generation.Dispose();
            _generation = null;
            GenerateButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            GenerationProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _generation?.Cancel();
    private void CloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OpenGalleryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_studio.GalleryPath);
        Process.Start(new ProcessStartInfo(_studio.GalleryPath) { UseShellExecute = true });
    }

    private async void ChangeGalleryPathClicked(object sender, RoutedEventArgs e)
    {
        if (CreativeTaskQueue.Shared.IsBusy) { StatusText.Text = "请等待生图和 SVG 队列结束后再更改图库目录。"; return; }
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var destination = Path.Combine(folder.Path, "WinCodexBar-Images");
        var previous = _studio.GalleryPath;
        try
        {
            StatusText.Text = "正在复制现有图片和索引…";
            await Task.Run(() => GalleryStorage.CopyLibrary<StudioImage>(previous, destination, ".png",
                item => item.Path, (item, path) => item with { Path = path }));
            _state.UpdateSettings(config => config.ImageGalleryPath = destination);
            GalleryPathText.Text = "当前目录：" + destination;
            RefreshGallery();
            StatusText.Text = "图片图库已切换；原目录保留为备份。";
        }
        catch (Exception ex) { StatusText.Text = "更改图库目录失败：" + ex.Message; }
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
            Title = "清理图片回收站？", Content = $"将永久删除{label}回收站文件。",
            PrimaryButtonText = "清理", CloseButtonText = "取消", XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (decision != ContentDialogResult.Primary) return;
        try
        {
            var count = await Task.Run(() => GalleryStorage.EmptyTrash(_studio.GalleryPath, days));
            StatusText.Text = $"已清理 {count} 个回收站文件。";
        }
        catch (Exception ex) { StatusText.Text = "清理失败：" + ex.Message; }
    }

    private void OpenVectorStudioClicked(object sender, RoutedEventArgs e)
    {
        var image = GalleryList.SelectedItem as StudioImage ?? _latestGeneratedImage;
        _ = ((App)Application.Current).OpenVectorStudioAsync(image?.Path);
    }

    private void ReusePromptClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item) return;
        PromptBox.Text = item.Prompt;
        ComposerScroll.ChangeView(null, 0, null, true);
        StatusText.Text = "已载入历史提示词，可继续编辑。";
    }

    private void UseResultAsReferenceClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item) return;
        AddReference(item.Path);
        StatusText.Text = "已载入所选结果作为参考图。";
    }

    private async void CopyImageClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item) { StatusText.Text = "请先选择一张图片。"; return; }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = "图片已复制，可粘贴到 Word、PowerPoint 等应用。";
        }
        catch (Exception ex) { StatusText.Text = "复制图片失败：" + ex.Message; }
    }

    private async void ConvertSvgClicked(object sender, RoutedEventArgs e)
    {
        var item = GalleryList.SelectedItem as StudioImage ?? _latestGeneratedImage;
        if (item is null) { SvgStatusText.Text = "请先生成图片或从历史中选择图片。"; return; }
        if (!_state.Settings.Config.VectorStudioEnabled)
        {
            await ((App)Application.Current).OpenVectorStudioAsync(item.Path);
            return;
        }
        if ((AccountCombo.SelectedItem as StudioAccountChoice)?.Account is not { } account) { SvgStatusText.Text = "请先选择一个账号。"; return; }
        var model = (VectorModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-6-luna";
        _saveTimer.Stop();
        SavePreferences();
        _vectorGeneration = new CancellationTokenSource();
        VectorConvertButton.IsEnabled = false;
        VectorCancelButton.Visibility = Visibility.Visible;
        SvgStatusText.Text = $"正在使用 {AccountUsageHelpers.DisplayName(account)} · {model} 重绘矢量图…";
        try
        {
            var effort = (VectorEffortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "low";
            var guidance = VectorGuidanceBox.Text;
            var result = await CreativeTaskQueue.Shared.EnqueueAsync("SVG", Path.GetFileName(item.Path),
                token => _vectors.ConvertAsync(account, item, model, effort, guidance, token), _vectorGeneration.Token);
            _studio.SaveVectorResult(item, result.Path, model,
                effort,
                AccountUsageHelpers.DisplayName(account), result.DurationMs, result.Usage);
            var updated = item with { SvgPath = result.Path, SvgModel = model, SvgCreatedAt = DateTimeOffset.Now };
            RefreshGallery();
            GalleryList.SelectedItem = GalleryList.Items.OfType<StudioImage>().FirstOrDefault(image => image.Path == updated.Path);
            ShowSvgPreview(updated);
            SvgStatusText.Text = "已生成真正可编辑的 SVG 路径与形状 · " + Path.GetFileName(result.Path);
            ((App)Application.Current).Notify("SVG 转换完成", "可编辑 SVG 已保存到本机图库。", "studio");
        }
        catch (OperationCanceledException) { SvgStatusText.Text = "已取消矢量转换。"; }
        catch (Exception ex)
        {
            SvgStatusText.Text = ex.Message;
            ((App)Application.Current).Notify("SVG 转换未完成", ex.Message, "studio");
        }
        finally
        {
            _vectorGeneration.Dispose();
            _vectorGeneration = null;
            VectorConvertButton.IsEnabled = true;
            VectorCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelSvgClicked(object sender, RoutedEventArgs e) => _vectorGeneration?.Cancel();

    private void PreviewSvgClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is StudioImage item) ShowSvgPreview(item);
    }

    private async void ShowSvgPreview(StudioImage item)
    {
        if (string.IsNullOrWhiteSpace(item.SvgPath) || !File.Exists(item.SvgPath)) { SvgStatusText.Text = "这张图片尚未转换为 SVG。"; return; }
        var revision = ++_svgPreviewRevision;
        try
        {
            var html = await Task.Run(() => SvgPreview.Html(item.SvgPath));
            if (revision != _svgPreviewRevision) return;
            SvgResultWebView.Visibility = Visibility.Visible;
            await SvgResultWebView.EnsureCoreWebView2Async();
            if (revision != _svgPreviewRevision) return;
            SvgResultWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            SvgResultWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            SvgResultWebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            SvgResultWebView.NavigateToString(html);
            _previewingSvg = true;
            ResultImage.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Collapsed;
            ResultTitle.Text = "可编辑 SVG 预览 · " + item.SvgModel;
        }
        catch (Exception ex) { SvgStatusText.Text = "无法预览 SVG：" + ex.Message; }
    }

    private void FullScreenImageClicked(object sender, RoutedEventArgs e) => OpenFullScreenPreview(_previewingSvg);

    private void ResultImageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenFullScreenPreview(_previewingSvg);

    private void FullScreenSvgClicked(object sender, RoutedEventArgs e) => OpenFullScreenPreview(svg: true);

    private void OpenFullScreenPreview(bool svg)
    {
        var item = GalleryList.SelectedItem as StudioImage ?? _latestGeneratedImage;
        var path = svg ? item?.SvgPath : item?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = svg ? "请先把选中的图片转成 SVG。" : "请先生成或选择一张图片。";
            return;
        }
        try
        {
            new FullScreenPreviewWindow(path, svg,
                svg ? "可编辑 SVG · " + Path.GetFileName(path) : "图片 · " + Path.GetFileName(path)).ShowPreview();
        }
        catch (Exception error) { StatusText.Text = "无法全屏预览：" + error.Message; }
    }

    private void OpenSvgClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item || string.IsNullOrWhiteSpace(item.SvgPath) || !File.Exists(item.SvgPath)) return;
        Process.Start(new ProcessStartInfo(item.SvgPath) { UseShellExecute = true });
    }

    private void CopySvgClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item || string.IsNullOrWhiteSpace(item.SvgPath) || !File.Exists(item.SvgPath)) return;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(File.ReadAllText(item.SvgPath));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        SvgStatusText.Text = "SVG 代码已复制。";
    }

    private async void DeleteImageClicked(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not StudioImage item) return;
        var result = await new ContentDialog
        {
            Title = "删除本机图片？", Content = item.Prompt,
            PrimaryButtonText = "删除", CloseButtonText = "取消",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        }.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        _studio.Delete(item);
        _svgPreviewRevision++;
        SvgResultWebView.Visibility = Visibility.Collapsed;
        ResultImage.Source = null;
        ResultImage.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        ResultTitle.Text = "本机图库";
        RefreshGallery();
    }

    public void CloseForExit()
    {
        _saveTimer.Stop();
        SavePreferences();
        _state.Changed -= StateChanged;
        CreativeTaskQueue.Shared.Changed -= QueueChanged;
        _generation?.Cancel();
        _vectorGeneration?.Cancel();
        _studio.Dispose();
        _vectors.Dispose();
        AppWindow.Destroy();
    }

    private void EditPromptWithNativeImeClicked(object sender, RoutedEventArgs e) =>
        ImeCompatibilityEditor.Open(this, PromptBox, "编辑生图提示词");

    private void EditVectorGuidanceWithNativeImeClicked(object sender, RoutedEventArgs e) =>
        ImeCompatibilityEditor.Open(this, VectorGuidanceBox, "编辑矢量转换要求");

    private sealed class StudioAccountChoice(TokenAccount account)
    {
        public TokenAccount Account { get; } = account;
        public string Name { get; } = AccountUsageHelpers.DisplayName(account);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
