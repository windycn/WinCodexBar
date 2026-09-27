using CodexBarImeHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.WinUI;

internal static class ImeCompatibilityEditor
{
    public static void Open(Window owner, TextBox target, string title)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var result = NativeImeEditor.Open(hwnd, target.Text, title);
        if (result is null) return;
        target.Text = result;
        target.Focus(FocusState.Programmatic);
    }
}
