using CodexBarWin.Models;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.WinUI;

internal static class UsageColors
{
    public static readonly Brush Neutral = Brush(0, 95, 175);
    public static readonly Brush Healthy = Brush(0, 128, 78);
    public static readonly Brush Warning = Brush(174, 96, 0);
    public static readonly Brush Danger = Brush(190, 34, 48);

    public static readonly Brush NeutralSurface = Brush(240, 247, 255);
    public static readonly Brush HealthySurface = Brush(239, 251, 245);
    public static readonly Brush WarningSurface = Brush(255, 248, 235);
    public static readonly Brush DangerSurface = Brush(255, 242, 243);

    public static readonly Brush NeutralBorder = Brush(192, 218, 245);
    public static readonly Brush HealthyBorder = Brush(183, 225, 202);
    public static readonly Brush WarningBorder = Brush(238, 211, 162);
    public static readonly Brush DangerBorder = Brush(239, 190, 194);

    public static (Brush Accent, Brush Surface, Brush Border) For(AccountHealthStatus status) => status switch
    {
        AccountHealthStatus.Healthy => (Healthy, HealthySurface, HealthyBorder),
        AccountHealthStatus.Warning => (Warning, WarningSurface, WarningBorder),
        AccountHealthStatus.Exhausted or AccountHealthStatus.Suspended or AccountHealthStatus.TokenExpired
            => (Danger, DangerSurface, DangerBorder),
        _ => (Neutral, NeutralSurface, NeutralBorder)
    };

    public static Brush ForQuota(double usedPercent, double warningUsed, double dangerUsed) =>
        usedPercent >= dangerUsed ? Danger : usedPercent >= warningUsed ? Warning : Healthy;

    private static Brush Brush(byte red, byte green, byte blue) =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(255, red, green, blue));
}
