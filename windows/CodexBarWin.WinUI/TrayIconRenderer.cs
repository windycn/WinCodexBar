using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.IO;
using System.Drawing.Imaging;

namespace CodexBarWin.WinUI;

internal readonly record struct TrayIconStyleOption(string Id, string Label, string Description);

internal static class TrayIconRenderer
{
    private static readonly TrayIconStyleOption[] StyleOptionsValue =
    {
        new("ring", "单额度环", "用圆环显示当前账号的 5 小时额度"),
        new("dual", "双额度环", "外环显示 5 小时，内环显示 7 天额度"),
        new("percent", "剩余数字", "显示当前 5 小时额度的剩余百分比"),
        new("status", "额度状态灯", "按 5 小时与 7 天中的较高用量显示颜色"),
        new("bars", "双额度条", "并排显示 5 小时与 7 天额度进度"),
        new("ringpercent", "圆环数字", "外圈显示 5 小时已用进度，中间显示剩余百分比"),
        new("numberbars", "数字横条", "顶部显示 5 小时剩余数字，下方显示 5 小时与 7 天用量横条"),
        new("badge", "醒目数字", "用状态色徽章和白色大数字显示剩余额度"),
        new("dotnumber", "状态点数字", "左侧显示额度状态，右侧显示 5 小时剩余数字"),
        new("pillring", "深色圆角数字环", "深色圆角底，白色数字芯，外圈显示 5 小时已用进度"),
        new("classic", "经典图标", "固定显示 WinCodexBar 应用图标"),
    };

    public static IReadOnlyList<TrayIconStyleOption> StyleOptions => StyleOptionsValue;

    public static bool IsSupported(string? style) => StyleOptionsValue.Any(option => option.Id == style);

    public static Bitmap Render(string style, double? primaryUsed, double? secondaryUsed, bool hasActiveAccount, int size = 128)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.ScaleTransform(size / 32f, size / 32f);

        var primaryKnown = IsUsageValue(primaryUsed);
        var secondaryKnown = IsUsageValue(secondaryUsed);
        var primary = primaryKnown ? Clamp(primaryUsed!.Value) : 0d;
        var secondary = secondaryKnown ? Clamp(secondaryUsed!.Value) : 0d;
        // Some plans expose only a weekly window. Numeric styles must not show
        // a dash while valid quota data is available in the other window.
        var displayKnown = primaryKnown || secondaryKnown;
        var displayUsed = primaryKnown ? primary : secondary;
        var mainColor = primaryKnown ? UsageColor(primary) : secondaryKnown ? UsageColor(secondary) : Muted;
        var track = hasActiveAccount ? Color.FromArgb(108, 124, 142) : Color.FromArgb(134, 145, 158);

        switch (style)
        {
            case "percent":
                DrawPercent(graphics, primaryKnown || secondaryKnown, primaryKnown ? primary : secondary, mainColor);
                break;
            case "dual":
                DrawRing(graphics, new RectangleF(1.8f, 1.8f, 28.4f, 28.4f), 4.3f, primary, primaryKnown, mainColor, track);
                DrawRing(graphics, new RectangleF(10.6f, 10.6f, 10.8f, 10.8f), 3.1f, secondary, secondaryKnown,
                    secondaryKnown ? Color.FromArgb(0, 120, 212) : Muted, Color.FromArgb(128, 150, 170));
                break;
            case "status":
                DrawStatus(graphics, primary, primaryKnown, secondary, secondaryKnown, hasActiveAccount);
                break;
            case "bars":
                DrawBars(graphics, primary, primaryKnown, secondary, secondaryKnown, hasActiveAccount);
                break;
            case "ringpercent":
                DrawRingPercent(graphics, displayUsed, displayKnown, mainColor, track);
                break;
            case "numberbars":
                DrawNumberBars(graphics, displayUsed, displayKnown, primary, primaryKnown, secondary, secondaryKnown);
                break;
            case "badge":
                DrawBadge(graphics, displayUsed, displayKnown, mainColor);
                break;
            case "dotnumber":
                DrawDotNumber(graphics, displayUsed, displayKnown, secondary, secondaryKnown);
                break;
            case "pillring":
                DrawPillRing(graphics, primaryKnown ? primary : secondary, primaryKnown || secondaryKnown,
                    primaryKnown ? mainColor : secondaryKnown ? UsageColor(secondary) : Muted);
                break;
            default:
                DrawRing(graphics, new RectangleF(1.8f, 1.8f, 28.4f, 28.4f), 5.2f, displayUsed, displayKnown, mainColor, track);
                using (var center = new SolidBrush(Color.FromArgb(242, 248, 251, 255)))
                {
                    graphics.FillEllipse(center, 11f, 11f, 10f, 10f);
                }
                break;
        }

