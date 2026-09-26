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
    private int _logicalActionWidth;
    private readonly Dictionary<Control, int> _actionWidths = new();
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
                _logicalActionWidth = _action.Width;
                _actionWidths.Clear();
                void Remember(Control control)
                {
                    _actionWidths[control] = control.Width;
                    foreach (Control child in control.Controls) Remember(child);
                }
                Remember(_action);
                _action.Anchor = AnchorStyles.Top | AnchorStyles.Left;
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
        var padding = D(20);
        var desiredActionWidth = D(_logicalActionWidth);
        var stacked = _action is not null && Width < desiredActionWidth + D(330);
        var textLeft = D(60);
        var textWidth = Math.Max(1, Width - textLeft - padding - (stacked ? 0 : desiredActionWidth + D(24)));
        var titleHeight = Math.Max(D(26), _titleLabel.Font.Height + D(4));
        var descriptionHeight = Math.Max(D(24), TextRenderer.MeasureText(_descriptionLabel.Text, _descriptionLabel.Font,
            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + D(4));
        _titleLabel.SetBounds(textLeft, D(16), textWidth, titleHeight);
        _descriptionLabel.SetBounds(textLeft, D(20) + titleHeight, textWidth, descriptionHeight);
        var textBottom = _descriptionLabel.Bottom;
        if (_action is not null)
        {
            FitAction(_action, Math.Max(1, Math.Min(desiredActionWidth, Width - padding * 2)));
            Height = Math.Max(D(LogicalMinimumHeight), stacked ? textBottom + D(16) + _action.Height + padding : Math.Max(textBottom + padding, _action.Height + padding * 2));
            _action.Location = new Point(stacked ? padding : Width - padding - _action.Width,
                stacked ? textBottom + D(16) : (Height - _action.Height) / 2);
            _action.BringToFront();
        }
        else Height = Math.Max(D(LogicalMinimumHeight), textBottom + padding);
        }
        finally { _reflowing = false; }
    }

    private void FitAction(Control action, int width)
    {
        action.Width = width;
        if (action is FlowLayoutPanel flow)
        {
            flow.WrapContents = true;
            foreach (Control child in flow.Controls)
            {
                var preferred = (int)Math.Round(_actionWidths.GetValueOrDefault(child, child.Width) * AppAppearance.ScaleFor(this));
                var available = Math.Max(1,width - child.Margin.Horizontal - flow.Padding.Horizontal);
                child.Width = Math.Min(preferred, available);
                if (child is FlowLayoutPanel or TableLayoutPanel) FitAction(child, child.Width);
            }
            flow.PerformLayout();
            action.Height = Math.Max(1, flow.Controls.Cast<Control>().Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max() + flow.Padding.Bottom);
        }
        else if (action is TableLayoutPanel table)
        {
            table.PerformLayout();
            var totalHeight = table.Padding.Vertical;
            for (var row = 0; row < table.RowCount; row++)
            {
                var height = 0;
                foreach (Control child in table.Controls.Cast<Control>().Where(c => table.GetRow(c) == row))
                {
                    FitAction(child, Math.Max(1,width - table.Padding.Horizontal - child.Margin.Horizontal));
                    height = Math.Max(height, child.Height + child.Margin.Vertical);
                }
                table.RowStyles[row].SizeType = SizeType.Absolute;
                table.RowStyles[row].Height = height;
                totalHeight += height;
            }
            table.Height = Math.Max(1,totalHeight);
        }
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
        var scale = AppAppearance.ScaleFor(this);
        var tileRect = new RectangleF(18 * scale, (Height - 36 * scale) / 2f, 36 * scale, 36 * scale);
        using var tileBrush = new SolidBrush(Color.FromArgb(239, 246, 253));
        using var tilePen = new Pen(Color.FromArgb(218, 232, 246), 1f);
        g.FillEllipse(tileBrush, tileRect);
        g.DrawEllipse(tilePen, tileRect);

        var iconRect = new RectangleF(tileRect.Left, tileRect.Top + 0.5f, tileRect.Width, tileRect.Height);
        using var iconFont = FluentTheme.IconFontPx(18 * scale);
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
