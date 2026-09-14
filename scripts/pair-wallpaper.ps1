param([string]$WallpaperPath = 'C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\projects\myprojects\gta-vi-wallpaper')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $WallpaperPath 'codex-meter.js'))) {
    throw 'Installer le module Codex Meter dans ce fond avant association.'
}
$meterDirectory = Join-Path $env:LOCALAPPDATA 'CodexMeter'
New-Item -ItemType Directory -Path $meterDirectory -Force | Out-Null
$tokenPath = Join-Path $meterDirectory 'wallpaper-token.txt'
$token = if (Test-Path -LiteralPath $tokenPath) { [IO.File]::ReadAllText($tokenPath).Trim() } else { '' }
if ($token -notmatch '^[A-Fa-f0-9]{64}$') {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [BitConverter]::ToString($bytes).Replace('-', '')
    [IO.File]::WriteAllText($tokenPath, $token)
}
$connection = @{ token = $token } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $WallpaperPath 'codex-connection.js'), "// Association privée à ce PC. Ne pas publier.`nwindow.CODEX_METER_CONNECTION = $connection;`n", (New-Object Text.UTF8Encoding $false))
Write-Host 'Fond associé à Codex Meter.'
