using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InventorySystem.Items;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using Mirror;
using NetworkManagerUtils.Dummies;
using NorthwoodLib;
using PlayerRoles;
using UnityEngine;
using ApiLogger = LabApi.Features.Console.Logger;

namespace ScpslPluginStarter;

public sealed class WarmupSandboxPlugin : Plugin<PluginConfig>
{
    private static readonly ActionDispatcher? MainThreadActions = typeof(MainThreadDispatcher)
        .GetField("UpdateDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
        .GetValue(null) as ActionDispatcher;

    private readonly Dictionary<int, ManagedBotState> _managedBots = new();
    private readonly Dictionary<int, string> _selectedHumanLoadouts = new();
    private readonly System.Random _random = new();
    private readonly HumanPresetService _humanPresetService = new();
    private readonly BotTargetingService _botTargetingService = new();
    private readonly BotCombatService _botCombatService = new();
    private readonly BotAimService _botAimService = new();
    private readonly BotMovementService _botMovementService = new();
    private int _botSequence;
    private int _warmupGeneration;
    private bool _warmupActive;

    public static WarmupSandboxPlugin? Instance { get; private set; }
    public override string Name => "WarmupSandbox";
    public override string Description => "Warmup sandbox with moving dummy bots.";
    public override string Author => "Michael";
    public override Version Version => new(0, 1, 0);
    public override Version RequiredApiVersion => new(1, 1, 6);

    public override void Enable()
    {
        Instance = this;
        ApplyDifficultyPreset(Config.DifficultyPreset, persist: false);
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundRestarted += OnRoundRestarted;
        ServerEvents.RoundEndingConditionsCheck += OnRoundEndingConditionsCheck;
        PlayerEvents.Joined += OnPlayerJoined;
        PlayerEvents.Spawned += OnPlayerSpawned;
        PlayerEvents.Death += OnPlayerDeath;
        PlayerEvents.Left += OnPlayerLeft;
        PlayerEvents.ShotWeapon += OnPlayerShotWeapon;
        PlayerEvents.ReloadedWeapon += OnPlayerReloadedWeapon;
        PlayerEvents.ChangedItem += OnPlayerChangedItem;
        ApiLogger.Info($"[{Name}] Enabled.");
    }

    public override void Disable()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }

