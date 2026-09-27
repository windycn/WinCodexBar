param(
    [string]$OutputRoot = 'artifacts/demo-home',
    [string]$SampleImagePath = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputRoot)
$bar = Join-Path $root '.codexbar'
$sessions = Join-Path $root '.codex/sessions'
$images = Join-Path $bar 'image-studio'
$vectors = Join-Path $bar 'svg-gallery'
foreach ($folder in @($bar, $sessions, $images, (Join-Path $images 'metadata'), $vectors, (Join-Path $vectors 'metadata'))) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

$now = [DateTimeOffset]::Now
$accounts = @(
    @{ account_id='demo-plus'; email='demo.plus@example.test'; plan_type='plus'; primary_window_available=$true; secondary_window_available=$true; primary_used_percent=31; secondary_used_percent=24; primary_limit_window_seconds=18000; secondary_limit_window_seconds=604800; primary_reset_at=$now.AddHours(3).ToString('o'); secondary_reset_at=$now.AddDays(4).ToString('o'); reset_credits_available=1; reset_credits_checked_at=$now.ToString('o'); reset_credit_details=@(@{ ExpiresAt=$now.AddDays(6).ToString('o'); ExpirationKnown=$true }); last_checked=$now.ToString('o') },
    @{ account_id='demo-prolite'; email='demo.prolite@example.test'; plan_type='prolite'; primary_window_available=$true; secondary_window_available=$true; primary_used_percent=68; secondary_used_percent=46; primary_limit_window_seconds=18000; secondary_limit_window_seconds=604800; primary_reset_at=$now.AddHours(2).ToString('o'); secondary_reset_at=$now.AddDays(3).ToString('o'); last_checked=$now.ToString('o') },
    @{ account_id='demo-free'; email='demo.free@example.test'; plan_type='free'; primary_window_available=$true; secondary_window_available=$true; primary_used_percent=92; secondary_used_percent=77; primary_limit_window_seconds=18000; secondary_limit_window_seconds=604800; primary_reset_at=$now.AddHours(1).ToString('o'); secondary_reset_at=$now.AddDays(2).ToString('o'); last_checked=$now.ToString('o') }
)
@{ ActiveAccountId='demo-plus'; Accounts=$accounts; MetadataByAccountId=@{} } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $bar 'windows_accounts.json') -Encoding utf8
@{ image_studio_enabled=$false; vector_studio_enabled=$false; quality_check_enabled=$false; start_with_windows=$false; auto_check_updates=$false; system_notifications_enabled=$false; openai=@{ token_pricing_model='gpt-6-luna' } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bar 'windows_settings.json') -Encoding utf8

for ($day=0; $day -lt 14; $day++) {
    for ($session=0; $session -lt 2; $session++) {
        $date = $now.AddDays(-$day).Date.AddHours(9 + $session * 5)
        $id = 'demo-{0:00}-{1}' -f $day, $session
        $path = Join-Path $sessions ($id + '.jsonl')
        $model = if ($session -eq 0) { 'gpt-6-luna' } else { 'gpt-6-sol' }
        $events = @()
        for ($event=0; $event -lt 2; $event++) {
            $inputTokens = 6000 + 1300 * $day + 2000 * $session + 900 * $event
            $outputTokens = 1100 + 300 * $day + 350 * $event
            $events += (@{ timestamp=$date.AddMinutes(9 * $event).ToString('o'); model=$model; payload=@{ info=@{ last_token_usage=@{ input_tokens=$inputTokens; output_tokens=$outputTokens; total_tokens=$inputTokens + $outputTokens } } } } | ConvertTo-Json -Depth 10 -Compress)
        }
        [IO.File]::WriteAllLines($path, [string[]]$events, [Text.UTF8Encoding]::new($false))
    }
}

$hasSample = $SampleImagePath -and (Test-Path -LiteralPath $SampleImagePath -PathType Leaf)
if (-not $hasSample) {
Add-Type -AssemblyName System.Drawing
$imagePath = Join-Path $images 'demo-geometric-study.png'
$bitmap = [Drawing.Bitmap]::new(960, 600)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::FromArgb(246, 249, 255))
    $blue = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(33, 108, 202))
    $green = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(35, 164, 123))
    $orange = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(239, 162, 77))
    $font = [Drawing.Font]::new('Segoe UI', 32, [Drawing.FontStyle]::Bold)
    try {
        $graphics.FillEllipse($blue, 80, 120, 290, 290)
        $graphics.FillRectangle($green, 410, 185, 330, 180)
        $graphics.FillEllipse($orange, 630, 80, 210, 210)
        $graphics.DrawString('DEMO · SVG', $font, [Drawing.Brushes]::Black, 85, 485)
        $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $font.Dispose(); $blue.Dispose(); $green.Dispose(); $orange.Dispose() }
}
finally { $graphics.Dispose(); $bitmap.Dispose() }
@{ Path=$imagePath; Prompt='演示素材：蓝色圆形、绿色矩形和橙色圆形'; AccountName='虚构账号 · 演示数据'; CreatedAt=$now.ToString('o'); RequestModel='demo'; Effort='low'; Model='演示图片'; Size='960x600'; Quality='演示' } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $images 'metadata/demo-geometric-study.json') -Encoding utf8
}

if ($hasSample) {
    $sample = Join-Path $images 'demo-transformer.png'
    Copy-Item -LiteralPath $SampleImagePath -Destination $sample -Force
    @{ Path=$sample; Prompt='Transformer 原理示意图（演示）'; AccountName='虚构账号 · 演示数据'; CreatedAt=$now.AddMinutes(1).ToString('o'); RequestModel='demo'; Effort='low'; Model='演示图片'; Size='原图'; Quality='演示' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $images 'metadata/demo-transformer.json') -Encoding utf8
}

if (-not $hasSample) {
$svgPath = Join-Path $vectors 'demo-geometric-study.svg'
@'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 960 600">
  <rect width="960" height="600" fill="#f6f9ff"/>
  <circle cx="225" cy="265" r="145" fill="#216cca"/>
  <rect x="410" y="185" width="330" height="180" fill="#23a47b"/>
  <circle cx="735" cy="185" r="105" fill="#efa24d"/>
</svg>
'@ | Set-Content -LiteralPath $svgPath -Encoding utf8
@{ Path=$svgPath; SourcePath=$imagePath; Prompt='演示素材：蓝色圆形、绿色矩形和橙色圆形'; AccountName='虚构账号 · 演示数据'; Model='演示 SVG · 未调用模型'; Effort='low'; CreatedAt=$now.ToString('o') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $vectors 'metadata/demo-geometric-study.json') -Encoding utf8
}

Write-Output "Demo fixture: $root"
