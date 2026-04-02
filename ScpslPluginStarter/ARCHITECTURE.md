# Warmup Sandbox Architecture

This document is the maintainer-oriented map of the current plugin code.

## Main Responsibilities

### `WarmupSandboxPlugin`

This class is the orchestration layer. It currently owns:

- LabAPI event wiring
- warmup lifecycle
- bot registry
- bot spawning and respawn scheduling
- delayed execution via `Schedule`
- command backing methods
- config persistence
- inventory/loadout application
- dummy-action invocation helpers
- debug logging

Even after the refactor, this file still contains the most surface area. When adding behavior, prefer calling into the focused services rather than adding new domain logic here.

### `PluginConfig`

Defines:

- top-level plugin config
- `WarmupDifficulty`
- `WarmupAiMode`
- `LoadoutDefinition`
- `NamedLoadoutDefinition`
- `AmmoGrant`
- `BotBehaviorDefinition`

This is the authoritative place for runtime defaults and YAML shape.

### `ManagedBotState`

This is persistent per-bot runtime state across the active life of a managed dummy. It stores:

- identity (`PlayerId`, `Nickname`)
- bot brain token
- aim and debug timing state
- movement state
- pending shot verification state
- preferred shoot action state
- `BotEngagementState`

### `BotEngagementState`

Only realistic-combat engagement data lives here:

- current target id
- last seen / visible since ticks
- reaction-ready tick
- remembered aim point
- initial random offsets
- post-reload lock offsets

## Service Boundaries

### `HumanPresetService`

Owns:

- preset enumeration
- fallback preset generation
- selection lookup by number
- selection lookup by preset name
- selection lookup by role enum name
- menu rendering

The plugin stores the selected preset name per player in `_selectedHumanLoadouts`, then asks this service to resolve the effective preset.

### `BotTargetingService`

Owns:

- filtering valid hostile human targets
- checking built-in `Player.Team` hostility
- realistic-vs-classic target selection
- LOS testing
- closest visible target selection
- remembered target handling
- engagement state updates when visibility changes

The most important rule split:

- `Classic` mode returns the closest hostile target.
- `Realistic` mode prefers the closest visible hostile, then falls back to a remembered target inside the sight-memory window.

### `BotAimService`

Owns:

- choosing the desired aim point
- realistic settle behavior from torso bias to head bias
- post-reload head lock
- applying yaw/pitch offsets
- translating desired yaw/pitch into dummy look actions

Current note:

- realistic random offset is applied from the bot’s eye line, not around the target origin
- target head aim uses `target.Camera.position` when available

### `BotCombatService`

Owns:

- firearm equip fallback
- realistic firing gate checks
- reaction-time gate checks
- reload-lock state updates

This service intentionally does not invoke dummy shoot actions itself. The plugin still owns the final fire path because that path also interacts with bot debug logging, shot verification, fallback handling, and release scheduling.

### `BotMovementService`

Owns:

- preferred range maintenance
- linear move vs strafe choice
- crowd avoidance
- stuck detection integration
- unstuck behavior

The bot brain gives it a target position, not a target player, so remembered-target movement can still work when LOS is lost.

## Bot Brain Flow

`RunBotBrain()` in `WarmupSandboxPlugin` is the core loop.

Order of operations:

1. reject stale generation or stale brain token
2. reject dead/destroyed/spectator dummies
3. update stuck state
4. select a target through `BotTargetingService`
5. move through `BotMovementService`
6. equip firearm through `BotCombatService`
7. aim through `BotAimService`
8. manage reserve ammo
9. reload if required
10. fire if allowed
11. schedule the next think tick

This loop is intentionally timer-driven rather than coroutine-driven. Every iteration reschedules itself.

## Human Spawn Flow

When a managed human spawns:

1. the plugin resolves the selected preset
2. if the preset uses role-default gear, only the role respawn matters
3. if the preset uses custom gear, the plugin applies that loadout after spawn
4. vitals are restored

`loadout` is the player-facing selector, but it now means “spawn preset”, not just “weapon kit”.

## Bot Spawn / Respawn Flow

When a bot is created:

1. `SpawnBot()` creates a dummy and allocates `ManagedBotState`
2. `ActivateSpawnedBot()` assigns the configured bot role
3. `ScheduleInitialBotActivation()` retries until the dummy is ready for combat actions
4. `ConfigureSpawnedBot()` applies loadout, resets state, and starts the bot brain

On death:

- the bot entry is kept
- its brain token increments
- respawn is scheduled
- stale delayed actions are ignored because the token changed

## Shot Verification And Action Fallback

The shoot path currently does more than “press the fire button”.

It tracks:

- requested/matched shoot action name
- whether loaded ammo changed after the attempt
- whether a `ShotWeapon` event arrived
- dry-fire count
- preferred shoot action name for future attempts

This exists because dummy actions can successfully invoke without producing a real firearm shot.

## Practical Editing Guidance

If you need to make changes:

- change presets or loadout defaults in `PluginConfig.cs`
- change command surface in `WarmupCommand.cs`, `LoadoutCommand.cs`, or `ModHelpCommand.cs`
- change target behavior in `BotTargetingService.cs`
- change aim behavior in `BotAimService.cs`
- change reload/fire gates in `BotCombatService.cs`
- change movement in `BotMovementService.cs`
- change lifecycle/event scheduling in `WarmupSandboxPlugin.cs`

If you are unsure where something belongs, ask:

- “Is this domain logic?” Put it in a service.
- “Is this event scheduling, config save/load, or cross-service glue?” Put it in `WarmupSandboxPlugin`.
