# SCP:SL Warmup Sandbox

[中文说明](README.zh-CN.md)

> **Development warning:** This plugin is still in active development. Expect rough edges, behavior changes, and occasional bot/pathing issues. Test on a private server before using it with players.

`ScpslPluginStarter` is a LabAPI plugin for hosting an SCP:SL warmup sandbox with managed dummy bots, configurable human loadouts, optional Dust2 arena support, bomb mode, runtime bot tuning commands, and localized help text.

The bot work is partially inspired by the older SwiftNPCs/SwiftAPI projects, but this plugin is an independent implementation for the current warmup sandbox use case.

The plugin is built for a local SCP:SL Dedicated Server install and copies itself into the LabAPI global plugin folder after a successful build.

## Features

- Auto-start warmup on round start, first player, or waiting-for-players depending on config.
- Spawn and maintain configurable dummy bot counts.
- Optionally reset the runtime bot count when no real players remain online.
- Runtime bot commands for count, difficulty, AI mode, map mode, SCP speeds, and close-retreat speed.
- Human loadout selection with `loadout`, `ld`, or `kit`, plus temporary in-place SCP practice roles.
- Player-facing `.help` and `.bots setcount <count>` commands with cooldowns.
- Server Specific Settings console for role changes, item/ammo grants, teleport helpers, and bot count changes.
- Role-default gear or fully custom loadouts with reserve ammo maintenance.
- Default fallback ammo reserve for role-default firearms, including 9x19 weapons.
- Native SCP:SL round spawn protection support.
- Optional Dust2 schematic arena with ProjectMER.
- Optional bomb mode with plant/defuse flow.
- Optional surface-zone runtime NavMesh while leaving the randomized facility alone.
- English and Chinese command/help strings through `bots language <en|cn>`.

## Requirements

- Windows host.
- SCP:SL Dedicated Server installed through Steam. Compatible with SCP:SL 14.2.6.
- LabAPI installed on the server.
- .NET SDK capable of building `net48` projects.
- ProjectMER only if you want Dust2 schematic loading.

Default server path used by the project:

```text
C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server
```

If your server is elsewhere, update `ScpslServerManagedDir` in `ScpslPluginStarter/ScpslPluginStarter.csproj`.

## Quick Host

From the repository root:

```bat
scripts\host-warmup-server.bat
```

That script:

- builds the plugin
- deploys the DLL through the project build target
- copies the sample config on first run only
- optionally starts `LocalAdmin.exe`

Start the server after deploy:

```bat
scripts\host-warmup-server.bat --start
```

Use a non-default server path:

```bat
scripts\host-warmup-server.bat --server "D:\Servers\SCP Secret Laboratory Dedicated Server" --start
```

Use a non-default config port:

```bat
scripts\host-warmup-server.bat --port 7778
```

Configure the recommended Chinese public-test server name, colored server-list title, and 50-player cap:

```bat
scripts\host-warmup-server.bat --configure-cn-public --start
```

For the public server description, use the plain-language Chinese template in:

```text
docs\server-description.zh-CN.txt
```

Publish that text to Pastebin, then set the ID in SCP:SL's native `serverinfo_pastebin_id` field or pass it to the helper:

```bat
scripts\host-warmup-server.bat --configure-cn-public --server-info-id pastebin_id --start
```

The script does not overwrite an existing live config.

## Monitor Player Count

Poll the public server through the Steam query protocol:

```bat
scripts\watch-player-count.bat
```

Run one query and exit:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\watch-player-count.ps1 -Once
```

Override the address or polling interval:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\watch-player-count.ps1 -HostName 60.205.222.32 -Port 7777 -IntervalSeconds 5
```

## Manual Build

```powershell
dotnet build .\ScpslPluginStarter\ScpslPluginStarter.csproj
```

After build, the project copies:

- plugin DLL to `%AppData%\SCP Secret Laboratory\LabAPI\plugins\global`
- Dust2 schematic files to `%AppData%\SCP Secret Laboratory\LabAPI\configs\ProjectMER\Schematics\de_dust2`

