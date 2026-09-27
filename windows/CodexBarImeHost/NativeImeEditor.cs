namespace CodexBarImeHost;

public static class NativeImeEditor
{
    public static string? Open(nint owner, string text, string title)
    {
        using var dialog = new System.Windows.Forms.Form
        {
            Text = title, StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            Size = new System.Drawing.Size(720, 520), MinimumSize = new System.Drawing.Size(460, 320),
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 11),
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable, ShowInTaskbar = false
        };
        var editor = new System.Windows.Forms.TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill, Text = text
        };
        var buttons = new System.Windows.Forms.FlowLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Padding = new System.Windows.Forms.Padding(8, 7, 8, 0)
        };
        var apply = new System.Windows.Forms.Button
        {
            Text = "应用到输入框", AutoSize = true, DialogResult = System.Windows.Forms.DialogResult.OK
        };
        var cancel = new System.Windows.Forms.Button
        {
            Text = "取消", AutoSize = true, DialogResult = System.Windows.Forms.DialogResult.Cancel
        };
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        var layout = new System.Windows.Forms.TableLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Fill, Padding = new System.Windows.Forms.Padding(12),
            RowCount = 2, ColumnCount = 1
        };
        layout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 56));
        layout.Controls.Add(editor, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => editor.Focus();
        editor.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != System.Windows.Forms.Keys.Enter) return;
            dialog.DialogResult = System.Windows.Forms.DialogResult.OK;
            e.SuppressKeyPress = true;
        };
        return dialog.ShowDialog(new NativeOwner(owner)) == System.Windows.Forms.DialogResult.OK ? editor.Text : null;
    }

    private sealed class NativeOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
