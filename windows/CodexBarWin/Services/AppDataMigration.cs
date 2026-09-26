namespace CodexBarWin.Services;

/// <summary>手动解压升级也备份旧数据；沿用原目录和 JSON 结构，不搬动 Codex 会话。</summary>
public static class AppDataMigration
{
    public static void EnsureVersionBackup()
    {
        CodexPaths.EnsureDirectories();
        var marker = Path.Combine(CodexPaths.CodexBarRoot, "data-version.txt");
        var version = AppUpdateService.DisplayVersion;
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;
        var files = new[] { CodexPaths.WindowsRegistryPath, CodexPaths.WindowsSettingsPath }.Where(File.Exists).ToArray();
        if (files.Length > 0)
        {
            var backup = Path.Combine(CodexPaths.CodexBarRoot, "backups", "before-" + version + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backup);
            foreach (var file in files) File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
        }
        AtomicFile.WriteAllText(marker, version);
    }
}
