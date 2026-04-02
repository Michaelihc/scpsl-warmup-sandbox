using System.Collections.Generic;
using PlayerRoles;

namespace ScpslPluginStarter;

internal sealed class ManagedBotState
{
    public ManagedBotState(int playerId, string nickname)
    {
        PlayerId = playerId;
        Nickname = nickname;
        Engagement = new BotEngagementState();
    }

    public int PlayerId { get; }

    public string Nickname { get; }

    public int BrainToken { get; set; }

    public BotEngagementState Engagement { get; }

    public bool SpawnSetupCompleted { get; set; }

    public RoleTypeId RespawnRole { get; set; } = RoleTypeId.None;

    public int StrafeDirection { get; set; } = 1;

    public int LastShotTick { get; set; }

    public int LastShotEventTick { get; set; }

    public int PendingShotVerificationTick { get; set; }

    public int PendingShotLoadedAmmo { get; set; } = -1;

    public int LastReloadAttemptTick { get; set; }

    public int LastDebugTick { get; set; }

    public int LastCombatDebugTick { get; set; }

    public int LastAimDebugTick { get; set; }

    public UnityEngine.Vector3 LastPosition { get; set; }

    public float LastDesiredYaw { get; set; }

    public float LastDesiredPitch { get; set; }

    public string LastAimMode { get; set; } = "none";

    public float LastAimSettleProgress { get; set; }

    public float LastAimYawOffset { get; set; }

    public float LastAimPitchOffset { get; set; }

    public bool LastPitchWasSanitized { get; set; }

    public float LastSanitizedPitch { get; set; }

    public bool VerticalAimDirectionInverted { get; set; }

    public bool LastVerticalAimRetriedInverted { get; set; }

    public UnityEngine.Vector3 LastEyeOrigin { get; set; }

    public UnityEngine.Vector3 LastBaseAimPoint { get; set; }

    public UnityEngine.Vector3 LastComputedAimPoint { get; set; }

    public UnityEngine.Vector3 LastTorsoAimPoint { get; set; }

    public UnityEngine.Vector3 LastHeadAimPoint { get; set; }

    public string LastHorizontalAimActions { get; set; } = "none";

    public string LastVerticalAimActions { get; set; } = "none";

    public float LastYawDelta { get; set; }

    public float LastPitchDelta { get; set; }

    public float LastRawPitchBeforeAim { get; set; }

    public float LastRawYawBeforeAim { get; set; }

    public string PreferredShootActionName { get; set; } = "";

    public string LastShotActionName { get; set; } = "";

    public int DryFireCount { get; set; }

    public bool LoggedShootActionCatalog { get; set; }

    public int StuckTicks { get; set; }

    public int UnstuckUntilTick { get; set; }

    public int ConsecutiveLinearMoves { get; set; }

    public List<UnityEngine.Vector3> NavigationWaypoints { get; } = new();

    public int NavigationWaypointIndex { get; set; }

    public UnityEngine.Vector3 LastNavigationTarget { get; set; }

    public int LastNavigationRecomputeTick { get; set; }

    public int NavigationPathFailedUntilTick { get; set; }

    public bool LastMoveUsedNavigation { get; set; }

    public string LastNavigationReason { get; set; } = "none";
}
