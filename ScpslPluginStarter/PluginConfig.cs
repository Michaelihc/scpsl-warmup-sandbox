using System;
using InventorySystem.Items;
using PlayerRoles;

namespace ScpslPluginStarter;

public sealed class PluginConfig
{
    public bool AutoStartOnWaitingForPlayers { get; set; } = false;

    public bool AutoStartOnFirstPlayer { get; set; } = false;

    public bool AutoStartOnRoundStarted { get; set; } = true;

    public bool ForceRoundStartOnFirstPlayer { get; set; } = true;

    public bool SuppressRoundEnd { get; set; } = true;

    public bool BroadcastWarmupStatus { get; set; } = true;

    public bool EnableDebugLogging { get; set; } = true;

    public int BotCount { get; set; } = 6;

    public string BotNamePrefix { get; set; } = "WarmupBot";

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

    public WarmupDifficulty DifficultyPreset { get; set; } = WarmupDifficulty.Normal;

    public NamedLoadoutDefinition[] HumanLoadoutPresets { get; set; } = NamedLoadoutDefinition.CreateDefaultHumanPresets();

    public LoadoutDefinition HumanLoadout { get; set; } = LoadoutDefinition.CreateDefaultHuman();

    public LoadoutDefinition BotLoadout { get; set; } = LoadoutDefinition.CreateDefaultBot();

    public BotBehaviorDefinition BotBehavior { get; set; } = new();
}

public enum WarmupDifficulty
{
    Easy,
    Normal,
    Hard,
    Hardest,
}

public sealed class LoadoutDefinition
{
    public bool ClearInventory { get; set; } = true;

    public bool InfiniteReserveAmmo { get; set; } = true;

    public bool EquipFirstFirearm { get; set; } = true;

    public bool RefillActiveFirearmOnSpawn { get; set; } = true;

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

    public LoadoutDefinition Loadout { get; set; } = LoadoutDefinition.CreateDefaultHuman();

    public static NamedLoadoutDefinition[] CreateDefaultHumanPresets()
    {
        return new[]
        {
            new NamedLoadoutDefinition
            {
                Name = "Rifle",
                Description = "E11 rifle with armor and flash.",
                Loadout = LoadoutDefinition.CreateDefaultHuman(),
            },
            new NamedLoadoutDefinition
            {
                Name = "AK",
                Description = "AK pressure setup.",
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
                Description = "Crossvec close-range setup.",
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

public sealed class BotBehaviorDefinition
{
    public bool EnableCombatActions { get; set; } = true;

    public bool EnableStepMovement { get; set; } = true;

    public bool EnableVerticalAim { get; set; } = true;

    public float TargetAimHeightOffset { get; set; } = 1.1f;

    public float MaxVerticalAimDegrees { get; set; } = 25.0f;

    public bool RefillAmmoBetweenBursts { get; set; } = true;

    public bool KeepMagazineFilled { get; set; } = false;

    public bool UseZoomWhileShooting { get; set; } = false;

    public int ThinkIntervalMinMs { get; set; } = 450;

    public int ThinkIntervalMaxMs { get; set; } = 850;

    public int MinShotIntervalMs { get; set; } = 180;

    public int MinReloadAttemptIntervalMs { get; set; } = 450;

    public int ShootReleaseDelayMs { get; set; } = 40;

    public int DebugLogIntervalMs { get; set; } = 800;

    public int UnstuckDurationMs { get; set; } = 900;

    public int StuckTickThreshold { get; set; } = 2;

    public int LinearMoveTickThreshold { get; set; } = 3;

    public int RandomStrafeAfterLinearChancePercent { get; set; } = 85;

    public int StrafeDirectionChangeChancePercent { get; set; } = 35;

    public float PreferredRange { get; set; } = 14.0f;

    public float RangeTolerance { get; set; } = 2.5f;

    public float StuckDistanceThreshold { get; set; } = 0.45f;

    public float NearbyBotAvoidanceRadius { get; set; } = 1.35f;

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

    public string ZoomActionName { get; set; } = "Zoom->Click";
}
