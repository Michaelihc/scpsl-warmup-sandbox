using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AdminToys;
using CommandSystem;
using CommandSystem.Commands.RemoteAdmin.Cleanup;
using CommandSystem.Commands.RemoteAdmin.Dummies;
using CustomPlayerEffects;
using InventorySystem.Items;
using InventorySystem.Items.Firearms.Attachments;
using InventorySystem.Items.Jailbird;
using InventorySystem.Items.MicroHID.Modules;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using MapGeneration;
using Mirror;
using NetworkManagerUtils.Dummies;
using NorthwoodLib;
using PlayerRoles;
using UserSettings.ServerSpecific;
using UnityEngine;
using UnityEngine.AI;
using ApiLogger = LabApi.Features.Console.Logger;
using PrimitiveObjectToyWrapper = LabApi.Features.Wrappers.PrimitiveObjectToy;
using TextToyWrapper = LabApi.Features.Wrappers.TextToy;

namespace ScpslPluginStarter;

public sealed class WarmupSandboxPlugin : Plugin<PluginConfig>
{
    private static readonly int[] ArenaSpawnCorrectionDelaysMs = { 150, 450, 900 };
    private const int BotPreForceRoleDelayMs = 350;
    private const int BotArenaSpawnDelayMs = 3000;
    private const int BotPostSpawnHookDelayMs = 500;
    private const int BotBrainReadyRetryDelayMs = 250;
    private const int BotBrainReadyMaxAttempts = 40;
    private const int BotNoRoomRespawnCooldownMs = 5000;
    private const int NavHeartbeatIntervalMs = 5000;
    private const int LiveUpdateSignalPollIntervalMs = 1000;
    private const int SafezoneHealthDrainIntervalMs = 1000;
    private const int DangerousItemProtectionMonitorIntervalMs = 250;
    private const float DangerousItemSpawnProtectionTimeLeftSeconds = 4f;
    private const string LiveUpdateSignalFileName = "live-update-warning.txt";
    private const string HotConfigReloadSignalFileName = "hot-reload-config.txt";
    private const int MinimumAutoCleanupIntervalMs = 10000;
    private const int ArmorPickupSanitizerIntervalMs = 1000;
    private const int DroppedArmorPickupDestroyDelayMs = 1;
    private const ushort DefaultReserveAmmoTarget = 240;
    private const int PlayerPanelFirstSettingId = 63001;
    private const int PlayerPanelRoleSettingId = 63001;
    private const int PlayerPanelLoadoutSettingId = 63002;
    private const int PlayerPanelItemSettingId = 63003;
    private const int PlayerPanelTeleportTargetSettingId = 63004;
    private const int PlayerPanelBotCountSettingId = 63005;
    private const int PlayerPanelDifficultySettingId = 63006;
    private const int PlayerPanelAiModeSettingId = 63007;
    private const int PlayerPanelBotTargetSettingId = 63008;
    private const int PlayerPanelBotRoleSettingId = 63009;
    private const int PlayerPanelRetreatSpeedSettingId = 63010;
    private const int PlayerPanelSetRoleButtonId = 63011;
    private const int PlayerPanelApplyLoadoutButtonId = 63012;
    private const int PlayerPanelGiveItemButtonId = 63013;
    private const int PlayerPanelGotoButtonId = 63014;
    private const int PlayerPanelBringBotsButtonId = 63015;
    private const int PlayerPanelRoomPresetSettingId = 63016;
    private const int PlayerPanelRoomTeleportButtonId = 63017;
    private const int PlayerPanelSetBotsButtonId = 63021;
    private const int PlayerPanelApplyDifficultyButtonId = 63022;
    private const int PlayerPanelApplyAiModeButtonId = 63023;
    private const int PlayerPanelApplyBotRoleButtonId = 63024;
    private const int PlayerPanelApplyRetreatSpeedButtonId = 63025;
    private const int PlayerPanelLastSettingId = 63050;
    private const int PlayerPanelPersonalFreeActions = 3;
    private const int PlayerPanelPersonalCooldownSeconds = 5;
    private const int PlayerPanelSelfTargetId = int.MinValue;
    private const int PlayerPanelAllBotsTargetId = int.MinValue + 1;
    private static readonly int[] BotAttachmentRandomizationDelaysMs = { 250, 1000, 2500 };
    private static readonly RoleTypeId[] PlayerPanelRoles = Enum.GetValues(typeof(RoleTypeId))
        .Cast<RoleTypeId>()
        .Where(IsPlayerPanelRoleAllowed)
        .OrderBy(role => role.ToString(), StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private static readonly RoleTypeId[] PlayerPanelBotRoles = PlayerPanelRoles
        .Where(role => role != RoleTypeId.Spectator)
        .ToArray();
    private static readonly ItemType[] PlayerPanelItems = Enum.GetValues(typeof(ItemType))
        .Cast<ItemType>()
        .Where(IsPlayerPanelItemAllowed)
        .OrderBy(item => item.ToString(), StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private static readonly WarmupDifficulty[] PlayerPanelDifficulties = Enum.GetValues(typeof(WarmupDifficulty))
        .Cast<WarmupDifficulty>()
        .ToArray();
    private static readonly WarmupAiMode[] PlayerPanelAiModes = Enum.GetValues(typeof(WarmupAiMode))
        .Cast<WarmupAiMode>()
        .ToArray();
    private static readonly PlayerPanelRoomPreset[] PlayerPanelRoomPresets =
    {
        new(RoomName.LczClassDSpawn, "LCZ Class-D Spawn", "轻收容 D 级出生点"),
        new(RoomName.Lcz173, "LCZ SCP-173", "轻收容 173"),
        new(RoomName.Lcz914, "LCZ SCP-914", "轻收容 914"),
        new(RoomName.Lcz330, "LCZ SCP-330", "轻收容 330"),
        new(RoomName.LczArmory, "LCZ Armory", "轻收容军械库"),
        new(RoomName.LczToilets, "LCZ Toilets", "轻收容厕所"),
        new(RoomName.LczCheckpointA, "LCZ Checkpoint A", "轻收容检查点 A"),
        new(RoomName.LczCheckpointB, "LCZ Checkpoint B", "轻收容检查点 B"),
        new(RoomName.Hcz049, "HCZ SCP-049", "重收容 049"),
        new(RoomName.Hcz079, "HCZ SCP-079", "重收容 079"),
        new(RoomName.Hcz096, "HCZ SCP-096", "重收容 096"),
        new(RoomName.Hcz106, "HCZ SCP-106", "重收容 106"),
        new(RoomName.Hcz939, "HCZ SCP-939", "重收容 939"),
        new(RoomName.HczWarhead, "HCZ Warhead", "重收容核弹"),
        new(RoomName.HczArmory, "HCZ Armory", "重收容军械库"),
        new(RoomName.HczMicroHID, "HCZ HID Chamber", "重收容 HID 房"),
        new(RoomName.HczServers, "HCZ Servers", "重收容服务器房"),
        new(RoomName.Hcz127, "HCZ SCP-127 Lab", "重收容 127 实验室"),
        new(RoomName.EzGateA, "Entrance Gate A", "办公区 A 门"),
        new(RoomName.EzGateB, "Entrance Gate B", "办公区 B 门"),
        new(RoomName.EzIntercom, "Entrance Intercom", "办公区广播室"),
        new(RoomName.EzEvacShelter, "Entrance Shelter", "办公区避难所"),
        new(RoomName.EzCollapsedTunnel, "Entrance Collapsed Tunnel", "办公区坍塌隧道"),
    };

    private readonly struct PlayerPanelRoomPreset
    {
        public PlayerPanelRoomPreset(RoomName roomName, string englishLabel, string chineseLabel)
        {
            RoomName = roomName;
            EnglishLabel = englishLabel;
            ChineseLabel = chineseLabel;
        }

        public RoomName RoomName { get; }

        public string EnglishLabel { get; }

        public string ChineseLabel { get; }

        public string Label => WarmupLocalization.T(EnglishLabel, ChineseLabel);
    }

    private static bool IsPlayerPanelRoleAllowed(RoleTypeId role)
    {
        if (role == RoleTypeId.None)
        {
            return false;
        }

        string name = role.ToString();
        return !string.Equals(name, "Overwatch", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "Filmmaker", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "Tutorial", StringComparison.OrdinalIgnoreCase)
            && name.IndexOf("Flamingo", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsPlayerPanelItemAllowed(ItemType item)
    {
        if (item == ItemType.None)
        {
            return false;
        }

        string name = item.ToString();
        return name.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsArmorItem(ItemType item)
    {
        return item.ToString().IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static readonly ActionDispatcher? MainThreadActions = typeof(MainThreadDispatcher)
        .GetField("UpdateDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
        .GetValue(null) as ActionDispatcher;
    private static readonly MethodInfo? DummyActionCollectorGetCacheMethod = typeof(DummyActionCollector)
        .GetMethod("GetCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? DummyActionCacheUpdateMethod = DummyActionCollectorGetCacheMethod?.ReturnType
        .GetMethod("UpdateCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? DummyActionProvidersField = DummyActionCollectorGetCacheMethod?.ReturnType
        .GetField("_providers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? RootDummyPopulateActionsMethod = DummyActionProvidersField?.FieldType.GetElementType()
        ?.GetMethod("PopulateDummyActions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? AttachmentsServerApplyPreferenceMethod = typeof(AttachmentsServerHandler)
        .GetMethod(
            "ServerApplyPreference",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(ReferenceHub), typeof(ItemType), typeof(uint) },
            null);
    private readonly Dictionary<int, ManagedBotState> _managedBots = new();
    private readonly Dictionary<int, string> _selectedHumanLoadouts = new();
    private readonly Dictionary<int, long> _playerBotCountCooldownUntilMs = new();
    private readonly Dictionary<int, long> _playerPanelCooldownUntilMs = new();
    private readonly Dictionary<int, long> _playerPanelWindowUntilMs = new();
    private readonly Dictionary<int, long> _playerPanelPersonalCooldownUntilMs = new();
    private readonly Dictionary<int, int> _playerPanelPersonalActionCounts = new();
    private readonly Dictionary<int, int> _playerPanelSelectedTargetIds = new();
    private readonly Dictionary<int, RoleTypeId> _playerPanelSelectedRoles = new();
    private readonly Dictionary<int, string> _playerPanelSelectedLoadouts = new();
    private readonly Dictionary<int, ItemType> _playerPanelSelectedItems = new();
    private readonly Dictionary<int, int> _playerPanelSelectedBotCounts = new();
    private readonly Dictionary<int, WarmupDifficulty> _playerPanelSelectedDifficulties = new();
    private readonly Dictionary<int, WarmupAiMode> _playerPanelSelectedAiModes = new();
    private readonly Dictionary<int, int> _playerPanelSelectedBotTargetIds = new();
    private readonly Dictionary<int, RoleTypeId> _playerPanelSelectedBotRoles = new();
    private readonly Dictionary<int, float> _playerPanelSelectedRetreatSpeedScales = new();
    private readonly Dictionary<int, RoomName> _playerPanelSelectedRoomPresets = new();
    private readonly System.Random _random = new();
    private readonly HumanPresetService _humanPresetService = new();
    private readonly BotCombatService _botCombatService = new();
    private readonly BotControllerService _botControllerService = new();
    private readonly Dust2MapService _dust2MapService = new();
    private readonly FacilityNavMeshService _facilityNavMeshService = new();
    private readonly BombModeService _bombModeService = new();
    private readonly PlaytimeTrackerService _playtimeTrackerService = new();
    private readonly List<PrimitiveObjectToyWrapper> _runtimeNavMeshDebugEdges = new();
    private readonly List<PrimitiveObjectToyWrapper> _escapeSafezoneVisuals = new();
    private readonly List<TextToyWrapper> _escapeSafezoneLabels = new();
    private readonly HashSet<int> _safezoneDrainDamagePlayerIds = new();
    private readonly Dictionary<int, PrimitiveObjectToyWrapper> _navAgentDebugToys = new();
    private int _botSequence;
    private int _warmupGeneration;
    private int _roundCampDisabledUntilTick;
    private long _playerBotCountGlobalCooldownUntilMs;
    private long _playerPanelGlobalCooldownUntilMs;
    private ServerSpecificSettingBase[]? _originalServerSpecificSettings;
    private int[] _playerPanelTargetIds = { PlayerPanelSelfTargetId };
    private int[] _playerPanelBotTargetIds = { PlayerPanelAllBotsTargetId };
    private bool _warmupActive;
    private bool _nextHelpReminderIsCommunity;
    private int _liveUpdateWarningUntilTick;
    private int _safezoneHealthDrainToken;
    private int _dangerousItemProtectionMonitorToken;

    public static WarmupSandboxPlugin? Instance { get; private set; }
    public override string Name => "WarmupSandbox";
    public override string Description => "Warmup sandbox with moving dummy bots.";
    public override string Author => "Michael";
    public override Version Version => new(0, 1, 1);
    public override Version RequiredApiVersion => new(1, 1, 6);

    public override void Enable()
    {
        Instance = this;
        WarmupLocalization.SetLanguage(Config.Language);
        ClampConfiguredLimits();
        ApplyDifficultyPreset(Config.DifficultyPreset, persist: false);
        ApplyNativeSpawnProtectionConfig();
        _playtimeTrackerService.Enable(Config.PlaytimeTracking);
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundRestarted += OnRoundRestarted;
        ServerEvents.RoundEndingConditionsCheck += OnRoundEndingConditionsCheck;
        ServerEvents.LczDecontaminationStarting += OnLczDecontaminationStarting;
        ServerEvents.LczDecontaminationAnnounced += OnLczDecontaminationAnnounced;
        PlayerEvents.Joined += OnPlayerJoined;
        PlayerEvents.Spawned += OnPlayerSpawned;
        PlayerEvents.Death += OnPlayerDeath;
        PlayerEvents.Hurting += OnPlayerHurting;
        PlayerEvents.Hurt += OnPlayerHurt;
        PlayerEvents.Left += OnPlayerLeft;
        PlayerEvents.UnlockingWarheadButton += OnPlayerUnlockingWarheadButton;
        PlayerEvents.InteractingWarheadLever += OnPlayerInteractingWarheadLever;
        PlayerEvents.ShootingWeapon += OnPlayerShootingWeapon;
        PlayerEvents.DryFiringWeapon += OnPlayerDryFiringWeapon;
        PlayerEvents.ShotWeapon += OnPlayerShotWeapon;
        PlayerEvents.ReloadedWeapon += OnPlayerReloadedWeapon;
        PlayerEvents.ChangedItem += OnPlayerChangedItem;
        PlayerEvents.SearchedToy += OnPlayerSearchedToy;
        PlayerEvents.SearchingToy += OnPlayerSearchingToy;
        PlayerEvents.SearchToyAborted += OnPlayerSearchToyAborted;
        PlayerEvents.UsingItem += OnPlayerUsingItem;
        PlayerEvents.UsedItem += OnPlayerUsedItem;
        PlayerEvents.CancelledUsingItem += OnPlayerCancelledUsingItem;
        PlayerEvents.ProcessingJailbirdMessage += OnPlayerProcessingJailbirdMessage;
        PlayerEvents.SearchingPickup += OnPlayerSearchingPickup;
        PlayerEvents.PickingUpItem += OnPlayerPickingUpItem;
        PlayerEvents.DroppedItem += OnPlayerDroppedItem;
        PlayerEvents.Cuffing += OnPlayerCuffing;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnServerSpecificSettingValueReceived;
        _originalServerSpecificSettings = ServerSpecificSettingsSync.DefinedSettings;
        RefreshPlayerPanelSettings(sendToPlayers: false);
        LabApi.Events.Handlers.WarheadEvents.Starting += OnWarheadStarting;
        LabApi.Events.Handlers.WarheadEvents.Detonating += OnWarheadDetonating;
        ApplyHazardDisableConfig();
        foreach (Player player in Player.ReadyList.Where(IsManagedHuman))
        {
            _playtimeTrackerService.PlayerJoined(player, Config.PlaytimeTracking);
        }

        SchedulePlaytimeFlush();
        ScheduleLiveUpdateSignalPoll();
        ScheduleSafezoneHealthDrain();
        ScheduleDangerousItemProtectionMonitor();
        Schedule(EnsureEscapeSafezoneVisuals, 5000);
        ApiLogger.Info($"[{Name}] Enabled.");
    }

    public override void Disable()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }

        _warmupGeneration++;
        _safezoneHealthDrainToken++;
        _dangerousItemProtectionMonitorToken++;
        _warmupActive = false;
        _playtimeTrackerService.Disable();
        CleanupManagedBots();
        _bombModeService.ResetRuntime();
        CleanupArenaMap(returnHumansToFacility: false);
        DestroyEscapeSafezoneVisuals();
        _facilityNavMeshService.RemoveRuntimeNavMesh();
        PlayerEvents.Cuffing -= OnPlayerCuffing;
        PlayerEvents.DroppedItem -= OnPlayerDroppedItem;
        PlayerEvents.PickingUpItem -= OnPlayerPickingUpItem;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnServerSpecificSettingValueReceived;
        if (_originalServerSpecificSettings != null)
        {
            ServerSpecificSettingsSync.DefinedSettings = _originalServerSpecificSettings;
            ServerSpecificSettingsSync.SendToAll();
            _originalServerSpecificSettings = null;
        }

        PlayerEvents.SearchingPickup -= OnPlayerSearchingPickup;
        PlayerEvents.ProcessingJailbirdMessage -= OnPlayerProcessingJailbirdMessage;
        PlayerEvents.CancelledUsingItem -= OnPlayerCancelledUsingItem;
        PlayerEvents.UsedItem -= OnPlayerUsedItem;
        PlayerEvents.UsingItem -= OnPlayerUsingItem;
        PlayerEvents.SearchToyAborted -= OnPlayerSearchToyAborted;
        PlayerEvents.SearchingToy -= OnPlayerSearchingToy;
        PlayerEvents.SearchedToy -= OnPlayerSearchedToy;
        PlayerEvents.ChangedItem -= OnPlayerChangedItem;
        PlayerEvents.ReloadedWeapon -= OnPlayerReloadedWeapon;
        PlayerEvents.ShotWeapon -= OnPlayerShotWeapon;
        PlayerEvents.DryFiringWeapon -= OnPlayerDryFiringWeapon;
        PlayerEvents.ShootingWeapon -= OnPlayerShootingWeapon;
        PlayerEvents.InteractingWarheadLever -= OnPlayerInteractingWarheadLever;
        PlayerEvents.UnlockingWarheadButton -= OnPlayerUnlockingWarheadButton;
        PlayerEvents.Hurting -= OnPlayerHurting;
        PlayerEvents.Hurt -= OnPlayerHurt;
        PlayerEvents.Left -= OnPlayerLeft;
        PlayerEvents.Death -= OnPlayerDeath;
        PlayerEvents.Spawned -= OnPlayerSpawned;
        PlayerEvents.Joined -= OnPlayerJoined;
        LabApi.Events.Handlers.WarheadEvents.Detonating -= OnWarheadDetonating;
        LabApi.Events.Handlers.WarheadEvents.Starting -= OnWarheadStarting;
        ServerEvents.LczDecontaminationAnnounced -= OnLczDecontaminationAnnounced;
        ServerEvents.LczDecontaminationStarting -= OnLczDecontaminationStarting;
        ServerEvents.RoundEndingConditionsCheck -= OnRoundEndingConditionsCheck;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        ServerEvents.RoundStarted -= OnRoundStarted;
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ApiLogger.Info($"[{Name}] Disabled.");
    }

    private void OnWaitingForPlayers()
    {
        EnsureEscapeSafezoneVisuals();
        ApplyHazardDisableConfig();

        if (Config.AutoStartOnWaitingForPlayers && Player.List.Any(IsManagedHuman))
        {
            RestartWarmup("waiting for players");
        }
    }

    private void OnRoundStarted()
    {
        EnsureEscapeSafezoneVisuals();
        ApplyHazardDisableConfig();
        _roundCampDisabledUntilTick = Environment.TickCount + BotControllerService.GetCampCooldownMs();
        Schedule(() => RefreshPlayerPanelSettings(sendToPlayers: true), 1500);
        if (Config.AutoStartOnRoundStarted)
        {
            RestartWarmup("round started");
        }
    }

    private void OnRoundRestarted()
    {
        _warmupGeneration++;
        _warmupActive = false;
        _roundCampDisabledUntilTick = 0;
        CleanupManagedBots();
        _bombModeService.ResetRuntime();
        CleanupArenaMap(returnHumansToFacility: false);
        DestroyEscapeSafezoneVisuals();
        _facilityNavMeshService.RemoveRuntimeNavMesh();
    }

    private void OnRoundEndingConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
    {
        // Bot-only mode never blocks the base game's round-ending conditions.
    }

    private void OnPlayerUnlockingWarheadButton(PlayerUnlockingWarheadButtonEventArgs ev)
    {
        if (!Config.DisableWarhead)
        {
            return;
        }

        ev.IsAllowed = false;
        TryDisableWarhead();
        ev.Player.SendHint(WarmupLocalization.T("Warhead is disabled.", "核弹已禁用。"), 2f);
    }

    private void OnPlayerInteractingWarheadLever(PlayerInteractingWarheadLeverEventArgs ev)
    {
        if (!Config.DisableWarhead)
        {
            return;
        }

        ev.IsAllowed = false;
        TryDisableWarhead();
        ev.Player.SendHint(WarmupLocalization.T("Warhead is disabled.", "核弹已禁用。"), 2f);
    }

    private void OnWarheadStarting(WarheadStartingEventArgs ev)
    {
        if (!Config.DisableWarhead)
        {
            return;
        }

        ev.IsAllowed = false;
        TryDisableWarhead();
    }

    private void OnWarheadDetonating(WarheadDetonatingEventArgs ev)
    {
        if (!Config.DisableWarhead)
        {
            return;
        }

        ev.IsAllowed = false;
        TryDisableWarhead();
    }

    private void OnLczDecontaminationStarting(LczDecontaminationStartingEventArgs ev)
    {
        if (!Config.DisableDecontamination)
        {
            return;
        }

        ev.IsAllowed = false;
        TryDisableDecontamination();
    }

    private void OnLczDecontaminationAnnounced(LczDecontaminationAnnouncedEventArgs ev)
    {
        if (Config.DisableDecontamination)
        {
            TryDisableDecontamination();
        }
    }

    private void ApplyHazardDisableConfig()
    {
        if (Config.DisableWarhead)
        {
            TryDisableWarhead();
        }

        if (Config.DisableDecontamination)
        {
            TryDisableDecontamination();
        }
    }

    private void TryDisableWarhead()
    {
        try
        {
            if (!Warhead.Exists)
            {
                return;
            }

            if (Warhead.IsDetonationInProgress)
            {
                Warhead.Stop(null);
            }

            Warhead.LeverStatus = false;
            Warhead.IsAuthorized = false;
            Warhead.IsLocked = true;
            Warhead.ForceCountdownToggle = false;
            Warhead.DeadManSwitchRemaining = 0f;
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[{Name}] Failed to disable warhead: {ex.Message}");
        }
    }

    private void TryDisableDecontamination()
    {
        try
        {
            Decontamination.Status = LightContainmentZoneDecontamination.DecontaminationController.DecontaminationStatus.Disabled;
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[{Name}] Failed to disable LCZ decontamination: {ex.Message}");
        }
    }

    private void OnPlayerJoined(PlayerJoinedEventArgs ev)
    {
        RefreshPlayerPanelSettings(sendToPlayers: true);
        _playtimeTrackerService.PlayerJoined(ev.Player, Config.PlaytimeTracking);

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

        // Joining players are not moved, respawned, or assigned a sandbox loadout.
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
            ClearBotSpawnProtection(ev.Player);
            return;
        }

        // Bot-only mode leaves human spawns exactly as the base round created them.
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

            CancelBotBrainForRound(ev.Player.PlayerId);
            RefreshPlayerPanelSettings(sendToPlayers: true);
            return;
        }
    }

    private void GrantSpawnProtection(Player player)
    {
        if (!Config.EnableSpawnProtection
            || Config.SpawnProtectionDurationMs <= 0
            || player.ReferenceHub == null)
        {
            return;
        }

        ApplyNativeSpawnProtectionConfig();
        SpawnProtected.TryGiveProtection(player.ReferenceHub);
    }

    private static void CancelSpawnProtection(Player player)
    {
        if (player.ReferenceHub == null)
        {
            return;
        }

        player.ReferenceHub.playerEffectsController.DisableEffect<SpawnProtected>();
    }

    private static bool TryGetSpawnProtection(Player player, out SpawnProtected spawnProtected)
    {
        spawnProtected = null!;
        return player.ReferenceHub != null
            && player.ReferenceHub.playerEffectsController.TryGetEffect(out spawnProtected)
            && spawnProtected != null;
    }

    private void ClearBotSpawnProtection(Player bot)
    {
        CancelSpawnProtection(bot);
        int playerId = bot.PlayerId;
        int generation = _warmupGeneration;
        foreach (int delayMs in new[] { 100, 500, 1500 })
        {
            Schedule(() =>
            {
                if (!IsCurrentGeneration(generation)
                    || !Player.TryGet(playerId, out Player liveBot)
                    || !IsManagedBot(liveBot))
                {
                    return;
                }

                CancelSpawnProtection(liveBot);
            }, delayMs);
        }
    }

    private void ApplyNativeSpawnProtectionConfig()
    {
        SpawnProtected.IsProtectionEnabled = Config.EnableSpawnProtection;
        SpawnProtected.SpawnDuration = Math.Max(0f, Config.SpawnProtectionDurationMs / 1000f);
    }

    private void OnPlayerHurting(PlayerHurtingEventArgs ev)
    {
        if (!_warmupActive)
        {
            return;
        }

        if (IsManagedBot(ev.Player)
            && IsInEscapeSafezone(ev.Player)
            && !_safezoneDrainDamagePlayerIds.Contains(ev.Player.PlayerId))
        {
            ev.IsAllowed = false;
            ZeroDamage(ev.DamageHandler);
            return;
        }
    }

    private void OnPlayerHurt(PlayerHurtEventArgs ev)
    {
        if (!_warmupActive)
        {
            return;
        }

        if (IsManagedBot(ev.Player)
            && IsInEscapeSafezone(ev.Player)
            && !_safezoneDrainDamagePlayerIds.Contains(ev.Player.PlayerId))
        {
            ZeroDamage(ev.DamageHandler);
            return;
        }

        if (!IsManagedBot(ev.Player)
            || !_managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState state))
        {
            return;
        }

        if (ev.Attacker == null || !AreCombatantsHostile(ev.Player, ev.Attacker))
        {
            return;
        }

        TriggerReactiveStrafe(ev.Player, state, ev.Attacker);
    }

    private static void ZeroDamage(PlayerStatsSystem.DamageHandlerBase damageHandler)
    {
        if (damageHandler is PlayerStatsSystem.StandardDamageHandler standardDamageHandler)
        {
            standardDamageHandler.Damage = 0f;
        }
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        _playtimeTrackerService.PlayerLeft(ev.Player, Config.PlaytimeTracking);
        _selectedHumanLoadouts.Remove(ev.Player.PlayerId);
        _playerBotCountCooldownUntilMs.Remove(ev.Player.PlayerId);
        _playerPanelCooldownUntilMs.Remove(ev.Player.PlayerId);
        _playerPanelWindowUntilMs.Remove(ev.Player.PlayerId);
        _playerPanelPersonalCooldownUntilMs.Remove(ev.Player.PlayerId);
        _playerPanelPersonalActionCounts.Remove(ev.Player.PlayerId);
        _playerPanelSelectedTargetIds.Remove(ev.Player.PlayerId);
        _playerPanelSelectedRoles.Remove(ev.Player.PlayerId);
        _playerPanelSelectedLoadouts.Remove(ev.Player.PlayerId);
        _playerPanelSelectedItems.Remove(ev.Player.PlayerId);
        _playerPanelSelectedBotCounts.Remove(ev.Player.PlayerId);
        _playerPanelSelectedDifficulties.Remove(ev.Player.PlayerId);
        _playerPanelSelectedAiModes.Remove(ev.Player.PlayerId);
        _playerPanelSelectedBotTargetIds.Remove(ev.Player.PlayerId);
        _playerPanelSelectedBotRoles.Remove(ev.Player.PlayerId);
        _playerPanelSelectedRetreatSpeedScales.Remove(ev.Player.PlayerId);
        _playerPanelSelectedRoomPresets.Remove(ev.Player.PlayerId);
        RefreshPlayerPanelSettings(sendToPlayers: true);

        if (RemoveManagedBot(ev.Player.PlayerId))
        {
            EnsureBotPopulation(_warmupGeneration);
        }

        ScheduleNoActivePlayersBotReset();
    }

    private void OnPlayerShootingWeapon(PlayerShootingWeaponEventArgs ev)
    {
        if (!_warmupActive || !IsManagedBot(ev.Player) || !IsInEscapeSafezone(ev.Player))
        {
            return;
        }

        ev.IsAllowed = false;
    }

    private void OnPlayerDryFiringWeapon(PlayerDryFiringWeaponEventArgs ev)
    {
        if (!_warmupActive || !IsManagedBot(ev.Player) || !IsInEscapeSafezone(ev.Player))
        {
            return;
        }

        ev.IsAllowed = false;
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

        if (IsManagedBot(ev.Player))
        {
            MaintainReserveAmmo(ev.Player, ev.FirearmItem);
        }
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

        if (IsManagedBot(ev.Player))
        {
            MaintainReserveAmmo(ev.Player, ev.FirearmItem);
        }

        if (IsManagedBot(ev.Player)
            && ev.FirearmItem != null
            && _managedBots.TryGetValue(ev.Player.PlayerId, out ManagedBotState state))
        {
            _botCombatService.OnReloaded(state, Config.BotBehavior, _random);
        }
    }

    private void OnPlayerChangedItem(PlayerChangedItemEventArgs ev)
    {
        _bombModeService.OnChangedItem(ev);

        if (_warmupActive && IsManagedBot(ev.Player))
        {
            MaintainReserveAmmo(ev.Player, ev.NewItem as FirearmItem);
        }
    }

    private void TrimSpawnProtectionForDangerousItemUse(Player player)
    {
        if (!_warmupActive
            || !IsManagedHuman(player)
            || !TryGetSpawnProtection(player, out SpawnProtected spawnProtected)
            || !spawnProtected.IsEnabled
            || spawnProtected.TimeLeft <= DangerousItemSpawnProtectionTimeLeftSeconds)
        {
            return;
        }

        spawnProtected.TimeLeft = DangerousItemSpawnProtectionTimeLeftSeconds;
    }

    private void OnPlayerProcessingJailbirdMessage(PlayerProcessingJailbirdMessageEventArgs ev)
    {
        if (!_warmupActive
            || !IsManagedHuman(ev.Player)
            || !IsJailbirdDangerousUseMessage(ev.Message))
        {
            return;
        }

        TrimSpawnProtectionForDangerousItemUse(ev.Player);
    }

    private static bool IsJailbirdDangerousUseMessage(JailbirdMessageType message)
    {
        return message is JailbirdMessageType.AttackTriggered
            or JailbirdMessageType.AttackPerformed
            or JailbirdMessageType.ChargeLoadTriggered
            or JailbirdMessageType.ChargeStarted;
    }

    private void OnPlayerSearchedToy(PlayerSearchedToyEventArgs ev)
    {
        _bombModeService.OnSearchedToy(ev);
    }

    private void OnPlayerSearchingToy(PlayerSearchingToyEventArgs ev)
    {
        _bombModeService.OnSearchingToy(ev);
    }

    private void OnPlayerSearchToyAborted(PlayerSearchToyAbortedEventArgs ev)
    {
        _bombModeService.OnSearchToyAborted(ev);
    }

    private void OnPlayerUsingItem(PlayerUsingItemEventArgs ev)
    {
        _bombModeService.OnUsingItem(ev);
    }

    private void OnPlayerUsedItem(PlayerUsedItemEventArgs ev)
    {
        _bombModeService.OnUsedItem(ev);
    }

    private void OnPlayerCancelledUsingItem(PlayerCancelledUsingItemEventArgs ev)
    {
        _bombModeService.OnCancelledUsingItem(ev);
    }

    private void OnPlayerSearchingPickup(PlayerSearchingPickupEventArgs ev)
    {
        _bombModeService.OnSearchingPickup(ev);
    }

    private void OnPlayerPickingUpItem(PlayerPickingUpItemEventArgs ev)
    {
        _bombModeService.OnPickingUpItem(ev);
    }

    private void OnPlayerDroppedItem(PlayerDroppedItemEventArgs ev)
    {
        _bombModeService.OnDroppedItem(ev);

        Pickup? droppedPickup = ev.Pickup;
        if (droppedPickup != null && IsArmorItem(droppedPickup.Type))
        {
            Schedule(() => TryDestroyArmorPickup(droppedPickup, "drop"), DroppedArmorPickupDestroyDelayMs);
        }
    }

    private void OnPlayerCuffing(PlayerCuffingEventArgs ev)
    {
        _bombModeService.OnCuffing(ev);
    }

    private void RestartWarmup(string reason)
    {
        _warmupGeneration++;
        _warmupActive = true;
        ApiLogger.Info($"[{Name}] Starting warmup sandbox ({reason}).");
        CleanupManagedBots();
        int generation = _warmupGeneration;
        ScheduleNavHeartbeat(generation);
        ScheduleAutoCleanup(generation);
        ScheduleArmorPickupSanitizer(generation);
        ScheduleHelpReminderBroadcast(generation);
        Schedule(() => SetupWarmup(generation), Config.InitialSetupDelayMs);
    }

    private int EnsureBotRuntimeActive(string reason)
    {
        if (_warmupActive)
        {
            return _warmupGeneration;
        }

        _warmupGeneration++;
        _warmupActive = true;
        int generation = _warmupGeneration;
        ApiLogger.Info($"[{Name}] Starting bot runtime ({reason}).");
        ScheduleNavHeartbeat(generation);
        ScheduleAutoCleanup(generation);
        ScheduleArmorPickupSanitizer(generation);
        ScheduleHelpReminderBroadcast(generation);
        PrepareArenaMapForWarmup();
        PrepareFacilityNavMeshForWarmup();
        CleanupArmorPickups();
        return generation;
    }

    private void SetupWarmup(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        ClampConfiguredBotCount();

        if (!Player.List.Any(IsManagedHuman))
        {
            ApiLogger.Info($"[{Name}] Warmup setup deferred because no authenticated human players are present yet.");
            ResetBotCountIfNoActivePlayers(generation);
            _warmupActive = false;
            return;
        }

        PrepareArenaMapForWarmup();
        PrepareFacilityNavMeshForWarmup();
        CleanupArmorPickups();

        Schedule(() =>
        {
            if (!IsCurrentGeneration(generation) || !_warmupActive)
            {
                return;
            }

            EnsureBotPopulation(generation);
        }, Config.BotSpawnDelayMs);

        if (_bombModeService.Enabled)
        {
            int bombRoundDelayMs = Config.BotSpawnDelayMs + Config.BotRoleAssignDelayMs + Config.BotInitialActivationDelayMs + 500;
            Schedule(() => BeginBombModeRound(generation), bombRoundDelayMs);
        }

        if (Config.BroadcastWarmupStatus)
        {
            string statusText = Config.PlayerPanelEnabled
                ? WarmupLocalization.T(
                    $"{Name} active: {Config.BotCount} bots. Open Server Specific Settings for controls.",
                    $"{Name} 已启用：{Config.BotCount} 个机器人。打开服务器专属设置使用控制。")
                : WarmupLocalization.T(
                    $"{Name} active: {Config.BotCount} bots.",
                    $"{Name} 已启用：{Config.BotCount} 个机器人。");

            foreach (Player player in Player.List.Where(IsManagedHuman))
            {
                player.SendHint(statusText, 4f);
            }
        }
    }

    private void EnsureBotPopulation(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        ClampConfiguredBotCount();
        CleanupMissingBotEntries();
        TrimExcessBots();
        while (_managedBots.Count < Config.BotCount)
        {
            SpawnBot(generation);
        }
    }

    private void ScheduleNoActivePlayersBotReset()
    {
        if (!Config.ResetBotCountWhenNoActivePlayers)
        {
            return;
        }

        int generation = _warmupGeneration;
        Schedule(() => ResetBotCountIfNoActivePlayers(generation), Math.Max(0, Config.NoActivePlayersBotResetDelayMs));
    }

    private void ResetBotCountIfNoActivePlayers(int generation)
    {
        if (!Config.ResetBotCountWhenNoActivePlayers
            || !IsCurrentGeneration(generation)
            || Player.List.Any(IsManagedHuman))
        {
            return;
        }

        int idleBotCount = ClampBotCount(Config.NoActivePlayersBotCount);
        if (Config.BotCount == idleBotCount)
        {
            return;
        }

        int previousBotCount = Config.BotCount;
        Config.BotCount = idleBotCount;

        if (_warmupActive)
        {
            EnsureBotPopulation(generation);
            TrimExcessBots();
        }

        ApiLogger.Info($"[{Name}] No active human players remain; bot count reset from {previousBotCount} to {Config.BotCount}.");
    }

    private int ClampBotCount(int botCount)
    {
        return Math.Min(Math.Max(0, botCount), Math.Max(0, Config.MaxBotCount));
    }

    private int ClampPlayerBotCount(int botCount)
    {
        int playerMax = Math.Min(Math.Max(0, Config.MaxPlayerBotCount), Math.Max(0, Config.MaxBotCount));
        return Math.Min(Math.Max(0, botCount), playerMax);
    }

    private void ClampConfiguredBotCount()
    {
        int clamped = ClampBotCount(Config.BotCount);
        if (clamped == Config.BotCount)
        {
            return;
        }

        ApiLogger.Warn($"[{Name}] Configured bot count {Config.BotCount} exceeds max {Config.MaxBotCount}; clamping to {clamped}.");
        Config.BotCount = clamped;
    }

    private void ClampConfiguredLimits()
    {
        if (Config.MaxBotCount < 0)
        {
            ApiLogger.Warn($"[{Name}] Configured max bot count {Config.MaxBotCount} is negative; clamping to 0.");
            Config.MaxBotCount = 0;
        }

        if (Config.MaxPlayerBotCount >= 0)
        {
            return;
        }

        ApiLogger.Warn($"[{Name}] Configured player max bot count {Config.MaxPlayerBotCount} is negative; clamping to 0.");
        Config.MaxPlayerBotCount = 0;
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
        RefreshPlayerPanelSettings(sendToPlayers: true);
        Schedule(() => ActivateSpawnedBot(bot.PlayerId, generation), Config.BotRoleAssignDelayMs);
    }

    public bool AddBots(int count, out string response)
    {
        if (count <= 0)
        {
            response = "Bot add count must be greater than zero.";
            return false;
        }

        int baselineCount = Math.Max(Config.BotCount, _managedBots.Count);
        int maxCount = Math.Max(0, Config.MaxBotCount);
        int available = Math.Max(0, maxCount - baselineCount);
        if (available <= 0)
        {
            response = $"Cannot add bots because max bot count is {maxCount}.";
            return false;
        }

        int addCount = Math.Min(count, available);
        int generation = EnsureBotRuntimeActive("remote admin add bot");
        Config.BotCount = baselineCount + addCount;
        for (int i = 0; i < addCount; i++)
        {
            SpawnBot(generation);
        }

        SaveConfig();
        response = addCount == count
            ? $"Added {addCount} bot(s). Managed bots now {_managedBots.Count}/{Config.BotCount}."
            : $"Added {addCount} bot(s); max bot count {maxCount} reached. Managed bots now {_managedBots.Count}/{Config.BotCount}.";
        return true;
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
        state.ResetNavigationRuntimeState();
        state.LastMoveIntentLabel = "none";
        state.LastMoveIntentTick = 0;
        state.ForwardStallSinceTick = 0;
        state.NextForwardJumpTick = 0;
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
        state.ZoomHeld = false;
        state.ZoomHeldTargetPlayerId = -1;
        state.LastZoomDebugTick = 0;
        state.AiState = BotAiState.Chase;
        state.AiStateEnteredTick = Environment.TickCount;
        state.OrbitDirection = _random.Next(0, 2) == 0 ? -1 : 1;
        state.NextStrafeFlipTick = 0;
        state.ReactiveStrafeUntilTick = 0;
        state.CampUntilTick = 0;
        state.CampCooldownUntilTick = 0;
        ApplyRoundCampGate(state);
        state.CampAimPoint = default;
        state.TargetSwitchLockUntilTick = 0;
        state.LastStateSummary = "chase";
        state.LastTargetSummary = "none";
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
            if (!state.SpawnSetupCompleted)
            {
                ConfigureSpawnedBot(bot);
            }
            ScheduleNextInitialActivationAttempt(playerId, generation, attempt);
        }, attempt == 0 ? Config.BotInitialActivationDelayMs : Config.BotActivationRetryDelayMs);
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

        LoadoutDefinition? botLoadout = GetBotLoadout(player, state);
        if (!Config.UseBotRoleDefaultLoadout && botLoadout != null)
        {
            ApplyLoadout(player, botLoadout, isBot: true);
        }
        EnsureFirearmEquipped(player, allowFallbackCom15: true);
        MaintainReserveAmmo(player, player.CurrentItem as FirearmItem);
        RandomizeBotInventoryFirearmAttachments(player, "spawn-configure");
        int arenaReadyDelayMs = ApplyArenaSpawnIfNeeded(player, isBot: true);
        RestoreVitals(player);
        state.SpawnSetupCompleted = true;
        state.LastPosition = player.Position;
        state.ResetNavigationRuntimeState();
        state.LastMoveIntentLabel = "none";
        state.LastMoveIntentTick = 0;
        state.ForwardStallSinceTick = 0;
        state.NextForwardJumpTick = 0;
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
        state.ZoomHeld = false;
        state.ZoomHeldTargetPlayerId = -1;
        state.LastZoomDebugTick = 0;
        state.HasStallAnchor = false;
        state.StallAnchorPosition = default;
        state.StallAnchorSinceTick = 0;
        state.AStarFallbackActive = false;
        state.AiState = BotAiState.Chase;
        state.AiStateEnteredTick = Environment.TickCount;
        state.OrbitDirection = _random.Next(0, 2) == 0 ? -1 : 1;
        state.NextStrafeFlipTick = 0;
        state.CampUntilTick = 0;
        state.CampCooldownUntilTick = 0;
        ApplyRoundCampGate(state);
        state.CampAimPoint = default;
        state.TargetSwitchLockUntilTick = 0;
        state.LastStateSummary = "chase";
        state.LastTargetSummary = "none";
        state.BrainToken++;
        int brainToken = state.BrainToken;
        ScheduleBotAttachmentRandomizationChecks(player.PlayerId, brainToken, _warmupGeneration);
        Schedule(() =>
        {
            if (!IsCurrentGeneration(_warmupGeneration)
                || !_managedBots.TryGetValue(player.PlayerId, out ManagedBotState latest)
                || latest.BrainToken != brainToken
                || !Player.TryGet(player.PlayerId, out Player liveBot)
                || liveBot.IsDestroyed
                || liveBot.Role == RoleTypeId.Spectator)
            {
                return;
            }

            EnsureFirearmEquipped(liveBot, allowFallbackCom15: true);
            ScheduleBotBrain(player.PlayerId, brainToken, _warmupGeneration);
        }, arenaReadyDelayMs);
    }

    private void ScheduleBotRespawn(int playerId)
    {
        if (!_managedBots.TryGetValue(playerId, out ManagedBotState state))
        {
            return;
        }

        state.SpawnSetupCompleted = false;
        state.ResetNavigationRuntimeState();
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
                Schedule(() =>
                {
                    if (!IsCurrentGeneration(generation)
                        || !_managedBots.TryGetValue(playerId, out ManagedBotState liveState)
                        || liveState.BrainToken != token
                        || !Player.TryGet(playerId, out Player liveBot)
                        || liveBot.IsDestroyed)
                    {
                        return;
                    }

                    liveBot.SetRole(GetBotRespawnRole(liveState), RoleChangeReason.Respawn, RoleSpawnFlags.All);
                }, BotPreForceRoleDelayMs);
                return;
            }

            RemoveManagedBot(playerId);
            EnsureBotPopulation(generation);
        }, Config.BotRespawnDelayMs);
    }

