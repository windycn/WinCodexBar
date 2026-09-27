using CodexBarWin.Services;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexBarWin.WinUI;

public static class ShortcutService
{
    private static string MarkerPath => Path.Combine(CodexPaths.CodexBarRoot, "winui-shortcuts-v1.txt");

    public static void EnsureOnFirstLaunch()
    {
        // 仅发布包（带引导入口）写入用户开始菜单，开发预览不修改真实快捷方式。
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WinCodexBar.exe")))
            return;
        if (File.Exists(MarkerPath)) return;
        CreateShortcuts();
        CodexPaths.EnsureDirectories();
        File.WriteAllText(MarkerPath, DateTimeOffset.Now.ToString("O"));
    }

    public static void CreateShortcuts()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "WinCodexBar.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("当前目录缺少 WinCodexBar 启动入口。", executable);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(desktop) || string.IsNullOrWhiteSpace(programs))
            throw new DirectoryNotFoundException("无法找到桌面或开始菜单目录。");

        Create(Path.Combine(desktop, "WinCodexBar.lnk"), executable);
        Create(Path.Combine(programs, "WinCodexBar.lnk"), executable);
    }

    private static void Create(string shortcutPath, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)(object)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("WinCodexBar · Codex 账号与用量");
            link.SetIconLocation(executable, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
