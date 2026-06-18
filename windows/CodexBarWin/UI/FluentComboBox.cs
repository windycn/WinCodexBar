using System.Drawing;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal sealed class FluentComboBox : ComboBox
{
    public FluentComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDown;
        BackColor = Color.White;
        ForeColor = FluentTheme.TextPrimary;
        ItemHeight = 24;
        Cursor = Cursors.Hand;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using var fill = new SolidBrush(selected ? Color.FromArgb(230, 240, 252) : Color.White);
        e.Graphics.FillRectangle(fill, e.Bounds);

        var text = GetItemText(Items[e.Index]);
        var color = selected ? FluentTheme.Accent : FluentTheme.TextPrimary;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height),
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
