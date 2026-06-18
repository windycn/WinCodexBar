using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexBarWin.UI;

/// <summary>
/// 集中管理 WinUI Light（Fluent）调色板与按钮样式。
/// </summary>
internal static class FluentTheme
{
    // 字体族
    public static readonly string TextFontFamily = ResolveTextFontFamily();
    public static readonly string IconFontFamily = ResolveIconFontFamily();

    // 文本
    public static readonly Color TextPrimary = Color.FromArgb(31, 31, 31);          // TextFillColorPrimary
    public static readonly Color TextSecondary = Color.FromArgb(96, 96, 96);        // TextFillColorSecondary
    public static readonly Color TextTertiary = Color.FromArgb(143, 143, 143);      // TextFillColorTertiary
    public static readonly Color TextOnAccent = Color.White;

    // 背景与表面
    public static readonly Color LayerBackground = Color.FromArgb(243, 243, 243);   // SolidBackgroundFillColorBase
    public static readonly Color CardBackground = Color.FromArgb(252, 252, 252);    // CardBackgroundFillColorDefault
    public static readonly Color SubtleBackground = Color.FromArgb(248, 248, 248);
    public static readonly Color StrokeDefault = Color.FromArgb(229, 229, 229);     // ControlStrokeColorDefault
    public static readonly Color StrokeSecondary = Color.FromArgb(214, 214, 214);

    // Accent
    public static readonly Color Accent = Color.FromArgb(0, 103, 192);              // AccentFillColorDefault
    public static readonly Color AccentHover = Color.FromArgb(0, 91, 178);
    public static readonly Color AccentPressed = Color.FromArgb(0, 79, 158);

    // 按钮（中性）
    public static readonly Color ControlBackground = Color.FromArgb(251, 251, 251);
    public static readonly Color ControlBackgroundHover = Color.FromArgb(243, 243, 243);
    public static readonly Color ControlBackgroundPressed = Color.FromArgb(235, 235, 235);

    // 状态色
    public static readonly Color Success = Color.FromArgb(15, 123, 15);
    public static readonly Color Warning = Color.FromArgb(157, 93, 0);
    public static readonly Color Critical = Color.FromArgb(196, 49, 75);

    // 阴影色
    public static readonly Color Shadow = Color.FromArgb(28, 0, 0, 0);
    public static readonly Color ShadowSoft = Color.FromArgb(14, 0, 0, 0);

    /// <summary>
    /// 对一个 WinForms <see cref="Button"/> 应用 Fluent 风格，分主按钮和次按钮。
    /// </summary>
    public static void ApplyButton(Button button, bool primary = false)
    {
        if (button is FluentButton fluentButton)
        {
            fluentButton.Primary = primary;
            fluentButton.Font ??= TextFont(13f, FontStyle.Regular);
            fluentButton.BackColor = primary ? Accent : ControlBackground;
            fluentButton.ForeColor = primary ? TextOnAccent : TextPrimary;
            fluentButton.Invalidate();
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Font ??= TextFont(13f, FontStyle.Regular);
        if (primary)
        {
            button.BackColor = Accent;
            button.ForeColor = TextOnAccent;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentPressed;
        }
        else
        {
            button.BackColor = ControlBackground;
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = StrokeDefault;
            button.FlatAppearance.MouseOverBackColor = ControlBackgroundHover;
            button.FlatAppearance.MouseDownBackColor = ControlBackgroundPressed;
        }

        button.FlatAppearance.BorderSize = 1;
        button.Cursor = Cursors.Hand;
    }

    /// <summary>
    /// 创建文本字体（Segoe UI Variable Text 优先，回落 Segoe UI / Microsoft YaHei UI）。
    /// </summary>
    public static Font TextFont(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(TextFontFamily, size, style);
    }

    public static Font TextFontPx(float pixelSize, FontStyle style = FontStyle.Regular)
    {
        return new Font(TextFontFamily, pixelSize, style, GraphicsUnit.Pixel);
    }

    public static Font IconFontPx(float pixelSize)
    {
        return new Font(IconFontFamily, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>
    /// 在卡片下面绘制 WinUI Light 风格的浅投影。
    /// </summary>
    public static void DrawCardShadow(Graphics graphics, RectangleF cardBounds, float radius)
    {
        var prevSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var inflate = i + 1;
                var rect = RectangleF.Inflate(cardBounds, inflate, inflate);
                rect.Y += inflate;
                using var pen = new Pen(Color.FromArgb(10 + 2 * (3 - i), 0, 0, 0), 1f);
                using var path = RoundedRectanglePath(rect, radius + inflate);
                graphics.DrawPath(pen, path);
            }
        }
        finally
        {
            graphics.SmoothingMode = prevSmoothing;
        }
    }

    public static GraphicsPath RoundedRectanglePath(RectangleF rect, float radius)
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

    private static string ResolveTextFontFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", "Microsoft YaHei UI" })
        {
            if (FontFamilyExists(name)) return name;
        }
        return FontFamily.GenericSansSerif.Name;
    }

    private static string ResolveIconFontFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            if (FontFamilyExists(name)) return name;
        }
        return "Segoe UI Symbol";
    }

    private static bool FontFamilyExists(string name)
    {
        try
        {
            using var family = new FontFamily(name);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
