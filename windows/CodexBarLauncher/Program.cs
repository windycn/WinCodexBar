using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexBarLauncher;

internal static class Program
{
    private const string RuntimeVersion = "2.5.1";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            var application = Path.Combine(directory, "WinCodexBar.Next.exe");
            if (!File.Exists(application)) throw new FileNotFoundException("安装目录缺少 WinUI 工作台。", application);

            var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinCodexBar", $"WindowsAppRuntime-{RuntimeVersion}.txt");
            if (!File.Exists(marker)) InstallRuntime(directory, marker);

            var app = Launch(application, args);
            if (app.WaitForExit(2500) && app.ExitCode != 0)
            {
                // 运行时被系统移除后，即使首次安装标记还在，也允许重新补装一次。
                InstallRuntime(directory, marker);
                Launch(application, args);
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(0, "WinCodexBar 启动失败：\n" + ex.Message, "WinCodexBar", 0x10);
            return 1;
        }
    }

    private static void InstallRuntime(string directory, string marker)
    {
        var installer = Path.Combine(directory, "WindowsAppRuntimeInstall.exe");
        if (!File.Exists(installer)) throw new FileNotFoundException("安装包缺少 Windows App Runtime 安装程序。", installer);
        var info = new ProcessStartInfo(installer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("--quiet");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Windows App Runtime 安装程序。");
        process.WaitForExit();
        if (process.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"Windows App Runtime 安装失败，退出码 {process.ExitCode}。应用尚未启动。");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, $"{RuntimeVersion} {DateTimeOffset.Now:O}");
    }

    private static Process Launch(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 WinUI 工作台。");
    }
}
