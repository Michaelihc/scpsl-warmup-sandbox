using System;
using InventorySystem.Items;
using PlayerRoles;
using UnityEngine;

namespace ScpslPluginStarter;

public sealed class PluginConfig
{
    public bool AutoStartOnWaitingForPlayers { get; set; } = false;

    public bool AutoStartOnFirstPlayer { get; set; } = false;

    public bool AutoStartOnRoundStarted { get; set; } = true;

    public bool ForceRoundStartOnFirstPlayer { get; set; } = true;

    public bool SuppressRoundEnd { get; set; } = true;

    public bool BroadcastWarmupStatus { get; set; } = true;

    public bool BroadcastHelpReminder { get; set; } = true;

    public int HelpReminderIntervalSeconds { get; set; } = 45;

    public ushort HelpReminderDurationSeconds { get; set; } = 6;

    public string Language { get; set; } = "en";

    public bool EnableDebugLogging { get; set; } = false;

    public bool EnableVerboseBotLogging { get; set; } = false;

    public bool EnableAttachmentLogging { get; set; } = false;

    public bool EnableArenaLogging { get; set; } = false;

    public bool EnableZoomLogging { get; set; } = false;

    public bool EnableSpawnProtection { get; set; } = true;

    public int SpawnProtectionDurationMs { get; set; } = 3000;

    public bool AutoCleanupEnabled { get; set; } = true;

    public int AutoCleanupIntervalSeconds { get; set; } = 180;

    public int PlayerBotCountGlobalCooldownSeconds { get; set; } = 60;

    public int PlayerBotCountCooldownSeconds { get; set; } = 180;

    public int PlayerBotCountCooldownJitterSeconds { get; set; } = 60;

    public bool PlayerPanelEnabled { get; set; } = true;

    public int PlayerPanelUseWindowSeconds { get; set; } = 20;

    public int PlayerPanelGlobalCooldownSeconds { get; set; } = 60;

    public int PlayerPanelCooldownSeconds { get; set; } = 180;

    public int PlayerPanelCooldownJitterSeconds { get; set; } = 60;

    public int BotCount { get; set; } = 6;

    public int MaxBotCount { get; set; } = 30;

    public bool ResetBotCountWhenNoActivePlayers { get; set; } = true;

    public int NoActivePlayersBotCount { get; set; } = 5;

    public int NoActivePlayersBotResetDelayMs { get; set; } = 3000;

    public string BotNamePrefix { get; set; } = "WarmupBot";

    public bool RandomizeFirearmAttachments { get; set; } = true;

    public FirearmAttachmentRandomizationMode FirearmAttachmentRandomizationMode { get; set; } = FirearmAttachmentRandomizationMode.BotsOnly;

    public int InitialSetupDelayMs { get; set; } = 1000;

    public int JoinSetupDelayMs { get; set; } = 1200;

    public int HumanRespawnDelayMs { get; set; } = 1200;

    public int BotRespawnDelayMs { get; set; } = 2500;

    public int BotSpawnDelayMs { get; set; } = 5000;

    public int BotRoleAssignDelayMs { get; set; } = 1000;

    public int BotInitialActivationDelayMs { get; set; } = 700;

    public int BotActivationRetryDelayMs { get; set; } = 450;

    public int BotActivationMaxAttempts { get; set; } = 6;

    public RoleTypeId HumanRole { get; set; } = RoleTypeId.NtfPrivate;

    public RoleTypeId BotRole { get; set; } = RoleTypeId.ChaosRifleman;

    public bool UseBotRoleDefaultLoadout { get; set; } = true;

    public WarmupDifficulty DifficultyPreset { get; set; } = WarmupDifficulty.Normal;

    public NamedLoadoutDefinition[] HumanLoadoutPresets { get; set; } = NamedLoadoutDefinition.CreateDefaultHumanPresets();

    public LoadoutDefinition HumanLoadout { get; set; } = LoadoutDefinition.CreateDefaultHuman();

    public LoadoutDefinition BotLoadout { get; set; } = LoadoutDefinition.CreateDefaultBot();

    public Dust2MapConfig Dust2Map { get; set; } = new();

    public BotBehaviorDefinition BotBehavior { get; set; } = new();
}

public enum WarmupDifficulty
{
    Easy,
    Normal,
    Hard,
    Hardest,
}

public enum WarmupAiMode
{
    Classic,
    Realistic,
}

public enum FirearmAttachmentRandomizationMode
{
    BotsOnly,
    AllLoadouts,
}