    private void ScheduleBotBrain(int playerId, int brainToken, int generation)
    {
        int minDelay = Config.BotBehavior.ThinkIntervalMinMs;
        int maxDelay = Config.BotBehavior.ThinkIntervalMaxMs;
        if (_managedBots.TryGetValue(playerId, out ManagedBotState state) && state.AiState == BotAiState.Camp)
        {
            minDelay = Math.Max(1, minDelay / 2);
            maxDelay = Math.Max(minDelay + 1, maxDelay / 2);
        }

        int delay = Next(minDelay, maxDelay);
        Schedule(() => RunBotBrain(playerId, brainToken, generation), delay);
    }

    private void ScheduleBotBrainWhenReady(int playerId, int brainToken, int generation, int attempt)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        if (!_managedBots.TryGetValue(playerId, out ManagedBotState state) || state.BrainToken != brainToken)
        {
            return;
        }

        if (_roundCampDisabledUntilTick != 0)
        {
            state.CampCooldownUntilTick = Math.Max(state.CampCooldownUntilTick, _roundCampDisabledUntilTick);
        }

        if (!Player.TryGet(playerId, out Player bot) || bot.IsDestroyed || bot.Role == RoleTypeId.Spectator)
        {
            return;
        }

        if (IsBotCombatReady(bot))
        {
            ScheduleBotBrain(playerId, brainToken, generation);
            return;
        }

        if (attempt >= BotBrainReadyMaxAttempts)
        {
            LogBotEvent(state, $"brain-start forcing retry={attempt} reason=not-ready");
            ScheduleBotBrain(playerId, brainToken, generation);
            return;
        }

        Schedule(() => ScheduleBotBrainWhenReady(playerId, brainToken, generation, attempt + 1), BotBrainReadyRetryDelayMs);
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

        ApplyRoundCampGate(state);
        bool useDust2Arena = ShouldUseDust2Arena() && _dust2MapService.IsLoaded;
        if (!useDust2Arena && TryRecoverBotOutsideAnyRoom(bot, state, generation))
        {
            ScheduleBotBrain(playerId, brainToken, generation);
            return;
        }

