# SCP:SL Warmup Sandbox

This project now contains a first-pass LabAPI warmup sandbox plugin:

- `ScpslPluginStarter/WarmupSandboxPlugin.cs`
- `ScpslPluginStarter/PluginConfig.cs`
- `ScpslPluginStarter/ManagedBotState.cs`

## What it does

- Turns SCP:SL into a facility-wide gunplay warmup sandbox
- Spawns configurable dummy bots
- Gives separate human and bot loadouts
- Respawns humans and bots automatically
- Keeps rounds alive with a single `SuppressRoundEnd` toggle
- Drives dummy fire/reload/zoom through the same dummy-action path exposed in Remote Admin

## Build

```powershell
dotnet build .\ScpslPluginStarter\ScpslPluginStarter.csproj
```

## Install

After the dedicated server has created its LabAPI folders, copy:

`ScpslPluginStarter\bin\Debug\net48\ScpslPluginStarter.dll`

to one of:

- `%AppData%\SCP Secret Laboratory\LabAPI\plugins\global`
- `%AppData%\SCP Secret Laboratory\LabAPI\plugins\<port>`

## Default behavior

- Auto-starts on `WaitingForPlayers` and `RoundStarted`
- Uses `LightContainment`, `HeavyContainment`, and `Entrance` by default
- Gives humans an `E11`, combat armor, medkit, and flash grenade
- Gives bots a `Crossvec`, light armor, and medkit
- Respawns players and bots automatically

## Next likely improvements

- runtime admin commands for changing bot count or loadouts without reloading
- better movement behaviors than teleport-based repositioning
- map-specific spawn curation once you add custom maps