Live config usually lives at:

```text
%AppData%\SCP Secret Laboratory\LabAPI\configs\7777\WarmupSandbox\config.yml
```

Copy `ScpslPluginStarter\config.yml` there if LabAPI has not generated one yet.

## Live Update Helper

Use the live deploy helper only when you intend to update the public server. It builds the plugin, uploads the DLL to a staging path, broadcasts a Chinese update warning, waits 30 seconds, installs the DLL, and restarts LocalAdmin:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-live.ps1
```

Preview the commands without uploading or restarting:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-live.ps1 -DryRun
```

Default restart warning:

```text
服务器将在 30 秒后重启更新。更新完成后可重新连接，感谢理解。
```

## Runtime Commands

Commands are registered for Remote Admin and game console where supported.

Primary command aliases:

```text
bots
bot
warmup
ws
warmupsandbox
```

Common commands:

```text
bots status
bots start
bots restart
bots roundrestart
bots stop
bots save
bots set <count>
bots setcount <count>
bots difficulty <easy|normal|hard|hardest>
bots aimode <classic|realistic>
bots language <en|cn>
bots map <bomb|standard|true|false>
bots setretreatspeed <scale>
bots set retreatspeed <scale>
```

SCP facility follow speeds:

```text
bots setspeed <speed>
bots set939speed <speed>
bots set3114speed <speed>
bots set049speed <speed>
bots set106speed <speed>
```

Generic setting form:

```text
bots set <key> <value>
```

Examples:

```text
bots set 2
bots difficulty hard
bots aimode realistic
bots language cn
bots setretreatspeed 0.92
bots set939speed 7.5
bots map bomb
```

Help:

```text
bots
modhelp
.help
```

Human loadouts:

```text
loadout
loadout <number|preset|role>
loadout <173|939|106|049|3114|096>
```

Aliases:

```text
ld
kit
```

Player-facing commands:

```text
.help
.loadout
.loadout <number|name>
.loadout <173|939|106|049|3114|096>
.bots setcount <count>
```

Temporary SCP loadouts switch you in-place, clear inventory/ammo, and do not become your respawn loadout. After death, you return to your last selected human loadout.

`.bots setcount <count>` is available to players with cooldowns: a 60 second global cooldown and a per-player cooldown of 3 minutes plus 0-60 random seconds by default.

Players can open SCP:SL Server Specific Settings for the console. Personal actions use a short local cooldown; global bot settings use the shared server cooldown.

## Configuration

The checked-in sample config is:

```text
ScpslPluginStarter\config.yml
```

Important top-level fields:

- `language`
- `bot_count`
- `max_bot_count`
- `reset_bot_count_when_no_active_players`
- `no_active_players_bot_count`
- `no_active_players_bot_reset_delay_ms`
- `difficulty_preset`
- `human_role`
- `bot_role`
- `use_bot_role_default_loadout`
- `enable_spawn_protection`
- `auto_cleanup_enabled`
- `auto_cleanup_interval_seconds`
- `player_bot_count_global_cooldown_seconds`
- `player_bot_count_cooldown_seconds`
- `player_bot_count_cooldown_jitter_seconds`
- `player_panel_enabled`
- `player_panel_use_window_seconds`
- `player_panel_global_cooldown_seconds`
- `player_panel_cooldown_seconds`
- `player_panel_cooldown_jitter_seconds`
- `dust2_map`
- `bot_behavior`

Important `bot_behavior` fields:

- `ai_mode`
- `preferred_range`
- `range_tolerance`
- `orbit_retreat_distance`
- `close_retreat_speed_scale`
- `enable_adaptive_close_range_retreat`
- `facility_dummy_follow_speed`
- `facility_dummy_follow_speed_scp939`
- `facility_dummy_follow_speed_scp3114`
- `facility_dummy_follow_speed_scp049`
- `facility_dummy_follow_speed_scp106`
- `nav_debug_logging`
- `realistic_los_debug_logging`

## Difficulty

Difficulty is changed at runtime with:

```text
bots difficulty <easy|normal|hard|hardest>
```