public sealed class LoadoutDefinition
{
    public bool ClearInventory { get; set; } = true;

    public bool InfiniteReserveAmmo { get; set; } = true;

    public bool EquipFirstFirearm { get; set; } = true;

    public bool RefillActiveFirearmOnSpawn { get; set; } = true;

    public bool RandomizeFirearmAttachmentsOnSpawn { get; set; } = false;

    public ItemType[] Items { get; set; } = Array.Empty<ItemType>();

    public AmmoGrant[] Ammo { get; set; } = Array.Empty<AmmoGrant>();

    public static LoadoutDefinition CreateDefaultHuman()
    {
        return new LoadoutDefinition
        {
            Items = new[]
            {
                ItemType.GunE11SR,
                ItemType.ArmorCombat,
                ItemType.Medkit,
                ItemType.GrenadeFlash,
            },
            Ammo = new[]
            {
                new AmmoGrant { Type = ItemType.Ammo556x45, Amount = 240 },
            },
        };
    }

    public static LoadoutDefinition CreateDefaultBot()
    {
        return new LoadoutDefinition
        {
            RandomizeFirearmAttachmentsOnSpawn = true,
            Items = new[]
            {
                ItemType.GunCrossvec,
                ItemType.ArmorLight,
                ItemType.Medkit,
            },
            Ammo = new[]
            {
                new AmmoGrant { Type = ItemType.Ammo9x19, Amount = 240 },
            },
        };
    }
}

public sealed class NamedLoadoutDefinition
{
    public string Name { get; set; } = "Default";

    public string Description { get; set; } = "";

    public RoleTypeId Role { get; set; } = RoleTypeId.NtfPrivate;

    public bool UseRoleDefaultLoadout { get; set; }

    public LoadoutDefinition? Loadout { get; set; } = LoadoutDefinition.CreateDefaultHuman();

    public static NamedLoadoutDefinition[] CreateDefaultHumanPresets()
    {
        return new[]
        {
            new NamedLoadoutDefinition
            {
                Name = "NtfPrivate",
                Description = "Spawn as NTF Private with default class gear.",
                Role = RoleTypeId.NtfPrivate,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "NtfSergeant",
                Description = "Spawn as NTF Sergeant with default class gear.",
                Role = RoleTypeId.NtfSergeant,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "NtfCaptain",
                Description = "Spawn as NTF Captain with default class gear.",
                Role = RoleTypeId.NtfCaptain,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "Guard",
                Description = "Spawn as Facility Guard with default class gear.",
                Role = RoleTypeId.FacilityGuard,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "ChaosRepressor",
                Description = "Spawn as Chaos Repressor with default class gear.",
                Role = RoleTypeId.ChaosRepressor,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "CiInsurgent",
                Description = "Spawn as Chaos Insurgent with default class gear.",
                Role = RoleTypeId.ChaosConscript,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "ChaosMarauder",
                Description = "Spawn as Chaos Marauder with default class gear.",
                Role = RoleTypeId.ChaosMarauder,
                UseRoleDefaultLoadout = true,
                Loadout = null,
            },
            new NamedLoadoutDefinition
            {
                Name = "Rifle",
                Description = "NTF Private with E11 rifle kit.",
                Role = RoleTypeId.NtfPrivate,
                UseRoleDefaultLoadout = false,
                Loadout = LoadoutDefinition.CreateDefaultHuman(),
            },
            new NamedLoadoutDefinition
            {
                Name = "AK",
                Description = "NTF Private with AK pressure setup.",
                Role = RoleTypeId.NtfPrivate,
                UseRoleDefaultLoadout = false,
                Loadout = new LoadoutDefinition
                {
                    Items = new[]
                    {
                        ItemType.GunAK,
                        ItemType.ArmorCombat,
                        ItemType.Medkit,
                        ItemType.GrenadeFlash,
                    },
                    Ammo = new[]
                    {
                        new AmmoGrant { Type = ItemType.Ammo762x39, Amount = 180 },
                    },
                },
            },
            new NamedLoadoutDefinition
            {
                Name = "SMG",
                Description = "NTF Private with Crossvec close-range setup.",
                Role = RoleTypeId.NtfPrivate,
                UseRoleDefaultLoadout = false,
                Loadout = new LoadoutDefinition
                {
                    Items = new[]
                    {
                        ItemType.GunCrossvec,
                        ItemType.ArmorCombat,
                        ItemType.Medkit,
                        ItemType.GrenadeFlash,
                    },
                    Ammo = new[]
                    {
                        new AmmoGrant { Type = ItemType.Ammo9x19, Amount = 240 },
                    },
                },
            },
        };
    }
}

