using System;
using System.IO;

namespace CodexBarWin.Services;

public static class CodexPaths
{
    public static string RealHome
    {
        get
        {
            var overrideHome = Environment.GetEnvironmentVariable("CODEXBAR_HOME");
            if (!string.IsNullOrWhiteSpace(overrideHome))
            {
                return overrideHome;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public static string CodexRoot => Path.Combine(RealHome, ".codex");

    public static string CodexBarRoot => Path.Combine(RealHome, ".codexbar");

    public static string AuthPath => Path.Combine(CodexRoot, "auth.json");

    public static string ConfigTomlPath => Path.Combine(CodexRoot, "config.toml");

    public static string AuthBackupPath => Path.Combine(CodexBarRoot, "auth.json.bak");

    public static string ConfigBackupPath => Path.Combine(CodexBarRoot, "config.toml.bak");

    public static string WindowsRegistryPath => Path.Combine(CodexBarRoot, "windows_accounts.json");

    public static string WindowsSettingsPath => Path.Combine(CodexBarRoot, "windows_settings.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(CodexRoot);
        Directory.CreateDirectory(CodexBarRoot);
    }
}
