param([string]$Version = '0.2.0', [string]$PublishRoot = 'artifacts', [string]$OutputRoot = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$checksums = @()
foreach ($rid in @('win-x64', 'win-x86', 'win-arm64')) {
    $source = Join-Path $PublishRoot $rid
    if (!(Test-Path (Join-Path $source 'WinCodexBar.exe'))) { throw "缺少 $rid 可执行文件" }
    foreach ($document in @('README.md', 'README.en.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath $document -Destination $source -Force
    }
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
