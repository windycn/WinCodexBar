using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexBarWin.UI;

/// <summary>
/// 模仿 WinUI Gallery 里 SettingsCard 的「左 icon + 标题/说明，右控件」布局。
/// </summary>
public sealed class SettingCard : Panel
{
    private readonly string _glyph;
    private readonly Label _titleLabel;
    private readonly MultilineEllipsisLabel _descriptionLabel;
    private Control? _action;

    public SettingCard(string glyph, string title, string description)
    {
        _glyph = glyph;
        BackColor = FluentTheme.CardBackground;
        Padding = new Padding(20, 16, 20, 16);
        Margin = new Padding(0, 0, 0, 12);
        Height = 124;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _titleLabel = new Label
        {
            Text = title,
            Font = FluentTheme.TextFontPx(16, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Bounds = new Rectangle(74, 16, 360, 28),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        _descriptionLabel = new MultilineEllipsisLabel
        {
            Text = description,
            Font = FluentTheme.TextFontPx(13),
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Bounds = new Rectangle(74, 48, 480, 58),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        Controls.Add(_titleLabel);
        Controls.Add(_descriptionLabel);
    }

    public Control? Action
    {
        get => _action;
        set
        {
            if (_action != null)
            {
                Controls.Remove(_action);
            }
            _action = value;
            if (_action != null)
            {
                _action.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                Controls.Add(_action);
                LayoutAction();
            }
        }
    }

    protected override void OnSizeChanged(System.EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_titleLabel is null || _descriptionLabel is null)
        {
            return;
        }

        var actionWidth = _action?.Width ?? 0;
        var stackedAction = _action is not null && (Width < 900 && actionWidth > 360);
        var textWidth = stackedAction
            ? Math.Max(220, Width - 98)
            : Math.Max(260, Width - 122 - actionWidth);

        _titleLabel.SetBounds(74, 16, textWidth, 28);
        var descriptionHeight = stackedAction && _action is not null
            ? Math.Max(32, Height - _action.Height - 70)
            : Math.Max(46, Height - 66);
        _descriptionLabel.SetBounds(74, 48, textWidth, descriptionHeight);
        LayoutAction();
    }

    private void LayoutAction()
    {
        if (_action == null) return;
        var stacked = Width < 900 && _action.Width > 360;
        var x = stacked ? 74 : Width - 24 - _action.Width;
        var y = stacked ? Height - _action.Height - 16 : (Height - _action.Height) / 2;
        _action.Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = FluentTheme.RoundedRectanglePath(bounds, 6f);
        using var bg = new SolidBrush(FluentTheme.CardBackground);
        g.FillPath(bg, path);
        using var pen = new Pen(FluentTheme.StrokeDefault, 1f);
        g.DrawPath(pen, path);

        // Fluent-style setting glyph tile.
        var tileRect = new RectangleF(18, (Height - 36) / 2f, 36, 36);
        using var tileBrush = new SolidBrush(Color.FromArgb(239, 246, 253));
        using var tilePen = new Pen(Color.FromArgb(218, 232, 246), 1f);
        g.FillEllipse(tileBrush, tileRect);
        g.DrawEllipse(tilePen, tileRect);

        var iconRect = new RectangleF(tileRect.Left, tileRect.Top + 0.5f, tileRect.Width, tileRect.Height);
        using var iconFont = FluentTheme.IconFontPx(18);
        using var iconBrush = new SolidBrush(Color.FromArgb(54, 92, 132));
        using var iconFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(_glyph, iconFont, iconBrush, iconRect, iconFormat);
    }

    private sealed class MultilineEllipsisLabel : Label
    {
        public MultilineEllipsisLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var flags = TextFormatFlags.Left
                | TextFormatFlags.Top
                | TextFormatFlags.WordBreak
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, BackColor, flags);
        }
    }
}
