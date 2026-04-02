# SCP:SL Warmup Sandbox

`ScpslPluginStarter` is a LabAPI plugin for running a gunplay warmup sandbox in SCP:SL. It manages a warmup lifecycle, respawns human players into selectable presets, spawns dummy bots, and drives those bots through dummy actions for movement, aiming, firing, reloading, and target selection.

## What The Plugin Does

- Keeps a warmup session running without normal round-end pressure when configured to do so.
- Respawns live human players into configurable spawn presets through the `loadout` command.
- Spawns and maintains a configurable number of dummy bots.
- Lets bot difficulty and bot AI mode be changed independently.
- Supports a `Realistic` AI overlay for faction-aware target selection, LOS-only firing, target memory, reacquire delay, aim settling, and post-reload head lock.
- Refills and manages bot/human loadouts, reserve ammo, and bot weapon equip fallback.

## Repository Layout

- `ScpslPluginStarter/ScpslPluginStarter.csproj`
  The .NET Framework 4.8 project. It references SCP:SL server assemblies from the local dedicated-server install and copies the built DLL into the LabAPI global plugin folder after build.
- `ScpslPluginStarter/WarmupSandboxPlugin.cs`
  Main plugin entry point and orchestration layer. This is where LabAPI events are subscribed, warmup lifecycle is controlled, bots are spawned/scheduled, commands are backed, and shared helpers such as loadout application, dummy-action invocation, and debug logging live.
- `ScpslPluginStarter/PluginConfig.cs`
  All config types and defaults: top-level plugin config, `WarmupDifficulty`, `WarmupAiMode`, loadout definitions, preset definitions, and bot behavior tuning.
- `ScpslPluginStarter/ManagedBotState.cs`
  Persistent runtime state per managed bot.
- `ScpslPluginStarter/BotEngagementState.cs`
  Per-bot realistic-combat engagement state: current target, last seen timing, reaction gating, initial aim offsets, reload lock state, and remembered aim point.
- `ScpslPluginStarter/HumanPresetService.cs`
  Resolves human presets and builds the `loadout` menu text.
- `ScpslPluginStarter/BotTargetingService.cs`
  Target filtering, team hostility checks, LOS evaluation, closest-visible target selection, and remembered-target behavior.
- `ScpslPluginStarter/BotAimService.cs`
  Converts desired aim points into dummy look actions.
- `ScpslPluginStarter/BotCombatService.cs`
  Firearm equip fallback, realistic firing gates, and reload-lock behavior.
- `ScpslPluginStarter/BotMovementService.cs`
  Range-keeping, strafing, crowd avoidance, and unstuck movement.
- `ScpslPluginStarter/WarmupCommand.cs`
  Remote Admin `warmup` command.
- `ScpslPluginStarter/LoadoutCommand.cs`
  Player/client `loadout` command.
- `ScpslPluginStarter/ModHelpCommand.cs`
  Remote Admin `modhelp` helper.
- `ScpslPluginStarter/config.yml`
  Checked-in sample config showing the current shape and defaults.
- `ScpslPluginStarter/ARCHITECTURE.md`
  Deeper code walkthrough for maintainers.

## Runtime Flow

1. `WarmupSandboxPlugin.Enable()` subscribes to server/player events and reapplies the configured difficulty preset.
2. Depending on config, the plugin can auto-start on `WaitingForPlayers`, first human join, or `RoundStarted`.
3. `RestartWarmup()` increments the generation token, clears managed bots, and schedules `SetupWarmup()`.
4. `SetupWarmup()` respawns all managed human players into their selected presets, then schedules bot population.
5. `SpawnBot()` creates dummy players and tracks them in `_managedBots`.
6. `ConfigureSpawnedBot()` applies the bot loadout, resets runtime state, and schedules the bot brain.
7. `RunBotBrain()` performs the bot loop:
   - update stuck state
   - select a target
   - move toward or around that target
   - ensure a firearm is equipped
   - aim
   - reload if needed
   - fire if allowed
   - schedule the next think tick

The plugin uses a warmup generation token and per-bot brain token so stale delayed actions do not continue after restarts, respawns, or shutdown.

## Human Presets And Loadouts

Human spawn presets are stored in `human_loadout_presets`. Each preset is a full spawn preset, not just a weapon kit:

- `name`
- `description`
- `role`
- `use_role_default_loadout`
- optional `loadout`

Preset selection rules:

