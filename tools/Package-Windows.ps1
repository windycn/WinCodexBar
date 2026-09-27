param([string]$Version = '1.0.0', [string]$PublishRoot = 'artifacts', [string]$OutputRoot = 'artifacts/packages', [string[]]$Architectures = @('win-x64', 'win-x86', 'win-arm64'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$buildId = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $buildId -notmatch '^[a-fA-F0-9]{40}$') { throw '无法确定发布构建标识' }
$buildId | Set-Content -LiteralPath (Join-Path $OutputRoot 'BUILD_ID.txt') -Encoding ascii
$checksums = @()
foreach ($rid in $Architectures) {
    $source = Join-Path $PublishRoot $rid
    $buildId | Set-Content -LiteralPath (Join-Path $source 'BUILD_ID.txt') -Encoding ascii
    foreach ($required in @('WinCodexBar.exe', 'WinCodexBar.Next.exe', 'WindowsAppRuntimeInstall.exe')) {
        if (!(Test-Path (Join-Path $source $required))) { throw "缺少 $rid $required" }
    }
    foreach ($document in @('README.md', 'README.en.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath $document -Destination $source -Force
    }
    $screenshots = Join-Path $source 'docs/screenshots'
    New-Item -ItemType Directory -Path $screenshots -Force | Out-Null
    Copy-Item -LiteralPath (Get-ChildItem -LiteralPath 'docs/screenshots' -Filter '*.png').FullName -Destination $screenshots -Force
    $licenses = Join-Path $source 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item 'windows/CodexBarWin/Assets/Fonts/*.txt' $licenses -Force
    # 符号仅用于调试，不放入用户安装包。
    $files = Get-ChildItem -LiteralPath $source | Where-Object { $_.Extension -ne '.pdb' }
    $name = "WinCodexBar-$Version-$rid.zip"
    $package = Join-Path $OutputRoot $name
    Compress-Archive -LiteralPath $files.FullName -DestinationPath $package -Force
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $name"
}
$checksums | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS.txt') -Encoding ascii
