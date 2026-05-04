param(
    [string]$HostName = "60.205.222.32",
    [string]$User = "root",
    [string]$KeyPath = "$env:USERPROFILE\.ssh\scpsl-warmup-20260501185759.pem",
    [int]$Port = 7777,
    [int]$WarningSeconds = 30,
    [string]$WarningMessage = "",
    [string]$RemoteRunUser = "scpsl",
    [string]$RemoteServerDir = "/home/scpsl/scpsl-server",
    [string]$RemotePluginDir = "/home/scpsl/.config/SCP Secret Laboratory/LabAPI/plugins/global",
    [string]$RemoteConfigDir = "/home/scpsl/.config/SCP Secret Laboratory/LabAPI/configs/7777/WarmupSandbox",
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "ScpslPluginStarter\ScpslPluginStarter.csproj"
$dll = Join-Path $root "ScpslPluginStarter\bin\Debug\net48\ScpslPluginStarter.dll"
$remoteTempDll = "/tmp/ScpslPluginStarter.dll.live-update"

function ConvertTo-Text {
    param([int[]]$CodePoints)

    return -join ($CodePoints | ForEach-Object { [char]$_ })
}

if ([string]::IsNullOrWhiteSpace($WarningMessage)) {
    $messageTemplate = ConvertTo-Text @(26381,21153,22120,23558,22312,32,123,48,125,32,31186,21518,37325,21551,26356,26032,12290,26356,26032,23436,25104,21518,21487,37325,26032,36830,25509,65292,24863,35874,29702,35299,12290)
    $WarningMessage = [string]::Format($messageTemplate, $WarningSeconds)
}

if (-not $SkipBuild) {
    dotnet build $project
}

if (-not (Test-Path -LiteralPath $dll)) {
    throw "Built plugin DLL was not found: $dll"
}

$target = "$User@$HostName"
$sshArgs = @(
    "-i", $KeyPath,
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", "ConnectTimeout=10",
    $target
)

if ($DryRun) {
    Write-Host "Dry run: would upload $dll to ${target}:$remoteTempDll"
} else {
    scp -i $KeyPath -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 $dll "${target}:$remoteTempDll"
}

$warningMessageBase64 = [Convert]::ToBase64String($OutputEncoding.GetBytes($WarningMessage))
$remoteServerDirBase64 = [Convert]::ToBase64String($OutputEncoding.GetBytes($RemoteServerDir))
$remotePluginDirBase64 = [Convert]::ToBase64String($OutputEncoding.GetBytes($RemotePluginDir))
$remoteConfigDirBase64 = [Convert]::ToBase64String($OutputEncoding.GetBytes($RemoteConfigDir))

$remoteScript = @'
set -euo pipefail

PORT="$1"
WARNING_SECONDS="$2"
WARNING_MESSAGE="$(printf '%s' "$3" | base64 -d)"
REMOTE_RUN_USER="$4"
REMOTE_SERVER_DIR="$(printf '%s' "$5" | base64 -d)"
REMOTE_PLUGIN_DIR="$(printf '%s' "$6" | base64 -d)"
REMOTE_CONFIG_DIR="$(printf '%s' "$7" | base64 -d)"
REMOTE_TEMP_DLL="$8"
DRY_RUN="$9"

plugin_path="$REMOTE_PLUGIN_DIR/ScpslPluginStarter.dll"
signal_path="$REMOTE_CONFIG_DIR/live-update-warning.txt"
broadcast_command="broadcast $WARNING_SECONDS $WARNING_MESSAGE"

run() {
    if [ "$DRY_RUN" = "1" ]; then
        printf '[dry-run] %s\n' "$*"
        return 0
    fi

    "$@"
}

find_localadmin_pids() {
    pgrep -u "$REMOTE_RUN_USER" -f "LocalAdmin( |$|.* $PORT)" 2>/dev/null || pgrep -f "LocalAdmin( |$|.* $PORT)" 2>/dev/null || true
}

send_localadmin_command() {
    command="$1"
    if [ "$DRY_RUN" = "1" ]; then
        printf '[dry-run] LocalAdmin command: %s\n' "$command"
        return 0
    fi

    pids="$(find_localadmin_pids)"
    if [ -z "$pids" ]; then
        echo "LocalAdmin is not running; skipping pre-restart broadcast." >&2
        return 1
    fi

    for pid in $pids; do
        fd="/proc/$pid/fd/0"
        target="$(readlink "$fd" 2>/dev/null || true)"
        if [ -w "$fd" ] && [ "$target" != "/dev/null" ]; then
            printf '%s\n' "$command" > "$fd" && return 0
        fi
    done

    echo "Could not write to LocalAdmin stdin; skipping pre-restart broadcast." >&2
    return 1
}

echo "Sending Chinese restart warning: $WARNING_MESSAGE"
if [ "$DRY_RUN" = "1" ]; then
    printf '[dry-run] would write update signal: %s\n' "$signal_path"
else
    mkdir -p "$REMOTE_CONFIG_DIR"
    {
        printf '%s\n' "$WARNING_SECONDS"
        printf '%s\n' "$WARNING_MESSAGE"
    } > "$signal_path"
fi
send_localadmin_command "$broadcast_command" || true

if [ "$WARNING_SECONDS" -gt 0 ]; then
    echo "Waiting $WARNING_SECONDS seconds before restart..."
    if [ "$DRY_RUN" != "1" ]; then
        sleep "$WARNING_SECONDS"
    fi
fi

echo "Installing staged plugin DLL..."
run mkdir -p "$REMOTE_PLUGIN_DIR"
if [ "$DRY_RUN" != "1" ]; then
    install -m 0644 "$REMOTE_TEMP_DLL" "$plugin_path"
fi

echo "Restarting SCP:SL LocalAdmin..."
if [ "$DRY_RUN" = "1" ]; then
    echo "[dry-run] would terminate LocalAdmin and SCPSL.x86_64, then start LocalAdmin $PORT"
    exit 0
fi

pkill -TERM -u "$REMOTE_RUN_USER" -f "SCPSL.x86_64" 2>/dev/null || true
pkill -TERM -u "$REMOTE_RUN_USER" -f "LocalAdmin" 2>/dev/null || true
sleep 5
pkill -KILL -u "$REMOTE_RUN_USER" -f "SCPSL.x86_64" 2>/dev/null || true
pkill -KILL -u "$REMOTE_RUN_USER" -f "LocalAdmin" 2>/dev/null || true

runuser -u "$REMOTE_RUN_USER" -- bash -lc "cd '$REMOTE_SERVER_DIR' && nohup ./LocalAdmin '$PORT' >> '$REMOTE_SERVER_DIR/localadmin-live-update.log' 2>&1 &"
echo "Live restart requested. LocalAdmin log: $REMOTE_SERVER_DIR/localadmin-live-update.log"
'@

$dryRunValue = if ($DryRun) { "1" } else { "0" }
$remoteScript | ssh @sshArgs bash -s -- $Port $WarningSeconds $warningMessageBase64 $RemoteRunUser $remoteServerDirBase64 $remotePluginDirBase64 $remoteConfigDirBase64 $remoteTempDll $dryRunValue
