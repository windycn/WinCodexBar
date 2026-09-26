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
    private bool _reflowing;
    public int LogicalMinimumHeight { get; set; } = 118;

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

        Reflow();
    }

    public void Reflow()
    {
        if (_titleLabel is null || _descriptionLabel is null || _reflowing) return;
        _reflowing = true;
        try
        {
        var scale = AppAppearance.ScaleFor(this);
        int D(float n) => (int)Math.Round(n * scale);
        var actionWidth = _action?.Width ?? 0;
        var stacked = _action is not null && Width < actionWidth + D(330);
        var requiredHeight = stacked ? D(114) + _action!.Height : D(118);
        Height = Math.Max(D(LogicalMinimumHeight), requiredHeight);
        var textWidth = Math.Max(1, Width - D(98) - (stacked ? 0 : actionWidth + D(24)));
        _titleLabel.SetBounds(D(74), D(16), textWidth, D(28));
        _descriptionLabel.SetBounds(D(74), D(48), textWidth, stacked ? D(48) : Math.Max(D(46), Height - D(66)));
        if (_action is not null)
        {
            _action.Location = new Point(stacked ? D(74) : Width - D(24) - actionWidth,
                stacked ? D(98) : (Height - _action.Height) / 2);
            _action.BringToFront();
        }
        }
        finally { _reflowing = false; }
    }

    private void LayoutAction() => Reflow();

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