        return bitmap;
    }

    public static Bitmap RenderPreview(string style)
    {
        if (style == "classic")
        {
            using var icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinCodexBar.ico"));
            return icon.ToBitmap();
        }

        return Render(style, 63, 38, true);
    }

    // The notification area requests different pixel sizes at different DPI scales. A single
    // bitmap ICO gets scaled by Explorer and loses the small text and thin progress strokes.
    public static void WriteIcon(Stream output, string style, double? primary, double? secondary, bool active)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128 };
        var images = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var source = Render(style, primary, secondary, active, size * 4);
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            images.Add(png.ToArray());
        }
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + sizes.Length * 16;
        for (var index = 0; index < sizes.Length; index++)
        {
            writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
            writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)images[index].Length);
            writer.Write((uint)offset);
            offset += images[index].Length;
        }
        foreach (var image in images) writer.Write(image);
    }

    public static string CacheKey(string style, double? primary, double? secondary, bool active)
    {
        static string Value(double? value) => IsUsageValue(value) ? Clamp(value!.Value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "?";
        return style switch
        {
            "classic" => "classic",
            "dual" or "bars" or "numberbars" or "dotnumber" => $"{style}:{Value(primary)}:{Value(secondary)}:{active}",
            "status" => $"status:{Value(primary)}:{Value(secondary)}:{active}",
            _ => $"{style}:{Value(primary ?? secondary)}:{active}",
        };
    }

    private static void DrawRing(Graphics graphics, RectangleF bounds, float width, double value, bool known, Color color, Color track)
    {
        using var trackPen = new Pen(track, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(trackPen, bounds, -90, 359.5f);
        if (!known || value <= 0.25) return;
        using var progress = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(progress, bounds, -90, (float)(359.5d * value / 100d));
    }

    private static void DrawPercent(Graphics graphics, bool known, double used, Color color)
    {
        using var background = new SolidBrush(Color.FromArgb(249, 252, 255));
        using var edge = new Pen(Color.FromArgb(87, 107, 126), 1.7f);
        graphics.FillEllipse(background, 1f, 1f, 30f, 30f);
        graphics.DrawEllipse(edge, 1f, 1f, 30f, 30f);
        var remaining = known ? Math.Clamp((int)Math.Round(100 - used), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, remaining, known ? DigitColor(used) : Muted, new RectangleF(2, 2, 28, 27), 17f);
    }

    private static void DrawRingPercent(Graphics graphics, double used, bool known, Color color, Color track)
    {
        DrawRing(graphics, new RectangleF(1.1f, 1.1f, 29.8f, 29.8f), 2.9f, used, known, color, track);
        using var center = new SolidBrush(Color.FromArgb(248, 250, 253));
        graphics.FillEllipse(center, 4f, 4f, 24f, 24f);
        var label = known ? Math.Clamp((int)Math.Round(100 - used), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, label, known ? DigitColor(used) : Muted, new RectangleF(4.1f, 5f, 23.8f, 22f), 19f);
    }

    private static void DrawNumberBars(Graphics graphics, double displayUsed, bool displayKnown,
        double primary, bool primaryKnown, double secondary, bool secondaryKnown)
    {
        using var panel = new SolidBrush(Color.FromArgb(245, 249, 253));
        using var edge = new Pen(Color.FromArgb(91, 110, 129), 1.2f);
        using (var path = RoundPath(new RectangleF(0.9f, 0.9f, 30.2f, 30.2f), 5.5f))
        {
            graphics.FillPath(panel, path);
            graphics.DrawPath(edge, path);
        }
        var label = displayKnown ? Math.Clamp((int)Math.Round(100 - displayUsed), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, label, displayKnown ? DigitColor(displayUsed) : Muted, new RectangleF(2.5f, 1, 27, 18), 18f);
        DrawBar(graphics, 4.5f, 20.5f, primary, primaryKnown, UsageColor(primary), Color.FromArgb(140, 157, 175));
        DrawBar(graphics, 4.5f, 26f, secondary, secondaryKnown, Color.FromArgb(0, 120, 212), Color.FromArgb(140, 157, 175));
    }

    private static void DrawBadge(Graphics graphics, double used, bool known, Color color)
    {
        using var disc = new SolidBrush(known ? color : Muted);
        graphics.FillEllipse(disc, 1.2f, 1.2f, 29.6f, 29.6f);
        var label = known ? Math.Clamp((int)Math.Round(100 - used), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, label, Color.White, new RectangleF(2, 2, 28, 28), 21f);
    }

    private static void DrawDotNumber(Graphics graphics, double primary, bool primaryKnown, double secondary, bool secondaryKnown)
    {
        var color = primaryKnown || secondaryKnown ? UsageColor(Math.Max(primaryKnown ? primary : 0, secondaryKnown ? secondary : 0)) : Muted;
        using var dot = new SolidBrush(color);
        graphics.FillEllipse(dot, 0.6f, 11.5f, 9f, 9f);
        var label = primaryKnown ? Math.Clamp((int)Math.Round(100 - primary), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, label, primaryKnown ? DigitColor(primary) : Muted, new RectangleF(9f, 2f, 23f, 28f), 20f);
    }

    private static void DrawPillRing(Graphics graphics, double used, bool known, Color color)
    {
        using var panel = new SolidBrush(Color.FromArgb(37, 40, 46));
        using var edge = new Pen(Color.FromArgb(76, 83, 91), 0.7f);
        using (var path = RoundPath(new RectangleF(0.3f, 0.3f, 31.4f, 31.4f), 7f))
        {
            graphics.FillPath(panel, path);
            graphics.DrawPath(edge, path);
        }
        DrawRing(graphics, new RectangleF(2.2f, 2.2f, 27.6f, 27.6f), 2.8f, used, known,
            color, Color.FromArgb(75, 89, 96));
        using var center = new SolidBrush(Color.FromArgb(253, 254, 255));
        graphics.FillEllipse(center, 4.6f, 4.6f, 22.8f, 22.8f);
        var label = known ? Math.Clamp((int)Math.Round(100 - used), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        DrawNumberGlyph(graphics, label, known ? DigitColor(used) : Muted, new RectangleF(4.7f, 5f, 22.6f, 22f), 18f);
    }

    private static void DrawNumberGlyph(Graphics graphics, string label, Color color, RectangleF bounds, float size)
    {
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        path.AddString(label, family, (int)FontStyle.Bold, size, new PointF(0, 0), format);
        var ink = path.GetBounds();
        if (ink.Width <= 0 || ink.Height <= 0) return;
        var fit = Math.Min(1f, Math.Min((bounds.Width - 1f) / ink.Width, (bounds.Height - 1f) / ink.Height));
        using var transform = new Matrix();
        transform.Translate(-ink.Left, -ink.Top);
        transform.Scale(fit, fit, MatrixOrder.Append);
        transform.Translate(bounds.Left + (bounds.Width - ink.Width * fit) / 2f,
            bounds.Top + (bounds.Height - ink.Height * fit) / 2f, MatrixOrder.Append);
        path.Transform(transform);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static void DrawStatus(Graphics graphics, double primary, bool primaryKnown, double secondary, bool secondaryKnown, bool active)
    {
        var status = Math.Max(primaryKnown ? primary : 0, secondaryKnown ? secondary : 0);
        var known = primaryKnown || secondaryKnown;
        var color = known ? UsageColor(status) : Muted;
        using var outer = new SolidBrush(Color.FromArgb(235, 241, 247));
        using var edge = new Pen(active ? Color.FromArgb(143, 159, 178) : Color.FromArgb(177, 187, 199), 1f);
        using var center = new SolidBrush(color);
        graphics.FillEllipse(outer, 2.5f, 2.5f, 27f, 27f);
        graphics.DrawEllipse(edge, 2.5f, 2.5f, 27f, 27f);
        graphics.FillEllipse(center, 8f, 8f, 16f, 16f);
        using var gleam = new SolidBrush(Color.FromArgb(65, 255, 255, 255));
        graphics.FillEllipse(gleam, 10.5f, 9.2f, 8.5f, 4.2f);
    }

    private static void DrawBars(Graphics graphics, double primary, bool primaryKnown, double secondary, bool secondaryKnown, bool active)
    {
        var panelColor = active ? Color.FromArgb(243, 247, 251) : Color.FromArgb(231, 236, 242);
        using var panel = new SolidBrush(panelColor);
        using var edge = new Pen(Color.FromArgb(91, 110, 129), 1.2f);
        FillRound(graphics, panel, new RectangleF(0.9f, 2f, 30.2f, 28f), 5f);
        using (var path = RoundPath(new RectangleF(0.9f, 2f, 30.2f, 28f), 5f)) graphics.DrawPath(edge, path);

        DrawBar(graphics, 4.5f, 10f, primary, primaryKnown, UsageColor(primary), Color.FromArgb(140, 157, 175));
        DrawBar(graphics, 4.5f, 20f, secondary, secondaryKnown, Color.FromArgb(0, 120, 212), Color.FromArgb(140, 157, 175));
    }

    private static void DrawBar(Graphics graphics, float x, float y, double value, bool known, Color color, Color track)
    {
        var bounds = new RectangleF(x, y, 23f, 4f);
        using var background = new SolidBrush(track);
        FillRound(graphics, background, bounds, 2f);
        if (!known || value <= 0) return;
        using var foreground = new SolidBrush(color);
        FillRound(graphics, foreground, new RectangleF(x, y, Math.Max(2f, bounds.Width * (float)value / 100f), bounds.Height), 2f);
    }

    private static void FillRound(Graphics graphics, Brush brush, RectangleF rect, float radius)
    {
        using var path = RoundPath(rect, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool IsUsageValue(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;
    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
    private static readonly Color Muted = Color.FromArgb(132, 143, 156);
    private static Color UsageColor(double used) => used >= 90 ? Color.FromArgb(220, 38, 38) : used >= 70 ? Color.FromArgb(245, 124, 0) : Color.FromArgb(0, 166, 90);
    private static Color DigitColor(double used) => used >= 90 ? Color.FromArgb(170, 27, 38) : used >= 70 ? Color.FromArgb(166, 76, 0) : Color.FromArgb(0, 107, 64);
}
