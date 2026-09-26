using CodexBarWin.Models;

namespace CodexBarWin.UI;

public sealed class AccountDetailsForm : AdaptiveForm
{
    public AccountDetailsForm(TokenAccount account, UsageDisplayMode mode)
    {
        Text = "账号额度与重置卡";
        Font = FluentTheme.TextFontPx(14);
        BackColor = FluentTheme.LayerBackground;
        Size = new Size(680, 600);
        MinimumSize = new Size(380, 300);
        StartPosition = FormStartPosition.CenterParent;
        AppIconProvider.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        InstallScrollableContent(root, Size.Empty);
        root.Controls.Add(new Label { Text = "额度与重置卡", Dock = DockStyle.Fill, Font = FluentTheme.TextFontPx(24, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var text = new TextBox
        {
            Text = AccountUsageHelpers.DetailsText(account, mode), Dock = DockStyle.Fill,
            ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = FluentTheme.CardBackground, ForeColor = FluentTheme.TextPrimary,
            Font = FluentTheme.TextFontPx(14), TabStop = true,
        };
        root.Controls.Add(text, 0, 1);
        var close = new FluentButton { Text = "关闭", Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0), DialogResult = DialogResult.Cancel };
        FluentTheme.ApplyButton(close, primary: true);
        root.Controls.Add(close, 0, 2);
        CancelButton = close;
    }
}
