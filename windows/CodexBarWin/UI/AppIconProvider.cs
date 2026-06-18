using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexBarWin.UI;

internal static class AppIconProvider
{
    public static void Apply(Form form)
    {
        var icon = CreateWindowIcon();
        if (icon is not null)
        {
            form.Icon = icon;
        }
    }

    public static Icon? CreateWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "WinCodexBar.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "app.png");
        if (File.Exists(pngPath))
        {
            using var source = Image.FromFile(pngPath);
            using var bitmap = new Bitmap(source, new Size(32, 32));
            var handle = bitmap.GetHicon();
            try
            {
                using var icon = Icon.FromHandle(handle);
                return (Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
