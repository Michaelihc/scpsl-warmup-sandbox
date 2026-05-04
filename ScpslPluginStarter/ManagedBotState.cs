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

    public int LastStateLogTick { get; set; }

    public UnityEngine.Vector3 LastPosition { get; set; }

    public string LastMoveIntentLabel { get; set; } = "none";

    public int LastMoveIntentTick { get; set; }

    public int ForwardStallSinceTick { get; set; }

    public int NextForwardJumpTick { get; set; }

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

    public string LastShotModuleName { get; set; } = "";

    public int DryFireCount { get; set; }

    public bool LoggedShootActionCatalog { get; set; }

    public bool ZoomHeld { get; set; }

    public int ZoomHeldTargetPlayerId { get; set; } = -1;

    public int LastZoomDebugTick { get; set; }

    public int StuckTicks { get; set; }

    public int UnstuckUntilTick { get; set; }

    public int ConsecutiveLinearMoves { get; set; }

    public List<UnityEngine.Vector3> NavigationWaypoints { get; } = new();

    public int NavigationWaypointIndex { get; set; }

    public UnityEngine.Vector3 LastNavigationTarget { get; set; }

    public int LastNavigationRecomputeTick { get; set; }

    public int NavigationPathFailedUntilTick { get; set; }

    public int LastNavigationStuckRepathTick { get; set; }

    public int NavigationStuckRecoveryCount { get; set; }

    public int LastRoomCenterTeleportTick { get; set; }

    public int NavMeshStuckNudgeLoopCount { get; set; }

    public int LastNavMeshStuckNudgeTick { get; set; }

    public bool HasSkippedStuckWaypoint { get; set; }

    public UnityEngine.Vector3 LastSkippedStuckWaypoint { get; set; }

    public int LastSkippedStuckWaypointTick { get; set; }

    public int LastDoorTeleportTick { get; set; }

    public UnityEngine.Vector3 LastRelevantDoorPosition { get; set; }

    public string LastRelevantDoorLabel { get; set; } = "";

    public int LastRelevantDoorTick { get; set; }

    public int LastLongRangeRoomTeleportTick { get; set; }

    public int LastDirectNavigationMoveTick { get; set; }

    public int LastOutOfBoundsRecoveryTick { get; set; }

    public bool HasDirectNavigationSafePosition { get; set; }

    public UnityEngine.Vector3 LastDirectNavigationSafePosition { get; set; }

    public bool LastMoveUsedNavigation { get; set; }

    public string LastNavigationReason { get; set; } = "none";

    public int LastNavigationDebugTick { get; set; }

    public int LastNavigationExecutionLogTick { get; set; }

    public string LastNavigationDebugSummary { get; set; } = "";

    public UnityEngine.GameObject? NavigationAgentObject { get; set; }

    public UnityEngine.AI.NavMeshAgent? NavigationAgent { get; set; }

    public void ResetNavigationRuntimeState()
    {
        NavigationWaypoints.Clear();
        NavigationWaypointIndex = 0;
        LastNavigationTarget = default;
        LastNavigationRecomputeTick = 0;
        NavigationPathFailedUntilTick = 0;
        LastNavigationStuckRepathTick = 0;
        NavigationStuckRecoveryCount = 0;
        LastRoomCenterTeleportTick = 0;
        NavMeshStuckNudgeLoopCount = 0;
        LastNavMeshStuckNudgeTick = 0;
        HasSkippedStuckWaypoint = false;
        LastSkippedStuckWaypoint = default;
        LastSkippedStuckWaypointTick = 0;
        LastDoorTeleportTick = 0;
        LastRelevantDoorPosition = default;
        LastRelevantDoorLabel = "";
        LastRelevantDoorTick = 0;
        LastLongRangeRoomTeleportTick = 0;
        LastDirectNavigationMoveTick = 0;
        LastOutOfBoundsRecoveryTick = 0;
        HasDirectNavigationSafePosition = false;
        LastDirectNavigationSafePosition = default;
        LastMoveUsedNavigation = false;
        LastNavigationReason = "none";
        LastNavigationDebugTick = 0;
        LastNavigationExecutionLogTick = 0;
        LastNavigationDebugSummary = "";
        HasPatrolTarget = false;
        PatrolTarget = default;
        PatrolTargetSetTick = 0;
        DestroyNavigationAgent();
    }

    public void DestroyNavigationAgent()
    {
        if (NavigationAgentObject != null)
        {
            UnityEngine.Object.Destroy(NavigationAgentObject);
        }

        NavigationAgentObject = null;
        NavigationAgent = null;
    }

    public bool HasStallAnchor { get; set; }

    public UnityEngine.Vector3 StallAnchorPosition { get; set; }

    public int StallAnchorSinceTick { get; set; }

    public bool AStarFallbackActive { get; set; }

    public string LastBlockedMoveLabel { get; set; } = "";

    public int BlockedMoveRepeatCount { get; set; }

    public int ForwardBlockedUntilTick { get; set; }

    public int BackBlockedUntilTick { get; set; }

    public int LeftBlockedUntilTick { get; set; }

    public int RightBlockedUntilTick { get; set; }

    public BotAiState AiState { get; set; } = BotAiState.Chase;

    public int AiStateEnteredTick { get; set; }

    public int OrbitDirection { get; set; } = 1;

    public int NextStrafeFlipTick { get; set; }

    public int ReactiveStrafeUntilTick { get; set; }

    public int ReactiveStrafeCooldownUntilTick { get; set; }

    public int CampUntilTick { get; set; }

    public int CampCooldownUntilTick { get; set; }

    public UnityEngine.Vector3 CampAimPoint { get; set; }

    public int TargetSwitchLockUntilTick { get; set; }

    public string LastStateSummary { get; set; } = "chase";

    public string LastTargetSummary { get; set; } = "none";

    public int VisibleCombatTargetPlayerId { get; set; } = -1;

    public int VisibleCombatTargetSinceTick { get; set; }

    public int CloseRetreatUntilTick { get; set; }

    public bool CloseRetreatActive { get; set; }

    public int LastCloseRetreatDirectTick { get; set; }

    public float LastCloseRetreatStepDistance { get; set; }

    public int LastCloseRetreatInputRepeatCount { get; set; }

    public bool HasPatrolTarget { get; set; }

    public UnityEngine.Vector3 PatrolTarget { get; set; }

    public int PatrolTargetSetTick { get; set; }

}
