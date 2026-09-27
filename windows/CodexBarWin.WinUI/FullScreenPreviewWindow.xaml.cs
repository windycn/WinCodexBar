using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace CodexBarWin.WinUI;

public sealed partial class FullScreenPreviewWindow : Window
{
    private readonly string? _svgHtml;

    public FullScreenPreviewWindow(string path, bool svg, string title)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("预览文件不存在。", path);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
        PreviewTitle.Text = title;
        if (svg)
        {
            _svgHtml = SvgPreview.Html(path);
            PreviewImage.Visibility = Visibility.Collapsed;
            SvgPreviewWebView.Visibility = Visibility.Visible;
        }
        else PreviewImage.Source = new BitmapImage(new Uri(path));
    }

    public async void ShowPreview()
    {
        Activate();
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        PreviewRoot.Focus(FocusState.Programmatic);
        if (_svgHtml is null) return;
        try
        {
            await SvgPreviewWebView.EnsureCoreWebView2Async();
            var core = SvgPreviewWebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                if (args.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    args.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                    args.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            SvgPreviewWebView.NavigateToString(_svgHtml);
        }
        catch { PreviewTitle.Text += " · 预览失败，请用外部 SVG 查看器打开"; }
    }

    private void ExitClicked(object sender, RoutedEventArgs e) => AppWindow.Destroy();

    private void PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        AppWindow.Destroy();
    }

}