- `loadout <number>` selects by menu position.
- `loadout <name>` selects by preset name.
- `loadout <role>` also works by role enum name.

Examples:

- `loadout CiInsurgent`
- `loadout ChaosConscript`
- `loadout NtfCaptain`
- `loadout AK`

Current default presets include:

- `NtfPrivate`
- `NtfSergeant`
- `NtfCaptain`
- `Guard`
- `ChaosRepressor`
- `CiInsurgent` -> `RoleTypeId.ChaosConscript`
- `ChaosMarauder`
- `Rifle`
- `AK`
- `SMG`

If `use_role_default_loadout` is `true`, the player respawns into the class and keeps the role’s default gear. If it is `false`, the configured custom loadout is applied.

## Bot Difficulty vs AI Mode

These are separate controls.

- `WarmupDifficulty`
  Controls timing/range defaults such as think interval, shot interval, release delay, and preferred range.
- `WarmupAiMode`
  Controls bot behavior model.

Current AI modes:

- `Classic`
  Legacy-style bot behavior using the same movement/shoot loop without realistic LOS/engagement logic.
- `Realistic`
  Human-only overlay for:
  - built-in team hostility checks
  - visible-target prioritization
  - LOS-only firing
  - sight memory
  - reacquire delay
  - initial inaccuracy that settles over time
  - post-reload head-lock behavior

## Commands

Remote Admin:

- `warmup status`
- `warmup start`
- `warmup restart`
- `warmup roundrestart`
- `warmup stop`
- `warmup save`
- `warmup difficulty <easy|normal|hard|hardest>`
- `warmup aimode <classic|realistic>`
- `warmup set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|keepmagfilled|aimode> <value>`
- `modhelp`

Player/client:

- `loadout`
- `loadout <number|preset|role>`

Aliases:

- `warmup`, `ws`, `warmupsandbox`
- `loadout`, `ld`, `kit`

## Config Model

Top-level config areas:

- warmup lifecycle and scheduling
- human role/preset defaults
- bot role/loadout defaults
- `bot_behavior`

Important `bot_behavior` fields:

- `ai_mode`
- `think_interval_min_ms`
- `think_interval_max_ms`
- `min_shot_interval_ms`
- `preferred_range`
- `range_tolerance`
- `keep_magazine_filled`
- `target_aim_height_offset`
- realistic combat tuning such as:
  - `realistic_sight_memory_ms`
  - `realistic_reacquire_delay_ms`
  - `realistic_initial_yaw_offset_max_degrees`
  - `realistic_initial_pitch_offset_max_degrees`
  - `realistic_aim_settle_ms`
  - `realistic_reload_lock_offset_max_degrees`
  - `realistic_head_aim_height_offset`
  - `realistic_los_debug_logging`

Use the checked-in sample at `ScpslPluginStarter/config.yml` as the reference shape.

## Build And Deploy

Build:

```powershell
dotnet build .\ScpslPluginStarter\ScpslPluginStarter.csproj
```

The project is set up to copy the built DLL into:

- `%AppData%\SCP Secret Laboratory\LabAPI\plugins\global`

That happens through the `DeployToLabApi` target in the project file after a successful build.

Manual plugin DLL path:

- `ScpslPluginStarter\bin\Debug\net48\ScpslPluginStarter.dll`

## Debugging Notes

The plugin has detailed bot logging when `EnableDebugLogging` is enabled.

Common log families:

- `[BotCombat:...]`
  Target, team relationship, LOS state, equipped item, and combat snapshot.
- `[BotAim:...]`
  Desired aim, applied aim, raw look state, and dummy look actions used.
- `[BotDebug:...] shot-*`
  Fire attempts, cooldown skips, release actions, and post-shot verification.
- `[BotDebug:...] reload-*`
  Reload attempts and reload events.

`realistic_los_debug_logging` is useful when tuning `Realistic` mode LOS behavior.

## Current Architecture Notes

The plugin has been partially refactored away from a single god script, but `WarmupSandboxPlugin.cs` still contains a large amount of orchestration and shared plumbing. The focused service files are the right place for future behavior work:

- preset lookup/menu work belongs in `HumanPresetService`
- target filtering/LOS logic belongs in `BotTargetingService`
- aim-point/aim-action logic belongs in `BotAimService`
- equip/reload/fire gating belongs in `BotCombatService`
- movement/unstuck logic belongs in `BotMovementService`

If you are extending behavior, prefer pushing logic outward into those services instead of growing `WarmupSandboxPlugin.cs` further.
