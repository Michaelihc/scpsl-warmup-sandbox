param(
    [string]$Port = "7777",
    [string]$ServerName = "",
    [int]$MaxPlayers = 50,
    [string]$ServerInfoPastebinId = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ServerName)) {
    $ServerName = "[CN] [" + [char]0x516C + [char]0x6D4B + "] " + [char]0x4EBA + [char]0x673A + [char]0x6218 + [char]0x6597 + [char]0x670D
}

$configDir = Join-Path $env:APPDATA "SCP Secret Laboratory\config\$Port"
$gameplayConfig = Join-Path $configDir "config_gameplay.txt"

if (-not (Test-Path -LiteralPath $gameplayConfig)) {
    throw "SCP:SL gameplay config was not found: $gameplayConfig. Start the server once so the game can generate it."
}

$backup = "$gameplayConfig.warmup-cn-public-backup"
if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $gameplayConfig -Destination $backup
}

$lines = Get-Content -LiteralPath $gameplayConfig
$changedServerName = $false
$changedPlayerListTitle = $false
$changedMaxPlayers = $false
$changedServerInfo = $false

for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*server_name\s*:') {
        $lines[$i] = "server_name: $ServerName"
        $changedServerName = $true
        continue
    }

    if ($lines[$i] -match '^\s*player_list_title\s*:') {
        $lines[$i] = "player_list_title: $ServerName"
        $changedPlayerListTitle = $true
        continue
    }

    if ($lines[$i] -match '^\s*max_players\s*:') {
        $lines[$i] = "max_players: $MaxPlayers"
        $changedMaxPlayers = $true
        continue
    }

    if (-not [string]::IsNullOrWhiteSpace($ServerInfoPastebinId) -and $lines[$i] -match '^\s*serverinfo_pastebin_id\s*:') {
        $lines[$i] = "serverinfo_pastebin_id: $ServerInfoPastebinId"
        $changedServerInfo = $true
        continue
    }
}

if (-not $changedServerName) {
    $lines = @("server_name: $ServerName") + $lines
}

if (-not $changedPlayerListTitle) {
    $lines = @("player_list_title: $ServerName") + $lines
}

if (-not $changedMaxPlayers) {
    $lines = @("max_players: $MaxPlayers") + $lines
}

if (-not [string]::IsNullOrWhiteSpace($ServerInfoPastebinId) -and -not $changedServerInfo) {
    $lines = @("serverinfo_pastebin_id: $ServerInfoPastebinId") + $lines
}

Set-Content -LiteralPath $gameplayConfig -Value $lines -Encoding UTF8

Write-Host "Updated SCP:SL server config:"
Write-Host "  File: $gameplayConfig"
Write-Host "  Server name: $ServerName"
Write-Host "  Max players: $MaxPlayers"
if (-not [string]::IsNullOrWhiteSpace($ServerInfoPastebinId)) {
    Write-Host "  Server info pastebin ID: $ServerInfoPastebinId"
}
Write-Host "  Backup: $backup"
