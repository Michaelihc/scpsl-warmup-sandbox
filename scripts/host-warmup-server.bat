@echo off
setlocal EnableExtensions

set "ROOT=%~dp0.."
set "SERVER_DIR=C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server"
set "PORT=7777"
set "START_SERVER=0"
set "CONFIGURE_CN_PUBLIC=0"
set "CN_PUBLIC_MAX_PLAYERS=50"
set "SERVER_INFO_ID="

:parse
if "%~1"=="" goto parsed
if /I "%~1"=="--start" (
    set "START_SERVER=1"
    shift
    goto parse
)
if /I "%~1"=="--configure-cn-public" (
    set "CONFIGURE_CN_PUBLIC=1"
    shift
    goto parse
)
if /I "%~1"=="--server" (
    if "%~2"=="" goto usage
    set "SERVER_DIR=%~2"
    shift
    shift
    goto parse
)
if /I "%~1"=="--port" (
    if "%~2"=="" goto usage
    set "PORT=%~2"
    shift
    shift
    goto parse
)
if /I "%~1"=="--server-info-id" (
    if "%~2"=="" goto usage
    set "SERVER_INFO_ID=%~2"
    shift
    shift
    goto parse
)
goto usage

:parsed
set "PROJECT=%ROOT%\ScpslPluginStarter\ScpslPluginStarter.csproj"
set "SAMPLE_CONFIG=%ROOT%\ScpslPluginStarter\config.yml"
set "LIVE_CONFIG_DIR=%APPDATA%\SCP Secret Laboratory\LabAPI\configs\%PORT%\WarmupSandbox"
set "LIVE_CONFIG=%LIVE_CONFIG_DIR%\config.yml"

echo Building and deploying WarmupSandbox...
dotnet build "%PROJECT%"
if errorlevel 1 exit /b 1

if not exist "%LIVE_CONFIG_DIR%" mkdir "%LIVE_CONFIG_DIR%"
if not exist "%LIVE_CONFIG%" (
    echo Installing sample config to "%LIVE_CONFIG%"
    copy "%SAMPLE_CONFIG%" "%LIVE_CONFIG%" >nul
) else (
    echo Existing config preserved: "%LIVE_CONFIG%"
)

if "%CONFIGURE_CN_PUBLIC%"=="1" (
    if "%SERVER_INFO_ID%"=="" (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\configure-cn-public-server.ps1" -Port "%PORT%" -MaxPlayers %CN_PUBLIC_MAX_PLAYERS%
    ) else (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\configure-cn-public-server.ps1" -Port "%PORT%" -MaxPlayers %CN_PUBLIC_MAX_PLAYERS% -ServerInfoPastebinId "%SERVER_INFO_ID%"
    )
    if errorlevel 1 exit /b 1
)

if "%START_SERVER%"=="1" (
    if not exist "%SERVER_DIR%\LocalAdmin.exe" (
        echo LocalAdmin.exe was not found in "%SERVER_DIR%".
        echo Pass --server "path\to\SCP Secret Laboratory Dedicated Server".
        exit /b 1
    )

    echo Starting SCP:SL LocalAdmin...
    pushd "%SERVER_DIR%"
    start "SCP SL Dedicated Server" "%SERVER_DIR%\LocalAdmin.exe"
    popd
)

echo Done.
exit /b 0

:usage
echo Usage:
echo   scripts\host-warmup-server.bat [--start] [--server "path"] [--port 7777] [--configure-cn-public] [--server-info-id pastebin_id]
exit /b 2
