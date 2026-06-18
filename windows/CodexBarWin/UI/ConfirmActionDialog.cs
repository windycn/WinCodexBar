using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexBarWin.UI;

public sealed class ConfirmActionDialog : Form
{
    public ConfirmActionDialog(string title, string message, string confirmText = "确认")
    {
        Text = title;
        Font = FluentTheme.TextFontPx(13);
        BackColor = FluentTheme.LayerBackground;
        Width = 420;
        Height = 210;
        MinimumSize = new Size(380, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AppIconProvider.Apply(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(22),
            RowCount = 3,
            ColumnCount = 1,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = FluentTheme.TextFontPx(18, FontStyle.Bold),
            ForeColor = FluentTheme.TextPrimary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            ForeColor = FluentTheme.TextSecondary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.TopLeft,
            AutoSize = false,
        }, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 3,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        root.Controls.Add(footer, 0, 2);

        var cancel = new Button { Text = "取消" };
        ConfigureButton(cancel, primary: false);
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        footer.Controls.Add(cancel, 1, 0);

        var confirm = new Button { Text = confirmText };
        ConfigureButton(confirm, primary: true);
        confirm.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        footer.Controls.Add(confirm, 2, 0);

        AcceptButton = confirm;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MicaSupport.TryApply(this, transient: true);
    }

    private static void ConfigureButton(Button button, bool primary)
    {
        button.Width = primary ? 100 : 80;
        button.Height = 32;
        button.Margin = new Padding(0, 5, 10, 0);
        button.Font = FluentTheme.TextFontPx(13, primary ? FontStyle.Bold : FontStyle.Regular);
        FluentTheme.ApplyButton(button, primary);
    }
}