Harder difficulty keeps the fastest brain update cadence and improves tracking accuracy more quickly. Easier difficulties mainly reduce tracking accuracy growth over time rather than slowing the entire brain loop.

## Loadouts And Ammo

Custom loadouts define inventory items and ammo grants:

```yaml
items:
- GunCrossvec
- ArmorLight
- Medkit
ammo:
- type: Ammo9x19
  amount: 240
```

If role-default gear is used, there may be no custom loadout object. The plugin now maintains reserve ammo from the equipped firearm's actual ammo type and falls back to 240 rounds when no explicit grant exists.

## Dust2 And Bomb Mode

Dust2 uses ProjectMER at runtime. The plugin does not compile against ProjectMER; it discovers the loader by reflection.

Enable Dust2:

```text
bots map true
```

Enable bomb mode:

```text
bots map bomb
```

Return to normal facility warmup:

```text
bots map standard
```

## Repository Layout

- `ScpslPluginStarter/ScpslPluginStarter.csproj`
  Main .NET Framework 4.8 plugin project.
- `ScpslPluginStarter/WarmupSandboxPlugin.cs`
  Plugin entry point, LabAPI event wiring, command backing, loadouts, warmup lifecycle, and shared runtime helpers.
- `ScpslPluginStarter/BotControllerService.cs`
  Main bot brain orchestration, movement decisions, orbit/retreat handling, NavMesh steering, and behavior copy logic.
- `ScpslPluginStarter/BotTargetingService.cs`
  Target filtering, LOS checks, target scoring, and faction hostility.
- `ScpslPluginStarter/BotAimService.cs`
  Aim point selection and dummy look action selection.
- `ScpslPluginStarter/BotCombatService.cs`
  Firearm equip fallback, fire gating, reload checks, and combat helpers.
- `ScpslPluginStarter/BotNavigationService.cs`
  Pathing and navigation helpers.
- `ScpslPluginStarter/BombModeService.cs`
  Bomb round state, carrier assignment, plant/defuse interactions, and win checks.
- `ScpslPluginStarter/Dust2MapService.cs`
  ProjectMER schematic loading and Dust2 spawn helpers.
- `ScpslPluginStarter/FacilityNavMeshService.cs`
  Optional runtime NavMesh work for supported zones/templates.
- `ScpslPluginStarter/FacilityNavAgentFollower.cs`
  NavMeshAgent follower bridge used where runtime NavMesh steering is enabled.
- `ScpslPluginStarter/HumanPresetService.cs`
  Human preset lookup and loadout menu formatting.
- `ScpslPluginStarter/WarmupCommand.cs`
  `bots` command implementation.
- `ScpslPluginStarter/LoadoutCommand.cs`
  `loadout` command implementation.
- `ScpslPluginStarter/ModHelpCommand.cs`
  Localized help command.
- `ScpslPluginStarter/WarmupLocalization.cs`
  Lightweight English/Chinese localization helper.
- `ScpslPluginStarter/Schematics/de_dust2`
  Dust2 schematic assets copied into ProjectMER config by the build.
- `ScpslPluginStarter/NavTemplates`
  Exported navigation template data.
- `tools/NavTemplateExporter`
  Utility used to export template data from an AssetRipper Unity project.
- `scripts/host-warmup-server.bat`
  Build, deploy, and optional server-start helper.

## Debugging

Most logging is disabled by default. Turn on only the log family you need:

- `enable_debug_logging`
- `enable_verbose_bot_logging`
- `enable_attachment_logging`
- `enable_arena_logging`
- `enable_zoom_logging`
- `bot_behavior.nav_debug_logging`
- `bot_behavior.realistic_los_debug_logging`

Useful log tags:

- `[BotCombat:...]`
- `[BotAim:...]`
- `[BotNav:...]`
- `[AttachmentDebug]`
- `[WarmupSandbox]`

## Git Notes

Do not commit local exported game projects or build output. `.gitignore` excludes:

- `ExportedSCPSL/`
- `bin/`
- `obj/`
- local LabAPI/server output