        _warmupGeneration++;
        _warmupActive = false;
        CleanupManagedBots();
        PlayerEvents.ChangedItem -= OnPlayerChangedItem;
        PlayerEvents.ReloadedWeapon -= OnPlayerReloadedWeapon;
        PlayerEvents.ShotWeapon -= OnPlayerShotWeapon;
        PlayerEvents.Left -= OnPlayerLeft;
        PlayerEvents.Death -= OnPlayerDeath;
        PlayerEvents.Spawned -= OnPlayerSpawned;
        PlayerEvents.Joined -= OnPlayerJoined;
        ServerEvents.RoundEndingConditionsCheck -= OnRoundEndingConditionsCheck;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        ServerEvents.RoundStarted -= OnRoundStarted;
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ApiLogger.Info($"[{Name}] Disabled.");
    }

    private void OnWaitingForPlayers()
    {
        if (Config.AutoStartOnWaitingForPlayers && Player.List.Any(IsManagedHuman))
        {
            RestartWarmup("waiting for players");
        }
    }

    private void OnRoundStarted()
    {
        if (Config.AutoStartOnRoundStarted)
        {
            RestartWarmup("round started");
        }
    }

    private void OnRoundRestarted()
    {
        _warmupGeneration++;
        _warmupActive = false;
        CleanupManagedBots();
    }

    private void OnRoundEndingConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
    {
        if (_warmupActive && Config.SuppressRoundEnd)
        {
            ev.CanEnd = false;
        }
    }

    private void OnPlayerJoined(PlayerJoinedEventArgs ev)
    {
        if (Config.ForceRoundStartOnFirstPlayer && IsManagedHuman(ev.Player) && !Round.IsRoundStarted)
        {
            int generation = _warmupGeneration;
            Schedule(() =>
            {
                if (IsCurrentGeneration(generation) && !Round.IsRoundStarted && Player.List.Any(IsManagedHuman))
                {
                    Round.Start();
                }
            }, Config.JoinSetupDelayMs);
        }

        if (!_warmupActive && Config.AutoStartOnFirstPlayer && IsManagedHuman(ev.Player))
        {
            int generation = _warmupGeneration;
            Schedule(() =>
            {
                if (IsCurrentGeneration(generation) && !_warmupActive)
                {
                    RestartWarmup("player joined");
                }
            }, Config.JoinSetupDelayMs);
        }

        if (!_warmupActive || !IsManagedHuman(ev.Player))
        {
            return;
        }

        int currentGeneration = _warmupGeneration;
        Schedule(() =>
        {
            if (IsCurrentGeneration(currentGeneration) && IsManagedHuman(ev.Player))
            {
                RespawnHuman(ev.Player);
            }
        }, Config.JoinSetupDelayMs);
    }

    private void OnPlayerSpawned(PlayerSpawnedEventArgs ev)
    {
        if (!_warmupActive)
        {
            return;
        }

        if (IsManagedBot(ev.Player))
        {
            if (_managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState botState)
                && ev.Player.Role != RoleTypeId.Spectator)
            {
                botState.RespawnRole = ev.Player.Role;
            }

            ConfigureSpawnedBot(ev.Player);
            return;
        }

        if (IsManagedHuman(ev.Player) && ev.Player.Role != RoleTypeId.Spectator)
        {
            ConfigureSpawnedHuman(ev.Player);
        }
    }

    private void OnPlayerDeath(PlayerDeathEventArgs ev)
    {
        if (!_warmupActive)
        {
            return;
        }

        if (IsManagedBot(ev.Player))
        {
            if (_managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState botState)
                && ev.Player.Role != RoleTypeId.Spectator)
            {
                botState.RespawnRole = ev.Player.Role;
            }

            ScheduleBotRespawn(ev.Player.PlayerId);
            return;
        }

        if (IsManagedHuman(ev.Player))
        {
            ScheduleHumanRespawn(ev.Player.PlayerId);
        }
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        _selectedHumanLoadouts.Remove(ev.Player.PlayerId);

        if (_managedBots.Remove(ev.Player.PlayerId))
        {
            EnsureBotPopulation(_warmupGeneration);
        }
    }

    private void OnPlayerShotWeapon(PlayerShotWeaponEventArgs ev)
    {
        if (!_warmupActive || !IsManagedParticipant(ev.Player))
        {
            return;
        }

        if (IsManagedBot(ev.Player))
        {
            if (_managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState state))
            {
                state.LastShotEventTick = Environment.TickCount;
                state.DryFireCount = 0;
                if (!string.IsNullOrWhiteSpace(state.LastShotActionName))
                {
                    state.PreferredShootActionName = state.LastShotActionName;
                }
            }

            LogBotEventByPlayerId(ev.Player.PlayerId, $"shot-event item={ev.FirearmItem?.Type} loaded={GetLoadedAmmoSafe(ev.FirearmItem)} reserve={GetReserveAmmoSafe(ev.Player, ev.FirearmItem)}");
        }

        MaintainReserveAmmo(ev.Player, ev.FirearmItem);
    }

    private void OnPlayerReloadedWeapon(PlayerReloadedWeaponEventArgs ev)
    {
        if (!_warmupActive || !IsManagedParticipant(ev.Player))
        {
            return;
        }

        if (IsManagedBot(ev.Player))
        {
            LogBotEventByPlayerId(ev.Player.PlayerId, $"reload-event item={ev.FirearmItem?.Type} loaded={GetLoadedAmmoSafe(ev.FirearmItem)} reserve={GetReserveAmmoSafe(ev.Player, ev.FirearmItem)}");
        }

        MaintainReserveAmmo(ev.Player, ev.FirearmItem);

        if (IsManagedBot(ev.Player)
            && ev.FirearmItem != null
            && _managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState state))
        {
            _botCombatService.OnReloaded(state, Config.BotBehavior, _random);
        }
    }

    private void OnPlayerChangedItem(PlayerChangedItemEventArgs ev)
    {
        if (_warmupActive && IsManagedParticipant(ev.Player))
        {
            MaintainReserveAmmo(ev.Player, ev.NewItem as FirearmItem);
        }
    }

    private void RestartWarmup(string reason)
    {
        _warmupGeneration++;
        _warmupActive = true;
        ApiLogger.Info($"[{Name}] Starting warmup sandbox ({reason}).");
        CleanupManagedBots();
        int generation = _warmupGeneration;
        Schedule(() => SetupWarmup(generation), Config.InitialSetupDelayMs);
    }

    private void SetupWarmup(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        if (!Player.List.Any(IsManagedHuman))
        {
            ApiLogger.Info($"[{Name}] Warmup setup deferred because no authenticated human players are present yet.");
            _warmupActive = false;
            return;
        }

        foreach (Player player in Player.List.Where(IsManagedHuman))
        {
            RespawnHuman(player);
        }

        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation) || !_warmupActive)
            {
                return;
            }

            EnsureBotPopulation(generation);
        }, Config.BotSpawnDelayMs);

        if (Config.BroadcastWarmupStatus)
        {
            foreach (Player player in Player.List.Where(IsManagedHuman))
            {
                player.SendHint($"{Name} active: {Config.BotCount} bots.", 4f);
            }
        }
    }

    private void RespawnHuman(Player player)
    {
        if (IsManagedHuman(player))
        {
            player.SetRole(GetHumanRole(player), RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        }
    }

    private void ScheduleHumanRespawn(int playerId)
    {
        int generation = _warmupGeneration;
        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation) || !Player.TryGet(playerId, out Player player) || !IsManagedHuman(player))
            {
                return;
            }

            player.SetRole(GetHumanRole(player), RoleChangeReason.Respawn, RoleSpawnFlags.All);
        }, Config.HumanRespawnDelayMs);
    }

    private void EnsureBotPopulation(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        CleanupMissingBotEntries();
        while (_managedBots.Count < Config.BotCount)
        {
            SpawnBot(generation);
        }
    }

    private void SpawnBot(int generation)
    {
        ReferenceHub hub = DummyUtils.SpawnDummy($"{Config.BotNamePrefix} {++_botSequence}");
        if (hub == null)
        {
            ApiLogger.Warn($"[{Name}] Failed to spawn a dummy bot.");
            return;
        }

        Player bot = Player.Get(hub);
        ManagedBotState state = new(bot.PlayerId, bot.Nickname);
        state.RespawnRole = Config.BotRole;
        state.LastPosition = bot.Position;
        _managedBots[bot.PlayerId] = state;
        Schedule(() => ActivateSpawnedBot(bot.PlayerId, generation), Config.BotRoleAssignDelayMs);
    }

    private void ActivateSpawnedBot(int playerId, int generation)
    {
        if (!IsCurrentGeneration(generation)
            || !_managedBots.TryGetValue(playerId, out ManagedBotState state)
            || !Player.TryGet(playerId, out Player bot)
            || bot.IsDestroyed)
        {
            return;
        }

        state.SpawnSetupCompleted = false;
        state.LastPosition = bot.Position;
        state.StuckTicks = 0;
        state.UnstuckUntilTick = 0;
        state.ConsecutiveLinearMoves = 0;
        state.Engagement.Reset();
        state.PreferredShootActionName = "";
        state.LastShotActionName = "";
        state.PendingShotLoadedAmmo = -1;
        state.PendingShotVerificationTick = 0;
        state.LastShotEventTick = 0;
        state.DryFireCount = 0;
        state.LoggedShootActionCatalog = false;

        bot.SetRole(GetBotRespawnRole(state), RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        ScheduleInitialBotActivation(bot.PlayerId, generation, attempt: 0);
    }

    private void ScheduleInitialBotActivation(int playerId, int generation, int attempt)
    {
        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation)
                || !_managedBots.TryGetValue(playerId, out ManagedBotState state)
                || !Player.TryGet(playerId, out Player bot)
                || bot.IsDestroyed)
            {
                return;
            }

            if (bot.Role == RoleTypeId.Spectator)
            {
                LogBotEvent(state, $"initial-activation retry={attempt} reason=spectator");
                bot.SetRole(GetBotRespawnRole(state), RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
                ScheduleNextInitialActivationAttempt(playerId, generation, attempt);
                return;
            }

            if (IsBotCombatReady(bot))
            {
                if (!state.SpawnSetupCompleted)
                {
                    LogBotEvent(state, $"initial-activation ready-configure retry={attempt}");
                    ConfigureSpawnedBot(bot);
                    ScheduleNextInitialActivationAttempt(playerId, generation, attempt);
                    return;
                }

                LogBotEvent(state, $"initial-activation ready retry={attempt}");
                return;
            }

            LogBotEvent(state, $"initial-activation retry={attempt} reason=not-ready");
            ConfigureSpawnedBot(bot);
            ScheduleNextInitialActivationAttempt(playerId, generation, attempt);
        }, attempt == 0 ? Config.BotInitialActivationDelayMs : Config.BotActivationRetryDelayMs);
    }

    private void ConfigureSpawnedHuman(Player player)
    {
        NamedLoadoutDefinition? preset = GetSelectedHumanPreset(player);
        LoadoutDefinition? loadout = GetHumanLoadout(player);
        if (!(preset?.UseRoleDefaultLoadout ?? false) && loadout != null)
        {
            ApplyLoadout(player, loadout);
        }

        RestoreVitals(player);
    }

    private void ConfigureSpawnedBot(Player player)
    {
        if (!_managedBots.TryGetValue(player.PlayerId, out ManagedBotState state))
        {
            return;
        }

        if (player.Role != RoleTypeId.Spectator)
        {
            state.RespawnRole = player.Role;
        }

        ApplyLoadout(player, Config.BotLoadout);
        RestoreVitals(player);
        state.SpawnSetupCompleted = true;
        state.LastPosition = player.Position;
        state.StuckTicks = 0;
        state.UnstuckUntilTick = 0;
        state.ConsecutiveLinearMoves = 0;
        state.Engagement.Reset();
        state.PreferredShootActionName = "";
        state.LastShotActionName = "";
        state.PendingShotLoadedAmmo = -1;
        state.PendingShotVerificationTick = 0;
        state.LastShotEventTick = 0;
        state.DryFireCount = 0;
        state.LoggedShootActionCatalog = false;
        state.BrainToken++;
        ScheduleBotBrain(player.PlayerId, state.BrainToken, _warmupGeneration);
    }

    private void ScheduleBotRespawn(int playerId)
    {
        if (!_managedBots.TryGetValue(playerId, out ManagedBotState state))
        {
            return;
        }

        state.SpawnSetupCompleted = false;
        state.BrainToken++;
        int token = state.BrainToken;
        int generation = _warmupGeneration;
        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation)
                || !_managedBots.TryGetValue(playerId, out ManagedBotState current)
                || current.BrainToken != token)
            {
                return;
            }

            if (Player.TryGet(playerId, out Player bot))
            {
                bot.SetRole(GetBotRespawnRole(state), RoleChangeReason.Respawn, RoleSpawnFlags.All);
                return;
            }

            _managedBots.Remove(playerId);
            EnsureBotPopulation(generation);
        }, Config.BotRespawnDelayMs);
    }

    private void ScheduleBotBrain(int playerId, int brainToken, int generation)
    {
        int delay = Next(Config.BotBehavior.ThinkIntervalMinMs, Config.BotBehavior.ThinkIntervalMaxMs);
        Schedule(() => RunBotBrain(playerId, brainToken, generation), delay);
    }

    private void ScheduleNextInitialActivationAttempt(int playerId, int generation, int attempt)
    {
        if (attempt + 1 >= Config.BotActivationMaxAttempts)
        {
            if (_managedBots.TryGetValue(playerId, out ManagedBotState state))
            {
                LogBotEvent(state, $"initial-activation gave-up attempts={Config.BotActivationMaxAttempts}");
            }

            return;
        }

        ScheduleInitialBotActivation(playerId, generation, attempt + 1);
    }

    private void RunBotBrain(int playerId, int brainToken, int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        if (!_managedBots.TryGetValue(playerId, out ManagedBotState state) || state.BrainToken != brainToken)
        {
            return;
        }

        if (!Player.TryGet(playerId, out Player bot) || bot.IsDestroyed || bot.Role == RoleTypeId.Spectator)
        {
            return;
        }

        BotTargetSelection? target = _botTargetingService.SelectTarget(
            bot,
            state,
            Player.List,
            Config.BotBehavior,
            _random);

        if (target != null)
        {
            FirearmItem? firearm = _botCombatService.EnsureFirearmEquipped(bot);
            LogCombatState(bot, state, target, firearm);
            _botAimService.AimAt(bot, state, target, Config.BotBehavior, TryInvokeDummyAction, LogAimStep, LogAimDebug);
            int nowTick = Environment.TickCount;
            bool aimAligned = _botAimService.IsAimAligned(bot, state, Config.BotBehavior);
            string? shootReason = null;
            bool shotCooldownActive = firearm != null
                && unchecked(nowTick - state.LastShotTick) < Config.BotBehavior.MinShotIntervalMs;
            bool canShoot = firearm != null
                && _botCombatService.CanShoot(bot, state, target, Config.BotBehavior, nowTick, out shootReason);
            float retreatStartDistance =
                Config.BotBehavior.PreferredRange - Config.BotBehavior.RangeTolerance + Config.BotBehavior.RetreatStartDistanceBuffer;
            bool spacingRetreatRequired = target.Distance < retreatStartDistance;
            bool shouldPressureMove = target.IsRememberedTarget
                || !target.HasLineOfSight
                || !aimAligned
                || shotCooldownActive
                || !canShoot;
            bool movementExpected = spacingRetreatRequired
                || shouldPressureMove
                || target.Distance > Config.BotBehavior.PreferredRange + Config.BotBehavior.RangeTolerance;
            _botMovementService.UpdateStuckState(bot, state, Config.BotBehavior.StuckDistanceThreshold, movementExpected);
            string moveState = spacingRetreatRequired
                ? "retreat"
                : target.Distance > Config.BotBehavior.PreferredRange + Config.BotBehavior.RangeTolerance
                    ? "chase"
                    : "hold";
            LogNavDebug(
                bot,
                state,
                $"move-state state={moveState} range={target.Distance:F1} retreatAt={retreatStartDistance:F1} preferred={Config.BotBehavior.PreferredRange:F1} tolerance={Config.BotBehavior.RangeTolerance:F1} visible={target.HasLineOfSight} remembered={target.IsRememberedTarget} aimAligned={aimAligned} canShoot={canShoot} shotCooldown={shotCooldownActive} pressure={shouldPressureMove}");

            if (spacingRetreatRequired)
            {
                _botMovementService.MoveBot(
                    bot,
                    state,
                    target.Target.Position,
                    Player.List,
                    Config.BotBehavior,
                    TryInvokeDummyAction,
                    Next,
                    IsManagedBot,
                    LogNavDebug);
            }
            else if (shouldPressureMove)
            {
                _botMovementService.PursueTargetDirectly(
                    bot,
                    state,
                    target.Target.Position,
                    Player.List,
                    Config.BotBehavior,
                    TryInvokeDummyAction,
                    Next,
                    LogNavDebug);
            }
            else
            {
                _botMovementService.MoveBot(
                    bot,
                    state,
                    target.Target.Position,
                    Player.List,
                    Config.BotBehavior,
                    TryInvokeDummyAction,
                    Next,
                    IsManagedBot,
                    LogNavDebug);
            }

            if (firearm != null)
            {
                MaintainReserveAmmo(bot, firearm);
                if (Config.BotBehavior.KeepMagazineFilled)
                {
                    RefillFirearm(firearm);
                }
                else if (firearm.IsReloadingOrUnloading)
                {
                    ScheduleBotBrain(playerId, brainToken, generation);
                    return;
                }
                else if (GetLoadedAmmo(firearm) <= 1)
                {
                    TryReloadBot(bot, state, firearm);
                    ScheduleBotBrain(playerId, brainToken, generation);
                    return;
                }
            }

            if (Config.BotBehavior.EnableCombatActions && firearm != null)
            {
                if (canShoot && !shotCooldownActive)
                {
                    TryShootBot(bot, state, target.Target, brainToken, generation);
                }
                else
                {
                    string reason = shotCooldownActive
                        ? "cooldown"
                        : (shootReason ?? (!aimAligned ? "not-aimed" : "blocked"));
                    LogBotDebug(
                        state,
                        $"shot-skip {reason} target={target.Target.Nickname}#{target.Target.PlayerId} distance={target.Distance:F1} remembered={target.IsRememberedTarget} visible={target.HasLineOfSight} aimAligned={aimAligned} headLos={target.HeadHasLineOfSight} torsoLos={target.TorsoHasLineOfSight}");
                }
            }
            else if (Config.BotBehavior.EnableCombatActions)
            {
                string inventory = string.Join(",", bot.Items.Select(item => item.Type.ToString()));
                LogBotDebug(state, $"shot-skip no-firearm current={bot.CurrentItem?.Type.ToString() ?? "none"} inventory=[{inventory}]");
            }
        }
        else
        {
            _botMovementService.UpdateStuckState(bot, state, Config.BotBehavior.StuckDistanceThreshold, movementExpected: false);
            state.Engagement.Reset();
            LogBotDebug(state, "no-target");
        }

        ScheduleBotBrain(playerId, brainToken, generation);
    }

    private void TryReloadBot(Player bot, ManagedBotState state, FirearmItem firearm)
    {
        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastReloadAttemptTick) < Config.BotBehavior.MinReloadAttemptIntervalMs)
        {
            return;
        }

        state.LastReloadAttemptTick = nowTick;
        bool triggered = false;
        if (firearm.CanReload && !firearm.IsReloadingOrUnloading)
        {
            triggered = firearm.Reload();
        }

        if (!triggered)
        {
            triggered = TryInvokeDummyAction(bot, Config.BotBehavior.ReloadActionName);
        }

        LogBotEvent(state, $"reload-attempt triggered={triggered} item={firearm.Type} loaded={GetLoadedAmmo(firearm)} reserve={bot.GetAmmo(firearm.AmmoType)}");
    }

    private void ApplyLoadout(Player player, LoadoutDefinition loadout)
    {
        if (loadout.ClearInventory)
        {
            player.ClearInventory();
            player.ClearAmmo();
        }

        FirearmItem? primaryFirearm = null;
        foreach (ItemType itemType in loadout.Items ?? Array.Empty<ItemType>())
        {
            Item item = player.AddItem(itemType, ItemAddReason.AdminCommand);
            if (primaryFirearm == null && item is FirearmItem firearm)
            {
                primaryFirearm = firearm;
            }
        }

        foreach (AmmoGrant grant in loadout.Ammo ?? Array.Empty<AmmoGrant>())
        {
            player.SetAmmo(grant.Type, grant.Amount);
        }

        if (loadout.EquipFirstFirearm && primaryFirearm != null)
        {
            player.CurrentItem = primaryFirearm;
        }

        if (loadout.RefillActiveFirearmOnSpawn)
        {
            RefillFirearm(primaryFirearm);
        }

        MaintainReserveAmmo(player, primaryFirearm);
    }

    private NamedLoadoutDefinition? GetSelectedHumanPreset(Player player)
    {
        return _humanPresetService.GetSelectedPreset(Config, _selectedHumanLoadouts, player);
    }

    private LoadoutDefinition? GetHumanLoadout(Player player)
    {
        return GetSelectedHumanPreset(player)?.Loadout ?? Config.HumanLoadout;
    }

    private RoleTypeId GetHumanRole(Player player)
    {
        return GetSelectedHumanPreset(player)?.Role ?? Config.HumanRole;
    }

    private NamedLoadoutDefinition[] GetHumanLoadoutPresets()
    {
        return _humanPresetService.GetPresets(Config);
    }

    private NamedLoadoutDefinition? FindHumanLoadoutPreset(string selector)
    {
        return _humanPresetService.FindPreset(Config, selector);
    }

    private void ShowLoadoutMenuHint(Player player, float duration)
    {
        player.SendHint(BuildLoadoutMenu(player), duration);
    }

    public string BuildLoadoutMenu(Player player)
    {
        return _humanPresetService.BuildMenu(Config, _selectedHumanLoadouts, player);
    }

    public string GetSelectedHumanLoadoutName(Player player)
    {
        return _humanPresetService.GetSelectedPresetName(Config, _selectedHumanLoadouts, player);
    }

    public bool TrySelectHumanLoadout(Player player, string selector, bool applyNow, out string response)
    {
        if (!IsManagedHuman(player))
        {
            response = "Only active human players can choose a loadout.";
            return false;
        }

        NamedLoadoutDefinition? preset = FindHumanLoadoutPreset(selector);
        if (preset == null)
        {
            response = BuildLoadoutMenu(player);
            return false;
        }

        _selectedHumanLoadouts[player.PlayerId] = preset.Name;
        response = $"Selected preset: {preset.Name} ({preset.Role}).";

        RoleTypeId selectedRole = preset.Role;
        bool shouldRespawnForPreset = preset.UseRoleDefaultLoadout || player.Role != selectedRole;
        if (applyNow && player.Role != RoleTypeId.Spectator)
        {
            if (shouldRespawnForPreset)
            {
                player.SetRole(selectedRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
                response += preset.UseRoleDefaultLoadout
                    ? " Respawning now with role-default gear."
                    : " Respawning now with the selected role.";
            }
            else if (preset.Loadout != null)
            {
                ApplyLoadout(player, preset.Loadout);
                RestoreVitals(player);
                FirearmItem? firearm = player.CurrentItem as FirearmItem ?? player.Items.OfType<FirearmItem>().FirstOrDefault();
                response += firearm == null
                    ? " Applied immediately."
                    : $" Applied immediately. Ammo={player.GetAmmo(firearm.AmmoType)}.";
            }
        }

        ShowLoadoutMenuHint(player, 6f);
        return true;
    }

    private void EnsureFirearmEquipped(Player player)
    {
        if (player.CurrentItem is FirearmItem)
        {
            return;
        }

        FirearmItem? firearm = player.Items.OfType<FirearmItem>().FirstOrDefault();
        if (firearm != null)
        {
            player.CurrentItem = firearm;
        }
    }

    private static void RefillFirearm(FirearmItem? firearm)
    {
        if (firearm == null)
        {
            return;
        }

        firearm.StoredAmmo = firearm.MaxAmmo;
        firearm.ChamberedAmmo = firearm.ChamberMax;
    }

    private void MaintainReserveAmmo(Player player, FirearmItem? firearm)
    {
        if (firearm == null)
        {
            return;
        }

        LoadoutDefinition? loadout = IsManagedBot(player) ? Config.BotLoadout : GetHumanLoadout(player);
        if (loadout == null)
        {
            return;
        }

        if (!loadout.InfiniteReserveAmmo)
        {
            return;
        }

        ushort targetReserve = GetReserveAmmoTarget(loadout, firearm.AmmoType);
        if (targetReserve > 0 && player.GetAmmo(firearm.AmmoType) < targetReserve)
        {
            player.SetAmmo(firearm.AmmoType, targetReserve);
        }
    }

    private static void RestoreVitals(Player player)
    {
        player.Health = player.MaxHealth;
        player.ArtificialHealth = 0f;
    }

    private void UpdateStuckState(Player bot, ManagedBotState state)
    {
        Vector3 current = bot.Position;
        Vector3 previous = state.LastPosition;
        current.y = 0f;
        previous.y = 0f;

        float movedDistance = Vector3.Distance(current, previous);
        if (movedDistance < Config.BotBehavior.StuckDistanceThreshold)
        {
            state.StuckTicks++;
        }
        else
        {
            state.StuckTicks = 0;
        }

        state.LastPosition = bot.Position;
    }

    private bool MoveBot(Player bot, ManagedBotState state, Player target)
    {
        if (!Config.BotBehavior.EnableStepMovement)
        {
            return false;
        }

        int nowTick = Environment.TickCount;
        if (state.StuckTicks >= Config.BotBehavior.StuckTickThreshold)
        {
            state.UnstuckUntilTick = nowTick + Config.BotBehavior.UnstuckDurationMs;
            state.StuckTicks = 0;
            state.StrafeDirection = Next(0, 2) == 0 ? -1 : 1;
        }

        Vector3 toTarget = target.Position - bot.Position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        bool crowded = IsCrowded(bot);
        if (nowTick < state.UnstuckUntilTick || (crowded && distance <= Config.BotBehavior.PreferredRange + Config.BotBehavior.RangeTolerance))
        {
            return TryUnstuckMove(bot, state);
        }

        if (Next(0, 100) < Config.BotBehavior.StrafeDirectionChangeChancePercent)
        {
            state.StrafeDirection *= -1;
        }

        if (state.ConsecutiveLinearMoves >= Config.BotBehavior.LinearMoveTickThreshold
            && Next(0, 100) < Config.BotBehavior.RandomStrafeAfterLinearChancePercent)
        {
            state.ConsecutiveLinearMoves = 0;
            return TryStrafeMove(bot, state);
        }

        if (distance > Config.BotBehavior.PreferredRange + Config.BotBehavior.RangeTolerance)
        {
            bool moved = TryInvokeDummyAction(bot, Config.BotBehavior.WalkForwardActionNames);
            if (!moved)
            {
                moved = TryStrafeMove(bot, state);
            }

            state.ConsecutiveLinearMoves = moved ? state.ConsecutiveLinearMoves + 1 : 0;
            return moved;
        }

        if (distance < Config.BotBehavior.PreferredRange - Config.BotBehavior.RangeTolerance)
        {
            bool moved = TryInvokeDummyAction(bot, Config.BotBehavior.WalkBackwardActionNames);
            if (!moved)
            {
                moved = TryStrafeMove(bot, state);
            }

            state.ConsecutiveLinearMoves = moved ? state.ConsecutiveLinearMoves + 1 : 0;
            return moved;
        }

        state.ConsecutiveLinearMoves = 0;
        return TryStrafeMove(bot, state);
    }

    private bool TryUnstuckMove(Player bot, ManagedBotState state)
    {
        int roll = Next(0, 100);
        state.StrafeDirection = Next(0, 2) == 0 ? -1 : 1;

        if (roll < 45)
        {
            return TryStrafeMove(bot, state)
                || TryInvokeDummyAction(bot, Config.BotBehavior.WalkBackwardActionNames)
                || TryInvokeDummyAction(bot, Config.BotBehavior.WalkForwardActionNames);
        }

        if (roll < 80)
        {
            return TryInvokeDummyAction(bot, Config.BotBehavior.WalkBackwardActionNames)
                || TryStrafeMove(bot, state)
                || TryInvokeDummyAction(bot, Config.BotBehavior.WalkForwardActionNames);
        }

        return TryInvokeDummyAction(bot, Config.BotBehavior.WalkForwardActionNames)
            || TryStrafeMove(bot, state)
            || TryInvokeDummyAction(bot, Config.BotBehavior.WalkBackwardActionNames);
    }

    private bool TryStrafeMove(Player bot, ManagedBotState state)
    {
        if (Next(0, 100) < Config.BotBehavior.StrafeDirectionChangeChancePercent)
        {
            state.StrafeDirection *= -1;
        }

        string[] actionNames = state.StrafeDirection >= 0
            ? Config.BotBehavior.WalkRightActionNames
            : Config.BotBehavior.WalkLeftActionNames;

        bool moved = TryInvokeDummyAction(bot, actionNames);
        if (!moved)
        {
            string[] opposite = state.StrafeDirection >= 0
                ? Config.BotBehavior.WalkLeftActionNames
                : Config.BotBehavior.WalkRightActionNames;
            moved = TryInvokeDummyAction(bot, opposite);
        }

        return moved;
    }

    private bool IsCrowded(Player bot)
    {
        float radiusSqr = Config.BotBehavior.NearbyBotAvoidanceRadius * Config.BotBehavior.NearbyBotAvoidanceRadius;
        foreach (Player other in Player.List)
        {
            if (other.PlayerId == bot.PlayerId || !IsManagedBot(other) || other.IsDestroyed || other.Role == RoleTypeId.Spectator)
            {
                continue;
            }

            Vector3 offset = other.Position - bot.Position;
            offset.y = 0f;
            if (offset.sqrMagnitude <= radiusSqr)
            {
                return true;
            }
        }

        return false;
    }

    private Player? FindNearestHuman(Vector3 origin)
    {
        return Player.List
            .Where(player => IsManagedHuman(player) && player.Role != RoleTypeId.Spectator)
            .OrderBy(player => (player.Position - origin).sqrMagnitude)
            .FirstOrDefault();
    }

    private void AimAt(Player bot, ManagedBotState state, Player target)
    {
        Vector3 botAimOrigin = bot.Position + Vector3.up * Config.BotBehavior.TargetAimHeightOffset;
        Vector3 targetAimPoint = target.Position + Vector3.up * Config.BotBehavior.TargetAimHeightOffset;
        Vector3 direction = targetAimPoint - botAimOrigin;
        Vector3 flatDirection = new(direction.x, 0f, direction.z);
        if (flatDirection.sqrMagnitude < 0.01f)
        {
            return;
        }

        float yaw = Mathf.Atan2(flatDirection.x, flatDirection.z) * Mathf.Rad2Deg;
        float pitch = 0f;
        if (Config.BotBehavior.EnableVerticalAim)
        {
            float flatDistance = flatDirection.magnitude;
            pitch = Mathf.Atan2(direction.y, Mathf.Max(flatDistance, 0.01f)) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, -Config.BotBehavior.MaxVerticalAimDegrees, Config.BotBehavior.MaxVerticalAimDegrees);
        }

        state.LastDesiredYaw = yaw;
        state.LastDesiredPitch = pitch;
        ApplyAimActions(bot, state, yaw, pitch);
        LogAimDebug(bot, state, target, yaw, pitch, direction);
    }

    private void ApplyAimActions(Player bot, ManagedBotState state, float desiredYaw, float desiredPitch)
    {
        Vector2 rawLookBefore = bot.LookRotation;
        Vector2 currentAim = GetCurrentAim(bot);
        float yawDelta = Mathf.DeltaAngle(currentAim.x, desiredYaw);
        float pitchDelta = desiredPitch - currentAim.y;
        state.LastRawPitchBeforeAim = rawLookBefore.x;
        state.LastRawYawBeforeAim = rawLookBefore.y;
        state.LastYawDelta = yawDelta;
        state.LastPitchDelta = pitchDelta;

        state.LastHorizontalAimActions = ApplyAimAxisActions(
            bot,
            yawDelta,
            Config.BotBehavior.HorizontalAimDeadzoneDegrees,
            Config.BotBehavior.MaxHorizontalAimActionsPerTick,
            Config.BotBehavior.LookHorizontalPositiveActionNames,
            Config.BotBehavior.LookHorizontalNegativeActionNames);

        if (!Config.BotBehavior.EnableVerticalAim)
        {
            state.LastVerticalAimActions = "disabled";
            return;
        }

        state.LastVerticalAimActions = ApplyAimAxisActions(
            bot,
            pitchDelta,
            Config.BotBehavior.VerticalAimDeadzoneDegrees,
            Config.BotBehavior.MaxVerticalAimActionsPerTick,
            Config.BotBehavior.LookVerticalPositiveActionNames,
            Config.BotBehavior.LookVerticalNegativeActionNames);
    }

    private static Vector2 GetCurrentAim(Player bot)
    {
        Vector2 rawLook = bot.LookRotation;
        float pitch = NormalizeSignedAngle(rawLook.x);
        float yaw = NormalizeSignedAngle(rawLook.y);
        return new Vector2(yaw, pitch);
    }

    private static float NormalizeSignedAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle <= -180f)
        {
            angle += 360f;
        }

        return angle;
    }

    private string ApplyAimAxisActions(
        Player bot,
        float delta,
        float deadzone,
        int maxActions,
        string[] positiveActionNames,
        string[] negativeActionNames)
    {
        if (maxActions <= 0 || Mathf.Abs(delta) <= deadzone)
        {
            return "none";
        }

        string[] actionNames = delta >= 0f ? positiveActionNames : negativeActionNames;
        float remaining = Mathf.Abs(delta);
        int used = 0;
        List<string> usedActions = new();

        string[] orderedActionNames = actionNames ?? Array.Empty<string>();
        while (used < maxActions && remaining > deadzone)
        {
            string? chosenActionName = null;
            float chosenStep = 0f;

            foreach (string actionName in orderedActionNames)
            {
                float step = ExtractAimStepDegrees(actionName);
                if (step <= remaining + deadzone || chosenActionName == null)
                {
                    chosenActionName = actionName;
                    chosenStep = step;
                }

                if (step <= remaining + deadzone)
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(chosenActionName) || !TryInvokeDummyAction(bot, chosenActionName))
            {
                break;
            }

            usedActions.Add(chosenActionName);
            remaining -= chosenStep;
            used++;
        }

        return usedActions.Count == 0 ? "none" : string.Join(",", usedActions);
    }

    private static float ExtractAimStepDegrees(string actionName)
    {
        int signIndex = Math.Max(actionName.LastIndexOf('+'), actionName.LastIndexOf('-'));
        if (signIndex < 0 || signIndex >= actionName.Length - 1)
        {
            return 1f;
        }

        string suffix = actionName.Substring(signIndex + 1);
        return float.TryParse(suffix, out float value) ? value : 1f;
    }

    private static int GetLoadedAmmo(FirearmItem firearm)
    {
        return firearm.StoredAmmo + firearm.ChamberedAmmo;
    }

    private static int GetLoadedAmmoSafe(FirearmItem? firearm)
    {
        return firearm == null ? -1 : GetLoadedAmmo(firearm);
    }

    private static int GetReserveAmmoSafe(Player player, FirearmItem? firearm)
    {
        return firearm == null ? -1 : player.GetAmmo(firearm.AmmoType);
    }

    private static ushort GetReserveAmmoTarget(LoadoutDefinition loadout, ItemType ammoType)
    {
        AmmoGrant? grant = (loadout.Ammo ?? Array.Empty<AmmoGrant>()).FirstOrDefault(entry => entry.Type == ammoType);
        return grant?.Amount ?? 0;
    }

    private bool IsBotCombatReady(Player bot)
    {
        if (bot.IsDestroyed || bot.Role == RoleTypeId.Spectator)
        {
            return false;
        }

        if (bot.CurrentItem is not FirearmItem)
        {
            return false;
        }

        return HasDummyAction(bot, Config.BotBehavior.ShootPressActionName)
            && HasAnyDummyAction(bot, Config.BotBehavior.WalkForwardActionNames)
            && HasAnyDummyAction(bot, Config.BotBehavior.WalkLeftActionNames)
            && HasAnyDummyAction(bot, Config.BotBehavior.WalkRightActionNames);
    }

    private bool HasAnyDummyAction(Player bot, IEnumerable<string> actionNames)
    {
        foreach (string actionName in actionNames ?? Array.Empty<string>())
        {
            if (HasDummyAction(bot, actionName))
            {
                return true;
            }
        }

        return false;
    }

    private string[] GetAvailableShootActionNames(Player bot)
    {
        try
        {
            return DummyActionCollector
                .ServerGetActions(bot.ReferenceHub)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)
                    && candidate.Name.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(candidate => candidate.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private bool TryResolveDummyAction(Player bot, string actionName, out DummyAction action, out string resolvedActionName)
    {
        action = default;
        resolvedActionName = string.Empty;
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return false;
        }

        try
        {
            DummyAction[] actions = DummyActionCollector
                .ServerGetActions(bot.ReferenceHub)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name) && candidate.Action != null)
                .ToArray();

            foreach (string variant in GetActionNameVariants(actionName))
            {
                DummyAction match = actions.FirstOrDefault(candidate => string.Equals(candidate.Name, variant, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Name) && match.Action != null)
                {
                    action = match;
                    resolvedActionName = match.Name;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static IEnumerable<string> GetActionNameVariants(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            yield break;
        }

        string trimmed = actionName.Trim();
        yield return trimmed;

        if (trimmed.IndexOf("->", StringComparison.Ordinal) >= 0)
        {
            yield return trimmed.Replace("->", ".");
        }

        if (trimmed.IndexOf(".", StringComparison.Ordinal) >= 0)
        {
            yield return trimmed.Replace(".", "->");
        }
    }

    private bool HasDummyAction(Player bot, string actionName)
    {
        return TryResolveDummyAction(bot, actionName, out _, out _);
    }

    private bool TryInvokeDummyAction(Player bot, IEnumerable<string> actionNames)
    {
        foreach (string actionName in actionNames ?? Array.Empty<string>())
        {
            if (TryInvokeDummyAction(bot, actionName))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryInvokeDummyAction(Player bot, string actionName)
    {
        return TryInvokeDummyAction(bot, actionName, out _);
    }

    private bool TryInvokeDummyAction(Player bot, string actionName, out string resolvedActionName)
    {
        resolvedActionName = string.Empty;

        try
        {
            if (!TryResolveDummyAction(bot, actionName, out DummyAction action, out resolvedActionName))
            {
                return false;
            }

            action.Action.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[{Name}] Failed to invoke dummy action '{actionName}' for {bot.Nickname}: {ex.Message}");
            return false;
        }
    }

    private void TryShootBot(Player bot, ManagedBotState state, Player target, int brainToken, int generation)
    {
        int nowTick = Environment.TickCount;
        FirearmItem? firearm = bot.CurrentItem as FirearmItem;
        int loadedAmmo = firearm == null ? -1 : GetLoadedAmmo(firearm);
        int reserveAmmo = firearm == null ? -1 : bot.GetAmmo(firearm.AmmoType);
        string itemName = bot.CurrentItem?.Type.ToString() ?? "none";
        string targetName = $"{target.Nickname}#{target.PlayerId}";

        if (unchecked(nowTick - state.LastShotTick) < Config.BotBehavior.MinShotIntervalMs)
        {
            LogBotDebug(state, $"shot-skip cooldown target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
            return;
        }

        state.LastShotTick = nowTick;

        if (Config.BotBehavior.UseZoomWhileShooting)
        {
            TryInvokeDummyAction(bot, Config.BotBehavior.ZoomActionName);
        }

        string[] shootCandidates = GetShootActionCandidates(bot, state);
        bool fired = false;
        string actionUsed = "";
        foreach (string candidate in shootCandidates)
        {
            if (!TryInvokeDummyAction(bot, candidate, out string resolvedCandidate))
            {
                continue;
            }

            fired = true;
            actionUsed = string.IsNullOrWhiteSpace(resolvedCandidate) ? candidate : resolvedCandidate;
            break;
        }

        if (!fired)
        {
            LogBotDebug(
                state,
                $"shot-fail candidates=[{string.Join(",", shootCandidates)}] release={Config.BotBehavior.ShootReleaseActionName} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
            return;
        }

        state.PendingShotVerificationTick = nowTick;
        state.PendingShotLoadedAmmo = loadedAmmo;
        state.LastShotActionName = actionUsed;
        LogBotDebug(
            state,
            $"shot-ok action={actionUsed} release={Config.BotBehavior.ShootReleaseActionName} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
        SchedulePostShotVerification(bot.PlayerId, brainToken, generation);

        bool shouldReleaseShoot = actionUsed.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0
            || actionUsed.IndexOf("press", StringComparison.OrdinalIgnoreCase) >= 0;
        if (shouldReleaseShoot && !string.IsNullOrWhiteSpace(Config.BotBehavior.ShootReleaseActionName))
        {
            int releaseToken = brainToken;
            Schedule(() =>
            {
                if (IsCurrentGeneration(generation)
                    && _managedBots.TryGetValue(bot.PlayerId, out ManagedBotState latest)
                    && latest.BrainToken == releaseToken
                    && Player.TryGet(bot.PlayerId, out Player liveBot))
                {
                    bool released = TryInvokeDummyAction(liveBot, Config.BotBehavior.ShootReleaseActionName, out string resolvedReleaseAction);
                    LogBotDebug(latest, $"shot-release action={(string.IsNullOrWhiteSpace(resolvedReleaseAction) ? Config.BotBehavior.ShootReleaseActionName : resolvedReleaseAction)} released={released}");
                }
            }, Config.BotBehavior.ShootReleaseDelayMs);
        }
    }

    private string[] GetShootActionCandidates(Player bot, ManagedBotState state)
    {
        List<string> candidates = new();

        void AddCandidate(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName)
                || candidates.Contains(actionName, StringComparer.OrdinalIgnoreCase)
                || !HasDummyAction(bot, actionName))
            {
                return;
            }

            candidates.Add(actionName);
        }

        AddCandidate(state.PreferredShootActionName);
        AddCandidate(Config.BotBehavior.ShootPressActionName);
        AddCandidate(Config.BotBehavior.AlternateShootPressActionName);
        AddCandidate("Shoot.Click");
        AddCandidate("Shoot.Press");
        AddCandidate("Shoot");

        foreach (string actionName in GetAvailableShootActionNames(bot))
        {
            if (actionName.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            AddCandidate(actionName);
        }

        return candidates.ToArray();
    }

    private void CleanupManagedBots()
    {
        foreach (int playerId in _managedBots.Keys.ToArray())
        {
            if (Player.TryGet(playerId, out Player bot) && bot.GameObject != null)
            {
                NetworkServer.Destroy(bot.GameObject);
            }
        }

        _managedBots.Clear();
    }

    private void CleanupMissingBotEntries()
    {
        foreach (int playerId in _managedBots.Keys.ToArray())
        {
            if (!Player.TryGet(playerId, out Player player) || player.IsDestroyed)
            {
                _managedBots.Remove(playerId);
            }
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        return _warmupGeneration == generation;
    }

    private bool IsManagedBot(Player player)
    {
        return _managedBots.ContainsKey(player.PlayerId);
    }

    private static bool IsManagedHuman(Player player)
    {
        return player != null && !player.IsHost && !player.IsDummy && !player.IsNpc && !player.IsDestroyed;
    }

    private bool IsManagedParticipant(Player player)
    {
        return IsManagedBot(player) || IsManagedHuman(player);
    }

    private int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        return _random.Next(minInclusive, maxExclusive);
    }

    private static void Schedule(Action action, int delayMs)
    {
        if (MainThreadActions == null)
        {
            action.Invoke();
            return;
        }

        if (delayMs <= 0)
        {
            MainThreadActions.Dispatch(action);
            return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            MainThreadActions.Dispatch(action);
        });
    }

    private void SchedulePostShotVerification(int playerId, int brainToken, int generation)
    {
        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation)
                || !_managedBots.TryGetValue(playerId, out ManagedBotState state)
                || state.BrainToken != brainToken
                || !Player.TryGet(playerId, out Player bot))
            {
                return;
            }

            FirearmItem? firearm = bot.CurrentItem as FirearmItem;
            string itemName = bot.CurrentItem?.Type.ToString() ?? "none";
            int currentLoadedAmmo = GetLoadedAmmoSafe(firearm);
            bool ammoConsumed = state.PendingShotLoadedAmmo >= 0 && currentLoadedAmmo >= 0 && currentLoadedAmmo < state.PendingShotLoadedAmmo;
            bool shotEventObserved = unchecked(state.LastShotEventTick - state.PendingShotVerificationTick) >= 0 && state.PendingShotVerificationTick > 0;

            LogBotEvent(
                state,
                $"post-shot-check item={itemName} action={state.LastShotActionName} loaded={currentLoadedAmmo} reserve={GetReserveAmmoSafe(bot, firearm)} ammoConsumed={ammoConsumed} shotEvent={shotEventObserved} dryFires={state.DryFireCount}");

            if (!shotEventObserved && !ammoConsumed)
            {
                state.DryFireCount++;
                PromoteShootFallback(bot, state);
                return;
            }

            state.DryFireCount = 0;
            if (!string.IsNullOrWhiteSpace(state.LastShotActionName))
            {
                state.PreferredShootActionName = state.LastShotActionName;
            }
        }, 90);
    }

    private void PromoteShootFallback(Player bot, ManagedBotState state)
    {
        if (!state.LoggedShootActionCatalog)
        {
            state.LoggedShootActionCatalog = true;
            LogBotEvent(state, $"shoot-actions available=[{string.Join(",", GetAvailableShootActionNames(bot))}]");
        }

        string previous = state.LastShotActionName;
        if (!string.IsNullOrWhiteSpace(Config.BotBehavior.AlternateShootPressActionName)
            && !string.Equals(previous, Config.BotBehavior.AlternateShootPressActionName, StringComparison.OrdinalIgnoreCase)
            && Config.BotBehavior.AlternateShootPressActionName.IndexOf("hold", StringComparison.OrdinalIgnoreCase) < 0
            && HasDummyAction(bot, Config.BotBehavior.AlternateShootPressActionName))
        {
            state.PreferredShootActionName = Config.BotBehavior.AlternateShootPressActionName;
            LogBotEvent(state, $"dry-fire-fallback previous={previous} next={state.PreferredShootActionName} count={state.DryFireCount}");
            return;
        }

        string? availableClickLike = GetAvailableShootActionNames(bot)
            .FirstOrDefault(name =>
                !string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)
                && name.IndexOf("hold", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!string.IsNullOrWhiteSpace(availableClickLike))
        {
            state.PreferredShootActionName = availableClickLike;
            LogBotEvent(state, $"dry-fire-fallback previous={previous} next={state.PreferredShootActionName} count={state.DryFireCount}");
            return;
        }

        LogBotEvent(state, $"dry-fire-no-fallback action={previous} count={state.DryFireCount}");
    }

    private void LogBotDebug(ManagedBotState state, string message)
    {
        if (!Config.EnableDebugLogging)
        {
            return;
        }

        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastDebugTick) < Config.BotBehavior.DebugLogIntervalMs)
        {
            return;
        }

        state.LastDebugTick = nowTick;
        ApiLogger.Info($"[{Name}] [BotDebug:{state.Nickname}] {message}");
    }

    private void LogBotEvent(ManagedBotState state, string message)
    {
        if (Config.EnableDebugLogging)
        {
            ApiLogger.Info($"[{Name}] [BotDebug:{state.Nickname}] {message}");
        }
    }

    private void LogBotEventByPlayerId(int playerId, string message)
    {
        if (_managedBots.TryGetValue(playerId, out ManagedBotState state))
        {
            LogBotEvent(state, message);
        }
    }

    private void LogAimStep(Player bot, ManagedBotState state, string message)
    {
        if (!Config.EnableDebugLogging)
        {
            return;
        }

        ApiLogger.Info($"[{Name}] [BotAimStep:{state.Nickname}] {message}");
    }

    private void LogNavDebug(Player bot, ManagedBotState state, string message)
    {
        if (!Config.BotBehavior.NavDebugLogging)
        {
            return;
        }

        ApiLogger.Info($"[{Name}] [BotNav:{state.Nickname}] {message}");
    }

    private void LogAimDebug(Player bot, ManagedBotState state, Player target, float yaw, float pitch, Vector3 direction)
    {
        if (!Config.EnableDebugLogging)
        {
            return;
        }

        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastAimDebugTick) < Config.BotBehavior.DebugLogIntervalMs)
        {
            return;
        }

        state.LastAimDebugTick = nowTick;
        Vector2 appliedAim = GetCurrentAim(bot);
        Vector2 rawLook = bot.LookRotation;
        float yawDelta = Mathf.DeltaAngle(appliedAim.x, yaw);
        float pitchDelta = pitch - appliedAim.y;
        Vector3 botEuler = bot.Rotation.eulerAngles;
        ApiLogger.Info(
            $"[{Name}] [BotAim:{state.Nickname}] " +
            $"target={target.Nickname}#{target.PlayerId} " +
            $"botPos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1}) " +
            $"targetPos=({target.Position.x:F1},{target.Position.y:F1},{target.Position.z:F1}) " +
            $"eye=({state.LastEyeOrigin.x:F1},{state.LastEyeOrigin.y:F1},{state.LastEyeOrigin.z:F1}) " +
            $"torsoAim=({state.LastTorsoAimPoint.x:F1},{state.LastTorsoAimPoint.y:F1},{state.LastTorsoAimPoint.z:F1}) " +
            $"headAim=({state.LastHeadAimPoint.x:F1},{state.LastHeadAimPoint.y:F1},{state.LastHeadAimPoint.z:F1}) " +
            $"baseAim=({state.LastBaseAimPoint.x:F1},{state.LastBaseAimPoint.y:F1},{state.LastBaseAimPoint.z:F1}) " +
            $"finalAim=({state.LastComputedAimPoint.x:F1},{state.LastComputedAimPoint.y:F1},{state.LastComputedAimPoint.z:F1}) " +
            $"aimMode={state.LastAimMode} " +
            $"settle={state.LastAimSettleProgress:F2} " +
            $"offsets=({state.LastAimYawOffset:F2},{state.LastAimPitchOffset:F2}) " +
            $"pitchSanitized={state.LastPitchWasSanitized} " +
            $"sanitizedPitch={state.LastSanitizedPitch:F1} " +
            $"verticalInvert={state.VerticalAimDirectionInverted} " +
            $"verticalRetryInvert={state.LastVerticalAimRetriedInverted} " +
            $"dir=({direction.x:F1},{direction.y:F1},{direction.z:F1}) " +
            $"desired=({yaw:F1},{pitch:F1}) " +
            $"applied=({appliedAim.x:F1},{appliedAim.y:F1}) " +
            $"rawLookBefore=({state.LastRawPitchBeforeAim:F1},{state.LastRawYawBeforeAim:F1}) " +
            $"rawLookAfter=({rawLook.x:F1},{rawLook.y:F1}) " +
            $"rotEuler=({botEuler.x:F1},{botEuler.y:F1},{botEuler.z:F1}) " +
            $"deltaBefore=({state.LastYawDelta:F1},{state.LastPitchDelta:F1}) " +
            $"deltaAfter=({yawDelta:F1},{pitchDelta:F1}) " +
            $"actionsH={state.LastHorizontalAimActions} " +
            $"actionsV={state.LastVerticalAimActions}");
    }

    private void LogCombatState(Player bot, ManagedBotState state, BotTargetSelection target, FirearmItem? firearm)
    {
        if (!Config.EnableDebugLogging)
        {
            return;
        }

        if (!Config.BotBehavior.RealisticLosDebugLogging && Config.BotBehavior.AiMode != WarmupAiMode.Realistic)
        {
            return;
        }

        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastCombatDebugTick) < Config.BotBehavior.DebugLogIntervalMs)
        {
            return;
        }

        state.LastCombatDebugTick = nowTick;
        string itemName = firearm?.Type.ToString() ?? bot.CurrentItem?.Type.ToString() ?? "none";
        int loadedAmmo = firearm == null ? -1 : GetLoadedAmmo(firearm);
        int reserveAmmo = firearm == null ? -1 : bot.GetAmmo(firearm.AmmoType);
        ApiLogger.Info(
            $"[{Name}] [BotCombat:{state.Nickname}] " +
            $"target={target.Target.Nickname}#{target.Target.PlayerId} " +
            $"team={bot.Team}->{target.Target.Team} " +
            $"mode={Config.BotBehavior.AiMode} " +
            $"distance={target.Distance:F1} " +
            $"visible={target.HasLineOfSight} " +
            $"remembered={target.IsRememberedTarget} " +
            $"headLos={target.HeadHasLineOfSight} " +
            $"torsoLos={target.TorsoHasLineOfSight} " +
            $"reactionReadyIn={Math.Max(0, state.Engagement.ReactionReadyTick - nowTick)} " +
            $"item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
    }

    public string BuildStatus()
    {
        return $"active={_warmupActive}, roundStarted={Round.IsRoundStarted}, bots={_managedBots.Count}/{Config.BotCount}, humanRole={Config.HumanRole}, botRole={Config.BotRole}, humanRespawnMs={Config.HumanRespawnDelayMs}, botRespawnMs={Config.BotRespawnDelayMs}, difficulty={Config.DifficultyPreset}, aimode={Config.BotBehavior.AiMode}";
    }

    public bool StartRoundIfNeeded(out string response)
    {
        if (Round.IsRoundStarted)
        {
            response = "Round is already started.";
            return true;
        }

        Round.Start();
        response = "Round start requested.";
        return true;
    }

    public bool RestartWarmupFromCommand(bool ensureRoundStarted, out string response)
    {
        if (ensureRoundStarted && !Round.IsRoundStarted)
        {
            Round.Start();
            response = "Round start requested. Warmup will begin when the round starts.";
            return true;
        }

        RestartWarmup("remote admin");
        response = "Warmup restart requested.";
        return true;
    }

    public bool RestartRound(out string response)
    {
        Round.RestartSilently();
        response = "Silent round restart requested.";
        return true;
    }

    public bool StopWarmup(out string response)
    {
        _warmupGeneration++;
        _warmupActive = false;
        CleanupManagedBots();
        response = "Warmup stopped and all managed bots were removed.";
        return true;
    }

    public bool SaveCurrentConfig(out string response)
    {
        SaveConfig();
        response = "Warmup config saved.";
        return true;
    }

    public bool UpdateSetting(string key, string value, out string response)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            response = "Missing setting name.";
            return false;
        }

        switch (key.Trim().ToLowerInvariant())
        {
            case "bots":
            case "botcount":
            case "players":
                if (!int.TryParse(value, out int botCount) || botCount < 0)
                {
                    response = "Bot count must be a non-negative integer.";
                    return false;
                }

                Config.BotCount = botCount;
                EnsureBotPopulation(_warmupGeneration);
                TrimExcessBots();
                response = $"Bot count set to {Config.BotCount}.";
                return true;

            case "humanrespawn":
            case "respawn":
            case "humanrespawnms":
                if (!int.TryParse(value, out int humanRespawnMs) || humanRespawnMs < 0)
                {
                    response = "Human respawn must be a non-negative integer in milliseconds.";
                    return false;
                }

                Config.HumanRespawnDelayMs = humanRespawnMs;
                response = $"Human respawn delay set to {Config.HumanRespawnDelayMs} ms.";
                return true;

            case "botrespawn":
            case "botrespawnms":
                if (!int.TryParse(value, out int botRespawnMs) || botRespawnMs < 0)
                {
                    response = "Bot respawn must be a non-negative integer in milliseconds.";
                    return false;
                }

                Config.BotRespawnDelayMs = botRespawnMs;
                response = $"Bot respawn delay set to {Config.BotRespawnDelayMs} ms.";
                return true;

            case "humanrole":
                if (!Enum.TryParse(value, true, out RoleTypeId humanRole))
                {
                    response = $"Unknown human role '{value}'.";
                    return false;
                }

                Config.HumanRole = humanRole;
                response = $"Human role set to {Config.HumanRole}.";
                return true;

            case "botrole":
                if (!Enum.TryParse(value, true, out RoleTypeId botRole))
                {
                    response = $"Unknown bot role '{value}'.";
                    return false;
                }

                Config.BotRole = botRole;
                int recycledBots = RespawnManagedBotsForRoleChange();
                response = recycledBots > 0
                    ? $"Bot role set to {Config.BotRole}. Recycled {recycledBots} bot(s) so they respawn with the new role."
                    : $"Bot role set to {Config.BotRole}.";
                return true;

            case "forceroundstart":
                if (!bool.TryParse(value, out bool forceRoundStart))
                {
                    response = "forceroundstart must be true or false.";
                    return false;
                }

                Config.ForceRoundStartOnFirstPlayer = forceRoundStart;
                response = $"ForceRoundStartOnFirstPlayer set to {Config.ForceRoundStartOnFirstPlayer}.";
                return true;

            case "suppressroundend":
                if (!bool.TryParse(value, out bool suppressRoundEnd))
                {
                    response = "suppressroundend must be true or false.";
                    return false;
                }

                Config.SuppressRoundEnd = suppressRoundEnd;
                response = $"SuppressRoundEnd set to {Config.SuppressRoundEnd}.";
                return true;

            case "keepmagfilled":
            case "keepmagazinefilled":
                if (!bool.TryParse(value, out bool keepMagazineFilled))
                {
                    response = "keepmagfilled must be true or false.";
                    return false;
                }

                Config.BotBehavior.KeepMagazineFilled = keepMagazineFilled;
                response = $"Bot keep-magazine-filled set to {Config.BotBehavior.KeepMagazineFilled}.";
                return true;

            case "aimode":
                return ApplyAiMode(value, out response);

            default:
                response = $"Unknown setting '{key}'.";
                return false;
        }
    }

    public bool ApplyAiMode(string rawValue, out string response)
    {
        if (!Enum.TryParse(rawValue, true, out WarmupAiMode mode))
        {
            response = $"Unknown AI mode '{rawValue}'. Use classic or realistic.";
            return false;
        }

        Config.BotBehavior.AiMode = mode;
        SaveConfig();
        response = $"AI mode set to {Config.BotBehavior.AiMode}.";
        return true;
    }

    public bool ApplyDifficultyPreset(string rawValue, out string response)
    {
        if (!Enum.TryParse(rawValue, true, out WarmupDifficulty preset))
        {
            response = $"Unknown difficulty '{rawValue}'. Use easy, normal, hard, or hardest.";
            return false;
        }

        ApplyDifficultyPreset(preset, persist: true);
        response = $"Difficulty set to {Config.DifficultyPreset}.";
        return true;
    }

    private void ApplyDifficultyPreset(WarmupDifficulty preset, bool persist)
    {
        Config.DifficultyPreset = preset;
        Config.BotBehavior.NavDebugLogging = true;
        Config.BotBehavior.WalkForwardActionNames = new[]
        {
            "Walk forward 1.5m",
            "Walk forward 0.5m",
            "Walk forward 0.2m",
            "Walk forward 0.05m",
        };
        Config.BotBehavior.WalkBackwardActionNames = new[]
        {
            "Walk back 1.5m",
            "Walk back 0.5m",
            "Walk back 0.2m",
            "Walk back 0.05m",
        };

        switch (preset)
        {
            case WarmupDifficulty.Easy:
                Config.BotBehavior.ThinkIntervalMinMs = 900;
                Config.BotBehavior.ThinkIntervalMaxMs = 1350;
                Config.BotBehavior.MinShotIntervalMs = 260;
                Config.BotBehavior.ShootReleaseDelayMs = 80;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 16f;
                Config.BotBehavior.RangeTolerance = 4f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 2;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 1;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 2.0f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 1.4f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 1500;
                Config.BotBehavior.RealisticReacquireDelayMs = 300;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Normal:
                Config.BotBehavior.ThinkIntervalMinMs = 450;
                Config.BotBehavior.ThinkIntervalMaxMs = 850;
                Config.BotBehavior.MinShotIntervalMs = 140;
                Config.BotBehavior.ShootReleaseDelayMs = 40;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 14f;
                Config.BotBehavior.RangeTolerance = 2.5f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 3;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 2;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 1.5f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 1.0f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 1300;
                Config.BotBehavior.RealisticReacquireDelayMs = 250;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Hard:
                Config.BotBehavior.ThinkIntervalMinMs = 120;
                Config.BotBehavior.ThinkIntervalMaxMs = 220;
                Config.BotBehavior.MinShotIntervalMs = 55;
                Config.BotBehavior.ShootReleaseDelayMs = 12;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 11f;
                Config.BotBehavior.RangeTolerance = 1f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 3;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 2;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 1.0f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 0.75f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 1000;
                Config.BotBehavior.RealisticReacquireDelayMs = 180;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Hardest:
                Config.BotBehavior.ThinkIntervalMinMs = 55;
                Config.BotBehavior.ThinkIntervalMaxMs = 95;
                Config.BotBehavior.MinShotIntervalMs = 24;
                Config.BotBehavior.ShootReleaseDelayMs = 4;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 8.5f;
                Config.BotBehavior.RangeTolerance = 0.4f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 5;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 4;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 0.35f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 0.2f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = true;
                Config.BotBehavior.CloseRangeStrafeDistance = 30f;
                Config.BotBehavior.VeryCloseRangeStrafeDistance = 18f;
                Config.BotBehavior.CloseRangeStrafeRepeatCount = 5;
                Config.BotBehavior.VeryCloseRangeStrafeRepeatCount = 9;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = true;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0.25f;
                Config.BotBehavior.CloseRangeRetreatRepeatCount = 2;
                Config.BotBehavior.VeryCloseRangeRetreatRepeatCount = 4;
                Config.BotBehavior.RealisticAimSettleMs = 425;
                Config.BotBehavior.RealisticReacquireDelayMs = 45;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
        }

        if (persist)
        {
            SaveConfig();
        }
    }

    private void TrimExcessBots()
    {
        while (_managedBots.Count > Config.BotCount)
        {
            int playerId = _managedBots.Keys.Last();
            if (Player.TryGet(playerId, out Player bot) && bot.GameObject != null)
            {
                NetworkServer.Destroy(bot.GameObject);
            }

            _managedBots.Remove(playerId);
        }
    }

    private int RespawnManagedBotsForRoleChange()
    {
        if (!_warmupActive || _managedBots.Count == 0)
        {
            return 0;
        }

        int recycled = 0;
        foreach (int playerId in _managedBots.Keys.ToArray())
        {
            ScheduleBotRespawn(playerId);
            recycled++;
        }

        return recycled;
    }

    private RoleTypeId GetBotRespawnRole(ManagedBotState state)
    {
        return state.RespawnRole == RoleTypeId.None || state.RespawnRole == RoleTypeId.Spectator
            ? Config.BotRole
            : state.RespawnRole;
    }
}
