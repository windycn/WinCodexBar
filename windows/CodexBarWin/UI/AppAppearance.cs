namespace CodexBarWin.UI;

public static class AppAppearance
{
    // 0 跟随显示器；其他值为用户选择的绝对缩放百分比。
    public static int ScalePercent { get; set; }
    public static void ScaleTree(Control root, float factor)
    {
        if (Math.Abs(factor - 1f) < 0.001f) return;
        static IEnumerable<Control> Tree(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Tree));
        // Control.Scale 调整几何，不保证显式指定的字体随之变化；先取快照避免继承字体重复放大。
        var fonts = Tree(root).Select(c => (Control: c, Font: c.Font)).ToArray();
        root.Scale(new SizeF(factor, factor));
        foreach (var item in fonts)
            item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * factor, item.Font.Style, item.Font.Unit);
        // Scale 递归时子控件可能已被父布局调整，随后又被放大。最后按父到子的顺序重新 Dock。
        foreach (var item in fonts) item.Control.PerformLayout();
    }

    public static float ScaleFor(Control control) => ScalePercent == 0 ? control.DeviceDpi / 96f : ScalePercent / 100f;
}