public sealed class AmmoGrant
{
    public ItemType Type { get; set; } = ItemType.Ammo556x45;

    public ushort Amount { get; set; } = 120;
}

public sealed class Dust2MapConfig
{
    public bool Enabled { get; set; }

    public string SchematicName { get; set; } = "de_dust2";

    public bool RuntimeNavMeshEnabled { get; set; } = true;

    public float RuntimeNavMeshAgentRadius { get; set; } = 0.18f;

    public float RuntimeNavMeshAgentHeight { get; set; } = 1.8f;

    public float RuntimeNavMeshAgentMaxSlope { get; set; } = 50f;

    public float RuntimeNavMeshAgentClimb { get; set; } = 3.0f;

    public bool RuntimeNavMeshUseRenderMeshes { get; set; } = true;

    public float RuntimeNavMeshSampleDistance { get; set; } = 3.0f;

    public float RuntimeNavMeshBoundsPadding { get; set; } = 6.0f;

    public float RuntimeNavMeshMinRegionArea { get; set; } = 0.5f;

    public bool VisualizeRuntimeNavMesh { get; set; } = true;

    public int RuntimeNavMeshMaxDebugEdges { get; set; } = 6000;

    public float RuntimeNavMeshDebugEdgeWidth { get; set; } = 0.035f;

    public float RuntimeNavMeshDebugHeightOffset { get; set; } = 0.06f;

    public SerializableVector3 Origin { get; set; } = new(0f, 30f, 30f);

    public SerializableVector3 Rotation { get; set; } = new(0f, 0f, 0f);

    public SerializableVector3 Scale { get; set; } = new(1f, 1f, 1f);

    public float HumanSpawnJitterRadius { get; set; } = 2f;

    public float BotSpawnJitterRadius { get; set; } = 2f;

    public bool RemoveWarmupWalls { get; set; } = true;

    public string[] HumanSpawnMarkerNames { get; set; } = { "Spawnpoint_Counter" };

    public string[] BotSpawnMarkerNames { get; set; } = { "Spawnpoint_Terrorist" };
}

public sealed class SerializableVector3
{
    public SerializableVector3()
    {
    }

    public SerializableVector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public Vector3 ToVector3()
    {
        return new Vector3(X, Y, Z);
    }
}

public sealed class BotBehaviorDefinition
{
    public WarmupAiMode AiMode { get; set; } = WarmupAiMode.Classic;

    public bool EnableCombatActions { get; set; } = true;

    public bool EnableStepMovement { get; set; } = true;

    public bool EnableObstacleNavigation { get; set; } = true;

    public bool UseFacilityNavMesh { get; set; } = false;

    public bool UseFacilitySurfaceNavMesh { get; set; } = true;

    public float FacilityNavMeshSampleDistance { get; set; } = 50.0f;

    public bool FacilityRuntimeNavMeshEnabled { get; set; } = false;

    public bool FacilitySurfaceRuntimeNavMeshEnabled { get; set; } = true;

    public float FacilityRuntimeNavMeshAgentRadius { get; set; } = 0.25f;

    public float FacilityRuntimeNavMeshAgentHeight { get; set; } = 1.8f;

    public float FacilityRuntimeNavMeshAgentMaxSlope { get; set; } = 65f;

    public float FacilityRuntimeNavMeshAgentClimb { get; set; } = 2.4f;

    public bool FacilityRuntimeNavMeshUseRenderMeshes { get; set; } = false;

    public bool FacilityRuntimeNavMeshUseRoomTemplates { get; set; } = true;

    public float FacilityRuntimeNavMeshBoundsPadding { get; set; } = 8.0f;

    public float FacilityRuntimeNavMeshMinRegionArea { get; set; } = 0.5f;

    public bool FacilityRuntimeNavMeshIgnoreDoors { get; set; } = true;

    public bool FacilityRuntimeNavMeshLogBuild { get; set; } = false;

    public bool FacilityRuntimeNavMeshCreateOffMeshLinks { get; set; } = false;

    public int FacilityRuntimeNavMeshMaxOffMeshLinks { get; set; } = 600;

    public float FacilityRuntimeNavMeshOffMeshLinkSearchRadius { get; set; } = 3.0f;

