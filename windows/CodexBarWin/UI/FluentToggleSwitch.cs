using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal sealed class FluentToggleSwitch : Control
{
    private bool _checked;
    private bool _hovered;

    public event EventHandler? CheckedChanged;

    public FluentToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        Height = 28;
        Width = 180;
        Cursor = Cursors.Hand;
        Font = FluentTheme.TextFontPx(14);
        ForeColor = FluentTheme.TextPrimary;
        BackColor = Color.Transparent;
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0.5f, 4.5f, 38, 20);
        using (var path = FluentTheme.RoundedRectanglePath(track, 10))
        using (var fill = new SolidBrush(Checked ? (_hovered ? FluentTheme.AccentHover : FluentTheme.Accent) : Color.FromArgb(232, 232, 232)))
        using (var stroke = new Pen(Checked ? FluentTheme.Accent : FluentTheme.StrokeSecondary))
        {
            g.FillPath(fill, path);
            g.DrawPath(stroke, path);
        }

        var knobX = Checked ? 20 : 3;
        using (var knob = new SolidBrush(Color.White))
        {
            g.FillEllipse(knob, knobX, 7, 14, 14);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            new Rectangle(48, 0, Math.Max(0, Width - 48), Height),
            Enabled ? ForeColor : FluentTheme.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
