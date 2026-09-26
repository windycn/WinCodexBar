using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CodexBarWin.UI;

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
        new("classic", "经典图标", "固定显示 WinCodexBar 应用图标"),
    };

    public static IReadOnlyList<TrayIconStyleOption> StyleOptions => StyleOptionsValue;

    public static bool IsSupported(string? style) => StyleOptionsValue.Any(option => option.Id == style);

    public static Bitmap Render(string style, double? primaryUsed, double? secondaryUsed, bool hasActiveAccount, int size = 64)
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
        var mainColor = primaryKnown ? UsageColor(primary) : secondaryKnown ? UsageColor(secondary) : Muted;
        var track = hasActiveAccount ? Color.FromArgb(217, 225, 235) : Color.FromArgb(194, 202, 212);

        switch (style)
        {
            case "percent":
                DrawPercent(graphics, primaryKnown || secondaryKnown, primaryKnown ? primary : secondary, mainColor);
                break;
            case "dual":
                DrawRing(graphics, new RectangleF(4.5f, 4.5f, 23f, 23f), 3.6f, primary, primaryKnown, mainColor, track);
                DrawRing(graphics, new RectangleF(11.4f, 11.4f, 9.2f, 9.2f), 2.7f, secondary, secondaryKnown,
                    secondaryKnown ? Color.FromArgb(0, 120, 212) : Muted, Color.FromArgb(222, 229, 237));
                break;
            case "status":
                DrawStatus(graphics, primary, primaryKnown, secondary, secondaryKnown, hasActiveAccount);
                break;
            case "bars":
                DrawBars(graphics, primary, primaryKnown, secondary, secondaryKnown, hasActiveAccount);
                break;
            default:
                DrawRing(graphics, new RectangleF(6.25f, 6.25f, 19.5f, 19.5f), 5.2f, primary, primaryKnown, mainColor, track);
                using (var center = new SolidBrush(Color.FromArgb(242, 248, 251, 255)))
                {
                    graphics.FillEllipse(center, 11.5f, 11.5f, 9f, 9f);
                }
                break;
        }

        return bitmap;
    }

    public static Bitmap RenderPreview(string style)
    {
        if (style == "classic")
        {
            using var icon = AppIconProvider.CreateWindowIcon() ?? (Icon)SystemIcons.Application.Clone();
            return icon.ToBitmap();
        }

        return Render(style, 63, 38, true);
    }

    public static string CacheKey(string style, double? primary, double? secondary, bool active)
    {
        static string Value(double? value) => IsUsageValue(value) ? Clamp(value!.Value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "?";
        return style switch
        {
            "classic" => "classic",
            "dual" or "bars" => $"{style}:{Value(primary)}:{Value(secondary)}:{active}",
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
        using var background = new SolidBrush(Color.FromArgb(244, 248, 252));
        using var edge = new Pen(Color.FromArgb(206, 216, 228), 1f);
        graphics.FillEllipse(background, 1.5f, 1.5f, 29f, 29f);
        graphics.DrawEllipse(edge, 1.5f, 1.5f, 29f, 29f);
        using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        var remaining = known ? Math.Clamp((int)Math.Round(100 - used), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "–";
        graphics.DrawString(remaining, font, brush, new RectangleF(2, 2, 28, 27), format);
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
        using var edge = new Pen(Color.FromArgb(200, 211, 224), 1f);
        FillRound(graphics, panel, new RectangleF(2.5f, 4.5f, 27f, 23f), 5f);
        using (var path = RoundPath(new RectangleF(2.5f, 4.5f, 27f, 23f), 5f)) graphics.DrawPath(edge, path);

        DrawBar(graphics, 7f, 11f, primary, primaryKnown, UsageColor(primary), Color.FromArgb(211, 220, 231));
        DrawBar(graphics, 7f, 19f, secondary, secondaryKnown, Color.FromArgb(0, 120, 212), Color.FromArgb(211, 220, 231));
    }

    private static void DrawBar(Graphics graphics, float x, float y, double value, bool known, Color color, Color track)
    {
        var bounds = new RectangleF(x, y, 18f, 3f);
        using var background = new SolidBrush(track);
        FillRound(graphics, background, bounds, 1.5f);
        if (!known || value <= 0) return;
        using var foreground = new SolidBrush(color);
        FillRound(graphics, foreground, new RectangleF(x, y, Math.Max(2f, bounds.Width * (float)value / 100f), bounds.Height), 1.5f);
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
}

internal sealed class TrayIconStylePreviewStrip : Control
{
    private readonly Dictionary<string, Bitmap> _icons = new(StringComparer.Ordinal);
    private readonly ToolTip _toolTip = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 3500, ShowAlways = true };
    private string _selectedStyle;
    private string? _visibleTip;
    public event Action<string>? StyleSelected;

    public string SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (_selectedStyle == value) return;
            _selectedStyle = value;
            Invalidate();
        }
    }

    public TrayIconStylePreviewStrip(string selectedStyle)
    {
        _selectedStyle = selectedStyle;
        Height = 82;
        MinimumSize = new Size(0, 56);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var options = TrayIconRenderer.StyleOptions;
        var columnWidth = ClientSize.Width / (float)options.Count;
        var scale = AppAppearance.ScaleFor(this);
        using var labelFont = FluentTheme.TextFontPx(10 * scale, FontStyle.Bold);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var item = new RectangleF(index * columnWidth + 2, 1, Math.Max(1, columnWidth - 4), Math.Max(1, Height - 2));
            var selected = string.Equals(_selectedStyle, option.Id, StringComparison.Ordinal);
            using (var fill = new SolidBrush(selected ? Color.FromArgb(230, 241, 252) : FluentTheme.CardBackground))
            using (var edge = new Pen(selected ? FluentTheme.Accent : FluentTheme.StrokeDefault, selected ? 1.3f : 1f))
            using (var path = FluentTheme.RoundedRectanglePath(item, 6))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }

            var iconSize = Math.Min(34 * scale, Math.Max(22, Height - 34 * scale));
            var iconBounds = new RectangleF(item.Left + (item.Width - iconSize) / 2, item.Top + 3 * scale, iconSize, iconSize);
            var bitmap = PreviewIcon(option.Id);
            e.Graphics.DrawImage(bitmap, iconBounds);
            var textBounds = new RectangleF(item.Left + 2, item.Bottom - 23 * scale, item.Width - 4, 18 * scale);
            TextRenderer.DrawText(e.Graphics, option.Label, labelFont, Rectangle.Round(textBounds),
                selected ? FluentTheme.Accent : FluentTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var options = TrayIconRenderer.StyleOptions;
        var index = ClientSize.Width <= 0 ? -1 : Math.Clamp((int)(e.X / (ClientSize.Width / (float)options.Count)), 0, options.Count - 1);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        var tip = index < 0 ? null : options[index].Description;
        if (tip == _visibleTip) return;
        _visibleTip = tip;
        _toolTip.Hide(this);
        if (tip is not null) _toolTip.Show(tip, this, e.Location.X + 10, e.Location.Y + 14);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _visibleTip = null;
        Cursor = Cursors.Default;
        _toolTip.Hide(this);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || ClientSize.Width <= 0) return;
        var options = TrayIconRenderer.StyleOptions;
        var index = Math.Clamp((int)(e.X / (ClientSize.Width / (float)options.Count)), 0, options.Count - 1);
        StyleSelected?.Invoke(options[index].Id);
    }

    private Bitmap PreviewIcon(string style)
    {
        if (!_icons.TryGetValue(style, out var bitmap))
        {
            bitmap = TrayIconRenderer.RenderPreview(style);
            _icons.Add(style, bitmap);
        }
        return bitmap;
    }
}