    public float FacilityRuntimeNavMeshOffMeshLinkMaxVerticalDelta { get; set; } = 2.0f;

    public float FacilityRuntimeNavMeshOffMeshLinkSampleSpacing { get; set; } = 2.0f;

    public float FacilityRuntimeNavMeshOffMeshLinkSampleDistance { get; set; } = 6.0f;

    public float FacilityRuntimeNavMeshOffMeshLinkWidth { get; set; } = 0.4f;

    public int FacilityRuntimeNavMeshOffMeshLinkCostModifier { get; set; } = 2;

    public bool VisualizeFacilityNavMesh { get; set; } = false;

    public int FacilityRuntimeNavMeshMaxDebugEdges { get; set; } = 12000;

    public float FacilityRuntimeNavMeshDebugEdgeWidth { get; set; } = 0.025f;

    public float FacilityRuntimeNavMeshDebugHeightOffset { get; set; } = 0.08f;

    public bool VisualizeFacilityNavMeshSamples { get; set; } = false;

    public int FacilityRuntimeNavMeshMaxDebugSamples { get; set; } = 2500;

    public float FacilityRuntimeNavMeshDebugSampleSpacing { get; set; } = 2.5f;

    public float FacilityRuntimeNavMeshDebugSampleRadius { get; set; } = 18.0f;

    public float FacilityRuntimeNavMeshDebugSampleDistance { get; set; } = 12.0f;

    public float FacilityRuntimeNavMeshDebugSampleSize { get; set; } = 0.3f;

    public bool VisualizeFacilityNavAgents { get; set; } = false;

    public float FacilityNavAgentDebugMarkerSize { get; set; } = 0.65f;

    public bool UseFacilityDummyFollowFallback { get; set; } = true;

    public bool FacilityNavMeshDirectPositionControl { get; set; } = false;

    public float FacilityNavMeshDirectPositionMaxStep { get; set; } = 2.5f;

    public float FacilityNavMeshDirectPositionVerticalOffset { get; set; } = 0.35f;

    public float FacilityNavMeshDirectPositionMaxDropPerStep { get; set; } = 1.4f;

    public float FacilityNavMeshDirectPositionBridgeDistance { get; set; } = 1.75f;

    public float FacilityDummyFollowMaxDistance { get; set; } = 1000.0f;

    public float FacilityDummyFollowMinDistance { get; set; } = 1.75f;

    public float FacilityDummyFollowSpeed { get; set; } = 8.0f;

    public float FacilityDummyFollowSpeedScp939 { get; set; } = 7.5f;

    public float FacilityDummyFollowSpeedScp3114 { get; set; } = 7.5f;

    public float FacilityDummyFollowSpeedScp049 { get; set; } = 7.5f;

    public float FacilityDummyFollowSpeedScp106 { get; set; } = 7.5f;

    public float FacilityDummyFollowDoorSlowSpeed { get; set; } = 3.5f;

    public bool EnableBotDoorOpening { get; set; } = true;

    public float BotDoorOpenRadius { get; set; } = 3.5f;

    public bool BotForceOpenUnlockedDoors { get; set; } = false;

    public bool BotWaitAtClosedDoors { get; set; } = true;

    public bool BotWaitAtClosedDoorsOnlyHcz { get; set; } = true;

    public float BotClosedDoorStopRadius { get; set; } = 2.75f;

    public bool EnableVerticalAim { get; set; } = true;

    public float TargetAimHeightOffset { get; set; } = 1.1f;

    public bool EnableGlobalVisionFallback { get; set; } = true;

    public float GlobalVisionMaxVerticalDelta { get; set; } = 25.0f;

    public int RealisticSightMemoryMs { get; set; } = 5000;

    public int RealisticReacquireDelayMs { get; set; } = 250;

    public float RealisticInitialYawOffsetMaxDegrees { get; set; } = 8.0f;

    public float RealisticInitialPitchOffsetMaxDegrees { get; set; } = 0f;

    public int RealisticAimSettleMs { get; set; } = 1300;

    public float RealisticReloadLockOffsetMaxDegrees { get; set; } = 0.75f;

    public float RealisticHeadAimHeightOffset { get; set; } = 1.45f;

    public bool RealisticLosDebugLogging { get; set; } = false;

    public float MaxVerticalAimDegrees { get; set; } = 60.0f;

    public bool EnableFarTargetAimAssist { get; set; } = true;

    public float FarTargetAimDistance { get; set; } = 20.0f;

