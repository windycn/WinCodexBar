using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal sealed class FluentButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public bool Primary { get; set; }

    public int CornerRadius { get; set; } = 6;

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.BorderColor = FluentTheme.StrokeDefault;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleCenter;
        TabStop = false;
    }

    protected override bool ShowFocusCues => false;

    protected override void OnMouseEnter(System.EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(System.EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(System.EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var surface = new SolidBrush(ResolveSurfaceFill()))
        {
            g.FillRectangle(surface, ClientRectangle);
        }

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var fill = ResolveFill();
        var stroke = ResolveStroke();
        using (var path = FluentTheme.RoundedRectanglePath(bounds, CornerRadius))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(stroke))
        {
            g.FillPath(brush, path);
            if (stroke.A > 0)
            {
                g.DrawPath(pen, path);
            }
        }

        var textColor = Enabled
            ? Primary ? FluentTheme.TextOnAccent : ForeColor
            : FluentTheme.TextTertiary;
        var flags = TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor, flags);
    }

    private Color ResolveFill()
    {
        if (!Enabled)
        {
            return Color.FromArgb(244, 244, 244);
        }

        if (Primary)
        {
            return _pressed ? FluentTheme.AccentPressed : _hovered ? FluentTheme.AccentHover : FluentTheme.Accent;
        }

        if (BackColor.ToArgb() != SystemColors.Control.ToArgb()
            && BackColor.ToArgb() != Color.Empty.ToArgb()
            && BackColor.ToArgb() != Color.Transparent.ToArgb())
        {
            return _pressed
                ? Blend(BackColor, Color.Black, 0.06f)
                : _hovered ? Blend(BackColor, Color.White, 0.28f) : BackColor;
        }

        return _pressed
            ? FluentTheme.ControlBackgroundPressed
            : _hovered ? FluentTheme.ControlBackgroundHover : FluentTheme.ControlBackground;
    }

    private Color ResolveStroke()
    {
        if (!Enabled)
        {
            return Color.Transparent;
        }

        if (Primary)
        {
            return Color.Transparent;
        }

        return _hovered || _pressed ? Color.FromArgb(210, 210, 210) : Color.Transparent;
    }

    private Color ResolveSurfaceFill()
    {
        var color = Parent?.BackColor ?? FluentTheme.LayerBackground;
        if (color == Color.Transparent || color.A == 0)
        {
            color = FluentTheme.LayerBackground;
        }

        return color;
    }

    private static Color Blend(Color first, Color second, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        var r = (int)Math.Round(first.R + (second.R - first.R) * amount);
        var g = (int)Math.Round(first.G + (second.G - first.G) * amount);
        var b = (int)Math.Round(first.B + (second.B - first.B) * amount);
        return Color.FromArgb(first.A, r, g, b);
    }
}
