namespace CodexBarWin.UI;

public static class AppAppearance
{
    // 0 跟随显示器；其他值为用户选择的绝对缩放百分比。
    public static int ScalePercent { get; set; }
    public static float ScaleFor(Control control) => ScalePercent == 0 ? control.DeviceDpi / 96f : ScalePercent / 100f;
}
