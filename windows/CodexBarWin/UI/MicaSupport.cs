using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal static class MicaSupport
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaMicaEffect = 1029;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmSbtMainWindow = 2;
    private const int DwmSbtTransientWindow = 3;

    public static void TryApply(Form form, bool transient = false)
    {
        if (form.FormBorderStyle == FormBorderStyle.None)
        {
            ApplyRoundedRegion(form);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        try
        {
            var lightMode = 0;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref lightMode, sizeof(int));

            var corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            var backdrop = transient ? DwmSbtTransientWindow : DwmSbtMainWindow;
            var result = DwmSetWindowAttribute(form.Handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            if (result != 0)
            {
                var enable = 1;
                _ = DwmSetWindowAttribute(form.Handle, DwmwaMicaEffect, ref enable, sizeof(int));
            }
        }
        catch
        {
            // Mica is an enhancement only. Older Windows builds keep the normal light background.
        }
    }

    public static void ApplyRoundedRegion(Form form)
    {
        if (form.FormBorderStyle != FormBorderStyle.None)
        {
            return;
        }

        if (form.WindowState == FormWindowState.Maximized)
        {
            form.Region = null;
            return;
        }

        try
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            var rect = form.ClientRectangle;
            var radius = 8;
            var diameter = radius * 2;
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            form.Region = new System.Drawing.Region(path);
        }
        catch
        {
            form.Region = null;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