    public int FarTargetMaxHorizontalAimActionsPerTick { get; set; } = 5;

    public int FarTargetMaxVerticalAimActionsPerTick { get; set; } = 4;

    public float FarTargetHorizontalAimDeadzoneDegrees { get; set; } = 0.5f;

    public float FarTargetVerticalAimDeadzoneDegrees { get; set; } = 0.35f;

    public int FarTargetRealisticAimSettleMs { get; set; } = 700;

    public bool RefillAmmoBetweenBursts { get; set; } = true;

    public bool KeepMagazineFilled { get; set; } = false;

    public bool UseZoomWhileShooting { get; set; } = false;

    public bool UseZoomForFarTargets { get; set; } = true;

    public float FarTargetZoomDistance { get; set; } = 20.0f;

    public float ScpAttackRange { get; set; } = 5.5f;

    public bool EnableOrbitMovement { get; set; } = true;

    public float OrbitRetreatDistance { get; set; } = 6.3f;

    public float OrbitRetreatBias { get; set; } = 1.35f;

    public int ThinkIntervalMinMs { get; set; } = 450;

    public int ThinkIntervalMaxMs { get; set; } = 850;

    public int MinShotIntervalMs { get; set; } = 140;

    public int MinReloadAttemptIntervalMs { get; set; } = 450;

    public int ShootReleaseDelayMs { get; set; } = 40;

    public int DebugLogIntervalMs { get; set; } = 800;

    public int UnstuckDurationMs { get; set; } = 900;

    public int ReactiveStrafeDurationMs { get; set; } = 2000;

    public int ReactiveStrafeCooldownMs { get; set; } = 7000;

    public int BotForwardMoveBurstCount { get; set; } = 2;

    public int DoorSlowForwardMoveBurstCount { get; set; } = 1;

    public float BotDoorSlowRadius { get; set; } = 4.5f;

    public int StuckTickThreshold { get; set; } = 2;

    public float NavWaypointReachDistance { get; set; } = 0.9f;

    public int NavRecomputeIntervalMs { get; set; } = 350;

    public int NavPathFailedCooldownMs { get; set; } = 350;

    public float NavProbeDistance { get; set; } = 2.5f;

    public int NavLateralProbeCount { get; set; } = 3;

    public float NavTargetMoveRecomputeDistance { get; set; } = 1.75f;

    public float NavMeshCornerMoveMaxStep { get; set; } = 0.75f;

    public float NavMeshForwardMoveMaxAngleDegrees { get; set; } = 50.0f;

    public bool NavMeshStuckNudgeEnabled { get; set; } = true;

    public float NavMeshStuckNudgeStep { get; set; } = 0.65f;

    public float NavMeshStuckNudgeMaxDistance { get; set; } = 8.0f;

    public bool NavMeshForwardClipEnabled { get; set; } = false;

    public float NavMeshForwardClipStep { get; set; } = 0.2f;

    public bool NavMeshRoomCenterTeleportEnabled { get; set; } = false;

    public int NavMeshRoomCenterTeleportRecoveryCount { get; set; } = 8;

    public int NavMeshRoomCenterTeleportCooldownMs { get; set; } = 4000;

    public float NavMeshRoomCenterTeleportSampleDistance { get; set; } = 6.0f;

    public bool NavMeshStuckDoorTeleportEnabled { get; set; } = true;

    public int NavMeshStuckDoorTeleportStuckMs { get; set; } = 15000;

    public int NavMeshStuckDoorTeleportCooldownMs { get; set; } = 7000;

    public float NavMeshStuckDoorTeleportSampleDistance { get; set; } = 4.0f;

    public bool NavMeshLocalDetourEnabled { get; set; } = false;

    public float NavMeshLocalDetourForwardDistance { get; set; } = 1.6f;

    public float NavMeshLocalDetourLateralDistance { get; set; } = 2.0f;

    public float NavMeshLocalDetourMaxWaypointDistance { get; set; } = 14.0f;

    public bool NavDebugLogging { get; set; } = false;

    public int NavigationExecutionLogIntervalMs { get; set; } = 750;

    public bool EnableAStarFallbackNavigation { get; set; } = true;

    public float AStarFallbackTriggerRadius { get; set; } = 5.0f;

    public int AStarFallbackTriggerMs { get; set; } = 2500;

    public float AStarGridStep { get; set; } = 1.75f;

    public float AStarSearchPadding { get; set; } = 8.0f;

    public int AStarMaxNodeCount { get; set; } = 4096;

