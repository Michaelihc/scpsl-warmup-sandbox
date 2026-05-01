# SCP:SL Warmup Sandbox

`ScpslPluginStarter` is a LabAPI plugin for hosting an SCP:SL warmup sandbox with managed dummy bots, configurable human loadouts, optional Dust2 arena support, bomb mode, runtime bot tuning commands, and localized help text.

The plugin is built for a local SCP:SL Dedicated Server install and copies itself into the LabAPI global plugin folder after a successful build.

## Features

- Auto-start warmup on round start, first player, or waiting-for-players depending on config.
- Spawn and maintain configurable dummy bot counts.
- Runtime bot commands for count, difficulty, AI mode, map mode, SCP speeds, and close-retreat speed.
- Human loadout selection with `loadout`, `ld`, or `kit`.
- Role-default gear or fully custom loadouts with reserve ammo maintenance.
- Default fallback ammo reserve for role-default firearms, including 9x19 weapons.
- Native SCP:SL round spawn protection support.
- Optional Dust2 schematic arena with ProjectMER.
- Optional bomb mode with plant/defuse flow.
- Optional surface-zone runtime NavMesh while leaving the randomized facility alone.
- English and Chinese command/help strings through `bots language <en|cn>`.

## Requirements

- Windows host.
- SCP:SL Dedicated Server installed through Steam.
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

The script does not overwrite an existing live config.

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
```

Human loadouts:

```text
loadout
loadout <number|preset|role>
```

Aliases:

```text
ld
kit
```

## Configuration

The checked-in sample config is:

```text
ScpslPluginStarter\config.yml
```

Important top-level fields:

- `language`
- `bot_count`
- `difficulty_preset`
- `human_role`
- `bot_role`
- `use_bot_role_default_loadout`
- `enable_spawn_protection`
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

