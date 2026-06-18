using System;
using System.IO;
using System.Text;

namespace CodexBarWin.Services;

internal static class AppLogService
{
    public static void LogException(Exception exception, string context)
    {
        try
        {
            CodexPaths.EnsureDirectories();
            var builder = new StringBuilder()
                .AppendLine("[" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "] " + context)
                .AppendLine(exception.ToString())
                .AppendLine();
            File.AppendAllText(Path.Combine(CodexPaths.CodexBarRoot, "windows-error.log"), builder.ToString());
        }
        catch
        {
            // Logging must never become another app failure.
        }
    }
}
