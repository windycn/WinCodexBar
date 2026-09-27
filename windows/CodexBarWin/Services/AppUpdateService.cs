using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBarWin.Services;

public sealed record AppUpdate(Version Version, string Tag, Uri Page, string AssetName, Uri Download, Uri Checksums)
{
    public string? DeltaAssetName { get; init; }
    public Uri? DeltaDownload { get; init; }
}

/// <summary>仅接受本仓库正式 Release；更新包先完整下载、校验，退出后才覆盖程序。</summary>
public sealed class AppUpdateService
{
    public const string ReleasesUrl = "https://github.com/windycn/WinCodexBar/releases";
    private static readonly HttpClient Client = CreateClient();
    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version
        ?? typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string DisplayVersion => CurrentVersion.ToString(3);
    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("此架构暂不支持自动更新。"),
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinCodexBar/" + DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Client.GetAsync("https://api.github.com/repos/windycn/WinCodexBar/releases/latest", timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return await CheckReleasePageAsync(timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        var newer = ParseRelease(json, CurrentVersion, ArchitectureName);
        if (newer is not null) return newer;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var releaseVersion) ||
            releaseVersion != new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build))) return null;
        var markerName = "BUILD_ID.txt";
        var expected = $"{ReleasesUrl}/download/{tag}/{markerName}";
        var marker = root.GetProperty("assets").EnumerateArray().FirstOrDefault(asset =>
            asset.GetProperty("name").GetString() == markerName);
        if (marker.ValueKind == JsonValueKind.Undefined) return null;
        if (marker.GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidDataException("更新构建标识不属于本项目发布。");
        var remoteId = await Client.GetStringAsync(expected, timeout.Token);
        return IsNewBuild(remoteId, ReadLocalBuildId())
            ? ParseRelease(json, new Version(0, 0, 0), ArchitectureName) : null;
    }

    // GitHub's unauthenticated API can return 403 on a shared network. Its public latest-release
    // redirect remains available without a token; only accept a version from this exact repo.
    private static async Task<AppUpdate?> CheckReleasePageAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(ReleasesUrl + "/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        var latest = ParseLatestReleaseUri(response.RequestMessage?.RequestUri, new Version(0, 0, 0), ArchitectureName);
        if (latest is null) return null;
        var current = new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build));
        if (latest.Version < current) return null;
        if (latest.Version == current)
        {
            try
            {
                var marker = await Client.GetStringAsync($"{ReleasesUrl}/download/{latest.Tag}/BUILD_ID.txt", cancellationToken);
                if (!IsNewBuild(marker, ReadLocalBuildId())) return null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        }
        var update = latest;
        using var checksum = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, update.Checksums), cancellationToken);
        using var package = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, update.Download), cancellationToken);
        if (!checksum.IsSuccessStatusCode || !package.IsSuccessStatusCode)
            throw new InvalidDataException("最新正式版缺少本机架构安装包或 SHA256 校验文件，请到发布页查看。");
        var deltaName = $"WinCodexBar-{update.Version}-from-{DisplayVersion}-win-{ArchitectureName}.zip";
        var delta = new Uri($"{ReleasesUrl}/download/{update.Tag}/{deltaName}");
        try
        {
            using var deltaResponse = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, delta), cancellationToken);
            if (deltaResponse.IsSuccessStatusCode && deltaResponse.Content.Headers.ContentLength < package.Content.Headers.ContentLength)
                update = update with { DeltaAssetName = deltaName, DeltaDownload = delta };
        }
        catch (HttpRequestException) { /* A missing optional delta leaves the verified full package. */ }
        return update;
    }

    public static bool IsNewBuild(string remoteId, string? localId)
    {
        var remote = remoteId.Trim();
        return Regex.IsMatch(remote, "^[a-fA-F0-9]{40}$") &&
            !remote.Equals(localId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadLocalBuildId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BUILD_ID.txt");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public static AppUpdate? ParseLatestReleaseUri(Uri? uri, Version current, string architecture)
    {
        const string prefix = "/windycn/WinCodexBar/releases/tag/";
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("无法确认 GitHub 最新正式版地址。");
        var tag = uri.AbsolutePath[prefix.Length..];
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("最新正式版版本号无效。");
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        if (architecture is not ("x64" or "x86" or "arm64")) throw new InvalidDataException("不支持的更新架构。");
        var name = $"WinCodexBar-{version}-win-{architecture}.zip";
        return new AppUpdate(version, tag, uri, name,
            new Uri($"{ReleasesUrl}/download/{tag}/{name}"),
            new Uri($"{ReleasesUrl}/download/{tag}/SHA256SUMS.txt"));
    }

    public static AppUpdate? ParseRelease(string json, Version current, string architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("发布版本号格式无效。请到发布页查看。");
        // AssemblyVersion 带第四位 0；比较时统一为三位。
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        if (architecture is not ("x64" or "x86" or "arm64")) throw new InvalidDataException("不支持的更新架构。");
        var name = $"WinCodexBar-{version}-win-{architecture}.zip";
        var currentVersion = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        var deltaName = $"WinCodexBar-{version}-from-{currentVersion}-win-{architecture}.zip";
        Uri? download = null, checksums = null;
        Uri? deltaDownload = null;
        long fullSize = long.MaxValue, deltaSize = long.MaxValue;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var assetName = asset.GetProperty("name").GetString();
            if (assetName != name && assetName != deltaName && assetName != "SHA256SUMS.txt") continue;
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var expected = $"https://github.com/windycn/WinCodexBar/releases/download/{tag}/{assetName}";
            if (!string.Equals(uri.AbsoluteUri, expected, StringComparison.Ordinal))
                throw new InvalidDataException("更新资源地址不属于本项目发布。已停止更新。");
            if (assetName == name)
            {
                download = uri;
                fullSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : long.MaxValue;
            }
            else if (assetName == deltaName)
            {
                deltaDownload = uri;
                deltaSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : long.MaxValue;
            }
            else checksums = uri;
        }
        if (download is null || checksums is null) throw new InvalidDataException("新版本缺少本机架构安装包或 SHA256 校验文件，请稍后重试。");
        return new AppUpdate(version, tag, new Uri(ReleasesUrl + "/tag/" + tag), name, download, checksums)
        {
            DeltaAssetName = deltaDownload is not null && deltaSize < fullSize ? deltaName : null,
            DeltaDownload = deltaDownload is not null && deltaSize < fullSize ? deltaDownload : null
        };
    }

    public async Task<string> DownloadAndVerifyAsync(AppUpdate update, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCodexBar", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sums = await Client.GetStringAsync(update.Checksums, cancellationToken);
        if (update.DeltaAssetName is not null && update.DeltaDownload is not null)
        {
            try
            {
                return await DownloadPackageAsync(update.DeltaDownload, update.DeltaAssetName, sums, directory, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken delta must never leave a partial install. The full package is verified separately.
            }
        }
        return await DownloadPackageAsync(update.Download, update.AssetName, sums, directory, cancellationToken);
    }

    private static async Task<string> DownloadPackageAsync(Uri download, string name, string sums, string directory, CancellationToken cancellationToken)
    {
        var zip = Path.Combine(directory, name);
        using (var response = await Client.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(zip);
            await response.Content.CopyToAsync(output, cancellationToken);
        }
        VerifyChecksum(zip, name, sums);
        var staged = Path.Combine(directory, "package");
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        ExtractPackage(zip, staged);
        return staged;
    }

    public static void VerifyChecksum(string zip, string assetName, string sums)
    {
        var line = sums.Split('\n').Select(s => s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .SingleOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == assetName);
        if (line is null || !Regex.IsMatch(line[0], "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("缺少有效 SHA256 校验值。");
        using var stream = File.OpenRead(zip);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(line[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验失败，未修改当前程序。请重新下载。");
    }

    public static void ExtractPackage(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "." or ".codex" or ".codexbar")
                || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("安装包包含不安全路径。");
        }
        if (!archive.Entries.Any(e => e.FullName == "WinCodexBar.exe" && e.Length > 0))
            throw new InvalidDataException("安装包缺少 WinCodexBar.exe。");
        Directory.CreateDirectory(destination);
        archive.ExtractToDirectory(destination);
    }

    public void StartInstaller(string staged, bool backgroundRestart = false)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序位置。");
        if (!Path.GetFileName(exe).Equals("WinCodexBar.exe", StringComparison.OrdinalIgnoreCase)
            && !(Path.GetFileName(exe).Equals("WinCodexBar.Next.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "WinCodexBar.exe"))))
            throw new InvalidOperationException("开发运行环境请使用发布包更新。");
        var target = Path.GetDirectoryName(exe)!;
        // 先检查写权限，失败时应用保持运行，用户可手动解压到可写目录。
        var probe = Path.Combine(target, ".wincodexbar-update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var script = Path.Combine(Path.GetDirectoryName(staged)!, "install.ps1");
        File.WriteAllText(script, BuildInstallScript(staged, target, CodexPaths.CodexBarRoot, Environment.ProcessId, backgroundRestart), new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
    }

    public static string BuildInstallScript(string staged, string target, string dataRoot, int processId, bool backgroundRestart = false)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
        $ErrorActionPreference = 'Stop'
        $source = {{Literal(staged)}}
        $target = {{Literal(target)}}
        $dataRoot = {{Literal(dataRoot)}}
        $restartArguments = {{Literal(backgroundRestart ? "--background" : "")}}
        $work = Split-Path -Parent $source
        $backup = Join-Path $work 'previous-program'
        $written = [System.Collections.Generic.List[string]]::new()
        $newFiles = [System.Collections.Generic.List[string]]::new()
        try {
            $old = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
            if ($old -and !$old.WaitForExit(30000)) { throw '程序未退出，请关闭后重试更新。' }
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
            $dataBackup = Join-Path $dataRoot ('backups\before-update-' + $stamp)
            New-Item -ItemType Directory -Force -Path $dataBackup | Out-Null
            foreach ($name in @('windows_accounts.json', 'windows_settings.json')) {
                $file = Join-Path $dataRoot $name
                if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $dataBackup }
            }
            $files = @(Get-ChildItem -LiteralPath $source -File -Recurse)
            # 先备份全部被覆盖文件，再写入；不删除安装目录中的其他文件。
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
                $dest = Join-Path $target $relative
                $parent = Get-Item -LiteralPath $target
                if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '不支持通过目录链接覆盖更新。' }
                $ancestor = Split-Path -Parent $dest
                while ($ancestor.Length -ge $target.Length) {
                    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '目标目录包含链接，已停止更新。' }
                    $ancestor = Split-Path -Parent $ancestor
                }
                if (Test-Path -LiteralPath $dest) {
                    if ((Get-Item -LiteralPath $dest).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '目标文件为链接，已停止更新。' }
                    $saved = Join-Path $backup $relative
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $saved) | Out-Null
                    Copy-Item -LiteralPath $dest -Destination $saved
                } else { $newFiles.Add($relative) }
            }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
                $dest = Join-Path $target $relative
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
                $written.Add($relative)
                Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
            }
            '更新完成，数据备份：' + $dataBackup | Set-Content -LiteralPath (Join-Path $work 'result.txt') -Encoding UTF8
            if ($restartArguments) {
                Start-Process -FilePath (Join-Path $target 'WinCodexBar.exe') -ArgumentList $restartArguments -WorkingDirectory $target -WindowStyle Hidden
            } else {
                Start-Process -FilePath (Join-Path $target 'WinCodexBar.exe') -WorkingDirectory $target -WindowStyle Hidden
            }
        } catch {
            $failure = $_.Exception.Message
            $rollbackFailures = @()
            foreach ($relative in $written) {
                try {
                    $dest = Join-Path $target $relative
                    $saved = Join-Path $backup $relative
                    if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $dest -Force }
                    elseif ($newFiles.Contains($relative)) { Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue }
                } catch { $rollbackFailures += $_.Exception.Message }
            }
            $message = "更新失败：$failure`n旧程序备份：$backup`n" + ($rollbackFailures -join "`n")
            $message | Set-Content -LiteralPath (Join-Path $work 'result.txt') -Encoding UTF8
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show($message, 'WinCodexBar 更新') | Out-Null
        }
        """;
    }
}
