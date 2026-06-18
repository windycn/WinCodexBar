using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal sealed class FluentNumberBox : UserControl
{
    private readonly TextBox _textBox = new();
    private decimal _value;
    private decimal _minimum;
    private decimal _maximum = 100;
    private decimal _increment = 1;
    private int _decimalPlaces;

    public event EventHandler? ValueChanged;

    public FluentNumberBox()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable, true);
        Width = 86;
        Height = 30;
        BackColor = Color.Transparent;
        Cursor = Cursors.IBeam;

        _textBox.BorderStyle = BorderStyle.None;
        _textBox.BackColor = Color.White;
        _textBox.ForeColor = FluentTheme.TextPrimary;
        _textBox.Font = FluentTheme.TextFontPx(12);
        _textBox.TextAlign = HorizontalAlignment.Left;
        _textBox.Leave += (_, _) => CommitText();
        _textBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitText();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                Step(1);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                Step(-1);
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(_textBox);
        UpdateText();
    }

    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            Value = _value;
        }
    }

    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            Value = _value;
        }
    }

    public decimal Increment
    {
        get => _increment;
        set => _increment = value <= 0 ? 1 : value;
    }

    public int DecimalPlaces
    {
        get => _decimalPlaces;
        set
        {
            _decimalPlaces = Math.Clamp(value, 0, 6);
            UpdateText();
        }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, _minimum, _maximum);
            next = decimal.Round(next, _decimalPlaces);
            if (_value == next)
            {
                UpdateText();
                return;
            }

            _value = next;
            UpdateText();
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutTextBox();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _textBox.Font = Font;
        LayoutTextBox();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var buttonWidth = Math.Min(24, Width / 3);
        if (e.X >= Width - buttonWidth)
        {
            Step(e.Y < Height / 2 ? 1 : -1);
            return;
        }

        _textBox.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Step(e.Delta > 0 ? 1 : -1);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = FluentTheme.RoundedRectanglePath(rect, 6))
        using (var fill = new SolidBrush(Color.White))
        using (var stroke = new Pen(Focused || _textBox.Focused ? FluentTheme.Accent : FluentTheme.StrokeDefault))
        {
            g.FillPath(fill, path);
            g.DrawPath(stroke, path);
        }

        var buttonX = Width - Math.Min(24, Width / 3);
        using var separator = new Pen(FluentTheme.StrokeDefault);
        g.DrawLine(separator, buttonX, 5, buttonX, Height - 5);

        using var glyph = new Pen(FluentTheme.TextSecondary, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        DrawChevron(g, glyph, new Rectangle(buttonX, 2, Width - buttonX, Height / 2 - 2), up: true);
        DrawChevron(g, glyph, new Rectangle(buttonX, Height / 2, Width - buttonX, Height / 2 - 2), up: false);
    }

    private static void DrawChevron(Graphics g, Pen pen, Rectangle rect, bool up)
    {
        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        var dx = 4f;
        var dy = 3f;
        if (up)
        {
            g.DrawLines(pen, new[] { new PointF(cx - dx, cy + dy / 2), new PointF(cx, cy - dy), new PointF(cx + dx, cy + dy / 2) });
        }
        else
        {
            g.DrawLines(pen, new[] { new PointF(cx - dx, cy - dy / 2), new PointF(cx, cy + dy), new PointF(cx + dx, cy - dy / 2) });
        }
    }

    private void Step(int direction)
    {
        CommitText(raiseOnInvalid: false);
        Value += direction * _increment;
    }

    private void CommitText(bool raiseOnInvalid = true)
    {
        if (decimal.TryParse(_textBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed)
            || decimal.TryParse(_textBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            Value = parsed;
            return;
        }

        if (raiseOnInvalid)
        {
            UpdateText();
        }
    }

    private void UpdateText()
    {
        _textBox.Text = _decimalPlaces == 0
            ? _value.ToString("0", CultureInfo.CurrentCulture)
            : _value.ToString("0." + new string('0', _decimalPlaces), CultureInfo.CurrentCulture);
    }

    private void LayoutTextBox()
    {
        var buttonWidth = Math.Min(24, Width / 3);
        _textBox.Location = new Point(10, Math.Max(4, (Height - _textBox.PreferredHeight) / 2));
        _textBox.Width = Math.Max(24, Width - buttonWidth - 14);
    }
}
