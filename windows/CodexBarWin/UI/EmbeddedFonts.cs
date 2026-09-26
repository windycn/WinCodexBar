using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexBarWin.UI;

internal static class EmbeddedFonts
{
    private static readonly PrivateFontCollection Collection = new();
    private static readonly List<IntPtr> Buffers = new();
    public static readonly FontFamily Icons = Load("FluentSystemIcons-Regular.ttf");
    public static readonly FontFamily Text = Load("NotoSansSC-Regular.ttf");
    private static readonly FontFamily Bold = Load("NotoSansSC-Bold.ttf");

    private static FontFamily Load(string filename)
    {
        var assembly = typeof(EmbeddedFonts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(filename, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Buffers.Add(pointer); // 字体系统在进程生命周期内引用该内存。
        Collection.AddMemoryFont(pointer, bytes.Length);
        uint count = 0;
        var handle = AddFontMemResourceEx(pointer, (uint)bytes.Length, IntPtr.Zero, ref count);
        if (handle == IntPtr.Zero || count == 0) throw new InvalidOperationException("无法注册内置字体: " + filename);
        return Collection.Families.First(f => filename.StartsWith("Fluent", StringComparison.Ordinal) ? f.Name.Contains("Fluent") : f.Name.Contains("WinCodexBar Sans"));
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr memory, uint size, IntPtr reserved, ref uint count);
}
