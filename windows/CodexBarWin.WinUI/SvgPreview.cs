using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Svg.Skia;
using System.Security.Cryptography;
using System.Text;

namespace CodexBarWin.WinUI;

/// <summary>Uses a browser for the full SVG and a cached raster only for small gallery cards.</summary>
public static class SvgPreview
{
    public static string Html(string path)
    {
        // These files are normally validated when saved. Revalidate on preview because users can
        // edit gallery files outside the app. CSP also prevents resource fetches in WebView2.
        var svg = VectorSvgService.ValidateSvg(File.ReadAllText(path));
        return "<!doctype html><html><head><meta charset='utf-8'>" +
            "<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; " +
            "style-src 'unsafe-inline'; img-src data:; font-src 'none'; " +
            "connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'\">" +
            "<style>html,body{height:100%;margin:0;background:white;overflow:auto}" +
            "body{display:flex;align-items:center;justify-content:center}" +
            "body>svg{max-width:100%;max-height:100%;width:auto;height:auto}</style>" +
            "</head><body>" + svg + "</body></html>";
    }

    public static ImageSource Thumbnail(string path)
    {
        try { return new BitmapImage(new Uri(GetThumbnailPath(path))) { DecodePixelWidth = 480 }; }
        catch { return new SvgImageSource(new Uri(path)); }
    }

    public static void Invalidate(string path)
    {
        var file = new FileInfo(path);
        var cache = Path.Combine(file.DirectoryName!, "metadata", "previews");
        if (!Directory.Exists(cache)) return;
        foreach (var preview in Directory.EnumerateFiles(cache, file.Name + ".*.png"))
            File.Delete(preview);
    }

    private static string GetThumbnailPath(string path)
    {
        var file = new FileInfo(path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks)))[..16];
        var cache = Path.Combine(file.DirectoryName!, "metadata", "previews");
        var preview = Path.Combine(cache, file.Name + "." + key + ".png");
        if (File.Exists(preview)) return preview;
        Directory.CreateDirectory(cache);
        using var svg = new SKSvg();
        if (svg.Load(path) is null || svg.Picture is null)
            throw new InvalidDataException("无法绘制 SVG 缩略图。");
        var bounds = svg.Picture.CullRect;
        var scale = Math.Clamp(480f / Math.Max(1f, Math.Max(bounds.Width, bounds.Height)), 0.1f, 4f);
        var pending = preview + ".pending";
        try
        {
            using (var output = File.Create(pending))
                svg.Save(output, SKColors.White, SKEncodedImageFormat.Png, 100, scale, scale);
            File.Move(pending, preview, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return preview;
    }
}