    public int LinearMoveTickThreshold { get; set; } = 3;

    public int RandomStrafeAfterLinearChancePercent { get; set; } = 85;

    public int StrafeDirectionChangeChancePercent { get; set; } = 35;

    public bool EnableAdaptiveCloseRangeStrafing { get; set; } = false;

    public float CloseRangeStrafeDistance { get; set; } = 10.0f;

    public float VeryCloseRangeStrafeDistance { get; set; } = 6.0f;

    public int CloseRangeStrafeRepeatCount { get; set; } = 2;

    public int VeryCloseRangeStrafeRepeatCount { get; set; } = 3;

    public bool EnableAdaptiveCloseRangeRetreat { get; set; } = false;

    public float CloseRetreatSpeedScale { get; set; } = 0.92f;

    public float RetreatStartDistanceBuffer { get; set; } = 0.0f;

    public int CloseRangeRetreatRepeatCount { get; set; } = 4;

    public int VeryCloseRangeRetreatRepeatCount { get; set; } = 8;

    public bool EnableNoTargetPatrol { get; set; } = true;

    public float NoTargetPatrolMinDistance { get; set; } = 8.0f;

    public float NoTargetPatrolMaxDistance { get; set; } = 45.0f;

    public float NoTargetPatrolReachDistance { get; set; } = 2.5f;

    public int NoTargetPatrolRefreshMs { get; set; } = 12000;

    public float PreferredRange { get; set; } = 12.6f;

    public float RangeTolerance { get; set; } = 2.25f;

    public float StuckDistanceThreshold { get; set; } = 0.45f;

    public float NearbyBotAvoidanceRadius { get; set; } = 1.35f;

    public int ForwardStuckJumpThresholdMs { get; set; } = 3500;

    public int ForwardStuckJumpIntervalMs { get; set; } = 1000;

    public int ForwardStuckJumpBurstCount { get; set; } = 2;

    public int MaxHorizontalAimActionsPerTick { get; set; } = 3;

    public int MaxVerticalAimActionsPerTick { get; set; } = 2;

    public float HorizontalAimDeadzoneDegrees { get; set; } = 1.5f;

    public float VerticalAimDeadzoneDegrees { get; set; } = 1.0f;

    public string[] WalkForwardActionNames { get; set; } =
    {
        "Walk forward 1.5m",
        "Walk forward 0.5m",
        "Walk forward 0.2m",
        "Walk forward 0.05m",
    };

    public string[] WalkBackwardActionNames { get; set; } =
    {
        "Walk back 1.5m",
        "Walk back 0.5m",
        "Walk back 0.2m",
        "Walk back 0.05m",
    };

    public string[] WalkLeftActionNames { get; set; } =
    {
        "Walk left 1.5m",
        "Walk left 0.5m",
        "Walk left 0.2m",
        "Walk left 0.05m",
    };

    public string[] WalkRightActionNames { get; set; } =
    {
        "Walk right 1.5m",
        "Walk right 0.5m",
        "Walk right 0.2m",
        "Walk right 0.05m",
    };

    public string[] JumpActionNames { get; set; } =
    {
        "Jump",
    };

    public string[] LookHorizontalPositiveActionNames { get; set; } =
    {
        "CurrentHorizontal+180",
        "CurrentHorizontal+45",
        "CurrentHorizontal+10",
        "CurrentHorizontal+1",
    };

    public string[] LookHorizontalNegativeActionNames { get; set; } =
    {
        "CurrentHorizontal-180",
        "CurrentHorizontal-45",
        "CurrentHorizontal-10",
        "CurrentHorizontal-1",
    };

    public string[] LookVerticalPositiveActionNames { get; set; } =
    {
        "CurrentVertical+180",
        "CurrentVertical+45",
        "CurrentVertical+10",
        "CurrentVertical+1",
    };

    public string[] LookVerticalNegativeActionNames { get; set; } =
    {
        "CurrentVertical-180",
        "CurrentVertical-45",
        "CurrentVertical-10",
        "CurrentVertical-1",
    };

    public string ShootPressActionName { get; set; } = "Shoot->Click";

    public string ShootReleaseActionName { get; set; } = "Shoot->Release";

    public string AlternateShootPressActionName { get; set; } = "";

    public string ReloadActionName { get; set; } = "Reload->Click";

    public string ZoomActionName { get; set; } = "Zoom->Hold";

    public string ZoomReleaseActionName { get; set; } = "Zoom->Release";
}