        bool useDust2NavMesh = useDust2Arena
            && Config.Dust2Map.RuntimeNavMeshEnabled
            && _dust2MapService.HasRuntimeNavMesh;
        bool useFacilityNavMesh = !useDust2Arena && ShouldUseFacilityNavMesh(bot);
        bool useNavMesh = useDust2NavMesh || useFacilityNavMesh;
        float navMeshSampleDistance = useDust2Arena
            ? Config.Dust2Map.RuntimeNavMeshSampleDistance
            : useFacilityNavMesh
                ? Config.BotBehavior.FacilityNavMeshSampleDistance
                : 0f;
        _botControllerService.TickBot(
            bot,
            state,
            Player.List.ToList(),
            Config.BotBehavior,
            _random,
            useDust2Arena,
            useNavMesh,
            navMeshSampleDistance,
            useDust2Arena,
            TryInvokeDummyAction,
            TryInvokeDummyAction,
            TryShootBot,
            TryReloadBot,
            MaintainReserveAmmo,
            LogNavDebug,
            UpdateFacilityDummyFollower,
            UpdateZoomHold,
            IsInEscapeSafezone,
            brainToken,
            generation);
        UpdateFacilityNavAgentFollower(bot, state, useNavMesh, useDust2Arena);
        UpdateNavAgentDebugVisual(bot, state, useNavMesh, useDust2Arena);

        ScheduleBotBrain(playerId, brainToken, generation);
    }

    private bool TryRecoverBotOutsideAnyRoom(Player bot, ManagedBotState state, int generation)
    {
        if (Room.TryGetRoomAtPosition(bot.Position, out Room room)
            && room != null
            && !room.IsDestroyed)
        {
            return false;
        }

        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastNoRoomRespawnTick) < BotNoRoomRespawnCooldownMs)
        {
            return true;
        }

        state.LastNoRoomRespawnTick = nowTick;
        state.ResetNavigationRuntimeState();
        state.Engagement.Reset();
        RoleTypeId respawnRole = GetBotRespawnRole(state);
        LogBotEvent(state, $"no-room-respawn role={respawnRole} pos={FormatVector(bot.Position)}");
        bot.SetRole(respawnRole, RoleChangeReason.Respawn, RoleSpawnFlags.All);
        ClearBotSpawnProtection(bot);

        int playerId = bot.PlayerId;
        Schedule(() =>
        {
            if (IsCurrentGeneration(generation)
                && Player.TryGet(playerId, out Player liveBot)
                && IsManagedBot(liveBot))
            {
                ClearBotSpawnProtection(liveBot);
            }
        }, BotPostSpawnHookDelayMs);

        return true;
    }

    private void ApplyRoundCampGate(ManagedBotState state)
    {
        if (_roundCampDisabledUntilTick == 0)
        {
            return;
        }

        state.CampCooldownUntilTick = Math.Max(state.CampCooldownUntilTick, _roundCampDisabledUntilTick);
    }

    private bool ShouldUseFacilityNavMesh(Player bot)
    {
        if (!_facilityNavMeshService.HasRuntimeNavMesh
            || !TryGetClosestRoomZone(bot.Position, out FacilityZone zone))
        {
            return false;
        }

        bool fullFacilityEnabled = Config.BotBehavior.UseFacilityNavMesh
            && Config.BotBehavior.FacilityRuntimeNavMeshEnabled;
        if (fullFacilityEnabled)
        {
            return zone != FacilityZone.None;
        }

        return Config.BotBehavior.UseFacilitySurfaceNavMesh
            && Config.BotBehavior.FacilitySurfaceRuntimeNavMeshEnabled
            && zone == FacilityZone.Surface;
    }

    private static bool TryGetClosestRoomZone(Vector3 position, out FacilityZone zone)
    {
        zone = FacilityZone.None;
        if (Room.List == null)
        {
            return false;
        }

        bool found = false;
        float bestScore = float.PositiveInfinity;
        foreach (Room room in Room.List)
        {
            if (room == null || room.IsDestroyed)
            {
                continue;
            }

            float verticalDelta = Mathf.Abs(room.Position.y - position.y);
            if (verticalDelta > 30f)
            {
                continue;
            }

            float horizontalDelta = Vector2.Distance(
                new Vector2(room.Position.x, room.Position.z),
                new Vector2(position.x, position.z));
            float score = horizontalDelta + (verticalDelta * 4f);
            if (score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            zone = room.Zone;
        }

        return found && zone != FacilityZone.None;
    }

    private void TryReloadBot(Player bot, ManagedBotState state, FirearmItem firearm)
    {
        int nowTick = Environment.TickCount;
        if (unchecked(nowTick - state.LastReloadAttemptTick) < Config.BotBehavior.MinReloadAttemptIntervalMs)
        {
            return;
        }

        state.LastReloadAttemptTick = nowTick;
        ReleaseZoomHold(bot, state, "reload");
        MaintainReserveAmmo(bot, firearm);

        bool triggered = false;
        if (firearm.CanReload && !firearm.IsReloadingOrUnloading)
        {
            triggered = firearm.Reload();
        }

        if (!triggered)
        {
            triggered = TryInvokeDummyAction(bot, Config.BotBehavior.ReloadActionName);
        }

        if (!triggered && GetLoadedAmmo(firearm) <= 1)
        {
            RefillFirearm(firearm);
            MaintainReserveAmmo(bot, firearm);
        }

        LogBotEvent(state, $"reload-attempt triggered={triggered} item={firearm.Type} loaded={GetLoadedAmmo(firearm)} reserve={GetReserveAmmoSafe(bot, firearm)}");
    }

    private Player? ResolveEngagementTarget(ManagedBotState state)
    {
        return state.Engagement.TargetPlayerId >= 0
            && Player.TryGet(state.Engagement.TargetPlayerId, out Player target)
            && !target.IsDestroyed
            && target.Role != RoleTypeId.Spectator
            ? target
            : null;
    }

    private void TriggerReloadEvasiveStrafe(Player bot, ManagedBotState state, Player? target)
    {
        int nowTick = Environment.TickCount;
        int preferredDirection = ChooseReloadStrafeDirection(bot, target);
        state.StrafeDirection = preferredDirection;
        string[] primaryActions = preferredDirection >= 0
            ? Config.BotBehavior.WalkRightActionNames
            : Config.BotBehavior.WalkLeftActionNames;
        string[] fallbackActions = preferredDirection >= 0
            ? Config.BotBehavior.WalkLeftActionNames
            : Config.BotBehavior.WalkRightActionNames;

        bool moved = TryInvokeDummyAction(bot, primaryActions);
        if (!moved)
        {
            moved = TryInvokeDummyAction(bot, fallbackActions);
        }

        LogBotEvent(
            state,
            $"reload-evasive-strafe moved={moved} direction={(preferredDirection >= 0 ? "right" : "left")} target={target?.Nickname ?? "none"}");
    }

    private int ChooseReloadStrafeDirection(Player bot, Player? target)
    {
        if (target == null)
        {
            return _random.Next(0, 2) == 0 ? -1 : 1;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 right = yawRotation * Vector3.right;
        Vector3 targetAimPoint = target.Camera != null
            ? target.Camera.position
            : target.Position + Vector3.up * Config.BotBehavior.TargetAimHeightOffset;

        bool leftExitsLos = !WouldHaveLineOfFireFrom(bot, bot.Position - right * 1.75f, target, targetAimPoint);
        bool rightExitsLos = !WouldHaveLineOfFireFrom(bot, bot.Position + right * 1.75f, target, targetAimPoint);
        if (leftExitsLos != rightExitsLos)
        {
            return rightExitsLos ? 1 : -1;
        }

        Vector3 toTarget = target.Position - bot.Position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude > 0.0001f)
        {
            return Vector3.Dot(toTarget.normalized, right) >= 0f ? -1 : 1;
        }

        return _random.Next(0, 2) == 0 ? -1 : 1;
    }

    private bool WouldHaveLineOfFireFrom(Player bot, Vector3 projectedPosition, Player target, Vector3 targetAimPoint)
    {
        Vector3 origin = projectedPosition + Vector3.up * Config.BotBehavior.TargetAimHeightOffset;
        Vector3 direction = targetAimPoint - origin;
        float distance = direction.magnitude;
        if (distance < 0.01f)
        {
            return true;
        }

        RaycastHit[] hits = Physics.RaycastAll(origin, direction.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        Transform botRoot = bot.ReferenceHub.transform;
        Transform targetRoot = target.ReferenceHub.transform;
        bool sawBlockingCandidate = false;

        foreach (RaycastHit hit in hits)
        {
            Transform hitTransform = hit.transform;
            if (hitTransform == null)
            {
                continue;
            }

            if (hitTransform == botRoot || hitTransform.IsChildOf(botRoot))
            {
                continue;
            }

            sawBlockingCandidate = true;
            if (hitTransform == targetRoot || hitTransform.IsChildOf(targetRoot))
            {
                return true;
            }

            return false;
        }

        return !sawBlockingCandidate;
    }

    private void TriggerReactiveStrafe(Player bot, ManagedBotState state, Player attacker)
    {
        int nowTick = Environment.TickCount;
        if (unchecked(state.ReactiveStrafeCooldownUntilTick - nowTick) > 0)
        {
            return;
        }

        state.ReactiveStrafeUntilTick = nowTick + BotControllerService.GetReactiveStrafeDurationMs(Config.BotBehavior);
        state.ReactiveStrafeCooldownUntilTick = nowTick + Math.Max(0, Config.BotBehavior.ReactiveStrafeCooldownMs);
        state.NextStrafeFlipTick = 0;
        state.TargetSwitchLockUntilTick = 0;

        Vector3 lastKnownAimPoint = attacker.Camera != null
            ? attacker.Camera.position
            : attacker.Position + Vector3.up * Config.BotBehavior.TargetAimHeightOffset;
        state.Engagement.LastKnownAimPoint = lastKnownAimPoint;

        Vector3 toAttacker = attacker.Position - bot.Position;
        toAttacker.y = 0f;
        if (toAttacker.sqrMagnitude > 0.0001f)
        {
            float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
            Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
            Vector3 right = yawRotation * Vector3.right;
            state.StrafeDirection = Vector3.Dot(toAttacker.normalized, right) >= 0f ? -1 : 1;
        }
        else
        {
            state.StrafeDirection = _random.Next(0, 2) == 0 ? -1 : 1;
        }

        string[] primaryActions = state.StrafeDirection >= 0
            ? Config.BotBehavior.WalkRightActionNames
            : Config.BotBehavior.WalkLeftActionNames;
        string[] fallbackActions = state.StrafeDirection >= 0
            ? Config.BotBehavior.WalkLeftActionNames
            : Config.BotBehavior.WalkRightActionNames;

        if (!TryInvokeDummyAction(bot, primaryActions))
        {
            TryInvokeDummyAction(bot, fallbackActions);
        }
    }

    private void ApplyLoadout(Player player, LoadoutDefinition loadout, bool isBot)
    {
        if (loadout.ClearInventory)
        {
            player.ClearInventory();
            player.ClearAmmo();
        }

        FirearmItem? primaryFirearm = null;
        List<FirearmItem> loadoutFirearms = new();
        foreach (ItemType itemType in loadout.Items ?? Array.Empty<ItemType>())
        {
            Item item = player.AddItem(itemType, ItemAddReason.AdminCommand);
            if (item is FirearmItem firearm)
            {
                loadoutFirearms.Add(firearm);
                primaryFirearm ??= firearm;
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

        if (ShouldRandomizeFirearmAttachments(loadout, isBot))
        {
            foreach (FirearmItem firearm in loadoutFirearms)
            {
                RandomizeFirearmAttachments(player, firearm, "loadout");
            }
        }

        if (loadout.RefillActiveFirearmOnSpawn)
        {
            RefillFirearm(primaryFirearm);
        }

        MaintainReserveAmmo(player, primaryFirearm);
    }

    private bool ShouldRandomizeFirearmAttachments(LoadoutDefinition loadout, bool isBot)
    {
        if (!Config.RandomizeFirearmAttachments
            || !loadout.RandomizeFirearmAttachmentsOnSpawn)
        {
            return false;
        }

        return Config.FirearmAttachmentRandomizationMode switch
        {
            FirearmAttachmentRandomizationMode.BotsOnly => isBot,
            FirearmAttachmentRandomizationMode.AllLoadouts => true,
            _ => isBot,
        };
    }

    private void ScheduleBotAttachmentRandomizationChecks(int playerId, int brainToken, int generation)
    {
        foreach (int delayMs in BotAttachmentRandomizationDelaysMs)
        {
            Schedule(() =>
            {
                if (!IsCurrentGeneration(generation)
                    || !_managedBots.TryGetValue(playerId, out ManagedBotState latest)
                    || latest.BrainToken != brainToken
                    || !Player.TryGet(playerId, out Player liveBot)
                    || liveBot.IsDestroyed
                    || liveBot.Role == RoleTypeId.Spectator)
                {
                    return;
                }

                EnsureFirearmEquipped(liveBot, allowFallbackCom15: true);
                RandomizeBotInventoryFirearmAttachments(liveBot, $"delayed-{delayMs}ms");
            }, delayMs);
        }
    }

    private void RandomizeBotInventoryFirearmAttachments(Player player, string phase)
    {
        if (!Config.RandomizeFirearmAttachments
            || (Config.FirearmAttachmentRandomizationMode != FirearmAttachmentRandomizationMode.BotsOnly
                && Config.FirearmAttachmentRandomizationMode != FirearmAttachmentRandomizationMode.AllLoadouts))
        {
            LogAttachment($"skip phase={phase} player={FormatPlayer(player)} reason=config global={Config.RandomizeFirearmAttachments} mode={Config.FirearmAttachmentRandomizationMode}");
            return;
        }

        FirearmItem[] firearms = player.Items.OfType<FirearmItem>().ToArray();
        LogAttachment(
            $"scan phase={phase} player={FormatPlayer(player)} role={player.Role} current={player.CurrentItem?.Type.ToString() ?? "none"} " +
            $"items=[{string.Join(",", player.Items.Select(item => item.Type))}] firearms=[{string.Join(",", firearms.Select(firearm => $"{firearm.Type}#{firearm.Serial} code={firearm.AttachmentsCode} active={FormatActiveAttachments(firearm)}"))}]");

        if (firearms.Length == 0)
        {
            LogAttachment($"skip phase={phase} player={FormatPlayer(player)} reason=no-firearms");
            return;
        }

        foreach (FirearmItem firearm in firearms)
        {
            RandomizeFirearmAttachments(player, firearm, phase);
        }
    }

    private void RandomizeFirearmAttachments(Player owner, FirearmItem firearm, string phase)
    {
        try
        {
            uint randomCode = GetRandomAttachmentCode(firearm);
            uint validatedCode = firearm.ValidateAttachmentsCode(randomCode);
            uint beforeCode = firearm.AttachmentsCode;
            string beforeActive = FormatActiveAttachments(firearm);
            ApplyFirearmAttachmentPreference(owner, firearm.Type, validatedCode);
            firearm.AttachmentsCode = validatedCode;
            AttachmentsUtils.ApplyAttachmentsCode(firearm.Base, validatedCode, true);
            AttachmentCodeSync.ServerSetCode(firearm.Serial, validatedCode);
            LogAttachment(
                $"apply phase={phase} player={FormatPlayer(owner)} weapon={firearm.Type} serial={firearm.Serial} " +
                $"beforeCode={beforeCode} randomCode={randomCode} validatedCode={validatedCode} " +
                $"before=[{beforeActive}] after=[{FormatActiveAttachments(firearm)}] " +
                $"preferenceMethod={(AttachmentsServerApplyPreferenceMethod == null ? "missing" : "ok")}");
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[{Name}] [AttachmentDebug] fail phase={phase} player={FormatPlayer(owner)} weapon={firearm.Type} serial={firearm.Serial}: {ex}");
        }
    }

    private static void ApplyFirearmAttachmentPreference(Player owner, ItemType firearmType, uint attachmentsCode)
    {
        AttachmentsServerApplyPreferenceMethod?.Invoke(null, new object[] { owner.ReferenceHub, firearmType, attachmentsCode });
    }

    private void LogAttachment(string message)
    {
        if (Config.EnableAttachmentLogging)
        {
            ApiLogger.Info($"[{Name}] [AttachmentDebug] {message}");
        }
    }

    private static string FormatPlayer(Player player)
    {
        return $"{player.Nickname}#{player.PlayerId}";
    }

    private static string FormatActiveAttachments(FirearmItem firearm)
    {
        try
        {
            string[] active = firearm.ActiveAttachments
                .Select(attachment => attachment.Name.ToString())
                .ToArray();
            return active.Length == 0 ? "none" : string.Join("+", active);
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private uint GetRandomAttachmentCode(FirearmItem firearm)
    {
        AttachmentName[] selectedAttachments = firearm.Attachments
            .Where(attachment => attachment != null && attachment.Slot != AttachmentSlot.Unassigned)
            .GroupBy(attachment => attachment.Slot)
            .Select(group =>
            {
                var choices = group.ToArray();
                return choices[_random.Next(choices.Length)].Name;
            })
            .Where(name => name != AttachmentName.None)
            .ToArray();

        return selectedAttachments.Length == 0
            ? AttachmentsUtils.GetRandomAttachmentsCode(firearm.Type)
            : firearm.ValidateAttachmentsCode(selectedAttachments);
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

    private static Player? FindNearestHostile(Player bot)
    {
        Player? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Player candidate in Player.List)
        {
            if (!AreCombatantsHostile(bot, candidate))
            {
                continue;
            }

            float distance = Vector3.Distance(bot.Position, candidate.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static bool AreCombatantsHostile(Player bot, Player candidate)
    {
        if (candidate.PlayerId == bot.PlayerId
            || !BotTargetingService.IsCombatTarget(candidate)
            || !BotTargetingService.IsCombatTarget(bot))
        {
            return false;
        }

        if (bot.Team == Team.SCPs)
        {
            return candidate.Team != Team.SCPs;
        }

        if (candidate.Team == Team.SCPs)
        {
            return true;
        }

        if (IsFoundationHumanRole(bot.Role) && IsFoundationHumanRole(candidate.Role))
        {
            return false;
        }

        if (IsChaosHumanRole(bot.Role) && IsChaosHumanRole(candidate.Role))
        {
            return false;
        }

        return candidate.Team != bot.Team;
    }

    private static bool IsFoundationHumanRole(RoleTypeId role)
    {
        return role is RoleTypeId.NtfCaptain
            or RoleTypeId.NtfPrivate
            or RoleTypeId.NtfSergeant
            or RoleTypeId.NtfSpecialist
            or RoleTypeId.FacilityGuard
            or RoleTypeId.Scientist;
    }

    private static bool IsChaosHumanRole(RoleTypeId role)
    {
        return role is RoleTypeId.ChaosConscript
            or RoleTypeId.ChaosMarauder
            or RoleTypeId.ChaosRepressor
            or RoleTypeId.ChaosRifleman
            or RoleTypeId.ClassD;
    }

    private void ShowLoadoutMenuHint(Player player, float duration)
    {
        player.SendHint(BuildLoadoutMenu(player), duration);
    }

    public string BuildLoadoutMenu(Player player)
    {
        return WarmupLocalization.T(
            "Human loadout controls are disabled in bot-only mode.",
            "仅机器人模式已禁用人类预设功能。");
    }

    public string GetSelectedHumanLoadoutName(Player player)
    {
        return _humanPresetService.GetSelectedPresetName(Config, _selectedHumanLoadouts, player);
    }

    public bool TrySelectHumanLoadout(Player player, string selector, bool applyNow, out string response)
    {
        response = WarmupLocalization.T(
            "Human loadout controls are disabled in bot-only mode.",
            "仅机器人模式已禁用人类预设功能。");
        return false;
    }

    private bool TryApplyTemporaryScpRole(Player player, RoleTypeId scpRole, out string response)
    {
        if (player.Role == RoleTypeId.Spectator)
        {
            player.SetRole(scpRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
            response = WarmupLocalization.T(
                $"Temporary SCP practice role: {scpRole}. Spawned at the default spawnpoint. Your selected human loadout is unchanged for your next respawn.",
                $"临时 SCP 练习角色：{scpRole}。已在默认出生点重生。你的下一次重生仍会使用之前选择的人类预设。");
            player.SendHint(response, 5f);
            return true;
        }

        Vector3 position = player.Position;
        Vector2 lookRotation = player.LookRotation;
        player.ClearInventory();
        player.ClearAmmo();
        player.SetRole(scpRole, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
        RestoreTemporaryScpPosition(player.PlayerId, scpRole, position, lookRotation, 50);
        RestoreTemporaryScpPosition(player.PlayerId, scpRole, position, lookRotation, 250);
        response = WarmupLocalization.T(
            $"Temporary SCP practice role: {scpRole}. Your selected human loadout is unchanged for your next respawn.",
            $"临时 SCP 练习角色：{scpRole}。你的下一次重生仍会使用之前选择的人类预设。");
        player.SendHint(response, 5f);
        return true;
    }

    private void RestoreTemporaryScpPosition(int playerId, RoleTypeId scpRole, Vector3 position, Vector2 lookRotation, int delayMs)
    {
        Schedule(() =>
        {
            if (!Player.TryGet(playerId, out Player livePlayer)
                || livePlayer.IsDestroyed
                || livePlayer.Role != scpRole)
            {
                return;
            }

            livePlayer.Position = position;
            livePlayer.LookRotation = lookRotation;
            livePlayer.ClearInventory();
            livePlayer.ClearAmmo();
            RestoreVitals(livePlayer);
        }, delayMs);
    }

    private static bool TryGetTemporaryScpRole(string selector, out RoleTypeId role)
    {
        switch (selector.Trim().ToLowerInvariant().Replace("scp", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty))
        {
            case "173":
                role = RoleTypeId.Scp173;
                return true;
            case "939":
                role = RoleTypeId.Scp939;
                return true;
            case "106":
                role = RoleTypeId.Scp106;
                return true;
            case "049":
            case "49":
                role = RoleTypeId.Scp049;
                return true;
            case "3114":
                role = RoleTypeId.Scp3114;
                return true;
            case "096":
            case "96":
                role = RoleTypeId.Scp096;
                return true;
            default:
                role = RoleTypeId.None;
                return false;
        }
    }

    private void EnsureFirearmEquipped(Player player, bool allowFallbackCom15)
    {
        if (allowFallbackCom15 && BotCombatService.IsSupportedScpAttacker(player.Role))
        {
            return;
        }

        if (player.CurrentItem is FirearmItem)
        {
            MaintainCom15FallbackAmmo(player, player.CurrentItem as FirearmItem);
            return;
        }

        FirearmItem? firearm = player.Items.OfType<FirearmItem>().FirstOrDefault();
        if (firearm == null && allowFallbackCom15)
        {
            firearm = player.AddItem(ItemType.GunCOM15, ItemAddReason.AdminCommand) as FirearmItem;
            if (firearm != null)
            {
                RefillFirearm(firearm);
                player.SetAmmo(ItemType.Ammo9x19, Math.Max(player.GetAmmo(ItemType.Ammo9x19), DefaultReserveAmmoTarget));
            }
        }

        if (firearm != null)
        {
            player.CurrentItem = firearm;
            MaintainCom15FallbackAmmo(player, firearm);
        }
    }

    private static void MaintainCom15FallbackAmmo(Player player, FirearmItem? firearm)
    {
        if (firearm?.Type != ItemType.GunCOM15)
        {
            return;
        }

        RefillFirearm(firearm);
        if (player.GetAmmo(ItemType.Ammo9x19) < DefaultReserveAmmoTarget)
        {
            player.SetAmmo(ItemType.Ammo9x19, DefaultReserveAmmoTarget);
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
        if (!IsManagedBot(player))
        {
            return;
        }

        if (firearm == null || !IsAmmoType(firearm.AmmoType))
        {
            return;
        }

        _managedBots.TryGetValue(player.PlayerId, out ManagedBotState? state);
        LoadoutDefinition? loadout = GetBotLoadout(player, state);

        if (loadout != null && !loadout.InfiniteReserveAmmo)
        {
            return;
        }

        ushort targetReserve = loadout == null
            ? DefaultReserveAmmoTarget
            : GetReserveAmmoTarget(loadout, firearm.AmmoType);
        if (targetReserve == 0)
        {
            targetReserve = DefaultReserveAmmoTarget;
        }

        if (targetReserve > 0 && player.GetAmmo(firearm.AmmoType) < targetReserve)
        {
            player.SetAmmo(firearm.AmmoType, targetReserve);
        }
    }

    private LoadoutDefinition? GetBotLoadout(Player player, ManagedBotState? state)
    {
        RoleTypeId role = state?.RespawnRole ?? player.Role;
        if (role == RoleTypeId.None || role == RoleTypeId.Spectator)
        {
            role = Config.BotRole;
        }

        // Keep the configured sandbox loadout for the default bot role, but let
        // manually switched roles keep their native role gear.
        return role == Config.BotRole ? Config.BotLoadout : null;
    }

    private static void RestoreVitals(Player player)
    {
        player.Health = player.MaxHealth;
        player.ArtificialHealth = 0f;
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
        return firearm == null || !IsAmmoType(firearm.AmmoType) ? -1 : player.GetAmmo(firearm.AmmoType);
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

        bool usesScpAttack = BotCombatService.IsSupportedScpAttacker(bot.Role);
        if (!usesScpAttack && bot.CurrentItem is not FirearmItem)
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

    private readonly struct GroupedDummyActionEntry
    {
        public GroupedDummyActionEntry(string category, DummyAction action)
        {
            Category = category;
            Action = action;
        }

        public string Category { get; }

        public DummyAction Action { get; }
    }

    private GroupedDummyActionEntry[] GetGroupedDummyActions(Player bot)
    {
        if (DummyActionCollectorGetCacheMethod == null
            || DummyActionProvidersField == null
            || RootDummyPopulateActionsMethod == null)
        {
            return Array.Empty<GroupedDummyActionEntry>();
        }

        try
        {
            object? cache = DummyActionCollectorGetCacheMethod.Invoke(null, new object[] { bot.ReferenceHub });
            if (cache == null)
            {
                return Array.Empty<GroupedDummyActionEntry>();
            }

            DummyActionCacheUpdateMethod?.Invoke(cache, Array.Empty<object>());
            object? providers = DummyActionProvidersField.GetValue(cache);
            if (providers is not Array providerArray || providerArray.Length == 0)
            {
                return Array.Empty<GroupedDummyActionEntry>();
            }

            List<GroupedDummyActionEntry> actions = new();
            foreach (object? provider in providerArray)
            {
                if (provider == null)
                {
                    continue;
                }

                string currentCategory = string.Empty;
                Action<DummyAction> addAction = action =>
                {
                    if (!string.IsNullOrWhiteSpace(action.Name) && action.Action != null)
                    {
                        actions.Add(new GroupedDummyActionEntry(currentCategory, action));
                    }
                };

                Action<string> addCategory = category =>
                {
                    currentCategory = category?.Trim() ?? string.Empty;
                };

                RootDummyPopulateActionsMethod.Invoke(provider, new object[] { addAction, addCategory });
            }

            return actions.ToArray();
        }
        catch
        {
            return Array.Empty<GroupedDummyActionEntry>();
        }
    }

    private static bool IsItemScopedActionName(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return false;
        }

        return actionName.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0
            || actionName.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0
            || actionName.IndexOf("zoom", StringComparison.OrdinalIgnoreCase) >= 0
            || actionName.IndexOf("inspect", StringComparison.OrdinalIgnoreCase) >= 0
            || actionName.IndexOf("holster", StringComparison.OrdinalIgnoreCase) >= 0
            || actionName.IndexOf("drop", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string[] GetCurrentItemModulePrefixes(Player bot)
    {
        return bot.CurrentItem == null
            ? Array.Empty<string>()
            : BotCombatService.GetItemActionCategoryAliases(bot.CurrentItem.Type);
    }

    private static int ScoreDummyActionCategory(string category, string[] itemModulePrefixes)
    {
        if (itemModulePrefixes == null || itemModulePrefixes.Length == 0)
        {
            return string.IsNullOrWhiteSpace(category) ? 10 : 0;
        }

        int bestScore = string.IsNullOrWhiteSpace(category) ? 100 : 0;
        foreach (string itemModulePrefix in itemModulePrefixes.Where(prefix => !string.IsNullOrWhiteSpace(prefix)))
        {
            if (category.StartsWith(itemModulePrefix + " (#", StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 500);
            }

            string anyCategory = $"{itemModulePrefix} (ANY)";
            if (string.Equals(category, anyCategory, StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 400);
            }

            if (category.StartsWith(itemModulePrefix + " (", StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 300);
            }

            if (string.Equals(category, itemModulePrefix, StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 200);
            }

            if (category.IndexOf(itemModulePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bestScore = Math.Max(bestScore, 100);
            }
        }

        return bestScore;
    }

    private string[] GetAvailableShootModuleCatalog(Player bot)
    {
        return GetGroupedDummyActions(bot)
            .Where(entry => entry.Action.Name.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0)
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Category) ? "<uncategorized>" : entry.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:[{string.Join(",", group.Select(entry => entry.Action.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name))}]")
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryResolveDummyAction(Player bot, string actionName, out DummyAction action, out string resolvedActionName)
    {
        return TryResolveDummyAction(bot, actionName, out action, out resolvedActionName, out _);
    }

    private bool TryResolveDummyAction(Player bot, string actionName, out DummyAction action, out string resolvedActionName, out string resolvedCategory)
    {
        action = default;
        resolvedActionName = string.Empty;
        resolvedCategory = string.Empty;
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return false;
        }

        try
        {
            if (IsItemScopedActionName(actionName))
            {
                string[] itemModulePrefixes = GetCurrentItemModulePrefixes(bot);
                GroupedDummyActionEntry[] groupedActions = GetGroupedDummyActions(bot);
                if (groupedActions.Length > 0)
                {
                    foreach (string variant in GetActionNameVariants(actionName))
                    {
                        GroupedDummyActionEntry groupedMatch = groupedActions
                            .Where(candidate => string.Equals(candidate.Action.Name, variant, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(candidate => ScoreDummyActionCategory(candidate.Category, itemModulePrefixes))
                            .ThenBy(candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();

                        if (!string.IsNullOrWhiteSpace(groupedMatch.Action.Name) && groupedMatch.Action.Action != null)
                        {
                            action = groupedMatch.Action;
                            resolvedActionName = groupedMatch.Action.Name;
                            resolvedCategory = groupedMatch.Category;
                            return true;
                        }
                    }
                }
            }

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
        return TryInvokeDummyAction(bot, actionName, out resolvedActionName, out _);
    }

    private bool TryInvokeDummyAction(Player bot, string actionName, out string resolvedActionName, out string resolvedCategory)
    {
        resolvedActionName = string.Empty;
        resolvedCategory = string.Empty;

        try
        {
            if (!TryResolveDummyAction(bot, actionName, out DummyAction action, out resolvedActionName, out resolvedCategory))
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
        if (IsInEscapeSafezone(bot))
        {
            LogBotShot(state, "shot-skip escape-safezone");
            return;
        }

        int nowTick = Environment.TickCount;
        FirearmItem? firearm = bot.CurrentItem as FirearmItem;
        int loadedAmmo = firearm == null ? -1 : GetLoadedAmmo(firearm);
        int reserveAmmo = GetReserveAmmoSafe(bot, firearm);
        string itemName = bot.CurrentItem?.Type.ToString() ?? "none";
        string targetName = $"{target.Nickname}#{target.PlayerId}";
        bool campBoost = state.AiState == BotAiState.Camp;
        int minShotIntervalMs = campBoost
            ? Math.Max(1, Config.BotBehavior.MinShotIntervalMs / 2)
            : Config.BotBehavior.MinShotIntervalMs;
        int weaponShotIntervalMs = GetWeaponShotIntervalMs(firearm);
        if (weaponShotIntervalMs > 0)
        {
            minShotIntervalMs = Math.Max(minShotIntervalMs, weaponShotIntervalMs);
        }

        if (unchecked(nowTick - state.LastShotTick) < minShotIntervalMs)
        {
            LogBotShot(state, $"shot-skip cooldown interval={minShotIntervalMs} weaponInterval={weaponShotIntervalMs} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
            return;
        }

        state.LastShotTick = nowTick;

        if (TryFireNativeFirearm(bot, firearm, out string nativeActionModule))
        {
            state.PendingShotVerificationTick = nowTick;
            state.PendingShotLoadedAmmo = loadedAmmo;
            state.LastShotActionName = "native-fire";
            state.LastShotModuleName = nativeActionModule;
            LogBotShot(
                state,
                $"shot-ok action=native-fire module={nativeActionModule} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
            SchedulePostShotVerification(bot.PlayerId, brainToken, generation);
            return;
        }

        string[] shootCandidates = GetShootActionCandidates(bot, state);
        bool fired = false;
        string actionUsed = "";
        string actionModule = "";
        foreach (string candidate in shootCandidates)
        {
            if (!TryInvokeDummyAction(bot, candidate, out string resolvedCandidate, out string resolvedCategory))
            {
                continue;
            }

            fired = true;
            actionUsed = string.IsNullOrWhiteSpace(resolvedCandidate) ? candidate : resolvedCandidate;
            actionModule = string.IsNullOrWhiteSpace(resolvedCategory) ? "<flat>" : resolvedCategory;
            break;
        }

        if (!fired)
        {
            LogBotShot(
                state,
                $"shot-fail candidates=[{string.Join(",", shootCandidates)}] release={Config.BotBehavior.ShootReleaseActionName} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
            return;
        }

        state.PendingShotVerificationTick = nowTick;
        state.PendingShotLoadedAmmo = loadedAmmo;
        state.LastShotActionName = actionUsed;
        state.LastShotModuleName = actionModule;
        LogBotShot(
            state,
            $"shot-ok action={actionUsed} module={actionModule} release={Config.BotBehavior.ShootReleaseActionName} target={targetName} item={itemName} loaded={loadedAmmo} reserve={reserveAmmo}");
        SchedulePostShotVerification(bot.PlayerId, brainToken, generation);

        bool shouldReleaseShoot = actionUsed.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0
            || actionUsed.IndexOf("press", StringComparison.OrdinalIgnoreCase) >= 0;
        if (shouldReleaseShoot && !string.IsNullOrWhiteSpace(Config.BotBehavior.ShootReleaseActionName))
        {
            int releaseToken = brainToken;
            int shootReleaseDelayMs = campBoost
                ? Math.Max(1, Config.BotBehavior.ShootReleaseDelayMs / 2)
                : Config.BotBehavior.ShootReleaseDelayMs;
            Schedule(() =>
            {
                if (IsCurrentGeneration(generation)
                    && _managedBots.TryGetValue(bot.PlayerId, out ManagedBotState latest)
                    && latest.BrainToken == releaseToken
                    && Player.TryGet(bot.PlayerId, out Player liveBot))
                {
                    bool released = TryInvokeDummyAction(liveBot, Config.BotBehavior.ShootReleaseActionName, out string resolvedReleaseAction);
                    LogBotShot(latest, $"shot-release action={(string.IsNullOrWhiteSpace(resolvedReleaseAction) ? Config.BotBehavior.ShootReleaseActionName : resolvedReleaseAction)} released={released}");
                }
            }, shootReleaseDelayMs);
        }

    }

    private bool TryFireNativeFirearm(Player bot, FirearmItem? firearm, out string actionModule)
    {
        actionModule = "";
        if (firearm == null || GetLoadedAmmo(firearm) <= 0 || firearm.IsReloadingOrUnloading)
        {
            return false;
        }

        try
        {
            object? module = typeof(FirearmItem)
                .GetProperty("ActionModule", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(firearm);
            if (module == null)
            {
                return false;
            }

            MethodInfo? method = new[] { "ServerShoot", "ServerProcessShot", "ServerFire" }
                .Select(name => module.GetType().GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ReferenceHub) },
                    modifiers: null))
                .FirstOrDefault(candidate => candidate != null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(module, new object[] { bot.ReferenceHub });
            actionModule = module.GetType().Name + "." + method.Name;
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ApiLogger.Warn($"[{Name}] Native firearm shot failed for {bot.Nickname}: {ex.InnerException.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[{Name}] Native firearm shot failed for {bot.Nickname}: {ex.Message}");
            return false;
        }
    }

    private static int GetWeaponShotIntervalMs(FirearmItem? firearm)
    {
        if (firearm == null)
        {
            return 0;
        }

        object? module = typeof(FirearmItem)
            .GetProperty("ActionModule", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(firearm);
        if (TryGetFloatProperty(module, "TimeBetweenShots", out float timeBetweenShots)
            && timeBetweenShots > 0f)
        {
            return Mathf.CeilToInt(timeBetweenShots * 1000f);
        }

        float fireRate = firearm.Firerate;
        if (fireRate <= 0f)
        {
            return 0;
        }

        float secondsBetweenShots = fireRate > 20f
            ? 60f / fireRate
            : 1f / fireRate;
        return Mathf.CeilToInt(secondsBetweenShots * 1000f);
    }

    private static bool TryGetFloatProperty(object? instance, string propertyName, out float value)
    {
        value = 0f;
        if (instance == null)
        {
            return false;
        }

        try
        {
            object? raw = instance.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);
            if (raw is float floatValue)
            {
                value = floatValue;
                return true;
            }

            if (raw is double doubleValue)
            {
                value = (float)doubleValue;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void UpdateZoomHold(Player bot, ManagedBotState state, BotTargetSelection? target)
    {
        if (target == null)
        {
            ReleaseZoomHold(bot, state, "no-target");
            return;
        }

        if (!target.HasLineOfSight)
        {
            ReleaseZoomHold(bot, state, $"no-los target={target.Target.Nickname}#{target.Target.PlayerId}");
            return;
        }

        bool shouldZoom = ShouldZoomForTarget(bot, target.Target, out float distance, out float threshold, out string reason);
        if (state.ZoomHeld)
        {
            if (!shouldZoom)
            {
                ReleaseZoomHold(bot, state, $"target-{reason} target={target.Target.Nickname}#{target.Target.PlayerId} distance={distance:F1} threshold={threshold:F1}");
                return;
            }

            state.ZoomHeldTargetPlayerId = target.Target.PlayerId;
            LogBotZoomThrottled(state, $"held target={target.Target.Nickname}#{target.Target.PlayerId} los=True distance={distance:F1} threshold={threshold:F1}");
            return;
        }

        if (!shouldZoom)
        {
            LogBotZoomThrottled(
                state,
                $"skip reason={reason} target={target.Target.Nickname}#{target.Target.PlayerId} distance={distance:F1} threshold={threshold:F1} held=False");
            return;
        }

        bool invoked = TryInvokeFirstDummyAction(bot, GetZoomActionCandidates(bot), out string actionUsed);
        state.ZoomHeld = invoked;
        state.ZoomHeldTargetPlayerId = invoked ? target.Target.PlayerId : -1;
        LogBotZoom(
            state,
            $"hold reason={reason} target={target.Target.Nickname}#{target.Target.PlayerId} distance={distance:F1} threshold={threshold:F1} " +
            $"invoked={invoked} action={(string.IsNullOrWhiteSpace(actionUsed) ? "none" : actionUsed)} candidates=[{string.Join(",", GetZoomActionCandidates(bot))}]");
    }

    private void ReleaseZoomHold(Player bot, ManagedBotState state, string reason)
    {
        if (!state.ZoomHeld)
        {
            LogBotZoomThrottled(state, $"release-skip reason={reason} held=False");
            return;
        }

        bool released = TryInvokeFirstDummyAction(bot, GetZoomReleaseActionCandidates(), out string resolvedReleaseAction);
        LogBotZoom(
            state,
            $"release reason={reason} targetId={state.ZoomHeldTargetPlayerId} " +
            $"action={(string.IsNullOrWhiteSpace(resolvedReleaseAction) ? Config.BotBehavior.ZoomReleaseActionName : resolvedReleaseAction)} released={released}");
        state.ZoomHeld = false;
        state.ZoomHeldTargetPlayerId = -1;
    }

    private bool ShouldZoomForTarget(Player bot, Player target, out float distance, out float threshold, out string reason)
    {
        distance = Vector3.Distance(bot.Position, target.Position);
        threshold = Config.BotBehavior.FarTargetZoomDistance > 0f
            ? Config.BotBehavior.FarTargetZoomDistance
            : Config.BotBehavior.FarTargetAimDistance;
        threshold = Mathf.Max(1f, threshold);

        if (Config.BotBehavior.UseZoomWhileShooting)
        {
            reason = "always";
            return true;
        }

        if (!Config.BotBehavior.UseZoomForFarTargets)
        {
            reason = "disabled";
            return false;
        }

        reason = distance >= threshold ? "far-target" : "too-close";
        return distance >= threshold;
    }

    private string[] GetZoomActionCandidates(Player bot)
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

        AddCandidate("Zoom->Hold");
        AddCandidate("Zoom.Hold");
        if (Config.BotBehavior.ZoomActionName.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            AddCandidate(Config.BotBehavior.ZoomActionName);
        }

        return candidates.ToArray();
    }

    private string[] GetZoomReleaseActionCandidates()
    {
        return new[]
        {
            Config.BotBehavior.ZoomReleaseActionName,
            "Zoom->Release",
            "Zoom.Release",
        };
    }

    private bool TryInvokeFirstDummyAction(Player bot, IEnumerable<string> actionNames, out string resolvedActionName)
    {
        resolvedActionName = string.Empty;
        foreach (string actionName in actionNames ?? Array.Empty<string>())
        {
            if (TryInvokeDummyAction(bot, actionName, out resolvedActionName))
            {
                return true;
            }
        }

        return false;
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

        AddCandidate(Config.BotBehavior.AlternateShootPressActionName);

        return candidates.ToArray();
    }

    private void CleanupManagedBots()
    {
        foreach (KeyValuePair<int, ManagedBotState> entry in _managedBots.ToArray())
        {
            int playerId = entry.Key;
            entry.Value.DestroyNavigationAgent();
            DestroyNavAgentDebugToy(playerId);
            if (Player.TryGet(playerId, out Player bot) && bot.GameObject != null)
            {
                FacilityNavAgentFollower? follower = bot.GameObject.GetComponent<FacilityNavAgentFollower>();
                if (follower != null)
                {
                    UnityEngine.Object.Destroy(follower);
                }

                NetworkServer.Destroy(bot.GameObject);
            }
        }

        _managedBots.Clear();
        ClearNavAgentDebugVisuals();
        RefreshPlayerPanelSettings(sendToPlayers: true);
    }

    private void CleanupMissingBotEntries()
    {
        foreach (int playerId in _managedBots.Keys.ToArray())
        {
            if (!Player.TryGet(playerId, out Player player) || player.IsDestroyed)
            {
                RemoveManagedBot(playerId);
            }
        }
    }

    private bool RemoveManagedBot(int playerId)
    {
        if (!_managedBots.TryGetValue(playerId, out ManagedBotState state))
        {
            return false;
        }

        state.DestroyNavigationAgent();
        DestroyNavAgentDebugToy(playerId);
        if (Player.TryGet(playerId, out Player bot) && bot.GameObject != null)
        {
            FacilityNavAgentFollower? follower = bot.GameObject.GetComponent<FacilityNavAgentFollower>();
            if (follower != null)
            {
                UnityEngine.Object.Destroy(follower);
            }
        }

        bool removed = _managedBots.Remove(playerId);
        if (removed)
        {
            RefreshPlayerPanelSettings(sendToPlayers: true);
        }

        return removed;
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

    private bool IsInEscapeSafezone(Player player)
    {
        if (player == null || player.IsDestroyed || player.ReferenceHub == null)
        {
            return false;
        }

        float coordinate = GetSurfaceEscapeSafezoneCoordinate(player.Position);
        bool insideByCoordinate = Config.SurfaceEscapeSafezoneLessThan
            ? coordinate <= Config.SurfaceEscapeSafezoneMaxZ
            : coordinate >= Config.SurfaceEscapeSafezoneMaxZ;
        if (!insideByCoordinate)
        {
            return false;
        }

        return player.Position.x > Config.SurfaceEscapeSafezoneMinX
            && TryGetClosestRoomZone(player.Position, out FacilityZone zone)
            && zone == FacilityZone.Surface;
    }

    private float GetSurfaceEscapeSafezoneCoordinate(Vector3 position)
    {
        return Config.SurfaceEscapeSafezoneAxis.Trim().ToLowerInvariant() switch
        {
            "x" => position.x,
            "y" => position.y,
            _ => position.z,
        };
    }

    private void SchedulePlaytimeFlush()
    {
        int delayMs = Math.Max(10, Config.PlaytimeTracking.FlushIntervalSeconds) * 1000;
        Schedule(RunPlaytimeFlush, delayMs);
    }

    private void RunPlaytimeFlush()
    {
        if (!ReferenceEquals(Instance, this))
        {
            return;
        }

        _playtimeTrackerService.FlushIfDue(Config.PlaytimeTracking);
        SchedulePlaytimeFlush();
    }

    private void ScheduleSafezoneHealthDrain()
    {
        int token = _safezoneHealthDrainToken;
        Schedule(() => RunSafezoneHealthDrain(token), SafezoneHealthDrainIntervalMs);
    }

    private void RunSafezoneHealthDrain(int token)
    {
        if (!ReferenceEquals(Instance, this) || token != _safezoneHealthDrainToken)
        {
            return;
        }

        if (_warmupActive
            && Config.SurfaceEscapeSafezoneHealthDrainEnabled
            && Config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond > 0f)
        {
            float drainFraction = Config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond / 100f;
            foreach (Player player in Player.List.Where(player => IsManagedHuman(player) && IsInEscapeSafezone(player)))
            {
                if (!player.IsAlive)
                {
                    continue;
                }

                float maxHealth = Math.Max(1f, player.MaxHealth);
                float drain = maxHealth * drainFraction;
                _safezoneDrainDamagePlayerIds.Add(player.PlayerId);
                try
                {
                    player.Damage(
                        drain,
                        WarmupLocalization.T("Safezone health drain", "安全区生命流失"),
                        string.Empty);
                }
                finally
                {
                    _safezoneDrainDamagePlayerIds.Remove(player.PlayerId);
                }

                SendSafezoneHealthDrainWarning(player);
            }
        }

        ScheduleSafezoneHealthDrain();
    }

    private void SendSafezoneHealthDrainWarning(Player player)
    {
        if (!Config.SurfaceEscapeSafezoneHealthDrainWarningEnabled
            || string.IsNullOrWhiteSpace(Config.SurfaceEscapeSafezoneHealthDrainWarningText))
        {
            return;
        }

        string percent = Config.SurfaceEscapeSafezoneHealthDrainPercentPerSecond.ToString("0.##");
        string message = Config.SurfaceEscapeSafezoneHealthDrainWarningText.Replace("{percent}", percent);
        if (!TrySendStackedHint(player, "warmup-safezone", message, 1.1f))
        {
            player.SendHint(message, 1.1f);
        }
    }

    private static bool TrySendStackedHint(Player player, string key, string message, float durationSeconds)
    {
        try
        {
            Type? pluginType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("ScpslRatingTags.RatingTagsPlugin", throwOnError: false))
                .FirstOrDefault(type => type != null);
            MethodInfo? method = pluginType?.GetMethod(
                "TryShowStackedHint",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Player), typeof(string), typeof(string), typeof(float) },
                null);

            return method != null
                && method.Invoke(null, new object[] { player, key, message, durationSeconds }) is true;
        }
        catch (Exception ex)
        {
            ApiLogger.Warn($"[WarmupSandbox] RatingTags stacked hint bridge failed: {ex.Message}");
            return false;
        }
    }

    private void ScheduleDangerousItemProtectionMonitor()
    {
        int token = _dangerousItemProtectionMonitorToken;
        Schedule(() => RunDangerousItemProtectionMonitor(token), DangerousItemProtectionMonitorIntervalMs);
    }

    private void RunDangerousItemProtectionMonitor(int token)
    {
        if (!ReferenceEquals(Instance, this) || token != _dangerousItemProtectionMonitorToken)
        {
            return;
        }

        if (_warmupActive)
        {
            foreach (Player player in Player.List.Where(IsManagedHuman))
            {
                if (IsChargingOrFiringMicroHid(player))
                {
                    TrimSpawnProtectionForDangerousItemUse(player);
                }
            }
        }

        ScheduleDangerousItemProtectionMonitor();
    }

    private static bool IsChargingOrFiringMicroHid(Player player)
    {
        if (player.CurrentItem is not MicroHIDItem microHid)
        {
            return false;
        }

        return microHid.Phase is MicroHidPhase.WindingUp
            or MicroHidPhase.WoundUpSustain
            or MicroHidPhase.Firing;
    }

    private void ScheduleLiveUpdateSignalPoll()
    {
        Schedule(RunLiveUpdateSignalPoll, LiveUpdateSignalPollIntervalMs);
    }

    private void RunLiveUpdateSignalPoll()
    {
        if (!ReferenceEquals(Instance, this))
        {
            return;
        }

        TryProcessLiveUpdateSignal();
        TryProcessHotConfigReloadSignal();
        ScheduleLiveUpdateSignalPoll();
    }

    private void TryProcessLiveUpdateSignal()
    {
        foreach (string signalPath in GetConfigSignalPaths(LiveUpdateSignalFileName))
        {
            if (!File.Exists(signalPath))
            {
                continue;
            }

            try
            {
                string[] lines = File.ReadAllLines(signalPath, Encoding.UTF8);
                File.Delete(signalPath);

                int seconds = 30;
                if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out int parsedSeconds))
                {
                    seconds = Math.Max(1, parsedSeconds);
                }

                string message = lines.Length > 1
                    ? string.Join(" ", lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line))).Trim()
                    : "";
                BroadcastLiveUpdateWarning(seconds, message, out _);
            }
            catch (Exception exception)
            {
                ApiLogger.Warn($"[{Name}] Failed to process live update signal '{signalPath}': {exception.Message}");
            }
        }
    }

    private void TryProcessHotConfigReloadSignal()
    {
        bool shouldReload = false;
        foreach (string signalPath in GetConfigSignalPaths(HotConfigReloadSignalFileName))
        {
            if (!File.Exists(signalPath))
            {
                continue;
            }

            try
            {
                File.Delete(signalPath);
                shouldReload = true;
            }
            catch (Exception exception)
            {
                ApiLogger.Warn($"[{Name}] Failed to process hot config reload signal '{signalPath}': {exception.Message}");
            }
        }

        if (shouldReload)
        {
            ReloadCurrentConfig(out _);
        }
    }

    private static IEnumerable<string> GetConfigSignalPaths(string fileName)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        string labApiConfigRoot = Path.Combine(appData, "SCP Secret Laboratory", "LabAPI", "configs");
        if (!Directory.Exists(labApiConfigRoot))
        {
            yield break;
        }

        foreach (string portDirectory in Directory.GetDirectories(labApiConfigRoot))
        {
            yield return Path.Combine(portDirectory, "WarmupSandbox", fileName);
        }
    }

    public string BuildPlaytimeReport(int limit = 10)
    {
        return _playtimeTrackerService.BuildReport(limit);
    }

    private void ScheduleAutoCleanup(int generation)
    {
        if (!Config.AutoCleanupEnabled || Config.AutoCleanupIntervalSeconds <= 0)
        {
            return;
        }

        long configuredDelayMs = (long)Config.AutoCleanupIntervalSeconds * 1000L;
        int delayMs = (int)Math.Min(int.MaxValue, Math.Max(MinimumAutoCleanupIntervalMs, configuredDelayMs));
        Schedule(() => RunAutoCleanup(generation), delayMs);
    }

    private void ScheduleArmorPickupSanitizer(int generation)
    {
        Schedule(() => RunArmorPickupSanitizer(generation), ArmorPickupSanitizerIntervalMs);
    }

    private void ScheduleHelpReminderBroadcast(int generation)
    {
        bool canBroadcastHelp = Config.PlayerPanelEnabled && Config.BroadcastHelpReminder;
        bool canBroadcastCommunity = Config.BroadcastCommunityReminder && !string.IsNullOrWhiteSpace(Config.CommunityReminderText);
        if ((!canBroadcastHelp && !canBroadcastCommunity)
            || Config.HelpReminderIntervalSeconds <= 0
            || Config.HelpReminderDurationSeconds <= 0)
        {
            return;
        }

        long configuredDelayMs = (long)Config.HelpReminderIntervalSeconds * 1000L;
        int delayMs = (int)Math.Min(int.MaxValue, Math.Max(5000L, configuredDelayMs));
        Schedule(() => RunHelpReminderBroadcast(generation), delayMs);
    }

    private void RunHelpReminderBroadcast(int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        bool canBroadcastHelp = Config.PlayerPanelEnabled && Config.BroadcastHelpReminder;
        bool canBroadcastCommunity = Config.BroadcastCommunityReminder && !string.IsNullOrWhiteSpace(Config.CommunityReminderText);
        if (!canBroadcastHelp && !canBroadcastCommunity)
        {
            return;
        }

        bool sendCommunity = canBroadcastCommunity && (_nextHelpReminderIsCommunity || !canBroadcastHelp);
        string text = sendCommunity
            ? Config.CommunityReminderText.Trim()
            : WarmupLocalization.T(
                "<size=28><color=#00ffff><b>Warmup controls</b></color></size>\n<size=22>Open Server Specific Settings for the bot console</size>",
                "<size=28><color=#00ffff><b>热身控制</b></color></size>\n<size=22>打开服务器专属设置（Server Specific Settings）使用人机控制台</size>");

        foreach (Player player in Player.List.Where(IsManagedHuman))
        {
            SendNonUpdateBroadcast(player, text, Config.HelpReminderDurationSeconds);
        }

        if (canBroadcastHelp && canBroadcastCommunity)
        {
            _nextHelpReminderIsCommunity = !sendCommunity;
        }

        ScheduleHelpReminderBroadcast(generation);
    }

    private void RunArmorPickupSanitizer(int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        int removed = CleanupArmorPickups();
        if (removed > 0 && Config.EnableDebugLogging)
        {
            ApiLogger.Info($"[{Name}] Removed armor pickups={removed} to prevent BodyArmorPickup update spam.");
        }

        ScheduleArmorPickupSanitizer(generation);
    }

    private void RunAutoCleanup(int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        int cleanablePickupCount = CountCleanablePickups();
        int pickupThreshold = Math.Max(0, Config.AutoCleanupPickupThreshold);
        if (pickupThreshold > 0 && cleanablePickupCount < pickupThreshold)
        {
            if (Config.EnableDebugLogging)
            {
                ApiLogger.Info($"[{Name}] Auto cleanup skipped pickups={cleanablePickupCount}/{pickupThreshold}.");
            }

            ScheduleAutoCleanup(generation);
            return;
        }

        int pickups = CleanupPickups();
        int ragdolls = CleanupRagdolls();
        bool bulletHoles = ExecuteCleanupCommand(new BulletHolesCommand(), out string bulletHoleResponse);
        bool blood = ExecuteCleanupCommand(new BloodCommand(), out string bloodResponse);

        if (Config.EnableDebugLogging)
        {
            ApiLogger.Info($"[{Name}] Auto cleanup removed pickups={pickups}/{cleanablePickupCount}, ragdolls={ragdolls}, bulletHoles={bulletHoles} ({bulletHoleResponse}), blood={blood} ({bloodResponse}).");
        }

        ScheduleAutoCleanup(generation);
    }

    private int CountCleanablePickups()
    {
        int count = 0;
        foreach (Pickup pickup in Pickup.List.ToArray())
        {
            if (pickup == null || pickup.IsDestroyed)
            {
                continue;
            }

            if (_bombModeService.RoundActive && pickup.Type == ItemType.SCP1576)
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private int CleanupPickups()
    {
        int removed = 0;
        foreach (Pickup pickup in Pickup.List.ToArray())
        {
            if (pickup == null || pickup.IsDestroyed)
            {
                continue;
            }

            if (_bombModeService.RoundActive && pickup.Type == ItemType.SCP1576)
            {
                continue;
            }

            try
            {
                pickup.Destroy();
                removed++;
            }
            catch (Exception exception)
            {
                if (Config.EnableDebugLogging)
                {
                    ApiLogger.Warn($"[{Name}] Failed to auto-clean pickup {pickup}: {exception.Message}");
                }
            }
        }

        return removed;
    }

    private int CleanupArmorPickups()
    {
        int removed = 0;
        foreach (Pickup pickup in Pickup.List.ToArray())
        {
            if (TryDestroyArmorPickup(pickup, "sanitizer"))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryDestroyArmorPickup(Pickup? pickup, string reason)
    {
        if (pickup == null || pickup.IsDestroyed || !IsArmorItem(pickup.Type))
        {
            return false;
        }

        try
        {
            pickup.Destroy();
            return true;
        }
        catch (Exception exception)
        {
            if (Config.EnableDebugLogging)
            {
                ApiLogger.Warn($"[{Name}] Failed to remove armor pickup ({reason}) {pickup}: {exception.Message}");
            }

            return false;
        }
    }

    private int CleanupRagdolls()
    {
        int removed = 0;
        foreach (Ragdoll ragdoll in Ragdoll.List.ToArray())
        {
            if (ragdoll == null || ragdoll.IsDestroyed)
            {
                continue;
            }

            try
            {
                ragdoll.Destroy();
                removed++;
            }
            catch (Exception exception)
            {
                if (Config.EnableDebugLogging)
                {
                    ApiLogger.Warn($"[{Name}] Failed to auto-clean ragdoll {ragdoll}: {exception.Message}");
                }
            }
        }

        return removed;
    }

    private static bool ExecuteCleanupCommand(ICommand command, out string response)
    {
        string[] arguments = { int.MaxValue.ToString() };
        return command.Execute(new ArraySegment<string>(arguments), AutoCleanupCommandSender.Instance, out response);
    }

    private static bool IsPrivilegedCommandSender(CommandSender sender)
    {
        return sender.FullPermissions || sender.Permissions != 0UL;
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
                $"post-shot-check item={itemName} action={state.LastShotActionName} module={state.LastShotModuleName} loaded={currentLoadedAmmo} reserve={GetReserveAmmoSafe(bot, firearm)} ammoConsumed={ammoConsumed} shotEvent={shotEventObserved} dryFires={state.DryFireCount}");

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
            LogBotEvent(state, $"shoot-modules available=[{string.Join(" | ", GetAvailableShootModuleCatalog(bot))}]");
        }

        string previous = state.LastShotActionName;
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

        if (!string.IsNullOrWhiteSpace(Config.BotBehavior.AlternateShootPressActionName)
            && !string.Equals(previous, Config.BotBehavior.AlternateShootPressActionName, StringComparison.OrdinalIgnoreCase)
            && HasDummyAction(bot, Config.BotBehavior.AlternateShootPressActionName))
        {
            state.PreferredShootActionName = Config.BotBehavior.AlternateShootPressActionName;
            LogBotEvent(state, $"dry-fire-fallback previous={previous} next={state.PreferredShootActionName} count={state.DryFireCount}");
            return;
        }

        LogBotEvent(state, $"dry-fire-no-fallback action={previous} count={state.DryFireCount}");
    }

    private void LogBotDebug(ManagedBotState state, string message)
    {
        if (!Config.EnableVerboseBotLogging)
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
        if (Config.EnableVerboseBotLogging)
        {
            ApiLogger.Info($"[{Name}] [BotDebug:{state.Nickname}] {message}");
        }
    }

    private void LogBotShot(ManagedBotState state, string message)
    {
        if (Config.EnableVerboseBotLogging)
        {
            ApiLogger.Info($"[{Name}] [BotShot:{state.Nickname}] {message}");
        }
    }

    private void LogBotZoom(ManagedBotState state, string message)
    {
    }

    private void LogBotZoomThrottled(ManagedBotState state, string message)
    {
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
        if (!Config.EnableVerboseBotLogging)
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

        int nowTick = Environment.TickCount;
        if (string.Equals(state.LastNavigationDebugSummary, message, StringComparison.Ordinal)
            && unchecked(nowTick - state.LastNavigationDebugTick) < Math.Max(1000, Config.BotBehavior.DebugLogIntervalMs))
        {
            return;
        }

        state.LastNavigationDebugTick = nowTick;
        state.LastNavigationDebugSummary = message;
        ApiLogger.Info($"[{Name}] [BotNav:{state.Nickname}] {message}");
    }

    private bool UpdateFacilityDummyFollower(Player bot, ManagedBotState state, Player? target, bool shouldFollow)
    {
        GameObject? botGameObject = bot.GameObject;
        if (botGameObject == null)
        {
            return false;
        }

        PlayerFollower? follower = botGameObject.GetComponent<PlayerFollower>();
        if (!shouldFollow || target == null || target.IsDestroyed || target.ReferenceHub == null)
        {
            if (follower != null)
            {
                UnityEngine.Object.Destroy(follower);
                LogNavDebug(bot, state, $"facility-follower stop target={target?.Nickname ?? "none"}");
            }

            return false;
        }

        if (follower == null)
        {
            follower = botGameObject.AddComponent<PlayerFollower>();
            LogNavDebug(bot, state, $"facility-follower start target={target.Nickname}#{target.PlayerId}");
        }

        float followSpeed = GetFacilityDummyFollowSpeed(bot);
        follower.Init(
            target.ReferenceHub,
            Config.BotBehavior.FacilityDummyFollowMaxDistance,
            Config.BotBehavior.FacilityDummyFollowMinDistance,
            followSpeed);
        state.LastMoveIntentLabel = "facility-follower";
        state.LastMoveIntentTick = Environment.TickCount;
        return true;
    }

    private float GetFacilityDummyFollowSpeed(Player bot)
    {
        return bot.Role switch
        {
            RoleTypeId.Scp939 => Config.BotBehavior.FacilityDummyFollowSpeedScp939,
            RoleTypeId.Scp3114 => Config.BotBehavior.FacilityDummyFollowSpeedScp3114,
            RoleTypeId.Scp049 => Config.BotBehavior.FacilityDummyFollowSpeedScp049,
            RoleTypeId.Scp106 => Config.BotBehavior.FacilityDummyFollowSpeedScp106,
            _ => Config.BotBehavior.FacilityDummyFollowSpeed,
        };
    }

    private void ScheduleNavHeartbeat(int generation)
    {
        Schedule(() => RunNavHeartbeat(generation), NavHeartbeatIntervalMs);
    }

    private void RunNavHeartbeat(int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive)
        {
            return;
        }

        if (!Config.EnableVerboseBotLogging)
        {
            ScheduleNavHeartbeat(generation);
            return;
        }

        foreach (KeyValuePair<int, ManagedBotState> entry in _managedBots.ToArray())
        {
            int playerId = entry.Key;
            ManagedBotState state = entry.Value;
            if (!Player.TryGet(playerId, out Player bot) || bot.IsDestroyed || bot.Role == RoleTypeId.Spectator)
            {
                continue;
            }

            ApiLogger.Info(
                $"[{Name}] [BotState:{state.Nickname}] state={state.LastStateSummary} target={state.LastTargetSummary} " +
                $"role={bot.Role} team={bot.Team} navReason={state.LastNavigationReason} path={state.NavigationWaypointIndex}/{state.NavigationWaypoints.Count} " +
                $"campRemainingMs={GetRemainingTickMs(state.CampUntilTick)} campCooldownMs={GetRemainingTickMs(state.CampCooldownUntilTick)} " +
                $"pos={FormatVector(bot.Position)}");
        }

        ScheduleNavHeartbeat(generation);
    }

    private void ClearArenaDebugVisuals()
    {
        DestroyDebugToys(_runtimeNavMeshDebugEdges);
        _runtimeNavMeshDebugEdges.Clear();
        ClearNavAgentDebugVisuals();
        DestroyLegacyArenaDebugToys();
    }

    private void UpdateFacilityNavAgentFollower(Player bot, ManagedBotState state, bool useNavMesh, bool useDust2Arena)
    {
        if (bot.GameObject == null)
        {
            return;
        }

        FacilityNavAgentFollower? follower = bot.GameObject.GetComponent<FacilityNavAgentFollower>();
        if (useDust2Arena
            || !useNavMesh
            || !Config.BotBehavior.FacilityNavMeshDirectPositionControl
            || state.NavigationAgent == null)
        {
            if (follower != null)
            {
                UnityEngine.Object.Destroy(follower);
            }

            return;
        }

        if (follower == null)
        {
            follower = bot.GameObject.AddComponent<FacilityNavAgentFollower>();
        }

        follower.Init(bot, state, () => Config.BotBehavior, LogNavDebug);
    }

    private void UpdateNavAgentDebugVisual(Player bot, ManagedBotState state, bool useNavMesh, bool useDust2Arena)
    {
        DestroyNavAgentDebugToy(bot.PlayerId);
    }

    private void DestroyNavAgentDebugToy(int playerId)
    {
        if (!_navAgentDebugToys.TryGetValue(playerId, out PrimitiveObjectToyWrapper toy))
        {
            return;
        }

        if (toy != null && !toy.IsDestroyed)
        {
            toy.Destroy();
        }

        _navAgentDebugToys.Remove(playerId);
    }

    private void ClearNavAgentDebugVisuals()
    {
        DestroyDebugToys(_navAgentDebugToys.Values);
        _navAgentDebugToys.Clear();
    }

    private void RebuildRuntimeNavMeshDebugVisuals()
    {
        ClearArenaDebugVisuals();
    }

    private void RebuildFacilityNavMeshDebugVisuals()
    {
        ClearArenaDebugVisuals();
    }

    private static void DestroyDebugToys(IEnumerable<PrimitiveObjectToyWrapper> toys)
    {
        foreach (PrimitiveObjectToyWrapper toy in toys.Where(toy => toy != null))
        {
            if (!toy.IsDestroyed)
            {
                toy.Destroy();
            }
        }
    }

    private void EnsureEscapeSafezoneVisuals()
    {
        bool hasWalls = _escapeSafezoneVisuals.Any(toy => toy != null && !toy.IsDestroyed);
        bool hasLabels = _escapeSafezoneLabels.Any(toy => toy != null && !toy.IsDestroyed);
        if (hasWalls && hasLabels)
        {
            return;
        }

        DestroyEscapeSafezoneVisuals();

        switch (Config.SurfaceEscapeSafezoneAxis.Trim().ToLowerInvariant())
        {
            case "x":
                CreateEscapeSafezoneWall(
                    new Vector3(Config.SurfaceEscapeSafezoneMaxZ, 295f, 0f),
                    new Vector3(0.08f, 36f, 260f));
                CreateEscapeSafezoneWall(
                    new Vector3(Config.SurfaceEscapeSafezoneMaxZ + 0.1f, 295f, 0f),
                    new Vector3(0.08f, 36f, 260f));
                CreateEscapeSafezoneLabel(
                    new Vector3(Config.SurfaceEscapeSafezoneMaxZ + 0.18f, 300f, 0f),
                    Quaternion.Euler(0f, 90f, 0f));
                CreateEscapeSafezoneLabel(
                    new Vector3(Config.SurfaceEscapeSafezoneMaxZ - 0.18f, 300f, 0f),
                    Quaternion.Euler(0f, -90f, 0f));
                break;

            case "y":
                CreateEscapeSafezoneWall(
                    new Vector3(125f, Config.SurfaceEscapeSafezoneMaxZ, 0f),
                    new Vector3(260f, 0.08f, 260f));
                CreateEscapeSafezoneWall(
                    new Vector3(125f, Config.SurfaceEscapeSafezoneMaxZ + 0.1f, 0f),
                    new Vector3(260f, 0.08f, 260f));
                CreateEscapeSafezoneLabel(
                    new Vector3(125f, Config.SurfaceEscapeSafezoneMaxZ + 0.18f, 0f),
                    Quaternion.Euler(90f, 0f, 0f));
                CreateEscapeSafezoneLabel(
                    new Vector3(125f, Config.SurfaceEscapeSafezoneMaxZ - 0.18f, 0f),
                    Quaternion.Euler(-90f, 0f, 0f));
                break;

            default:
                float minX = Config.SurfaceEscapeSafezoneMinX;
                float maxX = 260f;
                float width = Mathf.Max(1f, maxX - minX);
                float centerX = minX + (width * 0.5f);
                CreateEscapeSafezoneWall(
                    new Vector3(centerX, 295f, Config.SurfaceEscapeSafezoneMaxZ - 0.05f),
                    new Vector3(width, 36f, 0.08f));
                CreateEscapeSafezoneWall(
                    new Vector3(centerX, 295f, Config.SurfaceEscapeSafezoneMaxZ + 0.05f),
                    new Vector3(width, 36f, 0.08f));
                CreateEscapeSafezoneLabelColumn(
                    new Vector3(136.45f, 295.8f, -16.86f),
                    Quaternion.identity);
                break;
        }

        ApiLogger.Info($"[{Name}] Escape safezone visuals created walls={_escapeSafezoneVisuals.Count} labels={_escapeSafezoneLabels.Count} axis={Config.SurfaceEscapeSafezoneAxis} threshold={Config.SurfaceEscapeSafezoneMaxZ} minX={Config.SurfaceEscapeSafezoneMinX}");
    }

    private void CreateEscapeSafezoneWall(Vector3 position, Vector3 scale)
    {
        PrimitiveObjectToyWrapper wall = PrimitiveObjectToyWrapper.Create(
            position,
            Quaternion.identity,
            scale,
            parent: null,
            networkSpawn: false);
        wall.Type = PrimitiveType.Cube;
        wall.Flags = PrimitiveFlags.Visible;
        wall.Color = new Color(0.25f, 0.85f, 1f, 0.35f);
        wall.IsStatic = true;
        wall.SyncInterval = 0f;
        wall.Spawn();
        _escapeSafezoneVisuals.Add(wall);
    }

    private void CreateEscapeSafezoneLabel(Vector3 position, Quaternion rotation)
    {
        TextToyWrapper label = TextToyWrapper.Create(
            position,
            rotation,
            new Vector3(0.32f, 0.32f, 0.32f),
            parent: null,
            networkSpawn: false);
        label.TextFormat = "<alpha=#FF><b><color=#00FFFFFF><nobr>安全区</nobr></color></b>";
        label.DisplaySize = new Vector2(80f, 4f);
        label.IsStatic = true;
        label.SyncInterval = 0f;
        label.Spawn();
        _escapeSafezoneLabels.Add(label);
    }

    private void CreateEscapeSafezoneLabelColumn(Vector3 centerPosition, Quaternion rotation)
    {
        const float VerticalSpacing = 0.6f;
        CreateEscapeSafezoneLabel(centerPosition + Vector3.up * VerticalSpacing, rotation);
        CreateEscapeSafezoneLabel(centerPosition, rotation);
        CreateEscapeSafezoneLabel(centerPosition - Vector3.up * VerticalSpacing, rotation);
    }

    private void DestroyEscapeSafezoneVisuals()
    {
        DestroyDebugToys(_escapeSafezoneVisuals);
        _escapeSafezoneVisuals.Clear();
        DestroyTextToys(_escapeSafezoneLabels);
        _escapeSafezoneLabels.Clear();
    }

    private static void DestroyTextToys(IEnumerable<TextToyWrapper> toys)
    {
        foreach (TextToyWrapper toy in toys.Where(toy => toy != null))
        {
            try
            {
                if (!toy.IsDestroyed)
                {
                    toy.Destroy();
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup during plugin reload/restart.
            }
        }
    }

    private void DestroyLegacyArenaDebugToys()
    {
        Vector3 arenaOrigin = Config.Dust2Map.Origin.ToVector3();
        int destroyed = 0;
        foreach (PrimitiveObjectToyWrapper toy in PrimitiveObjectToyWrapper.List.ToArray())
        {
            if (toy == null
                || toy.IsDestroyed
                || !toy.IsStatic
                || Mathf.Abs(toy.Position.y - arenaOrigin.y) > 40f
                || HorizontalDistance(toy.Position, arenaOrigin) > 220f)
            {
                continue;
            }

            bool oldSphere = toy.Type == PrimitiveType.Sphere
                && toy.Scale.x <= 0.5f
                && toy.Scale.y <= 0.5f
                && toy.Scale.z <= 0.5f;
            bool oldNavMeshEdge = toy.Type == PrimitiveType.Cube
                && toy.Scale.x <= 0.12f
                && toy.Scale.y <= 0.12f
                && toy.Color.r <= 0.15f
                && toy.Color.g >= 0.65f
                && toy.Color.b >= 0.75f;
            if (!oldSphere && !oldNavMeshEdge)
            {
                continue;
            }

            toy.Destroy();
            destroyed++;
        }

        if (destroyed > 0 && Config.EnableArenaLogging)
        {
            ApiLogger.Info($"[{Name}] Removed {destroyed} legacy Dust2 debug toys.");
        }
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string BuildBlockedMoveSummary(ManagedBotState state, int nowTick)
    {
        List<string> parts = new();
        AddBlockedMoveSummary(parts, "fwd", state.ForwardBlockedUntilTick, nowTick);
        AddBlockedMoveSummary(parts, "back", state.BackBlockedUntilTick, nowTick);
        AddBlockedMoveSummary(parts, "left", state.LeftBlockedUntilTick, nowTick);
        AddBlockedMoveSummary(parts, "right", state.RightBlockedUntilTick, nowTick);
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static void AddBlockedMoveSummary(List<string> parts, string label, int untilTick, int nowTick)
    {
        int remainingMs = untilTick == 0 ? 0 : Math.Max(0, unchecked(untilTick - nowTick));
        if (remainingMs > 0)
        {
            parts.Add($"{label}:{remainingMs}");
        }
    }

    private static int GetRemainingTickMs(int untilTick)
    {
        return untilTick == 0 ? 0 : Math.Max(0, unchecked(untilTick - Environment.TickCount));
    }

    private static string FormatVector(Vector3 vector)
    {
        return $"({vector.x:F1},{vector.y:F1},{vector.z:F1})";
    }

    private void LogAimDebug(Player bot, ManagedBotState state, Player target, float yaw, float pitch, Vector3 direction)
    {
        if (!Config.EnableVerboseBotLogging)
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
        if (!Config.EnableVerboseBotLogging)
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
        int reserveAmmo = GetReserveAmmoSafe(bot, firearm);
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
        return $"active={_warmupActive}, roundStarted={Round.IsRoundStarted}, bots={_managedBots.Count}/{Config.BotCount}, maxBots={Config.MaxBotCount}, maxPlayerBots={GetPlayerBotCountLimit()}, humanRole={Config.HumanRole}, botRole={Config.BotRole}, humanRespawnMs={Config.HumanRespawnDelayMs}, botRespawnMs={Config.BotRespawnDelayMs}, difficulty={Config.DifficultyPreset}, aimode={Config.BotBehavior.AiMode}, scpSpeeds=(939:{Config.BotBehavior.FacilityDummyFollowSpeedScp939:F1},3114:{Config.BotBehavior.FacilityDummyFollowSpeedScp3114:F1},049:{Config.BotBehavior.FacilityDummyFollowSpeedScp049:F1},106:{Config.BotBehavior.FacilityDummyFollowSpeedScp106:F1}), bombMode=({_bombModeService.BuildStatus()}), dust2=({_dust2MapService.BuildStatus(Config.Dust2Map)}), facilityNav=({_facilityNavMeshService.BuildStatus(Config.BotBehavior)})";
    }

    public bool StartRoundIfNeeded(out string response)
    {
        EnsureBotRuntimeActive("remote admin start");
        response = "Bot runtime start requested. Round control is disabled on this branch.";
        return true;
    }

    public bool RestartWarmupFromCommand(bool ensureRoundStarted, out string response)
    {
        RestartWarmup("remote admin");
        response = "Bot runtime restart requested. Round control is disabled on this branch.";
        return true;
    }

    public bool RestartRound(out string response)
    {
        response = "Round restart is disabled on this bot-only branch.";
        return false;
    }

    public bool StopWarmup(out string response)
    {
        _warmupGeneration++;
        _warmupActive = false;
        CleanupManagedBots();
        _bombModeService.ResetRuntime();
        CleanupArenaMap(returnHumansToFacility: false);
        response = "Warmup stopped and all managed bots were removed.";
        return true;
    }

    public bool SaveCurrentConfig(out string response)
    {
        SaveConfig();
        response = "Warmup config saved.";
        return true;
    }

    public bool ReloadCurrentConfig(out string response)
    {
        int previousBotCount = Config.BotCount;
        int previousMaxBotCount = Config.MaxBotCount;
        string previousLanguage = Config.Language;
        WarmupDifficulty previousDifficulty = Config.DifficultyPreset;
        WarmupAiMode previousAiMode = Config.BotBehavior.AiMode;

        try
        {
            LoadConfigs();
        }
        catch (Exception exception)
        {
            response = $"Failed to reload Warmup config: {exception.Message}";
            ApiLogger.Warn($"[{Name}] {response}");
            return false;
        }

        WarmupLocalization.SetLanguage(Config.Language);
        ApplyDifficultyPreset(Config.DifficultyPreset, persist: false);
        ApplyNativeSpawnProtectionConfig();
        ClampConfiguredLimits();
        ClampConfiguredBotCount();
        ClampSelectedPlayerPanelBotCounts();

        if (_warmupActive)
        {
            EnsureBotPopulation(_warmupGeneration);
            TrimExcessBots();
        }

        RefreshPlayerPanelSettings(sendToPlayers: true);

        response = $"Warmup config reloaded. bots {previousBotCount}->{Config.BotCount}, maxBots {previousMaxBotCount}->{Config.MaxBotCount}, language {previousLanguage}->{Config.Language}, difficulty {previousDifficulty}->{Config.DifficultyPreset}, aimode {previousAiMode}->{Config.BotBehavior.AiMode}.";
        ApiLogger.Info($"[{Name}] {response}");
        return true;
    }

    public bool BroadcastLiveUpdateWarning(int seconds, string message, out string response)
    {
        int duration = Math.Max(1, seconds);
        string finalMessage = string.IsNullOrWhiteSpace(message)
            ? $"服务器将在 {duration} 秒后重启更新。更新完成后可重新连接，感谢理解。"
            : message.Trim();
        string broadcastText = $"<size=28><color=#ffff00>{finalMessage}</color></size>";
        ushort broadcastDuration = (ushort)Math.Min(ushort.MaxValue, Math.Max(5, duration));
        int warningMs = (int)Math.Min(int.MaxValue / 2L, Math.Max(1000L, (long)duration * 1000L));
        _liveUpdateWarningUntilTick = unchecked(Environment.TickCount + warningMs);
        int sent = 0;

        foreach (Player player in Player.List)
        {
            if (player == null || player.IsDestroyed || player.IsHost)
            {
                continue;
            }

            player.ClearBroadcasts();
            player.SendBroadcast(broadcastText, broadcastDuration, global::Broadcast.BroadcastFlags.Normal, true);
            sent++;
        }

        response = $"Live update restart warning sent to {sent} player(s).";
        ApiLogger.Info($"[{Name}] {response} seconds={duration} message={finalMessage}");
        return true;
    }

    private bool IsLiveUpdateWarningActive()
    {
        return unchecked(_liveUpdateWarningUntilTick - Environment.TickCount) > 0;
    }

    private void SendNonUpdateBroadcast(Player player, string text, ushort duration)
    {
        if (IsLiveUpdateWarningActive())
        {
            return;
        }

        player.SendBroadcast(text, duration, global::Broadcast.BroadcastFlags.Normal, true);
    }

    public bool TryPlayerSetBotCount(Player player, string value, out string response)
    {
        if (!int.TryParse(value, out int botCount) || botCount < 0)
        {
            response = WarmupLocalization.T(
                "Bot count must be a non-negative integer.",
                "机器人数量必须是非负整数。");
            return false;
        }

        int playerBotCountLimit = GetPlayerBotCountLimit();
        if (botCount > playerBotCountLimit)
        {
            response = WarmupLocalization.T(
                $"Bot count cannot exceed {playerBotCountLimit}.",
                $"机器人数量不能超过 {playerBotCountLimit}。");
            return false;
        }

        long now = NowMs();
        if (TryGetCooldownRemainingSeconds(_playerBotCountGlobalCooldownUntilMs, now, out int globalRemaining))
        {
            response = WarmupLocalization.T(
                $"Bot count is on global cooldown for {globalRemaining}s.",
                $"机器人数量全局冷却中，还剩 {globalRemaining} 秒。");
            return false;
        }

        if (_playerBotCountCooldownUntilMs.TryGetValue(player.PlayerId, out long playerCooldownUntil)
            && TryGetCooldownRemainingSeconds(playerCooldownUntil, now, out int playerRemaining))
        {
            response = WarmupLocalization.T(
                $"You can change bot count again in {playerRemaining}s.",
                $"你还需要 {playerRemaining} 秒后才能再次修改机器人数量。");
            return false;
        }

        Config.BotCount = botCount;
        EnsureBotPopulation(EnsureBotRuntimeActive("player bot count"));
        TrimExcessBots();
        SaveConfig();

        _playerBotCountGlobalCooldownUntilMs = now + Math.Max(0, Config.PlayerBotCountGlobalCooldownSeconds) * 1000L;
        _playerBotCountCooldownUntilMs[player.PlayerId] = now + BuildCooldownMs(
            Config.PlayerBotCountCooldownSeconds,
            Config.PlayerBotCountCooldownJitterSeconds);

        response = WarmupLocalization.T(
            $"Bot count set to {Config.BotCount}.",
            $"机器人数量已设置为 {Config.BotCount}。");
        return true;
    }

    private void RefreshPlayerPanelSettings(bool sendToPlayers)
    {
        if (!Config.PlayerPanelEnabled)
        {
            ClearPlayerPanelSettings(sendToPlayers);
            return;
        }

        List<Player> players = GetPlayerPanelTargets();

        string[] targetOptions = new string[players.Count + 1];
        int[] targetIds = new int[players.Count + 1];
        targetOptions[0] = WarmupLocalization.T("Self", "自己");
        targetIds[0] = PlayerPanelSelfTargetId;

        for (int i = 0; i < players.Count; i++)
        {
            Player candidate = players[i];
            targetOptions[i + 1] = $"#{candidate.PlayerId} {candidate.Nickname}";
            targetIds[i + 1] = candidate.PlayerId;
        }

        _playerPanelTargetIds = targetIds;
        List<Player> botTargets = GetPlayerPanelBotTargets();
        string[] botTargetOptions = new string[botTargets.Count + 1];
        int[] botTargetIds = new int[botTargets.Count + 1];
        botTargetOptions[0] = WarmupLocalization.T("All Bots", "全部机器人");
        botTargetIds[0] = PlayerPanelAllBotsTargetId;

        for (int i = 0; i < botTargets.Count; i++)
        {
            Player bot = botTargets[i];
            botTargetOptions[i + 1] = $"#{bot.PlayerId} {bot.Nickname}";
            botTargetIds[i + 1] = bot.PlayerId;
        }

        _playerPanelBotTargetIds = botTargetIds;
        int panelMaxBotCount = GetPlayerBotCountLimit();
        int defaultBotCount = ClampPanelBotCount(Config.BotCount);
        int defaultDifficulty = Math.Max(0, Array.IndexOf(PlayerPanelDifficulties, Config.DifficultyPreset));
        int defaultAiMode = Math.Max(0, Array.IndexOf(PlayerPanelAiModes, Config.BotBehavior.AiMode));
        int defaultRetreatSpeed = Mathf.RoundToInt(ClampCloseRetreatSpeedScale(Config.BotBehavior.CloseRetreatSpeedScale) * 100f);
        ServerSpecificSettingBase[] pluginSettings =
        {
            new SSGroupHeader(WarmupLocalization.T("Warmup Player Panel", "人机战斗控制台"), false, WarmupLocalization.T("Use the sections below.", "使用下方选项。")),
            new SSTextArea(
                null,
                WarmupLocalization.T("How to use", "使用说明"),
                SSTextArea.FoldoutMode.ExtendedByDefault,
                WarmupLocalization.T(
                    "Pick a value, then press Apply. Personal: 3 free actions, then 5s. Global: shared cooldown.",
                    "先选数值，再点应用。个人前 3 次无冷却，之后 5 秒；全局共享冷却。"),
                TMPro.TextAlignmentOptions.Left),
            new SSGroupHeader(WarmupLocalization.T("Bot Controls", "机器人功能"), false, WarmupLocalization.T("Only bots are changed.", "只修改机器人。")),
            new SSButton(PlayerPanelBringBotsButtonId, WarmupLocalization.T("Bring Bots", "召回机器人"), WarmupLocalization.T("BRING", "召回"), null, WarmupLocalization.T("Bring bots to you.", "将机器人召回到你身边。")),
            new SSSliderSetting(PlayerPanelBotCountSettingId, WarmupLocalization.T("Bot Count", "机器人数量"), 0, panelMaxBotCount, defaultBotCount, true, "0", "{0}", WarmupLocalization.T($"0-{panelMaxBotCount} bots.", $"0-{panelMaxBotCount} 个。"), 0, false),
            new SSButton(PlayerPanelSetBotsButtonId, WarmupLocalization.T("Apply Bot Count", "应用数量"), WarmupLocalization.T("APPLY", "应用"), null, WarmupLocalization.T("Apply bot count.", "应用数量。")),
            new SSDropdownSetting(PlayerPanelDifficultySettingId, WarmupLocalization.T("Difficulty", "难度"), PlayerPanelDifficulties.Select(difficulty => difficulty.ToString()).ToArray(), defaultDifficulty, SSDropdownSetting.DropdownEntryType.HybridLoop, WarmupLocalization.T("Bot difficulty.", "机器人难度。"), 0, false),
            new SSButton(PlayerPanelApplyDifficultyButtonId, WarmupLocalization.T("Apply Difficulty", "应用难度"), WarmupLocalization.T("APPLY", "应用"), null, WarmupLocalization.T("Apply difficulty.", "应用难度。")),
            new SSDropdownSetting(PlayerPanelAiModeSettingId, WarmupLocalization.T("AI Mode", "AI 模式"), PlayerPanelAiModes.Select(mode => mode.ToString()).ToArray(), defaultAiMode, SSDropdownSetting.DropdownEntryType.HybridLoop, WarmupLocalization.T("Bot AI mode.", "机器人 AI 模式。"), 0, false),
            new SSButton(PlayerPanelApplyAiModeButtonId, WarmupLocalization.T("Apply AI Mode", "应用 AI"), WarmupLocalization.T("APPLY", "应用"), null, WarmupLocalization.T("Apply AI mode.", "应用 AI 模式。")),
            new SSSliderSetting(PlayerPanelRetreatSpeedSettingId, WarmupLocalization.T("Bot Retreat Speed", "机器人后退速度"), 60, 100, defaultRetreatSpeed, true, "60%", "{0}%", WarmupLocalization.T("60%-100% retreat speed.", "后退速度 60%-100%。"), 0, false),
            new SSButton(PlayerPanelApplyRetreatSpeedButtonId, WarmupLocalization.T("Apply Retreat Speed", "应用后退速度"), WarmupLocalization.T("APPLY", "应用"), null, WarmupLocalization.T("Uses global cooldown.", "使用全局冷却。")),
            new SSDropdownSetting(PlayerPanelBotTargetSettingId, WarmupLocalization.T("Bot Target", "机器人目标"), botTargetOptions, 0, SSDropdownSetting.DropdownEntryType.HybridLoop, WarmupLocalization.T("Bot(s) to change.", "要修改的机器人。"), 0, false),
            new SSDropdownSetting(PlayerPanelBotRoleSettingId, WarmupLocalization.T("Bot Role", "机器人阵营"), PlayerPanelBotRoles.Select(role => role.ToString()).ToArray(), 0, SSDropdownSetting.DropdownEntryType.HybridLoop, WarmupLocalization.T("Sets the bot's persistent respawn role.", "设置机器人的永久重生阵营。"), 0, false),
            new SSButton(PlayerPanelApplyBotRoleButtonId, WarmupLocalization.T("Apply Bot Role", "应用机器人阵营"), WarmupLocalization.T("APPLY", "应用"), null, WarmupLocalization.T("Uses global cooldown.", "使用全局冷却。")),
        };

        ServerSpecificSettingsSync.DefinedSettings = MergeServerSpecificSettings(pluginSettings);
        if (sendToPlayers)
        {
            ServerSpecificSettingsSync.SendToAll();
        }
    }

    private List<Player> GetPlayerPanelTargets()
    {
        Dictionary<int, Player> targets = new();

        foreach (Player candidate in Player.List)
        {
            AddPlayerPanelTarget(targets, candidate);
        }

        foreach (int playerId in _managedBots.Keys.ToArray())
        {
            if (Player.TryGet(playerId, out Player bot))
            {
                AddPlayerPanelTarget(targets, bot);
            }
        }

        return targets.Values
            .OrderBy(candidate => IsManagedBot(candidate) ? 1 : 0)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();
    }

    private List<Player> GetPlayerPanelBotTargets()
    {
        return _managedBots.Keys
            .Select(playerId => Player.TryGet(playerId, out Player bot) ? bot : null)
            .Where(bot => bot != null && !bot.IsDestroyed)
            .Cast<Player>()
            .OrderBy(bot => bot.PlayerId)
            .ToList();
    }

    private static void AddPlayerPanelTarget(Dictionary<int, Player> targets, Player? candidate)
    {
        if (candidate == null
            || candidate.IsDestroyed
            || candidate.IsHost
            || targets.ContainsKey(candidate.PlayerId))
        {
            return;
        }

        targets[candidate.PlayerId] = candidate;
    }

    private ServerSpecificSettingBase[] MergeServerSpecificSettings(ServerSpecificSettingBase[] pluginSettings)
    {
        if (_originalServerSpecificSettings == null || _originalServerSpecificSettings.Length == 0)
        {
            return pluginSettings;
        }

        return _originalServerSpecificSettings
            .Where(setting => setting != null && (setting.SettingId < PlayerPanelFirstSettingId || setting.SettingId > PlayerPanelLastSettingId))
            .Concat(pluginSettings)
            .ToArray();
    }

    private static bool IsPlayerPanelSetting(ServerSpecificSettingBase setting)
    {
        return setting.SettingId >= PlayerPanelFirstSettingId && setting.SettingId <= PlayerPanelLastSettingId;
    }

    private void ClearPlayerPanelSettings(bool sendToPlayers)
    {
        ServerSpecificSettingBase[] currentSettings = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        ServerSpecificSettingsSync.DefinedSettings = currentSettings
            .Where(setting => setting != null && !IsPlayerPanelSetting(setting))
            .ToArray();

        if (sendToPlayers)
        {
            ServerSpecificSettingsSync.SendToAll();
        }
    }

    private void OnServerSpecificSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        Player actor = hub == null ? null! : Player.Get(hub);
        if (hub == null || setting == null || actor == null || actor.IsDestroyed)
        {
            return;
        }

        if (!Config.PlayerPanelEnabled || !IsPlayerPanelSetting(setting))
        {
            return;
        }

        switch (setting.SettingId)
        {
            case PlayerPanelTeleportTargetSettingId when setting is SSDropdownSetting targetDropdown:
                int targetIndex = Math.Max(0, Math.Min(_playerPanelTargetIds.Length - 1, targetDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedTargetIds[actor.PlayerId] = _playerPanelTargetIds[targetIndex];
                return;

            case PlayerPanelRoleSettingId when setting is SSDropdownSetting roleDropdown:
                int roleIndex = Math.Max(0, Math.Min(PlayerPanelRoles.Length - 1, roleDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedRoles[actor.PlayerId] = PlayerPanelRoles[roleIndex];
                return;

            case PlayerPanelLoadoutSettingId when setting is SSDropdownSetting loadoutDropdown:
                string[] loadoutOptions = GetHumanLoadoutPresets().Select(preset => preset.Name).DefaultIfEmpty("Default").ToArray();
                int loadoutIndex = Math.Max(0, Math.Min(loadoutOptions.Length - 1, loadoutDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedLoadouts[actor.PlayerId] = loadoutOptions[loadoutIndex];
                return;

            case PlayerPanelItemSettingId when setting is SSDropdownSetting itemDropdown:
                int itemIndex = Math.Max(0, Math.Min(PlayerPanelItems.Length - 1, itemDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedItems[actor.PlayerId] = PlayerPanelItems[itemIndex];
                return;

            case PlayerPanelBotCountSettingId when setting is SSSliderSetting slider:
                _playerPanelSelectedBotCounts[actor.PlayerId] = ClampPanelBotCount(slider.SyncIntValue);
                return;

            case PlayerPanelDifficultySettingId when setting is SSDropdownSetting difficultyDropdown:
                int difficultyIndex = Math.Max(0, Math.Min(PlayerPanelDifficulties.Length - 1, difficultyDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedDifficulties[actor.PlayerId] = PlayerPanelDifficulties[difficultyIndex];
                return;

            case PlayerPanelAiModeSettingId when setting is SSDropdownSetting aiModeDropdown:
                int aiModeIndex = Math.Max(0, Math.Min(PlayerPanelAiModes.Length - 1, aiModeDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedAiModes[actor.PlayerId] = PlayerPanelAiModes[aiModeIndex];
                return;

            case PlayerPanelRetreatSpeedSettingId when setting is SSSliderSetting retreatSpeedSlider:
                _playerPanelSelectedRetreatSpeedScales[actor.PlayerId] = ClampCloseRetreatSpeedScale(retreatSpeedSlider.SyncIntValue / 100f);
                return;

            case PlayerPanelBotTargetSettingId when setting is SSDropdownSetting botTargetDropdown:
                int botTargetIndex = Math.Max(0, Math.Min(_playerPanelBotTargetIds.Length - 1, botTargetDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedBotTargetIds[actor.PlayerId] = _playerPanelBotTargetIds[botTargetIndex];
                return;

            case PlayerPanelBotRoleSettingId when setting is SSDropdownSetting botRoleDropdown:
                int botRoleIndex = Math.Max(0, Math.Min(PlayerPanelBotRoles.Length - 1, botRoleDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedBotRoles[actor.PlayerId] = PlayerPanelBotRoles[botRoleIndex];
                return;

            case PlayerPanelRoomPresetSettingId when setting is SSDropdownSetting roomPresetDropdown:
                int roomPresetIndex = Math.Max(0, Math.Min(PlayerPanelRoomPresets.Length - 1, roomPresetDropdown.SyncSelectionIndexValidated));
                _playerPanelSelectedRoomPresets[actor.PlayerId] = PlayerPanelRoomPresets[roomPresetIndex].RoomName;
                return;

            case PlayerPanelSetRoleButtonId:
                ExecutePlayerPanelPersonalAction(actor, "role");
                return;

            case PlayerPanelApplyLoadoutButtonId:
                ExecutePlayerPanelPersonalAction(actor, "loadout");
                return;

            case PlayerPanelGiveItemButtonId:
                ExecutePlayerPanelPersonalAction(actor, "give");
                return;

            case PlayerPanelGotoButtonId:
                ExecutePlayerPanelPersonalAction(actor, "goto");
                return;

            case PlayerPanelBringBotsButtonId:
                ExecutePlayerPanelPersonalAction(actor, "bringbots");
                return;

            case PlayerPanelRoomTeleportButtonId:
                ExecutePlayerPanelPersonalAction(actor, "roomtp");
                return;

            case PlayerPanelSetBotsButtonId:
                ExecutePlayerPanelGlobalAction(actor, "bots");
                return;

            case PlayerPanelApplyDifficultyButtonId:
                ExecutePlayerPanelGlobalAction(actor, "difficulty");
                return;

            case PlayerPanelApplyAiModeButtonId:
                ExecutePlayerPanelGlobalAction(actor, "aimode");
                return;

            case PlayerPanelApplyRetreatSpeedButtonId:
                ExecutePlayerPanelGlobalAction(actor, "retreatspeed");
                return;

            case PlayerPanelApplyBotRoleButtonId:
                ExecutePlayerPanelGlobalAction(actor, "botrole");
                return;
        }
    }

    private void ExecutePlayerPanelPersonalAction(Player actor, string action)
    {
        if (!TryUsePlayerPanelPersonalCooldown(actor, out string cooldownResponse))
        {
            actor.SendHint(cooldownResponse, 1.05f);
            return;
        }

        switch (action)
        {
            case "role":
            case "loadout":
            case "give":
            case "goto":
            case "roomtp":
                actor.SendHint(WarmupLocalization.T(
                    "Player role, loadout, item, and teleport controls are disabled in bot-only mode.",
                    "仅机器人模式已禁用玩家阵营、预设、物品和传送功能。"), 4f);
                break;

            case "bringbots":
                TryPanelBringBots(actor, GetSelectedPanelBotTargetId(actor), out _);
                break;

        }
    }

    private void ExecutePlayerPanelGlobalAction(Player actor, string action)
    {
        if (!TryUsePlayerPanelGlobalCooldown(actor, out string cooldownResponse))
        {
            actor.SendHint(cooldownResponse, 1.05f);
            return;
        }

        string response;
        switch (action)
        {
            case "bots":
                int count = _playerPanelSelectedBotCounts.TryGetValue(actor.PlayerId, out int selectedCount)
                    ? selectedCount
                    : Config.BotCount;
                Config.BotCount = ClampPanelBotCount(count);
                SaveConfig();
                EnsureBotPopulation(EnsureBotRuntimeActive("player panel bot count"));
                response = WarmupLocalization.T(
                    $"Bot count set to {Config.BotCount}.",
                    $"机器人数量已设置为 {Config.BotCount}。");
                break;

            case "difficulty":
                WarmupDifficulty difficulty = _playerPanelSelectedDifficulties.TryGetValue(actor.PlayerId, out WarmupDifficulty selectedDifficulty)
                    ? selectedDifficulty
                    : Config.DifficultyPreset;
                ApplyDifficultyPreset(difficulty.ToString(), out response);
                break;

            case "aimode":
                WarmupAiMode aiMode = _playerPanelSelectedAiModes.TryGetValue(actor.PlayerId, out WarmupAiMode selectedAiMode)
                    ? selectedAiMode
                    : Config.BotBehavior.AiMode;
                ApplyAiMode(aiMode.ToString(), out response);
                break;

            case "retreatspeed":
                float retreatSpeedScale = _playerPanelSelectedRetreatSpeedScales.TryGetValue(actor.PlayerId, out float selectedRetreatSpeedScale)
                    ? selectedRetreatSpeedScale
                    : Config.BotBehavior.CloseRetreatSpeedScale;
                Config.BotBehavior.CloseRetreatSpeedScale = ClampCloseRetreatSpeedScale(retreatSpeedScale);
                SaveConfig();
                response = WarmupLocalization.T(
                    $"Bot retreat speed set to {Config.BotBehavior.CloseRetreatSpeedScale:P0}.",
                    $"机器人后退速度已设置为 {Config.BotBehavior.CloseRetreatSpeedScale:P0}。");
                break;

            case "botrole":
                TryApplyPanelBotRole(actor, out response);
                break;

            default:
                response = WarmupLocalization.T("Unknown global action.", "未知全局操作。");
                break;
        }

        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel global actor={actor.Nickname}#{actor.PlayerId} action={action} response={response}");
    }

    private Player ResolveSelectedPanelTarget(Player actor)
    {
        if (!_playerPanelSelectedTargetIds.TryGetValue(actor.PlayerId, out int targetId)
            || targetId == PlayerPanelSelfTargetId
            || !TryGetPlayerPanelTargetById(targetId, out Player target)
            || target == null
            || target.IsDestroyed)
        {
            return actor;
        }

        return target;
    }

    private int GetSelectedPanelBotTargetId(Player actor)
    {
        if (_playerPanelSelectedBotTargetIds.TryGetValue(actor.PlayerId, out int targetId))
        {
            return targetId;
        }

        Player? firstBot = GetPlayerPanelBotTargets().FirstOrDefault();
        return firstBot?.PlayerId ?? PlayerPanelAllBotsTargetId;
    }

    private bool TryApplyPanelBotRole(Player actor, out string response)
    {
        RoleTypeId role = _playerPanelSelectedBotRoles.TryGetValue(actor.PlayerId, out RoleTypeId selectedRole)
            ? selectedRole
            : PlayerPanelBotRoles.FirstOrDefault();

        if (role == RoleTypeId.None || role == RoleTypeId.Spectator)
        {
            response = WarmupLocalization.T("Choose a valid bot role first.", "请先选择有效的机器人阵营。");
            return false;
        }

        int targetId = GetSelectedPanelBotTargetId(actor);
        if (targetId == PlayerPanelAllBotsTargetId)
        {
            response = WarmupLocalization.T(
                "Choose one bot target before applying a bot role.",
                "应用机器人阵营前请选择单个机器人目标。");
            return false;
        }

        List<Player> targets = new();
        if (TryGetPlayerPanelTargetById(targetId, out Player target)
            && target != null
            && IsManagedBot(target))
        {
            targets.Add(target);
        }

        if (targets.Count == 0)
        {
            response = WarmupLocalization.T("No managed bot target was found.", "没有找到可修改的机器人。");
            return false;
        }

        int changed = 0;
        foreach (Player bot in targets)
        {
            if (bot == null
                || bot.IsDestroyed
                || !_managedBots.TryGetValue(bot.PlayerId, out ManagedBotState state))
            {
                continue;
            }

            ApplyPanelBotRole(bot, state, role);
            changed++;
        }

        RefreshPlayerPanelSettings(sendToPlayers: true);

        if (changed == 0)
        {
            response = WarmupLocalization.T("No managed bot target was found.", "没有找到可修改的机器人。");
            return false;
        }

        response = WarmupLocalization.T(
            $"Set {changed} bot(s) to {role} permanently.",
            $"已将 {changed} 个机器人永久设置为 {role}。");
        ApiLogger.Info($"[WarmupSandbox] Player panel botrole actor={actor.Nickname}#{actor.PlayerId} role={role} changed={changed} target={targetId}");
        return changed > 0;
    }

    private void ApplyPanelBotRole(Player bot, ManagedBotState state, RoleTypeId role)
    {
        state.SpawnSetupCompleted = false;
        state.ResetNavigationRuntimeState();
        state.BrainToken++;
        state.RespawnRole = role;
        bot.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
    }

    public bool TryOpenPlayerPanel(Player player, out string response)
    {
        if (!Config.PlayerPanelEnabled)
        {
            response = WarmupLocalization.T(
                "The player panel is disabled on this server.",
                "本服务器已关闭玩家控制台。");
            return false;
        }

        RefreshPlayerPanelSettings(sendToPlayers: true);
        response = BuildPlayerPanel(player);
        ServerSpecificSettingsSync.SendToPlayer(player.ReferenceHub);
        player.SendHint(response, 6f);
        ApiLogger.Info($"[WarmupSandbox] Player panel refreshed for {player.Nickname}#{player.PlayerId}.");
        return true;
    }

    private string BuildPlayerPanel(Player player)
    {
        return WarmupLocalization.T(
            "<size=28><color=#00ffff><b>Warmup console enabled</b></color></size>\n<size=20>Open <color=#ffd166>Server Specific Settings</color>. Personal Apply: first 3 free, then 5s cooldown. Global Apply: staged server changes with shared cooldown.</size>",
            "<size=28><color=#00ffff><b>人机控制台已开启</b></color></size>\n<size=20>打开<color=#ffd166>服务器专属设置（Server Specific Settings）</color>。个人应用：前 3 次无冷却，之后 5 秒；全局应用：先选择再生效，使用共享冷却。</size>");
    }

    private bool TryGetPlayerPanelTargetById(int playerId, out Player target)
    {
        if (Player.TryGet(playerId, out target)
            && target != null
            && !target.IsDestroyed
            && !target.IsHost)
        {
            return true;
        }

        if (_managedBots.ContainsKey(playerId)
            && Player.TryGet(playerId, out target)
            && target != null
            && !target.IsDestroyed)
        {
            return true;
        }

        target = null!;
        return false;
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array![arguments.Offset + index]!;
    }

    private bool TryPanelSetRole(Player actor, Player target, RoleTypeId role, out string response)
    {
        bool wasSpectator = target.Role == RoleTypeId.Spectator;
        Vector3 position = target.Position;
        Vector2 lookRotation = target.LookRotation;
        target.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        if (!wasSpectator && role != RoleTypeId.Spectator)
        {
            RestorePanelRolePosition(target.PlayerId, role, position, lookRotation, 50);
            RestorePanelRolePosition(target.PlayerId, role, position, lookRotation, 250);
            response = WarmupLocalization.T(
                $"Set {target.Nickname} to {role} in place.",
                $"已将 {target.Nickname} 原地设置为阵营 {role}。");
        }
        else
        {
            response = WarmupLocalization.T(
                $"Set {target.Nickname} to {role} using the default spawnpoint.",
                $"已将 {target.Nickname} 设置为阵营 {role}，并使用默认出生点。");
        }

        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel role actor={actor.Nickname}#{actor.PlayerId} target={target.Nickname}#{target.PlayerId} role={role} wasSpectator={wasSpectator}");
        return true;
    }

    private void RestorePanelRolePosition(int playerId, RoleTypeId role, Vector3 position, Vector2 lookRotation, int delayMs)
    {
        Schedule(() =>
        {
            if (!Player.TryGet(playerId, out Player livePlayer)
                || livePlayer.IsDestroyed
                || livePlayer.Role != role)
            {
                return;
            }

            livePlayer.Position = position;
            livePlayer.LookRotation = lookRotation;
        }, delayMs);
    }

    private bool TryPanelGive(Player actor, Player target, ItemType itemType, out string response)
    {
        if (IsAmmoType(itemType))
        {
            ushort current = target.GetAmmo(itemType);
            ushort next = (ushort)Math.Min(ushort.MaxValue, current + 120);
            target.SetAmmo(itemType, next);
            response = WarmupLocalization.T(
                $"Gave {target.Nickname} {itemType}: {current}->{next}.",
                $"已给 {target.Nickname} {itemType}：{current}->{next}。");
        }
        else
        {
            target.AddItem(itemType, ItemAddReason.AdminCommand);
            response = WarmupLocalization.T(
                $"Gave {target.Nickname} {itemType}.",
                $"已给 {target.Nickname} {itemType}。");
        }

        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel give actor={actor.Nickname}#{actor.PlayerId} target={target.Nickname}#{target.PlayerId} item={itemType}");
        return true;
    }

    private static bool IsAmmoType(ItemType itemType)
    {
        return itemType is ItemType.Ammo9x19
            or ItemType.Ammo556x45
            or ItemType.Ammo762x39
            or ItemType.Ammo12gauge
            or ItemType.Ammo44cal;
    }

    private bool TryPanelBringBots(Player actor, int targetId, out string response)
    {
        if (actor.Role == RoleTypeId.Spectator)
        {
            response = WarmupLocalization.T(
                "Spawn first before bringing bots.",
                "请先出生，再召回机器人。");
            actor.SendHint(response, 4f);
            return false;
        }

        List<Player> bots = new();
        if (targetId == PlayerPanelAllBotsTargetId)
        {
            bots.AddRange(GetPlayerPanelBotTargets());
        }
        else if (TryGetPlayerPanelTargetById(targetId, out Player target)
            && target != null
            && IsManagedBot(target))
        {
            bots.Add(target);
        }

        if (bots.Count == 0)
        {
            response = WarmupLocalization.T(
                "No selected bot is alive.",
                "当前没有可召回的机器人。");
            actor.SendHint(response, 4f);
            return false;
        }

        Vector3 origin = actor.Position;
        Vector3 forward = GetPlanarDirection(GetForwardOrDefault(actor), Vector3.forward);
        Vector3 right = GetPlanarDirection(GetRightOrDefault(actor), Vector3.right);
        int changed = 0;

        for (int index = 0; index < bots.Count; index++)
        {
            Player bot = bots[index];
            if (bot == null
                || bot.IsDestroyed
                || !_managedBots.TryGetValue(bot.PlayerId, out ManagedBotState state))
            {
                continue;
            }

            double angle = bots.Count <= 1 ? 0.0 : (Math.PI * 2.0 * index) / bots.Count;
            float radius = 1.75f + 0.5f * (index / 8);
            Vector3 offset = (forward * (float)Math.Cos(angle) + right * (float)Math.Sin(angle)) * radius;
            Vector3 position = origin + offset;
            bot.Position = position;
            state.LastPosition = position;
            state.ResetNavigationRuntimeState();
            changed++;
        }

        if (changed == 0)
        {
            response = WarmupLocalization.T(
                "No bot could be brought.",
                "没有可召回的机器人。");
            actor.SendHint(response, 4f);
            return false;
        }

        response = WarmupLocalization.T(
            $"Brought {changed} bot(s) to you.",
            $"已召回 {changed} 个机器人到你身边。");
        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel bringbots actor={actor.Nickname}#{actor.PlayerId} changed={changed} target={targetId}");
        return changed > 0;
    }

    private bool TryPanelTeleportToRoom(Player actor, RoomName roomName, out string response)
    {
        if (RoomIdentifier.AllRoomIdentifiers == null || RoomIdentifier.AllRoomIdentifiers.Count == 0)
        {
            response = WarmupLocalization.T(
                "Room list is not ready yet.",
                "房间列表尚未加载。");
            actor.SendHint(response, 4f);
            return false;
        }

        RoomIdentifier? selectedRoom = null;
        float bestDistance = float.PositiveInfinity;
        Vector3 actorPosition = actor.Position;

        foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
        {
            if (room == null || room.transform == null || room.Name != roomName)
            {
                continue;
            }

            float distance = Vector3.SqrMagnitude(room.transform.position - actorPosition);
            if (distance >= bestDistance)
            {
                continue;
            }

            selectedRoom = room;
            bestDistance = distance;
        }

        if (selectedRoom == null)
        {
            response = WarmupLocalization.T(
                $"{GetPlayerPanelRoomLabel(roomName)} does not exist in this round.",
                $"本局不存在 {GetPlayerPanelRoomLabel(roomName)}。");
            actor.SendHint(response, 4f);
            return false;
        }

        if (!TryFindPlayerPanelRoomTeleportPosition(selectedRoom, out Vector3 teleportPosition))
        {
            response = WarmupLocalization.T(
                $"Could not find a safe point in {GetPlayerPanelRoomLabel(roomName)}.",
                $"未能在 {GetPlayerPanelRoomLabel(roomName)} 找到安全位置。");
            actor.SendHint(response, 4f);
            return false;
        }

        actor.Position = teleportPosition;
        response = WarmupLocalization.T(
            $"Teleported to {GetPlayerPanelRoomLabel(roomName)}.",
            $"已传送到 {GetPlayerPanelRoomLabel(roomName)}。");
        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel roomtp actor={actor.Nickname}#{actor.PlayerId} room={roomName} pos={FormatVector(teleportPosition)}");
        return true;
    }

    private static bool TryFindPlayerPanelRoomTeleportPosition(RoomIdentifier room, out Vector3 position)
    {
        Transform transform = room.transform;
        Vector3 center = transform.position;
        Vector3 forward = GetPlanarDirection(transform.forward, Vector3.forward);
        Vector3 right = GetPlanarDirection(transform.right, Vector3.right);
        Vector3[] candidates =
        {
            center,
            center + forward * 2.5f,
            center - forward * 2.5f,
            center + right * 2.5f,
            center - right * 2.5f,
            center + forward * 4f,
            center - forward * 4f,
            center + right * 4f,
            center - right * 4f,
        };

        foreach (Vector3 candidate in candidates)
        {
            if (NavMesh.SamplePosition(candidate, out NavMeshHit navHit, 6f, NavMesh.AllAreas)
                && Mathf.Abs(navHit.position.y - center.y) <= 8f)
            {
                position = navHit.position + Vector3.up * 0.35f;
                return true;
            }
        }

        foreach (Vector3 candidate in candidates)
        {
            Vector3 rayStart = candidate + Vector3.up * 12f;
            if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.collider == null
                || hit.normal.y < 0.45f
                || Mathf.Abs(hit.point.y - center.y) > 12f)
            {
                continue;
            }

            position = hit.point + Vector3.up * 0.65f;
            return true;
        }

        position = center + Vector3.up;
        return true;
    }

    private static string GetPlayerPanelRoomLabel(RoomName roomName)
    {
        foreach (PlayerPanelRoomPreset preset in PlayerPanelRoomPresets)
        {
            if (preset.RoomName == roomName)
            {
                return preset.Label;
            }
        }

        return roomName.ToString();
    }

    private bool TryPanelGoto(Player actor, Player target, out string response)
    {
        actor.Position = target.Position + GetForwardOrDefault(target);
        response = WarmupLocalization.T(
            $"Teleported to {target.Nickname}.",
            $"已传送到 {target.Nickname}。");
        actor.SendHint(response, 4f);
        ApiLogger.Info($"[WarmupSandbox] Player panel goto actor={actor.Nickname}#{actor.PlayerId} target={target.Nickname}#{target.PlayerId}");
        return true;
    }

    private static Vector3 GetForwardOrDefault(Player player)
    {
        return player.GameObject == null ? Vector3.forward : player.GameObject.transform.forward;
    }

    private static Vector3 GetRightOrDefault(Player player)
    {
        return player.GameObject == null ? Vector3.right : player.GameObject.transform.right;
    }

    private static Vector3 GetPlanarDirection(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = fallback;
        }

        return direction.normalized;
    }

    private int ClampPanelBotCount(int count)
    {
        return ClampPlayerBotCount(count);
    }

    private int GetPlayerBotCountLimit()
    {
        return Math.Min(Math.Max(0, Config.MaxPlayerBotCount), Math.Max(0, Config.MaxBotCount));
    }

    private void ClampSelectedPlayerPanelBotCounts()
    {
        foreach (int playerId in _playerPanelSelectedBotCounts.Keys.ToArray())
        {
            _playerPanelSelectedBotCounts[playerId] = ClampPanelBotCount(_playerPanelSelectedBotCounts[playerId]);
        }
    }

    private static float ClampCloseRetreatSpeedScale(float scale)
    {
        return Mathf.Clamp(scale, 0.6f, 1.0f);
    }

    private bool TryUsePlayerPanelPersonalCooldown(Player player, out string response)
    {
        long now = NowMs();
        int playerId = player.PlayerId;
        if (_playerPanelPersonalCooldownUntilMs.TryGetValue(playerId, out long cooldownUntil))
        {
            if (TryGetCooldownRemainingSeconds(cooldownUntil, now, out int remaining))
            {
                response = WarmupLocalization.T(
                    $"Personal console cooldown: {remaining}s.",
                    $"个人控制台操作冷却中：{remaining} 秒。");
                return false;
            }

            _playerPanelPersonalCooldownUntilMs.Remove(playerId);
            _playerPanelPersonalActionCounts.Remove(playerId);
        }

        int actionCount = _playerPanelPersonalActionCounts.TryGetValue(playerId, out int previousCount)
            ? previousCount + 1
            : 1;

        if (actionCount >= PlayerPanelPersonalFreeActions)
        {
            _playerPanelPersonalActionCounts.Remove(playerId);
            _playerPanelPersonalCooldownUntilMs[playerId] = now + PlayerPanelPersonalCooldownSeconds * 1000L;
        }
        else
        {
            _playerPanelPersonalActionCounts[playerId] = actionCount;
        }

        response = string.Empty;
        return true;
    }

    private bool TryUsePlayerPanelGlobalCooldown(Player player, out string response)
    {
        long now = NowMs();
        if (TryGetCooldownRemainingSeconds(_playerPanelGlobalCooldownUntilMs, now, out int globalRemaining))
        {
            response = WarmupLocalization.T(
                $"Global panel action cooldown: {globalRemaining}s.",
                $"全局控制台操作冷却中：{globalRemaining} 秒。");
            return false;
        }

        if (_playerPanelCooldownUntilMs.TryGetValue(player.PlayerId, out long playerCooldownUntil)
            && TryGetCooldownRemainingSeconds(playerCooldownUntil, now, out int playerRemaining))
        {
            response = WarmupLocalization.T(
                $"You can apply another global setting in {playerRemaining}s.",
                $"你还需要 {playerRemaining} 秒后才能再次应用全局设置。");
            return false;
        }

        _playerPanelGlobalCooldownUntilMs = now + Math.Max(0, Config.PlayerPanelGlobalCooldownSeconds) * 1000L;
        _playerPanelCooldownUntilMs[player.PlayerId] = now + BuildCooldownMs(
            Config.PlayerPanelCooldownSeconds,
            Config.PlayerPanelCooldownJitterSeconds);
        response = string.Empty;
        return true;
    }

    private bool IsPlayerPanelWindowActive(int playerId)
    {
        long now = NowMs();
        return _playerPanelWindowUntilMs.TryGetValue(playerId, out long windowUntil)
            && windowUntil > now;
    }

    private void SchedulePlayerPanelCooldown(int playerId, long windowUntilMs)
    {
        int delayMs = Math.Max(1, (int)Math.Min(int.MaxValue, windowUntilMs - NowMs()));
        Schedule(() =>
        {
            if (!_playerPanelWindowUntilMs.TryGetValue(playerId, out long currentWindowUntil)
                || currentWindowUntil != windowUntilMs)
            {
                return;
            }

            _playerPanelWindowUntilMs.Remove(playerId);
            long now = NowMs();
            _playerPanelGlobalCooldownUntilMs = now + Math.Max(0, Config.PlayerPanelGlobalCooldownSeconds) * 1000L;
            double cooldownScale = Math.Max(1, Config.PlayerPanelUseWindowSeconds) / 20.0;
            int scaledCooldownSeconds = (int)Math.Ceiling(Math.Max(0, Config.PlayerPanelCooldownSeconds) * cooldownScale);
            int scaledJitterSeconds = (int)Math.Ceiling(Math.Max(0, Config.PlayerPanelCooldownJitterSeconds) * cooldownScale);
            _playerPanelCooldownUntilMs[playerId] = now + BuildCooldownMs(scaledCooldownSeconds, scaledJitterSeconds);
        }, delayMs);
    }

    private long BuildCooldownMs(int seconds, int jitterSeconds)
    {
        int jitter = jitterSeconds <= 0 ? 0 : _random.Next(0, jitterSeconds + 1);
        return (long)Math.Max(0, seconds + jitter) * 1000L;
    }

    private static bool TryGetCooldownRemainingSeconds(long cooldownUntilMs, long nowMs, out int remainingSeconds)
    {
        long remainingMs = cooldownUntilMs - nowMs;
        if (remainingMs <= 0)
        {
            remainingSeconds = 0;
            return false;
        }

        remainingSeconds = Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0));
        return true;
    }

    private static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

                if (botCount > Config.MaxBotCount)
                {
                    response = $"Bot count cannot exceed {Config.MaxBotCount}.";
                    return false;
                }

                Config.BotCount = botCount;
                EnsureBotPopulation(EnsureBotRuntimeActive("remote admin bot count"));
                TrimExcessBots();
                response = $"Bot count set to {Config.BotCount}.";
                return true;

            case "maxbots":
            case "maxbotcount":
                if (!int.TryParse(value, out int maxBotCount) || maxBotCount < 0)
                {
                    response = "Max bot count must be a non-negative integer.";
                    return false;
                }

                Config.MaxBotCount = maxBotCount;
                ClampConfiguredBotCount();
                TrimExcessBots();
                response = $"Max bot count set to {Config.MaxBotCount}. Current bot count is {Config.BotCount}.";
                return true;

            case "maxplayerbots":
            case "maxplayerbotcount":
            case "playermaxbots":
            case "playermaxbotcount":
                if (!int.TryParse(value, out int maxPlayerBotCount) || maxPlayerBotCount < 0)
                {
                    response = "Max player bot count must be a non-negative integer.";
                    return false;
                }

                Config.MaxPlayerBotCount = maxPlayerBotCount;
                ClampSelectedPlayerPanelBotCounts();
                RefreshPlayerPanelSettings(sendToPlayers: true);
                response = $"Max player bot count set to {GetPlayerBotCountLimit()} (configured {Config.MaxPlayerBotCount}, admin max {Config.MaxBotCount}).";
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

            case "speed":
            case "followspeed":
            case "facilityspeed":
                return SetFacilityFollowSpeed(value, out response);

            case "939speed":
            case "scp939speed":
                return SetScpFacilityFollowSpeed(RoleTypeId.Scp939, value, out response);

            case "3114speed":
            case "scp3114speed":
                return SetScpFacilityFollowSpeed(RoleTypeId.Scp3114, value, out response);

            case "049speed":
            case "scp049speed":
                return SetScpFacilityFollowSpeed(RoleTypeId.Scp049, value, out response);

            case "106speed":
            case "scp106speed":
                return SetScpFacilityFollowSpeed(RoleTypeId.Scp106, value, out response);

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
                Config.ForceRoundStartOnFirstPlayer = false;
                response = "ForceRoundStartOnFirstPlayer is disabled in bot-only mode.";
                return true;

            case "suppressroundend":
                Config.SuppressRoundEnd = false;
                response = "SuppressRoundEnd is disabled in bot-only mode.";
                return true;

            case "mode":
                return SetRoundMode(value, out response);

            case "map":
            case "dust2":
            case "dust2map":
                if (!bool.TryParse(value, out bool dust2Enabled))
                {
                    response = "map must be true or false.";
                    return false;
                }

                return SetDust2MapEnabled(dust2Enabled, out response);

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

            case "retreatspeed":
            case "backoffspeed":
            case "closeretreatspeed":
            case "closeretreatspeedscale":
                return SetCloseRetreatSpeedScale(value, out response);

            case "language":
            case "lang":
            case "locale":
                return SetLanguage(value, out response);

            default:
                response = $"Unknown setting '{key}'.";
                return false;
        }
    }

    public bool SetLanguage(string rawValue, out string response)
    {
        if (!WarmupLocalization.TryNormalizeLanguage(rawValue, out string language))
        {
            response = WarmupLocalization.T("Unknown language. Use en or cn.", "未知语言。请使用 en 或 cn。");
            return false;
        }

        Config.Language = language;
        WarmupLocalization.SetLanguage(language);
        response = WarmupLocalization.T($"Language set to {language}.", $"语言已设置为 {language}。");
        return true;
    }

    public bool SetCloseRetreatSpeedScale(string rawValue, out string response)
    {
        if (!float.TryParse(rawValue, out float scale))
        {
            response = WarmupLocalization.T(
                "Retreat speed scale must be a number from 0.6 to 1.",
                "后退速度倍率必须是 0.6 到 1 之间的数字。");
            return false;
        }

        Config.BotBehavior.CloseRetreatSpeedScale = ClampCloseRetreatSpeedScale(scale);
        response = WarmupLocalization.T(
            $"Close retreat speed scale set to {Config.BotBehavior.CloseRetreatSpeedScale:F2}.",
            $"近距离后退速度倍率已设置为 {Config.BotBehavior.CloseRetreatSpeedScale:F2}。");
        return true;
    }

    public bool SetFacilityFollowSpeed(string rawValue, out string response)
    {
        if (!TryParsePositiveSpeed(rawValue, out float speed, out response))
        {
            return false;
        }

        Config.BotBehavior.FacilityDummyFollowSpeed = speed;
        response = $"Default facility follow speed set to {speed:F2}.";
        return true;
    }

    public bool SetScpFacilityFollowSpeed(RoleTypeId role, string rawValue, out string response)
    {
        if (!TryParsePositiveSpeed(rawValue, out float speed, out response))
        {
            return false;
        }

        switch (role)
        {
            case RoleTypeId.Scp939:
                Config.BotBehavior.FacilityDummyFollowSpeedScp939 = speed;
                break;
            case RoleTypeId.Scp3114:
                Config.BotBehavior.FacilityDummyFollowSpeedScp3114 = speed;
                break;
            case RoleTypeId.Scp049:
                Config.BotBehavior.FacilityDummyFollowSpeedScp049 = speed;
                break;
            case RoleTypeId.Scp106:
                Config.BotBehavior.FacilityDummyFollowSpeedScp106 = speed;
                break;
            default:
                response = $"Unsupported SCP speed role: {role}.";
                return false;
        }

        response = $"{role} facility follow speed set to {speed:F2}.";
        return true;
    }

    private static bool TryParsePositiveSpeed(string rawValue, out float speed, out string response)
    {
        if (!float.TryParse(rawValue, out speed) || speed <= 0f || speed > 50f)
        {
            response = "Speed must be a number greater than 0 and no more than 50.";
            return false;
        }

        response = "";
        return true;
    }

    public bool SetRoundMode(string rawValue, out string response)
    {
        if (!Enum.TryParse(rawValue, true, out WarmupRoundMode mode))
        {
            response = "Unknown mode. Use standard or bomb.";
            return false;
        }

        _bombModeService.SetMode(mode);
        if (_warmupActive)
        {
            RestartWarmup($"mode changed to {mode}");
            response = $"Round mode set to {mode}. Warmup restart requested.";
            return true;
        }

        response = $"Round mode set to {mode}.";
        return true;
    }

    public bool SetDust2MapEnabled(bool enabled, out string response)
    {
        Config.Dust2Map.Enabled = enabled;
        if (_warmupActive)
        {
            RestartWarmup(enabled ? "dust2 map enabled" : "dust2 map disabled");
            response = enabled
                ? "Dust2 map enabled. Warmup restart requested."
                : "Dust2 map disabled. Warmup restart requested.";
            return true;
        }

        if (!enabled)
        {
            CleanupArenaMap(returnHumansToFacility: false);
        }

        response = enabled
            ? "Dust2 map enabled. It will load on the next warmup start."
            : "Dust2 map disabled.";
        return true;
    }

    private void PrepareArenaMapForWarmup()
    {
        _dust2MapService.Unload();
        ClearArenaDebugVisuals();
        if (!ShouldUseDust2Arena())
        {
            if (Config.EnableArenaLogging)
            {
                ApiLogger.Info($"[{Name}] Dust2 arena not requested for this warmup run.");
            }

            return;
        }

        bool forceDust2Load = _bombModeService.Enabled && !Config.Dust2Map.Enabled;
        if (Config.EnableArenaLogging)
        {
            ApiLogger.Info($"[{Name}] Attempting to load Dust2 arena. {_dust2MapService.BuildStatus(Config.Dust2Map, forceDust2Load)}");
        }

        if (_dust2MapService.TryLoad(Config.Dust2Map, out string response, forceDust2Load))
        {
            if (_dust2MapService.TryBakeRuntimeNavMesh(Config.Dust2Map, out string navMeshResponse))
            {
                if (Config.EnableArenaLogging)
                {
                    ApiLogger.Info($"[{Name}] {navMeshResponse}");
                }

                RebuildRuntimeNavMeshDebugVisuals();
            }
            else if (Config.Dust2Map.RuntimeNavMeshEnabled && Config.EnableArenaLogging)
            {
                ApiLogger.Warn($"[{Name}] {navMeshResponse}");
            }

            if (Config.EnableArenaLogging)
            {
                ApiLogger.Info($"[{Name}] {response}");
            }

            return;
        }

        if (Config.EnableArenaLogging)
        {
            ApiLogger.Warn($"[{Name}] Dust2 warmup arena could not be loaded: {response}");
        }
    }

    private void PrepareFacilityNavMeshForWarmup()
    {
        if (ShouldUseDust2Arena())
        {
            return;
        }

        _facilityNavMeshService.RemoveRuntimeNavMesh();
        DestroyDebugToys(_runtimeNavMeshDebugEdges);
        _runtimeNavMeshDebugEdges.Clear();
        ClearNavAgentDebugVisuals();

        bool fullFacilityEnabled = Config.BotBehavior.UseFacilityNavMesh
            && Config.BotBehavior.FacilityRuntimeNavMeshEnabled;
        bool surfaceEnabled = Config.BotBehavior.UseFacilitySurfaceNavMesh
            && Config.BotBehavior.FacilitySurfaceRuntimeNavMeshEnabled;
        if (!fullFacilityEnabled && !surfaceEnabled)
        {
            return;
        }

        string navMeshResponse = string.Empty;
        if (fullFacilityEnabled
            && _facilityNavMeshService.TryBakeRuntimeNavMesh(Config.BotBehavior, out navMeshResponse))
        {
            if (Config.BotBehavior.FacilityRuntimeNavMeshLogBuild || Config.BotBehavior.NavDebugLogging)
            {
                ApiLogger.Info($"[{Name}] {navMeshResponse}");
            }

            RebuildFacilityNavMeshDebugVisuals();
            return;
        }

        if (fullFacilityEnabled && (Config.BotBehavior.FacilityRuntimeNavMeshLogBuild || Config.BotBehavior.NavDebugLogging))
        {
            ApiLogger.Warn($"[{Name}] {navMeshResponse}");
        }

        if (!surfaceEnabled)
        {
            return;
        }

        if (_facilityNavMeshService.TryBakeSurfaceRuntimeNavMesh(Config.BotBehavior, out navMeshResponse))
        {
            if (Config.BotBehavior.FacilityRuntimeNavMeshLogBuild || Config.BotBehavior.NavDebugLogging)
            {
                ApiLogger.Info($"[{Name}] {navMeshResponse}");
            }

            RebuildFacilityNavMeshDebugVisuals();
        }
        else if (Config.BotBehavior.FacilityRuntimeNavMeshLogBuild || Config.BotBehavior.NavDebugLogging)
        {
            ApiLogger.Warn($"[{Name}] {navMeshResponse}");
        }
    }

    private void CleanupArenaMap(bool returnHumansToFacility)
    {
        bool wasLoaded = _dust2MapService.IsLoaded;
        if (returnHumansToFacility && wasLoaded)
        {
            ReturnManagedHumansToFacility();
        }

        _dust2MapService.Unload();
        ClearArenaDebugVisuals();
    }

    private bool ShouldUseDust2Arena()
    {
        return Config.Dust2Map.Enabled || _bombModeService.Enabled;
    }

    private void ReturnManagedHumansToFacility()
    {
        foreach (Player player in Player.List.Where(IsManagedHuman))
        {
            player.SetRole(GetHumanRole(player), RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
        }
    }

    private int ApplyArenaSpawnIfNeeded(Player player, bool isBot)
    {
        if (!ShouldUseDust2Arena() || !_dust2MapService.IsLoaded)
        {
            if (ShouldUseDust2Arena() && Config.EnableArenaLogging)
            {
                ApiLogger.Warn($"[{Name}] Arena spawn skipped for {player.Nickname} because Dust2 is not loaded.");
            }

            return 0;
        }

        Vector3 spawnPosition;
        bool hasSpawn;
        string spawnSide;
        if (player.IsNTF)
        {
            hasSpawn = _dust2MapService.TryGetHumanSpawnPosition(Config.Dust2Map, _random, out spawnPosition);
            spawnSide = "ct";
        }
        else if (player.IsChaos)
        {
            hasSpawn = _dust2MapService.TryGetBotSpawnPosition(Config.Dust2Map, _random, out spawnPosition);
            spawnSide = "t";
        }
        else
        {
            hasSpawn = isBot
                ? _dust2MapService.TryGetBotSpawnPosition(Config.Dust2Map, _random, out spawnPosition)
                : _dust2MapService.TryGetHumanSpawnPosition(Config.Dust2Map, _random, out spawnPosition);
            spawnSide = isBot ? "t-fallback" : "ct-fallback";
        }

        if (!hasSpawn)
        {
            if (Config.EnableArenaLogging)
            {
                ApiLogger.Warn($"[{Name}] Failed to find a Dust2 spawn marker for {(isBot ? "bot" : "human")} {player.Nickname}. Human markers=[{string.Join(", ", Config.Dust2Map.HumanSpawnMarkerNames ?? Array.Empty<string>())}] Bot markers=[{string.Join(", ", Config.Dust2Map.BotSpawnMarkerNames ?? Array.Empty<string>())}]");
            }

            return 0;
        }

        int initialDelayMs = isBot ? BotArenaSpawnDelayMs : 0;
        if (initialDelayMs <= 0)
        {
            player.Position = spawnPosition;
            if (Config.EnableArenaLogging)
            {
                ApiLogger.Info($"[{Name}] Arena spawn applied to {(isBot ? "bot" : "human")} {player.Nickname} side={spawnSide} at ({spawnPosition.x:F1}, {spawnPosition.y:F1}, {spawnPosition.z:F1}).");
            }
        }
        else if (Config.EnableArenaLogging)
        {
            ApiLogger.Info($"[{Name}] Arena spawn scheduled for {(isBot ? "bot" : "human")} {player.Nickname} side={spawnSide} after {initialDelayMs} ms at ({spawnPosition.x:F1}, {spawnPosition.y:F1}, {spawnPosition.z:F1}).");
        }

        ScheduleArenaSpawnCorrections(player.PlayerId, spawnPosition, _warmupGeneration, isBot, initialDelayMs);
        return initialDelayMs + (isBot ? ArenaSpawnCorrectionDelaysMs.Max() : 0);
    }

    private void ScheduleArenaSpawnCorrections(int playerId, Vector3 spawnPosition, int generation, bool isBot, int initialDelayMs)
    {
        if (initialDelayMs > 0)
        {
            Schedule(() =>
            {
                if (!IsCurrentGeneration(generation)
                    || !ShouldUseDust2Arena()
                    || !_dust2MapService.IsLoaded
                    || !Player.TryGet(playerId, out Player livePlayer)
                    || livePlayer.IsDestroyed
                    || livePlayer.Role == RoleTypeId.Spectator)
                {
                    return;
                }

                livePlayer.Position = spawnPosition;
                if (Config.EnableArenaLogging)
                {
                    ApiLogger.Info($"[{Name}] Arena spawn applied to {(isBot ? "bot" : "human")} {livePlayer.Nickname} after {initialDelayMs} ms at ({spawnPosition.x:F1}, {spawnPosition.y:F1}, {spawnPosition.z:F1}).");
                }
            }, initialDelayMs);
        }

        foreach (int delayMs in ArenaSpawnCorrectionDelaysMs)
        {
            Schedule(() =>
            {
                if (!IsCurrentGeneration(generation)
                    || !ShouldUseDust2Arena()
                    || !_dust2MapService.IsLoaded
                    || !Player.TryGet(playerId, out Player livePlayer)
                    || livePlayer.IsDestroyed
                    || livePlayer.Role == RoleTypeId.Spectator)
                {
                    return;
                }

                livePlayer.Position = spawnPosition;
                if (Config.EnableArenaLogging)
                {
                    ApiLogger.Info($"[{Name}] Arena spawn correction reapplied to {(isBot ? "bot" : "human")} {livePlayer.Nickname} after {initialDelayMs + delayMs} ms at ({spawnPosition.x:F1}, {spawnPosition.y:F1}, {spawnPosition.z:F1}).");
                }
            }, initialDelayMs + delayMs);
        }
    }

    private void BeginBombModeRound(int generation)
    {
        if (!IsCurrentGeneration(generation) || !_warmupActive || !_bombModeService.Enabled)
        {
            return;
        }

        List<Player> participants = Player.List.Where(IsManagedParticipant).ToList();
        if (!_dust2MapService.IsLoaded)
        {
            ApiLogger.Warn($"[{Name}] Bomb mode could not start because Dust2 is not loaded.");
            return;
        }

        if (!_bombModeService.TryStartRound(_dust2MapService, participants, out string response))
        {
            ApiLogger.Warn($"[{Name}] {response}");
            return;
        }

        ApiLogger.Info($"[{Name}] {response}");
        foreach (Player player in participants.Where(player => player.IsAlive))
        {
            player.SendHint(response, 4f);
        }

        ScheduleBombModeTick(_bombModeService.RoundToken, generation);
    }

    private void ScheduleBombModeTick(int bombRoundToken, int generation)
    {
        Schedule(() => RunBombModeTick(bombRoundToken, generation), 1000);
    }

    private void RunBombModeTick(int bombRoundToken, int generation)
    {
        if (!IsCurrentGeneration(generation)
            || !_warmupActive
            || !_bombModeService.RoundActive
            || _bombModeService.RoundToken != bombRoundToken)
        {
            return;
        }

        List<Player> participants = Player.List.Where(IsManagedParticipant).ToList();
        foreach (Player player in participants.Where(player => player.IsAlive))
        {
            player.SendHint(_bombModeService.BuildHud(player, participants), 1.1f);
        }

        BombRoundResult result = _bombModeService.Tick(participants);
        if (result == BombRoundResult.None)
        {
            ScheduleBombModeTick(bombRoundToken, generation);
            return;
        }

        if (result == BombRoundResult.Exploded)
        {
            _bombModeService.HandleExplosionKill(participants);
        }

        string resultText = _bombModeService.DescribeResult(result);
        if (!string.IsNullOrWhiteSpace(resultText))
        {
            foreach (Player player in participants)
            {
                SendNonUpdateBroadcast(player, resultText, 6);
            }
        }

        Schedule(() =>
        {
            if (IsCurrentGeneration(generation) && _warmupActive)
            {
                RestartWarmup("bomb mode round reset");
            }
        }, 6000);
    }

    private bool IsBombModeRoundActive()
    {
        return _bombModeService.RoundActive;
    }

    private void CancelBotBrainForRound(int playerId)
    {
        if (_managedBots.TryGetValue(playerId, out ManagedBotState? state))
        {
            state.SpawnSetupCompleted = false;
            state.BrainToken++;
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
        Config.BotBehavior.OrbitRetreatDistance = 6.3f;

        switch (preset)
        {
            case WarmupDifficulty.Easy:
                Config.BotBehavior.ThinkIntervalMinMs = 55;
                Config.BotBehavior.ThinkIntervalMaxMs = 95;
                Config.BotBehavior.MinShotIntervalMs = 260;
                Config.BotBehavior.ShootReleaseDelayMs = 80;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 14.4f;
                Config.BotBehavior.RangeTolerance = 3.6f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 2;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 1;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 2.0f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 1.4f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 4000;
                Config.BotBehavior.FarTargetRealisticAimSettleMs = 4000;
                Config.BotBehavior.RealisticReacquireDelayMs = 300;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Normal:
                Config.BotBehavior.ThinkIntervalMinMs = 55;
                Config.BotBehavior.ThinkIntervalMaxMs = 95;
                Config.BotBehavior.MinShotIntervalMs = 140;
                Config.BotBehavior.ShootReleaseDelayMs = 40;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 12.6f;
                Config.BotBehavior.RangeTolerance = 2.25f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 3;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 2;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 1.5f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 1.0f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 2500;
                Config.BotBehavior.FarTargetRealisticAimSettleMs = 2500;
                Config.BotBehavior.RealisticReacquireDelayMs = 250;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Hard:
                Config.BotBehavior.ThinkIntervalMinMs = 55;
                Config.BotBehavior.ThinkIntervalMaxMs = 95;
                Config.BotBehavior.MinShotIntervalMs = 55;
                Config.BotBehavior.ShootReleaseDelayMs = 12;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 9.9f;
                Config.BotBehavior.RangeTolerance = 0.9f;
                Config.BotBehavior.MaxHorizontalAimActionsPerTick = 3;
                Config.BotBehavior.MaxVerticalAimActionsPerTick = 2;
                Config.BotBehavior.HorizontalAimDeadzoneDegrees = 1.0f;
                Config.BotBehavior.VerticalAimDeadzoneDegrees = 0.75f;
                Config.BotBehavior.EnableAdaptiveCloseRangeStrafing = false;
                Config.BotBehavior.EnableAdaptiveCloseRangeRetreat = false;
                Config.BotBehavior.RetreatStartDistanceBuffer = 0f;
                Config.BotBehavior.RealisticAimSettleMs = 1400;
                Config.BotBehavior.FarTargetRealisticAimSettleMs = 1400;
                Config.BotBehavior.RealisticReacquireDelayMs = 180;
                Config.BotBehavior.RealisticInitialPitchOffsetMaxDegrees = 0f;
                break;
            case WarmupDifficulty.Hardest:
                Config.BotBehavior.ThinkIntervalMinMs = 55;
                Config.BotBehavior.ThinkIntervalMaxMs = 95;
                Config.BotBehavior.MinShotIntervalMs = 24;
                Config.BotBehavior.ShootReleaseDelayMs = 4;
                Config.BotBehavior.EnableStepMovement = true;
                Config.BotBehavior.PreferredRange = 7.65f;
                Config.BotBehavior.RangeTolerance = 0.36f;
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
                Config.BotBehavior.CloseRangeRetreatRepeatCount = 5;
                Config.BotBehavior.VeryCloseRangeRetreatRepeatCount = 10;
                Config.BotBehavior.RealisticAimSettleMs = 425;
                Config.BotBehavior.FarTargetRealisticAimSettleMs = 425;
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

            RemoveManagedBot(playerId);
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

internal sealed class AutoCleanupCommandSender : ICommandSender
{
    public static readonly AutoCleanupCommandSender Instance = new();

    private AutoCleanupCommandSender()
    {
    }

    public string LogName => "WarmupSandbox AutoCleanup";

    public void Respond(string message, bool success)
    {
    }
}
