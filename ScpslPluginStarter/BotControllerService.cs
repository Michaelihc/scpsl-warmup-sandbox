using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using UnityEngine;
using UnityEngine.AI;

namespace ScpslPluginStarter;

internal sealed class BotControllerService
{
    private const int CampDurationMs = 30000;
    private const int CampCooldownMs = 30000;
    private const int MinimumForwardJumpIntervalMs = 1000;
    private const int RelevantDoorCacheMs = 1500;
    private const int SustainedStuckRoomTeleportMs = 10000;
    private const int SustainedStuckRoomTeleportCooldownMs = 10000;
    private const float ElevatedPropForwardTeleportDistance = 1.35f;
    private const float ChaseStrafeBias = 0.6f;
    private const float ReactiveChaseStrafeBias = 1.05f;
    private const float ScpForwardStrafeBias = 0.35f;
    private const float OrbitInwardBias = 0.12f;
    private const float OrbitDistanceScaleMin = 0.7f;
    private const float SeparationWeight = 1.1f;
    private const float CampExitDistanceBuffer = 0.1f;
    private const float ReactiveStrafeFlipMinMs = 45f;
    private const float ReactiveStrafeFlipMaxMs = 120f;
    private const float StrafeFlipMinMs = 120f;
    private const float StrafeFlipMaxMs = 320f;
    private const int StrafeBurstMin = 2;
    private const int StrafeBurstMax = 4;
    private const int ForwardRecoverySidestepMinMs = 450;
    private const int FallbackFireAfterVisibleMs = 900;
    private const int CloseRetreatLockMs = 450;
    private const int NavMeshStuckNudgeLoopWindowMs = 6000;
    private const int NavMeshStuckNudgeLoopEscalationCount = 4;

    private readonly BotNavigationService _navigationService = new();
    private readonly BotTargetingService _targetingService = new();
    private readonly BotCombatService _combatService = new();
    private readonly BotAimService _aimService = new();

    public void SetArenaWaypoints(IEnumerable<Vector3> waypoints)
    {
        _navigationService.SetArenaWaypoints(waypoints);
    }

    public void ClearArenaWaypoints()
    {
        _navigationService.ClearArenaWaypoints();
    }

    public int GetArenaWaypointCount()
    {
        return _navigationService.GetArenaWaypointCount();
    }

    public static int GetCampCooldownMs()
    {
        return CampCooldownMs;
    }

    public static int GetReactiveStrafeDurationMs(BotBehaviorDefinition behavior)
    {
        return Math.Max(0, behavior.ReactiveStrafeDurationMs);
    }

    public void TickBot(
        Player bot,
        ManagedBotState state,
        IReadOnlyCollection<Player> players,
        BotBehaviorDefinition behavior,
        System.Random random,
        bool useDust2Arena,
        bool useNavMesh,
        float navMeshSampleDistance,
        bool stopOnNavigationFailure,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, Player, int, int> tryShoot,
        Action<Player, ManagedBotState, FirearmItem> tryReload,
        Action<Player, FirearmItem?> maintainReserveAmmo,
        Action<Player, ManagedBotState, string> logNavDebug,
        Func<Player, ManagedBotState, Player?, bool, bool> updateFacilityFollower,
        Action<Player, ManagedBotState, BotTargetSelection?> updateZoomHold,
        int brainToken,
        int generation)
    {
        FirearmItem? firearm = _combatService.EnsureFirearmEquipped(bot);
        bool canUseScpAttack = BotCombatService.IsSupportedScpAttacker(bot.Role);
        BotTargetSelection? target = _targetingService.SelectTarget(bot, state, players, behavior, random);
        int nowTick = Environment.TickCount;
        bool shouldCloseRetreat = UpdateCloseRetreatState(bot, state, target, behavior, canUseScpAttack);
        bool closeRetreatLockActive = IsCloseRetreatLockActive(state, nowTick) || shouldCloseRetreat;
        if (shouldCloseRetreat)
        {
            closeRetreatLockActive = true;
        }

        if (!closeRetreatLockActive)
        {
            TryRecoverDirectNavMeshPosition(bot, state, behavior, useDust2Arena, nowTick, logNavDebug);
        }
        if (target == null)
        {
            UpdateTargetSummary(state, null);
            updateFacilityFollower(bot, state, null, false);
            updateZoomHold(bot, state, null);
            UpdateForwardProgressState(bot, state, behavior, nowTick);
            TriggerMovementStallJumpIfReady(bot, state, behavior, tryInvokeDummyActions, logNavDebug, nowTick);
            if (!closeRetreatLockActive)
            {
                MaybeForceNavigationRepathOnStuck(bot, state, behavior, allowFacilityDoorTeleport: !useDust2Arena, tryInvokeDummyActions, logNavDebug, nowTick);
            }
            RefreshStrafe(state, random, nowTick);
            if (!TryPatrolWithoutTarget(
                    bot,
                    state,
                    players,
                    behavior,
                    random,
                    useDust2Arena,
                    useNavMesh,
                    navMeshSampleDistance,
                    canUseScpAttack,
                    tryInvokeDummyActions,
                    tryInvokeDummyAction,
                    logNavDebug,
                    nowTick))
            {
                MaybeLogNavigationExecution(bot, state, null, behavior, useNavMesh, false, bot.Position, "no-target", logNavDebug, nowTick);
                StopWithoutTarget(state, nowTick);
            }

            if (firearm != null)
            {
                maintainReserveAmmo(bot, firearm);
                if (!firearm.IsReloadingOrUnloading && GetLoadedAmmo(firearm) <= 1)
                {
                    tryReload(bot, state, firearm);
                }
            }

            return;
        }

        UpdateForwardProgressState(bot, state, behavior, nowTick);
        TriggerMovementStallJumpIfReady(bot, state, behavior, tryInvokeDummyActions, logNavDebug, nowTick);
        if (!closeRetreatLockActive)
        {
            MaybeForceNavigationRepathOnStuck(bot, state, behavior, allowFacilityDoorTeleport: !useDust2Arena, tryInvokeDummyActions, logNavDebug, nowTick);
        }
        RefreshStrafe(state, random, nowTick);
        EvaluateState(bot, state, target, behavior, random, nowTick);
        if (state.AiState == BotAiState.Orbit
            && (!behavior.EnableOrbitMovement || canUseScpAttack))
        {
            state.AiState = BotAiState.Chase;
            state.AiStateEnteredTick = nowTick;
            state.LastStateSummary = "chase";
        }

        UpdateTargetSummary(state, target);
        UpdateVisibleCombatTargetWindow(state, target, nowTick);
        state.HasPatrolTarget = false;
        updateZoomHold(bot, state, target);
        if (TryTeleportToRandomRoomForFarTarget(bot, state, target, behavior, random, useDust2Arena, logNavDebug, nowTick))
        {
            return;
        }

        bool canMove = state.AiState != BotAiState.Camp;

        Vector3 moveGoal = ResolveMoveGoal(bot, state, target);
        Vector3 navTarget = moveGoal;
        bool hasActiveNavigation = false;
        if (canMove && !closeRetreatLockActive)
        {
            navTarget = useNavMesh
                ? _navigationService.ResolveMoveTarget(bot, state, moveGoal, players, behavior, useNavMesh, navMeshSampleDistance, useSwiftStyleNavMeshPath: !useDust2Arena, stopOnNavigationFailure, logNavDebug)
                : moveGoal;
            hasActiveNavigation = useNavMesh && _navigationService.HasActivePath(state);
        }
        else if (closeRetreatLockActive)
        {
            _navigationService.ForceRepath(state, "close-retreat-lock");
            hasActiveNavigation = false;
            navTarget = bot.Position;
        }

        if (state.AiState == BotAiState.Camp
            && hasActiveNavigation
            && target != null
            && !target.HasLineOfSight)
        {
            state.AiState = BotAiState.Chase;
            state.AiStateEnteredTick = nowTick;
            state.LastStateSummary = "chase";
        }

        BotBehaviorDefinition activeBehavior = ResolveActiveBehaviorProfile(state, target, behavior);

        float navDistance = hasActiveNavigation
            ? HorizontalDistance(bot.Position, navTarget)
            : 0f;
        bool useNavigationAim = canMove
            && hasActiveNavigation
            && ShouldAimAtNavigationTarget(bot, target, activeBehavior, navTarget, navDistance)
            && navDistance > Mathf.Max(behavior.NavWaypointReachDistance * 1.25f, 1.2f)
            && state.AiState != BotAiState.Camp;
        Vector3 aimPoint = ResolveAimPoint(state, target, navTarget, bot.Position, useNavigationAim);
        bool shouldAim = useNavigationAim || target != null || state.AiState == BotAiState.Camp;
        if (shouldAim)
        {
            if (useNavigationAim)
            {
                _aimService.AimAtPoint(bot, state, aimPoint, activeBehavior, tryInvokeDummyAction, NoAimLog);
            }
            else if (target != null)
            {
                _aimService.AimAt(bot, state, target, activeBehavior, tryInvokeDummyAction, NoAimLog, NoAimDebug);
            }
            else
            {
                _aimService.AimAtPoint(bot, state, aimPoint, activeBehavior, tryInvokeDummyAction, NoAimLog);
            }
        }

        bool aimAligned = shouldAim && _aimService.IsAimAligned(bot, state, activeBehavior);
        bool aimingAtCombatTarget = shouldAim && !useNavigationAim && target != null;
        bool hasLineOfSight = target?.HasLineOfSight == true;
        bool orbitCombatFire = state.AiState == BotAiState.Orbit
            && aimingAtCombatTarget
            && hasLineOfSight;
        bool fallbackFireAllowed = firearm != null
            && aimingAtCombatTarget
            && hasLineOfSight
            && state.VisibleCombatTargetSinceTick > 0
            && unchecked(nowTick - state.VisibleCombatTargetSinceTick) >= FallbackFireAfterVisibleMs;
        bool shouldUseFacilityFollower = canMove
            && !closeRetreatLockActive
            && !useDust2Arena
            && behavior.UseFacilityDummyFollowFallback
            && target != null
            && !hasLineOfSight
            && (!useNavMesh || !hasActiveNavigation);
        bool pausedAtDoor = canMove
            && !closeRetreatLockActive
            && TryHandleNearbyDoor(bot, state, behavior, navTarget, useDust2Arena, logNavDebug, nowTick);
        bool facilityFollowerActive = updateFacilityFollower(bot, state, target?.Target, shouldUseFacilityFollower && !pausedAtDoor);

        bool canAttackNow = false;
        string? canAttackReason = null;
        if (behavior.EnableCombatActions
            && (firearm != null || canUseScpAttack)
            && target != null
            && hasLineOfSight
            && aimingAtCombatTarget)
        {
            canAttackNow = _combatService.CanAttack(bot, state, target, activeBehavior, firearm, nowTick, out canAttackReason);
        }

        if (state.AiState != BotAiState.Camp && !facilityFollowerActive && !pausedAtDoor)
        {
            if (TryApplyDirectNavMeshPositionControl(
                    bot,
                    state,
                    behavior,
                    useDust2Arena,
                    navTarget,
                    hasActiveNavigation,
                    nowTick,
                    logNavDebug))
            {
            }
            else
            {
                MoveBot(bot, state, players, behavior, useDust2Arena, target, navTarget, hasActiveNavigation, tryInvokeDummyActions, random, nowTick, canUseScpAttack, logNavDebug);
            }
        }
        else
        {
            ClearMoveIntent(state);
        }

        if (firearm != null)
        {
            maintainReserveAmmo(bot, firearm);
            if (!firearm.IsReloadingOrUnloading && GetLoadedAmmo(firearm) <= 1)
            {
                tryReload(bot, state, firearm);
                return;
            }
        }

        if (behavior.EnableCombatActions
            && (firearm != null || canUseScpAttack)
            && target != null
            && hasLineOfSight
            && aimingAtCombatTarget
            && (aimAligned || orbitCombatFire || fallbackFireAllowed)
            && canAttackNow)
        {
            tryShoot(bot, state, target.Target, brainToken, generation);
        }
        else if (target != null && hasLineOfSight && aimingAtCombatTarget)
        {
            MaybeLogCombatHold(
                bot,
                state,
                target,
                behavior,
                firearm,
                canUseScpAttack,
                aimAligned,
                orbitCombatFire,
                fallbackFireAllowed,
                canAttackNow,
                canAttackReason,
                logNavDebug,
                nowTick);
        }
    }

    private bool TryPatrolWithoutTarget(
        Player bot,
        ManagedBotState state,
        IReadOnlyCollection<Player> players,
        BotBehaviorDefinition behavior,
        System.Random random,
        bool useDust2Arena,
        bool useNavMesh,
        float navMeshSampleDistance,
        bool allowForwardRecoveryJump,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (useDust2Arena || !behavior.EnableNoTargetPatrol)
        {
            state.HasPatrolTarget = false;
            return false;
        }

        if (state.AiState != BotAiState.Chase)
        {
            state.AiState = BotAiState.Chase;
            state.AiStateEnteredTick = nowTick;
        }

        state.LastStateSummary = "patrol";
        state.CampUntilTick = 0;
        state.ReactiveStrafeUntilTick = 0;

        if (!state.HasPatrolTarget
            || HorizontalDistance(bot.Position, state.PatrolTarget) <= Mathf.Max(0.5f, behavior.NoTargetPatrolReachDistance)
            || unchecked(nowTick - state.PatrolTargetSetTick) > Math.Max(1000, behavior.NoTargetPatrolRefreshMs)
            || state.StuckTicks >= Math.Max(2, behavior.StuckTickThreshold * 2))
        {
            if (!TrySelectPatrolTarget(bot.Position, behavior, random, out Vector3 patrolTarget))
            {
                state.HasPatrolTarget = false;
                return false;
            }

            state.HasPatrolTarget = true;
            state.PatrolTarget = patrolTarget;
            state.PatrolTargetSetTick = nowTick;
            logNavDebug(bot, state, $"patrol-target target={FormatVector(patrolTarget)} pos={FormatVector(bot.Position)}");
        }

        Vector3 moveGoal = state.PatrolTarget;
        Vector3 navTarget = moveGoal;
        bool hasActiveNavigation = false;
        if (useNavMesh)
        {
            navTarget = _navigationService.ResolveMoveTarget(
                bot,
                state,
                moveGoal,
                players,
                behavior,
                useRuntimeNavMesh: true,
                runtimeNavMeshSampleDistance: navMeshSampleDistance,
                useSwiftStyleNavMeshPath: !useDust2Arena,
                stopOnNavigationFailure: true,
                logNavDebug);
            hasActiveNavigation = _navigationService.HasActivePath(state);
            if (!hasActiveNavigation)
            {
                state.HasPatrolTarget = false;
                ClearMoveIntent(state);
                return false;
            }
        }

        if (TryHandleNearbyDoor(bot, state, behavior, navTarget, useDust2Arena, logNavDebug, nowTick))
        {
            return true;
        }

        _aimService.AimAtPoint(bot, state, navTarget, behavior, tryInvokeDummyAction, NoAimLog);

        if (TryApplyForwardRecoverySidestep(bot, state, behavior, tryInvokeDummyActions, nowTick))
        {
            return true;
        }

        Vector3 patrolDirection = navTarget - bot.Position;
        patrolDirection.y = 0f;
        patrolDirection += BuildSeparationDirection(bot, players, behavior.NearbyBotAvoidanceRadius) * SeparationWeight;
        float navDistance = hasActiveNavigation ? HorizontalDistance(bot.Position, navTarget) : 0f;
        float navStepDistance = hasActiveNavigation
            ? Mathf.Clamp(navDistance * 0.85f, 0.15f, behavior.NavMeshCornerMoveMaxStep)
            : float.PositiveInfinity;
        if (TryMoveTowardDirection(bot, patrolDirection, behavior, tryInvokeDummyActions, allowBackward: false, out string moveLabel, navStepDistance))
        {
            RecordMoveIntent(state, $"patrol-{moveLabel}", nowTick);
            MaybeTriggerForwardRecoveryJump(bot, state, behavior, tryInvokeDummyActions, nowTick, allowForwardRecoveryJump);
            if (hasActiveNavigation)
            {
                LogNavigationMove(bot, state, behavior, null, navTarget, navDistance, navStepDistance, moveLabel, "patrol-nav", logNavDebug, nowTick);
            }
            else
            {
                MaybeLogNavigationExecution(bot, state, null, behavior, false, false, moveGoal, "patrol", logNavDebug, nowTick);
            }
            return true;
        }

        ClearMoveIntent(state);
        return true;
    }

    private static bool TrySelectPatrolTarget(
        Vector3 botPosition,
        BotBehaviorDefinition behavior,
        System.Random random,
        out Vector3 patrolTarget)
    {
        patrolTarget = default;
        if (!TryGetClosestRoomZone(botPosition, out FacilityZone zone) || zone == FacilityZone.Surface)
        {
            return false;
        }

        float minDistance = Mathf.Max(0f, behavior.NoTargetPatrolMinDistance);
        float maxDistance = Mathf.Max(minDistance + 1f, behavior.NoTargetPatrolMaxDistance);
        bool found = false;
        float bestScore = float.PositiveInfinity;
        Vector3 bestTarget = default;

        if (Room.List != null)
        {
            foreach (Room room in Room.List)
            {
                if (room == null || room.IsDestroyed || room.Zone != zone)
                {
                    continue;
                }

                Vector3 roomPosition = room.Position;
                float verticalDelta = Mathf.Abs(roomPosition.y - botPosition.y);
                float distance = HorizontalDistance(botPosition, roomPosition);
                if (verticalDelta > 12f || distance < minDistance || distance > maxDistance)
                {
                    continue;
                }

                float score = Mathf.Abs(distance - ((minDistance + maxDistance) * 0.45f))
                    + (verticalDelta * 2f)
                    + (float)(random.NextDouble() * 6.0);
                if (score >= bestScore)
                {
                    continue;
                }

                found = true;
                bestScore = score;
                bestTarget = roomPosition;
            }
        }

        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.Zone != zone)
            {
                continue;
            }

            Vector3 doorPosition = door.Position;
            float verticalDelta = Mathf.Abs(doorPosition.y - botPosition.y);
            float distance = HorizontalDistance(botPosition, doorPosition);
            if (verticalDelta > 8f || distance < minDistance * 0.5f || distance > maxDistance)
            {
                continue;
            }

            float score = distance * 0.75f
                + (verticalDelta * 2f)
                + (float)(random.NextDouble() * 4.0);
            if (score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            bestTarget = doorPosition;
        }

        patrolTarget = bestTarget;
        return found;
    }

    private static bool TryHandleNearbyDoor(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Vector3 moveGoal,
        bool useDust2Arena,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (useDust2Arena || !behavior.EnableBotDoorOpening || Door.List == null)
        {
            return false;
        }

        float openRadius = Mathf.Max(0.5f, behavior.BotDoorOpenRadius);
        Door? door = FindRelevantNearbyDoor(bot.Position, moveGoal, openRadius);
        if (door == null || door.IsOpened || door.IsDestroyed)
        {
            ClearStaleRelevantDoor(state, nowTick);
            return false;
        }

        RememberRelevantDoor(state, door, nowTick);
        if (door.IsLocked && !behavior.BotForceOpenUnlockedDoors)
        {
            if (ShouldPauseAtClosedDoor(bot.Position, door, behavior))
            {
                state.LastMoveIntentLabel = "wait-door";
                state.LastMoveIntentTick = nowTick;
                logNavDebug(bot, state, $"door-wait locked={GetDoorLabel(door)} zone={door.Zone} pos={FormatVector(bot.Position)}");
                return true;
            }

            return false;
        }

        if (door.IsLocked)
        {
            door.IsLocked = false;
        }

        door.IsOpened = true;
        logNavDebug(bot, state, $"door-open door={GetDoorLabel(door)} zone={door.Zone} pos={FormatVector(door.Position)}");
        return false;
    }

    private static void RememberRelevantDoor(ManagedBotState state, Door door, int nowTick)
    {
        state.LastRelevantDoorPosition = door.Position;
        state.LastRelevantDoorLabel = GetDoorLabel(door);
        state.LastRelevantDoorTick = nowTick;
    }

    private static void ClearStaleRelevantDoor(ManagedBotState state, int nowTick)
    {
        if (state.LastRelevantDoorTick != 0
            && unchecked(nowTick - state.LastRelevantDoorTick) > RelevantDoorCacheMs)
        {
            state.LastRelevantDoorPosition = default;
            state.LastRelevantDoorLabel = "";
            state.LastRelevantDoorTick = 0;
        }
    }

    private static Door? FindRelevantNearbyDoor(Vector3 botPosition, Vector3 moveGoal, float radius)
    {
        Door? bestDoor = null;
        float bestScore = float.PositiveInfinity;
        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.IsOpened)
            {
                continue;
            }

            Vector3 doorPosition = door.Position;
            if (Mathf.Abs(doorPosition.y - botPosition.y) > 5.0f)
            {
                continue;
            }

            float distance = HorizontalDistance(botPosition, doorPosition);
            if (distance > radius)
            {
                continue;
            }

            float segmentDistance = DistancePointToSegment2D(doorPosition, botPosition, moveGoal);
            if (segmentDistance > radius * 0.75f)
            {
                continue;
            }

            float score = distance + (segmentDistance * 1.5f);
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestDoor = door;
        }

        return bestDoor;
    }

    private static bool ShouldPauseAtClosedDoor(Vector3 botPosition, Door door, BotBehaviorDefinition behavior)
    {
        if (!behavior.BotWaitAtClosedDoors)
        {
            return false;
        }

        if (behavior.BotWaitAtClosedDoorsOnlyHcz && door.Zone != FacilityZone.HeavyContainment)
        {
            return false;
        }

        return HorizontalDistance(botPosition, door.Position) <= Mathf.Max(0.5f, behavior.BotClosedDoorStopRadius);
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 start, Vector3 end)
    {
        return DistancePointToSegment2D(point, start, end, out _);
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 start, Vector3 end, out float t)
    {
        point.y = 0f;
        start.y = 0f;
        end.y = 0f;
        Vector3 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared < 0.001f)
        {
            t = 0f;
            return Vector3.Distance(point, start);
        }

        t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
        return Vector3.Distance(point, start + (segment * t));
    }

    private static string GetDoorLabel(Door door)
    {
        string nameTag = door.NameTag;
        if (!string.IsNullOrWhiteSpace(nameTag))
        {
            return nameTag;
        }

        string doorName = door.DoorName.ToString();
        return string.IsNullOrWhiteSpace(doorName) ? door.GetType().Name : doorName;
    }

    private void MoveBot(
        Player bot,
        ManagedBotState state,
        IReadOnlyCollection<Player> players,
        BotBehaviorDefinition behavior,
        bool useDust2Arena,
        BotTargetSelection? target,
        Vector3 navTarget,
        bool hasActiveNavigation,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        System.Random random,
        int nowTick,
        bool useScpForwardStrafe,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        bool humanCloseRetreat = !useScpForwardStrafe
            && target != null
            && state.CloseRetreatActive;
        if (humanCloseRetreat)
        {
            _navigationService.ForceRepath(state, "close-retreat-start");
            bool retreated = TryRetreatFromCloseTarget(bot, state, target!.Target.Position, behavior, tryInvokeDummyActions, nowTick);
            LogCloseRetreatDecision(bot, state, target, behavior, retreated, logNavDebug, nowTick);
            if (retreated)
            {
                ApplySafeStrafeBurst(bot, state, behavior, tryInvokeDummyActions, useScpForwardStrafe);
                return;
            }
        }

        if (state.AiState == BotAiState.Orbit && target?.HasLineOfSight == true)
        {
            bool retreatingFromCloseTarget = state.CloseRetreatActive || IsOrbitRetreatingFromCloseTarget(bot, target.Target.Position, behavior);
            if (retreatingFromCloseTarget
                && TryRetreatFromCloseTarget(bot, state, target.Target.Position, behavior, tryInvokeDummyActions, nowTick))
            {
                ApplySafeStrafeBurst(bot, state, behavior, tryInvokeDummyActions, useScpForwardStrafe);
                return;
            }

            Vector3 orbitDirection = BuildOrbitDirection(bot, state, target.Target.Position, players, behavior);
            if (TryMoveTowardDirection(bot, orbitDirection, behavior, tryInvokeDummyActions, allowBackward: retreatingFromCloseTarget, out string orbitMoveLabel))
            {
                RecordMoveIntent(state, orbitMoveLabel, nowTick);
                MaybeTriggerForwardRecoveryJump(bot, state, behavior, tryInvokeDummyActions, nowTick, useScpForwardStrafe);
            }
            else
            {
                ClearMoveIntent(state);
            }

            ApplyStrafeBurst(bot, state, behavior, tryInvokeDummyActions, random, useScpForwardStrafe);
            return;
        }

        Vector3 moveGoal = ResolveMoveGoal(bot, state, target);
        if (hasActiveNavigation)
        {
            Vector3 purePathDirection = navTarget - bot.Position;
            purePathDirection.y = 0f;
            float navDistance = HorizontalDistance(bot.Position, navTarget);
            float navStepDistance = Mathf.Clamp(navDistance * 0.85f, 0.15f, behavior.NavMeshCornerMoveMaxStep);
            if (TryMoveTowardDirection(bot, purePathDirection, behavior, tryInvokeDummyActions, allowBackward: false, out string pathMoveLabel, navStepDistance))
            {
                RecordMoveIntent(state, pathMoveLabel, nowTick);
                MaybeTriggerForwardRecoveryJump(bot, state, behavior, tryInvokeDummyActions, nowTick, useScpForwardStrafe);
                LogNavigationMove(bot, state, behavior, target, navTarget, navDistance, navStepDistance, pathMoveLabel, "nav-path", logNavDebug, nowTick);
                return;
            }

            MaybeLogNavigationExecution(bot, state, target, behavior, true, hasActiveNavigation, navTarget, "nav-aligning", logNavDebug, nowTick);
            ClearMoveIntent(state);
            return;
        }

        if (TryApplyForwardRecoverySidestep(bot, state, behavior, tryInvokeDummyActions, nowTick))
        {
            return;
        }

        if (useDust2Arena && target != null && !target.HasLineOfSight)
        {
            Vector3 hiddenTargetDirection = moveGoal - bot.Position;
            hiddenTargetDirection.y = 0f;
            if (!_navigationService.IsMoveDirectionClear(bot, hiddenTargetDirection, hiddenTargetDirection.magnitude, players))
            {
                ClearMoveIntent(state);
                return;
            }
        }

        bool applyChaseStrafe = ShouldApplyChaseStrafe(state, target, nowTick);
        Vector3 chaseDirection = BuildChaseDirection(bot, state, navTarget, players, behavior, applyChaseStrafe, useScpForwardStrafe);
        if (TryMoveTowardDirection(bot, chaseDirection, behavior, tryInvokeDummyActions, allowBackward: false, out string chaseMoveLabel))
        {
            RecordMoveIntent(state, chaseMoveLabel, nowTick);
            MaybeTriggerForwardRecoveryJump(bot, state, behavior, tryInvokeDummyActions, nowTick, useScpForwardStrafe);
            if (applyChaseStrafe)
            {
                ApplyStrafeBurst(bot, state, behavior, tryInvokeDummyActions, random, useScpForwardStrafe);
            }
            return;
        }

        Vector3 directDirection = moveGoal - bot.Position;
        directDirection.y = 0f;
        directDirection += BuildSeparationDirection(bot, players, behavior.NearbyBotAvoidanceRadius) * SeparationWeight;
        if (TryMoveTowardDirection(bot, directDirection, behavior, tryInvokeDummyActions, allowBackward: false, out string directMoveLabel))
        {
            RecordMoveIntent(state, directMoveLabel, nowTick);
            MaybeTriggerForwardRecoveryJump(bot, state, behavior, tryInvokeDummyActions, nowTick, useScpForwardStrafe);
            if (ShouldApplyChaseStrafe(state, target, nowTick))
            {
                ApplyStrafeBurst(bot, state, behavior, tryInvokeDummyActions, random, useScpForwardStrafe);
            }
            return;
        }

        ClearMoveIntent(state);
    }

    private void TryRecoverDirectNavMeshPosition(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        bool useDust2Arena,
        int nowTick,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (useDust2Arena || !behavior.FacilityNavMeshDirectPositionControl)
        {
            return;
        }

        if (state.NavigationAgent != null)
        {
            return;
        }

        float currentSampleDistance = Mathf.Clamp(behavior.FacilityNavMeshSampleDistance, 0.75f, 2.0f);
        float maxVerticalDrift = Mathf.Max(1.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        Vector3 botPosition = bot.Position;
        bool hasCurrentSample = NavMesh.SamplePosition(botPosition, out NavMeshHit currentHit, currentSampleDistance, NavMesh.AllAreas);
        if (hasCurrentSample && Mathf.Abs(currentHit.position.y - botPosition.y) <= maxVerticalDrift)
        {
            state.HasDirectNavigationSafePosition = true;
            state.LastDirectNavigationSafePosition = currentHit.position;
            return;
        }

        if (!state.HasDirectNavigationSafePosition)
        {
            if (hasCurrentSample)
            {
                bot.Position = currentHit.position;
                state.HasDirectNavigationSafePosition = true;
                state.LastDirectNavigationSafePosition = currentHit.position;
                state.LastDirectNavigationMoveTick = nowTick;
                _navigationService.ForceRepath(state, "direct-nav-bootstrap", currentHit.position);
                bot.Position = ApplyDirectNavPlayerOffset(currentHit.position, behavior);
                logNavDebug(
                    bot,
                    state,
                    $"direct-nav-recover reason=bootstrap pos=({botPosition.x:F1},{botPosition.y:F1},{botPosition.z:F1}) sample=({currentHit.position.x:F1},{currentHit.position.y:F1},{currentHit.position.z:F1})");
            }

            return;
        }

        Vector3 safePosition = state.LastDirectNavigationSafePosition;
        bot.Position = ApplyDirectNavPlayerOffset(safePosition, behavior);
        state.LastDirectNavigationMoveTick = nowTick;
        _navigationService.ForceRepath(state, "direct-nav-recover", safePosition);
        logNavDebug(
            bot,
            state,
            $"direct-nav-recover reason={(hasCurrentSample ? "vertical-drift" : "off-navmesh")} pos=({botPosition.x:F1},{botPosition.y:F1},{botPosition.z:F1}) safe=({safePosition.x:F1},{safePosition.y:F1},{safePosition.z:F1})");
    }

    private bool TryApplyDirectNavMeshPositionControl(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        bool useDust2Arena,
        Vector3 navTarget,
        bool hasActiveNavigation,
        int nowTick,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (useDust2Arena
            || !behavior.FacilityNavMeshDirectPositionControl
            || !hasActiveNavigation
            || state.NavigationAgent == null
            || !state.NavigationAgent.enabled
            || !state.NavigationAgent.isOnNavMesh
            || !state.LastNavigationReason.StartsWith("navmesh-agent", StringComparison.Ordinal))
        {
            return false;
        }

        state.LastMoveIntentLabel = "navmesh-agent-follow";
        state.LastMoveIntentTick = nowTick;
        state.LastNavigationReason = string.Equals(state.LastNavigationReason, "navmesh-agent-pending", StringComparison.Ordinal)
            ? state.LastNavigationReason
            : "navmesh-agent-follow";
        return true;
    }

    private static Vector3 ApplyDirectNavPlayerOffset(Vector3 navMeshPosition, BotBehaviorDefinition behavior)
    {
        return navMeshPosition + (Vector3.up * Mathf.Max(0f, behavior.FacilityNavMeshDirectPositionVerticalOffset));
    }

    private static Vector3 ResolveMoveGoal(Player bot, ManagedBotState state, BotTargetSelection? target)
    {
        if (target != null)
        {
            return target.Target.Position;
        }

        return bot.Position;
    }

    private static void EvaluateState(
        Player bot,
        ManagedBotState state,
        BotTargetSelection? target,
        BotBehaviorDefinition behavior,
        System.Random random,
        int nowTick)
    {
        BotAiState nextState;
        if (target?.HasLineOfSight == true)
        {
            state.CampAimPoint = target.AimPoint;
            bool isScpAttacker = BotCombatService.IsSupportedScpAttacker(bot.Role);
            if (!behavior.EnableOrbitMovement)
            {
                nextState = BotAiState.Chase;
            }
            else if (isScpAttacker)
            {
                nextState = BotAiState.Chase;
            }
            else
            {
                nextState = target.Distance > behavior.PreferredRange + behavior.RangeTolerance
                    ? BotAiState.Chase
                    : BotAiState.Orbit;
            }
        }
        else
        {
            nextState = BotAiState.Chase;
        }

        if (state.AiState == nextState)
        {
            state.LastStateSummary = nextState.ToString().ToLowerInvariant();
            return;
        }

        if (nextState == BotAiState.Chase)
        {
            state.CampCooldownUntilTick = nowTick + CampCooldownMs;
            state.CampUntilTick = 0;
        }

        state.AiState = nextState;
        state.AiStateEnteredTick = nowTick;
        state.LastStateSummary = nextState.ToString().ToLowerInvariant();
        if (nextState == BotAiState.Orbit)
        {
            state.OrbitDirection = random.Next(0, 2) == 0 ? -1 : 1;
        }
        else if (nextState == BotAiState.Camp)
        {
            state.CampUntilTick = nowTick + CampDurationMs;
            if (target != null)
            {
                state.CampAimPoint = target.AimPoint;
            }
        }
    }

    private static void UpdateVisibleCombatTargetWindow(ManagedBotState state, BotTargetSelection? target, int nowTick)
    {
        if (target?.HasLineOfSight != true)
        {
            state.VisibleCombatTargetPlayerId = -1;
            state.VisibleCombatTargetSinceTick = 0;
            return;
        }

        int targetId = target.Target.PlayerId;
        if (state.VisibleCombatTargetPlayerId != targetId)
        {
            state.VisibleCombatTargetPlayerId = targetId;
            state.VisibleCombatTargetSinceTick = nowTick;
        }
    }

    private static void UpdateTargetSummary(ManagedBotState state, BotTargetSelection? target)
    {
        if (target != null)
        {
            string mode = target.HasLineOfSight
                ? "visible"
                : (target.IsRememberedTarget ? "remembered" : (target.IsGlobalVisionTarget ? "global" : "hidden"));
            state.LastTargetSummary = $"{target.Target.Nickname}#{target.Target.PlayerId} {mode} d={target.Distance:F1}";
            return;
        }

        state.LastTargetSummary = "none";
    }

    private static Vector3 ResolveAimPoint(
        ManagedBotState state,
        BotTargetSelection? target,
        Vector3 navigationTarget,
        Vector3 fallback,
        bool useNavigationAim)
    {
        if (useNavigationAim)
        {
            return navigationTarget;
        }

        if (target != null)
        {
            return target.AimPoint;
        }

        if (state.AiState == BotAiState.Camp)
        {
            return state.CampAimPoint;
        }

        return fallback;
    }

    private static bool ShouldAimAtNavigationTarget(
        Player bot,
        BotTargetSelection? target,
        BotBehaviorDefinition activeBehavior,
        Vector3 navigationTarget,
        float navDistance)
    {
        if (target?.HasLineOfSight != true)
        {
            return true;
        }

        float effectiveRange = Mathf.Max(0.5f, activeBehavior.PreferredRange + activeBehavior.RangeTolerance);
        if (target.Distance <= effectiveRange)
        {
            return false;
        }

        Vector3 toNavigationTarget = navigationTarget - bot.Position;
        toNavigationTarget.y = 0f;
        if (toNavigationTarget.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Vector3 forward = Quaternion.Euler(0f, movementYaw, 0f) * Vector3.forward;
        float forwardDot = Vector3.Dot(toNavigationTarget.normalized, forward);
        float offAxisThreshold = Mathf.Cos(55f * Mathf.Deg2Rad);
        return navDistance > 2.5f && forwardDot < offAxisThreshold;
    }

    private static void RefreshStrafe(ManagedBotState state, System.Random random, int nowTick)
    {
        if (unchecked(state.NextStrafeFlipTick - nowTick) > 0)
        {
            return;
        }

        state.StrafeDirection = random.Next(0, 2) == 0 ? -1 : 1;
        bool reactiveStrafe = IsReactiveStrafeActive(state, nowTick);
        int minFlipMs = reactiveStrafe ? (int)ReactiveStrafeFlipMinMs : (int)StrafeFlipMinMs;
        int maxFlipMs = reactiveStrafe ? (int)ReactiveStrafeFlipMaxMs : (int)StrafeFlipMaxMs;
        state.NextStrafeFlipTick = nowTick + random.Next(minFlipMs, maxFlipMs);
    }

    private static Vector3 BuildChaseDirection(
        Player bot,
        ManagedBotState state,
        Vector3 moveGoal,
        IReadOnlyCollection<Player> players,
        BotBehaviorDefinition behavior,
        bool applyStrafeBias,
        bool useScpForwardStrafe)
    {
        Vector3 toGoal = moveGoal - bot.Position;
        toGoal.y = 0f;
        if (toGoal.sqrMagnitude < 0.0001f)
        {
            return Vector3.zero;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 right = yawRotation * Vector3.right;
        Vector3 separation = BuildSeparationDirection(bot, players, behavior.NearbyBotAvoidanceRadius);
        float chaseStrafeBias = useScpForwardStrafe
            ? ScpForwardStrafeBias
            : IsReactiveStrafeActive(state, Environment.TickCount)
            ? ReactiveChaseStrafeBias
            : ChaseStrafeBias;
        Vector3 desired = toGoal.normalized
            + (applyStrafeBias ? right * state.StrafeDirection * chaseStrafeBias : Vector3.zero)
            + (separation * SeparationWeight);
        desired.y = 0f;
        return desired.sqrMagnitude < 0.0001f ? toGoal.normalized : desired.normalized;
    }

    private static Vector3 BuildOrbitDirection(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        IReadOnlyCollection<Player> players,
        BotBehaviorDefinition behavior)
    {
        Vector3 toTarget = targetPosition - bot.Position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return Vector3.zero;
        }

        Vector3 radial = toTarget.normalized;
        Vector3 tangent = Vector3.Cross(Vector3.up, radial) * state.OrbitDirection;
        float orbitMinDistance = Mathf.Max(2f, behavior.PreferredRange * OrbitDistanceScaleMin);
        float currentDistance = toTarget.magnitude;
        float retreatDistance = Mathf.Max(0f, behavior.OrbitRetreatDistance);
        bool retreating = retreatDistance > 0.05f && currentDistance < retreatDistance;
        Vector3 distanceCorrection = retreating
            ? -radial * Mathf.Max(0.1f, behavior.OrbitRetreatBias)
            : currentDistance < orbitMinDistance
                ? Vector3.zero
                : radial * OrbitInwardBias;
        Vector3 radialStrafe = retreating ? Vector3.zero : radial * state.StrafeDirection * 0.22f;
        Vector3 separation = BuildSeparationDirection(bot, players, behavior.NearbyBotAvoidanceRadius);
        Vector3 desired = tangent
            + distanceCorrection
            + radialStrafe
            + (separation * SeparationWeight);
        desired.y = 0f;
        return desired.sqrMagnitude < 0.0001f ? tangent.normalized : desired.normalized;
    }

    private static bool IsOrbitRetreatingFromCloseTarget(Player bot, Vector3 targetPosition, BotBehaviorDefinition behavior)
    {
        float retreatDistance = Mathf.Max(0f, behavior.OrbitRetreatDistance);
        if (retreatDistance <= 0.05f)
        {
            return false;
        }

        return HorizontalDistance(bot.Position, targetPosition) < retreatDistance;
    }

    private static bool UpdateCloseRetreatState(
        Player bot,
        ManagedBotState state,
        BotTargetSelection? target,
        BotBehaviorDefinition behavior,
        bool canUseScpAttack)
    {
        if (canUseScpAttack || target == null)
        {
            state.CloseRetreatActive = false;
            state.LastCloseRetreatDirectTick = 0;
            return false;
        }

        float minOrbitDistance = Mathf.Max(0f, behavior.OrbitRetreatDistance);
        if (minOrbitDistance <= 0.05f)
        {
            state.CloseRetreatActive = false;
            state.LastCloseRetreatDirectTick = 0;
            return false;
        }

        float distance = HorizontalDistance(bot.Position, target.Target.Position);
        if (distance < minOrbitDistance)
        {
            state.CloseRetreatActive = true;
            return true;
        }

        if (state.CloseRetreatActive && distance < minOrbitDistance + Mathf.Max(0.35f, behavior.RangeTolerance))
        {
            return true;
        }

        state.CloseRetreatActive = false;
        state.LastCloseRetreatDirectTick = 0;
        return false;
    }

    private static bool IsCloseRetreatLockActive(ManagedBotState state, int nowTick)
    {
        return unchecked(state.CloseRetreatUntilTick - nowTick) > 0;
    }

    private static bool TryRetreatFromCloseTarget(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        int nowTick)
    {
        state.CloseRetreatUntilTick = nowTick + CloseRetreatLockMs;
        state.CloseRetreatActive = true;
        state.LastMoveUsedNavigation = false;
        state.NavigationWaypoints.Clear();
        state.NavigationWaypointIndex = 0;
        state.LastCloseRetreatInputRepeatCount = 0;
        if (TryApplyDirectCloseRetreatStep(bot, state, targetPosition, behavior, nowTick))
        {
            RecordMoveIntent(state, "direct-back", nowTick);
            state.LastNavigationReason = "close-retreat-direct";
            return true;
        }

        Vector3 away = bot.Position - targetPosition;
        away.y = 0f;
        return TryMoveTowardDirectionSafely(
            bot,
            away,
            behavior,
            tryInvokeDummyActions,
            allowBackward: true,
            out string fallbackMoveLabel)
            && RecordRetreatFallback(state, fallbackMoveLabel, nowTick);
    }

    private static bool TryApplyDirectCloseRetreatStep(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        int nowTick)
    {
        state.LastCloseRetreatStepDistance = 0f;
        Vector3 away = bot.Position - targetPosition;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
        {
            float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
            away = -(Quaternion.Euler(0f, movementYaw, 0f) * Vector3.forward);
            away.y = 0f;
        }

        if (away.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        away.Normalize();
        int elapsedMs = state.LastCloseRetreatDirectTick == 0
            ? Math.Max(25, behavior.ThinkIntervalMinMs)
            : unchecked(nowTick - state.LastCloseRetreatDirectTick);
        elapsedMs = Math.Max(25, Math.Min(Math.Max(250, behavior.ThinkIntervalMaxMs), elapsedMs));
        float speed = Mathf.Max(0.1f, behavior.FacilityDummyFollowSpeed);
        const float DiagonalMovementScale = 0.70710678f;
        float step = Mathf.Clamp(speed * (elapsedMs / 1000f) * DiagonalMovementScale * GetCloseRetreatSpeedScale(behavior), 0.08f, 0.75f);
        float verticalOffset = Mathf.Max(0f, behavior.FacilityNavMeshDirectPositionVerticalOffset);
        Vector3 currentProbe = bot.Position - (Vector3.up * verticalOffset);
        float sampleDistance = Mathf.Clamp(step + 0.75f, 1.25f, 3.5f);

        if (NavMesh.SamplePosition(currentProbe, out NavMeshHit currentHit, sampleDistance, NavMesh.AllAreas))
        {
            Vector3 candidate = currentHit.position + (away * step);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit stepHit, sampleDistance, NavMesh.AllAreas))
            {
                return false;
            }

            Vector3 navDelta = stepHit.position - currentHit.position;
            navDelta.y = 0f;
            if (navDelta.magnitude < 0.15f
                || navDelta.magnitude > step + 0.9f
                || Vector3.Dot(navDelta.normalized, away) < 0.35f
                || Mathf.Abs(stepHit.position.y - currentHit.position.y) > Mathf.Max(2.0f, behavior.FacilityRuntimeNavMeshAgentHeight + 0.5f))
            {
                return false;
            }

            if (NavMesh.Raycast(currentHit.position, stepHit.position, out _, NavMesh.AllAreas)
                || IsForwardClipBlocked(bot, bot.Position, away, navDelta.magnitude)
                || !HasStableFloorAfterStep(bot.Position, away, navDelta.magnitude, behavior))
            {
                return false;
            }

            bot.Position = ApplyDirectNavPlayerOffset(stepHit.position, behavior);
            state.HasDirectNavigationSafePosition = true;
            state.LastDirectNavigationSafePosition = stepHit.position;
            state.LastDirectNavigationMoveTick = nowTick;
            state.LastCloseRetreatDirectTick = nowTick;
            state.LastCloseRetreatStepDistance = navDelta.magnitude;
            state.LastPosition = bot.Position;
            return true;
        }

        Vector3 flatCandidate = bot.Position + (away * Mathf.Min(step, 1.0f));
        flatCandidate.y = bot.Position.y;
        float flatStep = Mathf.Min(step, 1.0f);
        if (IsForwardClipBlocked(bot, bot.Position, away, flatStep)
            || !HasStableFloorAfterStep(bot.Position, away, flatStep, behavior))
        {
            return false;
        }

        float flatStepDistance = HorizontalDistance(bot.Position, flatCandidate);
        bot.Position = flatCandidate;
        state.LastCloseRetreatStepDistance = flatStepDistance;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastCloseRetreatDirectTick = nowTick;
        state.LastPosition = bot.Position;
        return true;
    }

    private static bool TryMoveTowardDirectionSafely(
        Player bot,
        Vector3 desiredDirection,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        bool allowBackward,
        out string moveLabel)
    {
        moveLabel = "none";
        desiredDirection.y = 0f;
        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 forward = yawRotation * Vector3.forward;
        Vector3 right = yawRotation * Vector3.right;
        Vector3 normalized = desiredDirection.normalized;

        List<(float Score, string[] Actions, string Label, Vector3 Direction)> candidates = new()
        {
            (Vector3.Dot(normalized, forward), behavior.WalkForwardActionNames, "forward", forward),
            (Vector3.Dot(normalized, right), behavior.WalkRightActionNames, "right", right),
            (Vector3.Dot(normalized, -right), behavior.WalkLeftActionNames, "left", -right),
        };

        if (allowBackward)
        {
            candidates.Add((Vector3.Dot(normalized, -forward), behavior.WalkBackwardActionNames, "back", -forward));
        }

        foreach ((float score, string[] actions, string label, Vector3 direction) in candidates
            .OrderByDescending(candidate => candidate.Score))
        {
            if (score < 0.15f)
            {
                continue;
            }

            float clearStep = GetClearStepDistance(bot, direction, behavior);
            if (clearStep <= 0f)
            {
                continue;
            }

            string[] boundedActions = SelectWalkActionsForDistance(actions, clearStep * GetCloseRetreatSpeedScale(behavior));
            if (boundedActions.Length > 0 && tryInvokeDummyActions(bot, boundedActions))
            {
                moveLabel = label;
                return true;
            }
        }

        return false;
    }

    private static float GetCloseRetreatSpeedScale(BotBehaviorDefinition behavior)
    {
        return Mathf.Clamp(behavior.CloseRetreatSpeedScale, 0.6f, 1.0f);
    }

    private static float GetClearStepDistance(Player bot, Vector3 direction, BotBehaviorDefinition behavior)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return 0f;
        }

        float[] steps = { 1.5f, 0.5f, 0.2f, 0.05f };
        foreach (float step in steps)
        {
            if (!IsForwardClipBlocked(bot, bot.Position, direction, step)
                && HasStableFloorAfterStep(bot.Position, direction, step, behavior))
            {
                return step;
            }
        }

        return 0f;
    }

    private static bool HasStableFloorAfterStep(Vector3 position, Vector3 direction, float step, BotBehaviorDefinition behavior)
    {
        direction.y = 0f;
        if (step <= 0f || direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        Vector3 candidate = position + (direction.normalized * step);
        Vector3 probe = candidate + (Vector3.up * 1.35f);
        if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 2.75f, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (hit.normal.y < 0.55f)
        {
            return false;
        }

        float maxFloorDelta = Mathf.Max(0.85f, behavior.FacilityRuntimeNavMeshAgentClimb + 0.35f);
        return Mathf.Abs(hit.point.y - position.y) <= maxFloorDelta;
    }

    private static int GetCloseRetreatRepeatCount(Vector3 botPosition, Vector3 targetPosition, BotBehaviorDefinition behavior)
    {
        float distance = HorizontalDistance(botPosition, targetPosition);
        float veryCloseDistance = Mathf.Max(0.5f, behavior.VeryCloseRangeStrafeDistance);
        int repeatCount = distance <= veryCloseDistance
            ? behavior.VeryCloseRangeRetreatRepeatCount
            : behavior.CloseRangeRetreatRepeatCount;
        return Math.Max(1, Math.Min(12, repeatCount));
    }

    private static bool RecordRetreatFallback(ManagedBotState state, string moveLabel, int nowTick)
    {
        RecordMoveIntent(state, string.IsNullOrWhiteSpace(moveLabel) ? "retreat" : moveLabel, nowTick);
        return true;
    }

    private static Vector3 BuildSeparationDirection(Player bot, IReadOnlyCollection<Player> players, float radius)
    {
        if (radius <= 0.05f)
        {
            return Vector3.zero;
        }

        Vector3 separation = Vector3.zero;
        foreach (Player other in players)
        {
            if (other == null
                || other.PlayerId == bot.PlayerId
                || other.IsDestroyed
                || other.Role == RoleTypeId.Spectator
                || other.Team != bot.Team)
            {
                continue;
            }

            Vector3 away = bot.Position - other.Position;
            away.y = 0f;
            float distance = away.magnitude;
            if (distance < 0.01f || distance > radius)
            {
                continue;
            }

            float weight = 1f - (distance / radius);
            separation += away.normalized * weight;
        }

        separation.y = 0f;
        return separation.sqrMagnitude < 0.0001f ? Vector3.zero : separation.normalized;
    }

    private static Vector3 BuildStrafeDirection(Player bot, ManagedBotState state)
    {
        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        return (yawRotation * Vector3.right) * state.StrafeDirection;
    }

    private static float GetNavigationStrafeWeight(ManagedBotState state, int nowTick, bool useScpForwardStrafe)
    {
        if (useScpForwardStrafe)
        {
            return ScpForwardStrafeBias;
        }

        return IsReactiveStrafeActive(state, nowTick) ? ReactiveChaseStrafeBias : ChaseStrafeBias;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static void LogNavigationMove(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        BotTargetSelection? target,
        Vector3 navTarget,
        float navDistance,
        float navStepDistance,
        string moveLabel,
        string phase,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!ShouldLogNavigationExecution(state, behavior, nowTick))
        {
            return;
        }

        logNavDebug(
            bot,
            state,
            BuildNavigationExecutionSummary(bot, state, target, true, true, navTarget, phase) +
            $" move={moveLabel} navDist={navDistance:F2} stepCap={navStepDistance:F2}");
    }

    private static void MaybeLogNavigationExecution(
        Player bot,
        ManagedBotState state,
        BotTargetSelection? target,
        BotBehaviorDefinition behavior,
        bool useNavMesh,
        bool hasActiveNavigation,
        Vector3 navTarget,
        string phase,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!ShouldLogNavigationExecution(state, behavior, nowTick))
        {
            return;
        }

        logNavDebug(
            bot,
            state,
            BuildNavigationExecutionSummary(bot, state, target, useNavMesh, hasActiveNavigation, navTarget, phase));
    }

    private static void LogCloseRetreatDecision(
        Player bot,
        ManagedBotState state,
        BotTargetSelection target,
        BotBehaviorDefinition behavior,
        bool retreated,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!ShouldLogNavigationExecution(state, behavior, nowTick))
        {
            return;
        }

        logNavDebug(
            bot,
            state,
            $"close-retreat retreated={retreated} ai={state.AiState} target={target.Target.Nickname}#{target.Target.PlayerId} " +
            $"dist={target.Distance:F1} minOrbit={behavior.OrbitRetreatDistance:F1} maxOrbit={behavior.PreferredRange:F1} active={state.CloseRetreatActive} move={state.LastMoveIntentLabel} " +
            $"directStep={state.LastCloseRetreatStepDistance:F2} repeat={state.LastCloseRetreatInputRepeatCount} reason={state.LastNavigationReason} " +
            $"actions=[{string.Join(",", behavior.WalkBackwardActionNames ?? Array.Empty<string>())}]");
    }

    private static void MaybeLogCombatHold(
        Player bot,
        ManagedBotState state,
        BotTargetSelection target,
        BotBehaviorDefinition behavior,
        FirearmItem? firearm,
        bool canUseScpAttack,
        bool aimAligned,
        bool orbitCombatFire,
        bool fallbackFireAllowed,
        bool canAttackNow,
        string? canAttackReason,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!ShouldLogNavigationExecution(state, behavior, nowTick))
        {
            return;
        }

        string weapon = firearm != null ? firearm.Type.ToString() : (canUseScpAttack ? bot.Role.ToString() : "none");
        logNavDebug(
            bot,
            state,
            $"combat-hold target={target.Target.Nickname}#{target.Target.PlayerId} dist={target.Distance:F1} " +
            $"weapon={weapon} aimAligned={aimAligned} orbitFire={orbitCombatFire} fallbackFire={fallbackFireAllowed} " +
            $"canAttack={canAttackNow} reason={canAttackReason ?? "none"} loaded={(firearm != null ? GetLoadedAmmo(firearm).ToString() : "na")} " +
            $"aimMode={state.LastAimMode} yawDelta={state.LastYawDelta:F1} pitchDelta={state.LastPitchDelta:F1}");
    }

    private static bool ShouldLogNavigationExecution(ManagedBotState state, BotBehaviorDefinition behavior, int nowTick)
    {
        int interval = Math.Max(250, behavior.NavigationExecutionLogIntervalMs);
        if (unchecked(nowTick - state.LastNavigationExecutionLogTick) < interval)
        {
            return false;
        }

        state.LastNavigationExecutionLogTick = nowTick;
        return true;
    }

    private static string BuildNavigationExecutionSummary(
        Player bot,
        ManagedBotState state,
        BotTargetSelection? target,
        bool useNavMesh,
        bool hasActiveNavigation,
        Vector3 navTarget,
        string phase)
    {
        Vector3 botPosition = bot.Position;
        string targetSummary = target == null
            ? "none"
            : $"{target.Target.Nickname}#{target.Target.PlayerId} los={target.HasLineOfSight} dist={target.Distance:F1}";
        int waypointCount = state.NavigationWaypoints.Count;
        int waypointIndex = state.NavigationWaypointIndex;
        Vector3 waypoint = waypointIndex >= 0 && waypointIndex < waypointCount
            ? state.NavigationWaypoints[waypointIndex]
            : navTarget;
        float waypointDistance = HorizontalDistance(botPosition, waypoint);
        float segmentDrift = CalculateNavigationSegmentDrift(botPosition, state);

        return
            $"exec phase={phase} ai={state.AiState} target={targetSummary} useNav={useNavMesh} active={hasActiveNavigation} " +
            $"reason={state.LastNavigationReason} pos={FormatVector(botPosition)} navTarget={FormatVector(navTarget)} " +
            $"wp={waypointIndex + 1}/{waypointCount} wpPos={FormatVector(waypoint)} wpDist={waypointDistance:F2} " +
            $"segDrift={segmentDrift:F2} lastMove={state.LastMoveIntentLabel} stuck={state.StuckTicks}";
    }

    private static float CalculateNavigationSegmentDrift(Vector3 position, ManagedBotState state)
    {
        int count = state.NavigationWaypoints.Count;
        int index = state.NavigationWaypointIndex;
        if (count == 0 || index < 0 || index >= count)
        {
            return 0f;
        }

        Vector3 end = state.NavigationWaypoints[index];
        Vector3 start = index > 0 ? state.NavigationWaypoints[index - 1] : state.LastPosition;
        start.y = 0f;
        end.y = 0f;
        position.y = 0f;
        Vector3 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared < 0.001f)
        {
            return Vector3.Distance(position, end);
        }

        float t = Mathf.Clamp01(Vector3.Dot(position - start, segment) / lengthSquared);
        Vector3 closest = start + (segment * t);
        return Vector3.Distance(position, closest);
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }

    private static void UpdateForwardProgressState(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        int nowTick)
    {
        Vector3 current = bot.Position;
        Vector3 previous = state.LastPosition;
        current.y = 0f;
        previous.y = 0f;
        float movedDistance = Vector3.Distance(current, previous);
        bool hasMoveIntent = !string.IsNullOrWhiteSpace(state.LastMoveIntentLabel)
            && !string.Equals(state.LastMoveIntentLabel, "none", StringComparison.Ordinal);
        bool recentMoveIntent = hasMoveIntent
            && unchecked(nowTick - state.LastMoveIntentTick) <= Math.Max(behavior.ThinkIntervalMaxMs * 2, 1000);

        if (recentMoveIntent)
        {
            float threshold = Mathf.Max(0.05f, behavior.StuckDistanceThreshold);
            if (!state.HasStallAnchor)
            {
                state.HasStallAnchor = true;
                state.StallAnchorPosition = bot.Position;
                state.StallAnchorSinceTick = nowTick;
            }

            Vector3 anchor = state.StallAnchorPosition;
            anchor.y = 0f;
            float anchorDistance = Vector3.Distance(current, anchor);
            if (movedDistance < threshold && anchorDistance < threshold)
            {
                state.StuckTicks++;
                if (state.ForwardStallSinceTick == 0)
                {
                    state.ForwardStallSinceTick = nowTick;
                }
            }
            else
            {
                state.StuckTicks = 0;
                state.ForwardStallSinceTick = 0;
                state.NavigationStuckRecoveryCount = 0;
                state.StallAnchorPosition = bot.Position;
                state.StallAnchorSinceTick = nowTick;
            }
        }
        else
        {
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.HasStallAnchor = false;
            state.StallAnchorSinceTick = 0;
        }

        state.LastPosition = bot.Position;
    }

    private void MaybeForceNavigationRepathOnStuck(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        bool allowFacilityDoorTeleport,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (state.StuckTicks < Math.Max(1, behavior.StuckTickThreshold)
            || unchecked(nowTick - state.LastNavigationStuckRepathTick) < Math.Max(500, behavior.NavRecomputeIntervalMs * 2))
        {
            return;
        }

        state.LastNavigationStuckRepathTick = nowTick;
        state.NavigationStuckRecoveryCount++;
        TriggerStuckJumpIfReady(bot, state, behavior, tryInvokeDummyActions, logNavDebug, nowTick);

        bool repeatedNudgeLoop = IsRecentNavMeshStuckNudgeLoop(state, nowTick);
        if (repeatedNudgeLoop)
        {
            state.NavigationStuckRecoveryCount = Math.Max(
                state.NavigationStuckRecoveryCount,
                Math.Max(1, behavior.NavMeshRoomCenterTeleportRecoveryCount));
        }

        state.ForwardBlockedUntilTick = 0;
        state.LeftBlockedUntilTick = 0;
        state.RightBlockedUntilTick = 0;
        if (TryTeleportToRandomRoomInSameZoneAfterSustainedStuck(bot, state, behavior, logNavDebug, nowTick))
        {
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.NavigationStuckRecoveryCount = 0;
            ResetNavMeshStuckNudgeLoop(state);
            state.LastPosition = bot.Position;
            return;
        }

        if (allowFacilityDoorTeleport && TryTeleportToRandomDoorInSameZone(bot, state, behavior, logNavDebug, nowTick))
        {
            logNavDebug(
                bot,
                state,
                $"stuck-door-teleport ticks={state.StuckTicks} recoveries={state.NavigationStuckRecoveryCount} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.NavigationStuckRecoveryCount = 0;
            ResetNavMeshStuckNudgeLoop(state);
            state.LastPosition = bot.Position;
            return;
        }

        if (!state.LastMoveUsedNavigation)
        {
            logNavDebug(
                bot,
                state,
                $"stuck-no-nav ticks={state.StuckTicks} move={state.LastMoveIntentLabel} stallMs={GetForwardStallMs(state, nowTick)} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            return;
        }

        if (TryRecoverFromElevatedPropStuck(bot, state, behavior, logNavDebug, nowTick))
        {
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.NavigationStuckRecoveryCount = 0;
            ResetNavMeshStuckNudgeLoop(state);
            state.LastPosition = bot.Position;
            return;
        }

        if (TryTeleportToClosestRoomCenter(bot, state, behavior, logNavDebug, nowTick))
        {
            logNavDebug(
                bot,
                state,
                $"stuck-room-teleport ticks={state.StuckTicks} recoveries={state.NavigationStuckRecoveryCount} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.NavigationStuckRecoveryCount = 0;
            ResetNavMeshStuckNudgeLoop(state);
            state.LastPosition = bot.Position;
            return;
        }

        if (repeatedNudgeLoop)
        {
            logNavDebug(
                bot,
                state,
                $"stuck-nudge-escalate count={state.NavMeshStuckNudgeLoopCount} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
        }

        if (!repeatedNudgeLoop && TryApplyNavMeshStuckNudge(bot, state, behavior, logNavDebug, nowTick))
        {
            logNavDebug(
                bot,
                state,
                $"stuck-nudge ticks={state.StuckTicks} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            state.LastPosition = bot.Position;
            return;
        }

        if (_navigationService.TryInsertLocalDetourWaypoint(bot, state, behavior, logNavDebug))
        {
            logNavDebug(
                bot,
                state,
                $"stuck-detour ticks={state.StuckTicks} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            state.ForwardStallSinceTick = 0;
            ResetNavMeshStuckNudgeLoop(state);
            return;
        }

        if (_navigationService.TrySoftAdvanceStuckWaypoint(bot, state, behavior, logNavDebug))
        {
            logNavDebug(
                bot,
                state,
                $"stuck-advance ticks={state.StuckTicks} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            state.StuckTicks = 0;
            ResetNavMeshStuckNudgeLoop(state);
            return;
        }

        _navigationService.ForceRepath(state, "stuck-repath");
        ResetNavMeshStuckNudgeLoop(state);
        logNavDebug(
            bot,
            state,
            $"stuck-repath ticks={state.StuckTicks} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
        state.StuckTicks = 0;
    }

    private static int GetForwardStallMs(ManagedBotState state, int nowTick)
    {
        return state.ForwardStallSinceTick == 0
            ? 0
            : Math.Max(0, unchecked(nowTick - state.ForwardStallSinceTick));
    }

    private static bool IsRecentNavMeshStuckNudgeLoop(ManagedBotState state, int nowTick)
    {
        return state.NavMeshStuckNudgeLoopCount >= NavMeshStuckNudgeLoopEscalationCount
            && unchecked(nowTick - state.LastNavMeshStuckNudgeTick) <= NavMeshStuckNudgeLoopWindowMs;
    }

    private static void ResetNavMeshStuckNudgeLoop(ManagedBotState state)
    {
        state.NavMeshStuckNudgeLoopCount = 0;
        state.LastNavMeshStuckNudgeTick = 0;
    }

    private static bool IsForwardClipBlocked(Player bot, Vector3 position, Vector3 direction, float step)
    {
        if (direction.sqrMagnitude < 0.0001f)
        {
            return true;
        }

        Vector3 normalized = direction.normalized;
        Vector3[] offsets =
        {
            Vector3.up * 0.35f,
            Vector3.up * 0.95f,
            Vector3.up * 1.45f,
        };

        foreach (Vector3 offset in offsets)
        {
            RaycastHit[] hits = Physics.SphereCastAll(
                position + offset,
                0.18f,
                normalized,
                step + 0.08f,
                ~0,
                QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
            foreach (RaycastHit hit in hits)
            {
                Transform hitTransform = hit.transform;
                if (hitTransform == null || IsPlayerOwnedTransform(bot, hitTransform))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private void TryResetPathAfterTeleport(ManagedBotState state, Vector3 navPosition)
    {
        _navigationService.ForceRepath(state, "teleport-recover", navPosition);
    }

    private bool TryTeleportToRandomDoorInSameZone(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!behavior.NavMeshStuckDoorTeleportEnabled
            || state.ForwardStallSinceTick == 0
            || unchecked(nowTick - state.ForwardStallSinceTick) < Math.Max(1000, behavior.NavMeshStuckDoorTeleportStuckMs)
            || unchecked(nowTick - state.LastDoorTeleportTick) < Math.Max(1000, behavior.NavMeshStuckDoorTeleportCooldownMs))
        {
            return false;
        }

        Vector3 zoneAnchor = state.HasDirectNavigationSafePosition
            ? state.LastDirectNavigationSafePosition
            : bot.Position;
        if (!TryGetClosestRoomZone(zoneAnchor, out FacilityZone zone))
        {
            return false;
        }

        if (zone == FacilityZone.Surface)
        {
            return false;
        }

        List<(Door Door, Vector3 NavPosition)> candidates = new();
        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.Zone != zone)
            {
                continue;
            }

            if (TryFindSafeDoorTeleportPoint(door, zoneAnchor, behavior, out Vector3 navPosition))
            {
                candidates.Add((door, navPosition));
            }
        }

        if (candidates.Count == 0)
        {
            logNavDebug(bot, state, $"door-teleport-no-candidate zone={zone} anchor=({zoneAnchor.x:F1},{zoneAnchor.y:F1},{zoneAnchor.z:F1})");
            return false;
        }

        int index = UnityEngine.Random.Range(0, candidates.Count);
        (Door selectedDoor, Vector3 selectedNavPosition) = candidates[index];
        bot.Position = ApplyDirectNavPlayerOffset(selectedNavPosition, behavior);
        state.LastDoorTeleportTick = nowTick;
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = selectedNavPosition;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "door-teleport";
        state.LastMoveIntentTick = nowTick;
        TryResetPathAfterTeleport(state, selectedNavPosition);
        logNavDebug(
            bot,
            state,
            $"door-teleport zone={zone} door={selectedDoor.DoorName} nav=({selectedNavPosition.x:F1},{selectedNavPosition.y:F1},{selectedNavPosition.z:F1}) candidates={candidates.Count}");
        return true;
    }

    private bool TryTeleportToRandomRoomForFarTarget(
        Player bot,
        ManagedBotState state,
        BotTargetSelection? target,
        BotBehaviorDefinition behavior,
        System.Random random,
        bool useDust2Arena,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (useDust2Arena
            || target == null
            || !behavior.LongRangeRandomRoomTeleportEnabled
            || target.Distance <= Mathf.Max(1.0f, behavior.LongRangeRandomRoomTeleportDistance)
            || unchecked(nowTick - state.LastLongRangeRoomTeleportTick) < Math.Max(1000, behavior.LongRangeRandomRoomTeleportCooldownMs)
            || RoomIdentifier.AllRoomIdentifiers == null)
        {
            return false;
        }

        bool botZoneKnown = TryGetClosestRoomZone(bot.Position, out FacilityZone botZone);
        bool targetZoneKnown = TryGetClosestRoomZone(target.Target.Position, out FacilityZone targetZone);
        if (!botZoneKnown
            || botZone == FacilityZone.None
            || botZone == FacilityZone.Surface
            || (targetZoneKnown && targetZone == FacilityZone.Surface))
        {
            return false;
        }

        List<(RoomIdentifier Room, Vector3 NavPosition)> candidates = CollectSafeRoomTeleportCandidates(behavior, botZone);
        if (candidates.Count == 0)
        {
            logNavDebug(
                bot,
                state,
                $"long-range-room-teleport-no-candidate target={target.Target.Nickname} dist={target.Distance:F1} botZone={botZone} targetZone={targetZone}");
            return false;
        }

        (RoomIdentifier selectedRoom, Vector3 selectedNavPosition) = candidates[random.Next(candidates.Count)];
        bot.Position = ApplyDirectNavPlayerOffset(selectedNavPosition, behavior);
        state.LastLongRangeRoomTeleportTick = nowTick;
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = selectedNavPosition;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "long-range-room-teleport";
        state.LastMoveIntentTick = nowTick;
        state.StuckTicks = 0;
        state.ForwardStallSinceTick = 0;
        state.NavigationStuckRecoveryCount = 0;
        state.LastPosition = bot.Position;
        TryResetPathAfterTeleport(state, selectedNavPosition);
        logNavDebug(
            bot,
            state,
            $"long-range-room-teleport target={target.Target.Nickname} dist={target.Distance:F1} botZone={botZone} targetZone={targetZone} room={selectedRoom.Name} zone={selectedRoom.Zone} nav=({selectedNavPosition.x:F1},{selectedNavPosition.y:F1},{selectedNavPosition.z:F1}) candidates={candidates.Count}");
        return true;
    }

    private static List<(RoomIdentifier Room, Vector3 NavPosition)> CollectSafeRoomTeleportCandidates(
        BotBehaviorDefinition behavior,
        FacilityZone zone)
    {
        List<(RoomIdentifier Room, Vector3 NavPosition)> candidates = new();
        if (RoomIdentifier.AllRoomIdentifiers == null)
        {
            return candidates;
        }

        float sampleDistance = Mathf.Max(1.0f, behavior.LongRangeRandomRoomTeleportSampleDistance);
        foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
        {
            if (room == null
                || room.transform == null
                || room.Zone == FacilityZone.None
                || room.Zone == FacilityZone.Surface
                || (zone != FacilityZone.None && room.Zone != zone))
            {
                continue;
            }

            Vector3 center = room.transform.position;
            if (!NavMesh.SamplePosition(center, out NavMeshHit hit, sampleDistance, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - center.y) > Math.Max(4.0f, behavior.FacilityRuntimeNavMeshAgentHeight * 2.5f)
                || !IsSafeTeleportFloor(hit.position, behavior)
                || IsTeleportBodyBlocked(hit.position, behavior))
            {
                continue;
            }

            candidates.Add((room, hit.position));
        }

        return candidates;
    }

    private bool TryTeleportToRandomRoomInSameZoneAfterSustainedStuck(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        int stallMs = GetForwardStallMs(state, nowTick);
        if (stallMs < SustainedStuckRoomTeleportMs
            || unchecked(nowTick - state.LastRoomCenterTeleportTick) < SustainedStuckRoomTeleportCooldownMs
            || RoomIdentifier.AllRoomIdentifiers == null)
        {
            return false;
        }

        Vector3 zoneAnchor = bot.Position;
        if (!TryGetClosestRoomZone(zoneAnchor, out FacilityZone zone)
            && state.HasDirectNavigationSafePosition)
        {
            zoneAnchor = state.LastDirectNavigationSafePosition;
            TryGetClosestRoomZone(zoneAnchor, out zone);
        }

        if (zone == FacilityZone.None || zone == FacilityZone.Surface)
        {
            logNavDebug(
                bot,
                state,
                $"sustained-stuck-room-teleport-no-zone stallMs={stallMs} zone={zone} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1})");
            return false;
        }

        List<(RoomIdentifier Room, Vector3 NavPosition)> candidates = CollectSafeRoomTeleportCandidates(behavior, zone);
        if (candidates.Count == 0)
        {
            logNavDebug(
                bot,
                state,
                $"sustained-stuck-room-teleport-no-candidate stallMs={stallMs} zone={zone} anchor=({zoneAnchor.x:F1},{zoneAnchor.y:F1},{zoneAnchor.z:F1})");
            return false;
        }

        (RoomIdentifier selectedRoom, Vector3 selectedNavPosition) = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        bot.Position = ApplyDirectNavPlayerOffset(selectedNavPosition, behavior);
        state.LastRoomCenterTeleportTick = nowTick;
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = selectedNavPosition;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "sustained-stuck-room-teleport";
        state.LastMoveIntentTick = nowTick;
        TryResetPathAfterTeleport(state, selectedNavPosition);
        logNavDebug(
            bot,
            state,
            $"sustained-stuck-room-teleport stallMs={stallMs} zone={zone} room={selectedRoom.Name} nav=({selectedNavPosition.x:F1},{selectedNavPosition.y:F1},{selectedNavPosition.z:F1}) candidates={candidates.Count}");
        return true;
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

            float score = HorizontalDistance(room.Position, position) + (verticalDelta * 4f);
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

    private static bool TryFindSafeDoorTeleportPoint(
        Door door,
        Vector3 zoneAnchor,
        BotBehaviorDefinition behavior,
        out Vector3 navPosition)
    {
        navPosition = default;
        Transform? transform = door.Transform;
        Vector3 doorPosition = door.Position;
        Vector3 forward = transform != null ? transform.forward : Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = transform != null ? transform.right : Vector3.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, forward);
        }

        right.Normalize();
        Vector3[] probes =
        {
            doorPosition,
            doorPosition + forward * 1.1f,
            doorPosition - forward * 1.1f,
            doorPosition + right * 1.1f,
            doorPosition - right * 1.1f,
            doorPosition + forward * 1.8f,
            doorPosition - forward * 1.8f,
        };

        float maxVerticalDelta = 35f;
        bool found = false;
        float bestScore = float.PositiveInfinity;
        Vector3 bestPosition = default;
        foreach (Vector3 probe in probes)
        {
            if (!TryFindPhysicsFloorPosition(probe, behavior, out Vector3 floorPosition))
            {
                continue;
            }

            if (Mathf.Abs(floorPosition.y - zoneAnchor.y) > maxVerticalDelta
                || !IsSafeTeleportFloor(floorPosition, behavior)
                || IsTeleportBodyBlocked(floorPosition, behavior))
            {
                continue;
            }

            float score = HorizontalDistance(floorPosition, doorPosition) + Mathf.Abs(floorPosition.y - doorPosition.y);
            if (score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            bestPosition = floorPosition;
        }

        navPosition = bestPosition;
        return found;
    }

    private bool TryRecoverFromElevatedPropStuck(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (state.NavigationWaypoints.Count == 0
            || state.NavigationWaypointIndex < 0
            || state.NavigationWaypointIndex >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        Vector3 botPosition = bot.Position;
        if (!IsElevatedRelativeToNavMesh(botPosition, state, behavior, out NavMeshHit currentNavHit)
            && state.NavigationStuckRecoveryCount < 3)
        {
            return false;
        }

        Vector3 waypoint = state.NavigationWaypoints[state.NavigationWaypointIndex];
        Vector3 forwardDirection = ResolveElevatedPropRecoveryDirection(bot, botPosition, waypoint);
        Vector3 candidate = botPosition + (forwardDirection * ElevatedPropForwardTeleportDistance);
        if (TryClampPropForwardTeleportBeforeCachedDoor(state, botPosition, candidate, nowTick, out Vector3 clampedCandidate, out string doorLabel)
            && HorizontalDistance(botPosition, clampedCandidate) < 0.25f)
        {
            logNavDebug(
                bot,
                state,
                $"prop-forward-door-blocked door={doorLabel} from=({botPosition.x:F1},{botPosition.y:F1},{botPosition.z:F1}) candidate=({candidate.x:F1},{candidate.y:F1},{candidate.z:F1}) wp=({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1})");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(doorLabel))
        {
            candidate = clampedCandidate;
        }

        bot.Position = candidate;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "prop-forward-teleport";
        state.LastMoveIntentTick = nowTick;

        if (IsOutOfNavigationBounds(candidate, behavior, out NavMeshHit candidateNavHit))
        {
            if (TryTeleportToFallbackRoom(
                    bot,
                    state,
                    behavior,
                    logNavDebug,
                    nowTick,
                    "prop-forward-oob",
                    state.HasDirectNavigationSafePosition ? state.LastDirectNavigationSafePosition : currentNavHit.position))
            {
                state.StuckTicks = 0;
                state.ForwardStallSinceTick = 0;
                return true;
            }

            bot.Position = botPosition;
            logNavDebug(
                bot,
                state,
                $"prop-forward-oob-no-room from=({botPosition.x:F1},{botPosition.y:F1},{botPosition.z:F1}) candidate=({candidate.x:F1},{candidate.y:F1},{candidate.z:F1}) wp=({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1})");
            return false;
        }

        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = candidateNavHit.position;
        TryResetPathAfterTeleport(state, candidateNavHit.position);
        logNavDebug(
            bot,
            state,
            $"prop-forward-teleport from=({botPosition.x:F1},{botPosition.y:F1},{botPosition.z:F1}) to=({candidate.x:F1},{candidate.y:F1},{candidate.z:F1}) nav=({candidateNavHit.position.x:F1},{candidateNavHit.position.y:F1},{candidateNavHit.position.z:F1}) wp=({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1})");
        return true;
    }

    private static bool TryClampPropForwardTeleportBeforeCachedDoor(
        ManagedBotState state,
        Vector3 start,
        Vector3 candidate,
        int nowTick,
        out Vector3 clampedCandidate,
        out string doorLabel)
    {
        clampedCandidate = candidate;
        doorLabel = "";
        Vector3 segment = candidate - start;
        segment.y = 0f;
        float segmentLength = segment.magnitude;
        if (segmentLength < 0.001f)
        {
            return false;
        }

        if (state.LastRelevantDoorTick == 0
            || unchecked(nowTick - state.LastRelevantDoorTick) > RelevantDoorCacheMs
            || Mathf.Abs(state.LastRelevantDoorPosition.y - start.y) > 5.0f)
        {
            return false;
        }

        float segmentDistance = DistancePointToSegment2D(state.LastRelevantDoorPosition, start, candidate, out float t);
        if (t < 0f || t > 1f || segmentDistance > 1.1f)
        {
            return false;
        }

        Vector3 direction = segment / segmentLength;
        float bestAlong = t * segmentLength;
        float safeDistance = Mathf.Max(0f, bestAlong - 0.4f);
        clampedCandidate = start + (direction * safeDistance);
        clampedCandidate.y = candidate.y;
        doorLabel = state.LastRelevantDoorLabel;
        return true;
    }

    private static bool IsElevatedRelativeToNavMesh(
        Vector3 botPosition,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        out NavMeshHit navHit)
    {
        float sampleDistance = Mathf.Max(1.0f, behavior.FacilityNavMeshSampleDistance);
        if (!NavMesh.SamplePosition(botPosition, out navHit, sampleDistance, NavMesh.AllAreas))
        {
            return false;
        }

        return botPosition.y > navHit.position.y + 0.55f
            || IsElevatedPropStuckCandidate(botPosition, state);
    }

    private static Vector3 ResolveElevatedPropRecoveryDirection(Player bot, Vector3 botPosition, Vector3 waypoint)
    {
        Vector3 direction = waypoint - botPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
            direction = Quaternion.Euler(0f, movementYaw, 0f) * Vector3.forward;
            direction.y = 0f;
        }

        return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
    }

    private static bool TryFindPhysicsFloorPosition(Vector3 probe, BotBehaviorDefinition behavior, out Vector3 floorPosition)
    {
        floorPosition = default;
        Vector3 origin = probe + Vector3.up * Math.Max(1.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 6.0f, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (hit.normal.y < 0.55f)
        {
            return false;
        }

        floorPosition = hit.point;
        return true;
    }

    private static bool IsOutOfNavigationBounds(
        Vector3 position,
        BotBehaviorDefinition behavior,
        out NavMeshHit navHit)
    {
        float sampleDistance = Mathf.Max(8.0f, behavior.FacilityNavMeshSampleDistance * 2.0f);
        if (!NavMesh.SamplePosition(position, out navHit, sampleDistance, NavMesh.AllAreas))
        {
            return true;
        }

        float verticalDelta = Mathf.Abs(position.y - navHit.position.y);
        if (verticalDelta > Math.Max(8.0f, behavior.FacilityRuntimeNavMeshAgentHeight * 5.0f))
        {
            return true;
        }

        Vector3 floorProbe = position + (Vector3.up * 1.5f);
        return !Physics.Raycast(floorProbe, Vector3.down, 10.0f, ~0, QueryTriggerInteraction.Ignore);
    }

    private bool TryTeleportToFallbackRoom(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick,
        string reason,
        Vector3 anchor)
    {
        if (RoomIdentifier.AllRoomIdentifiers == null)
        {
            return false;
        }

        bool hasZone = TryGetClosestRoomZone(anchor, out FacilityZone zone)
            && zone != FacilityZone.None
            && zone != FacilityZone.Surface;
        List<(RoomIdentifier Room, Vector3 NavPosition)> candidates = CollectSafeRoomTeleportCandidates(
            behavior,
            hasZone ? zone : FacilityZone.None);
        if (candidates.Count == 0 && hasZone)
        {
            candidates = CollectSafeRoomTeleportCandidates(behavior, FacilityZone.None);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        (RoomIdentifier selectedRoom, Vector3 selectedNavPosition) = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        bot.Position = ApplyDirectNavPlayerOffset(selectedNavPosition, behavior);
        state.LastRoomCenterTeleportTick = nowTick;
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = selectedNavPosition;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "fallback-room-teleport";
        state.LastMoveIntentTick = nowTick;
        state.NavigationStuckRecoveryCount = 0;
        state.LastPosition = bot.Position;
        TryResetPathAfterTeleport(state, selectedNavPosition);
        logNavDebug(
            bot,
            state,
            $"fallback-room-teleport reason={reason} room={selectedRoom.Name} zone={selectedRoom.Zone} anchor=({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}) nav=({selectedNavPosition.x:F1},{selectedNavPosition.y:F1},{selectedNavPosition.z:F1}) candidates={candidates.Count}");
        return true;
    }

    private bool TryTeleportToClosestRoomCenter(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!behavior.NavMeshRoomCenterTeleportEnabled
            || state.NavigationStuckRecoveryCount < Math.Max(1, behavior.NavMeshRoomCenterTeleportRecoveryCount)
            || unchecked(nowTick - state.LastRoomCenterTeleportTick) < Math.Max(500, behavior.NavMeshRoomCenterTeleportCooldownMs)
            || RoomIdentifier.AllRoomIdentifiers == null)
        {
            return false;
        }

        float sampleDistance = Mathf.Max(1.0f, behavior.NavMeshRoomCenterTeleportSampleDistance);
        Vector3 botPosition = bot.Position;
        float maxVerticalDelta = Mathf.Max(4.0f, behavior.FacilityRuntimeNavMeshAgentHeight * 2.5f);
        bool found = false;
        float bestScore = float.PositiveInfinity;
        Vector3 bestRoomCenter = default;
        Vector3 bestNavPosition = default;
        string bestRoomName = "unknown";

        foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
        {
            if (room == null || room.transform == null)
            {
                continue;
            }

            Vector3 center = room.transform.position;
            if (!NavMesh.SamplePosition(center, out NavMeshHit hit, sampleDistance, NavMesh.AllAreas))
            {
                continue;
            }

            if (Mathf.Abs(hit.position.y - botPosition.y) > maxVerticalDelta
            || !IsSafeTeleportFloor(hit.position, behavior)
            || IsTeleportBodyBlocked(hit.position, behavior))
            {
                continue;
            }

            float score = Vector3.Distance(botPosition, hit.position);
            if (score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            bestRoomCenter = center;
            bestNavPosition = hit.position;
            bestRoomName = room.Name.ToString();
        }

        if (!found)
        {
            return false;
        }

        bot.Position = ApplyDirectNavPlayerOffset(bestNavPosition, behavior);
        state.LastRoomCenterTeleportTick = nowTick;
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = bestNavPosition;
        state.LastDirectNavigationMoveTick = nowTick;
        state.LastMoveIntentLabel = "room-center-teleport";
        state.LastMoveIntentTick = nowTick;
        TryResetPathAfterTeleport(state, bestNavPosition);
        logNavDebug(
            bot,
            state,
            $"room-center-teleport room={bestRoomName} roomCenter=({bestRoomCenter.x:F1},{bestRoomCenter.y:F1},{bestRoomCenter.z:F1}) nav=({bestNavPosition.x:F1},{bestNavPosition.y:F1},{bestNavPosition.z:F1}) score={bestScore:F1}");
        return true;
    }

    private static bool IsSafeTeleportFloor(Vector3 navPosition, BotBehaviorDefinition behavior)
    {
        Vector3[] offsets =
        {
            Vector3.zero,
            Vector3.forward * 0.45f,
            Vector3.back * 0.45f,
            Vector3.right * 0.45f,
            Vector3.left * 0.45f,
        };

        foreach (Vector3 offset in offsets)
        {
            Vector3 probe = navPosition + offset + (Vector3.up * 1.25f);
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 2.25f, ~0, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            if (hit.normal.y < 0.55f)
            {
                return false;
            }

            float floorDelta = Mathf.Abs(hit.point.y - navPosition.y);
            if (floorDelta > Mathf.Max(0.8f, behavior.FacilityRuntimeNavMeshAgentClimb + 0.35f))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTeleportBodyBlocked(Vector3 navPosition, BotBehaviorDefinition behavior)
    {
        float radius = Mathf.Max(0.2f, behavior.FacilityRuntimeNavMeshAgentRadius);
        Vector3[] checks =
        {
            navPosition + Vector3.up * 0.65f,
            navPosition + Vector3.up * 1.25f,
        };

        foreach (Vector3 check in checks)
        {
            Collider[] overlaps = Physics.OverlapSphere(
                check,
                radius,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (Collider collider in overlaps)
            {
                if (collider == null || collider.transform == null)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsPlayerOwnedTransform(Player player, Transform transform)
    {
        if (player.GameObject == null)
        {
            return false;
        }

        Transform playerTransform = player.GameObject.transform;
        return transform == playerTransform
            || transform.IsChildOf(playerTransform)
            || playerTransform.IsChildOf(transform);
    }

    private static bool TryApplyNavMeshStuckNudge(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        if (!behavior.NavMeshStuckNudgeEnabled
            || !string.Equals(state.LastNavigationReason, "navmesh-swift-corners", StringComparison.Ordinal)
            || state.NavigationWaypointIndex < 0
            || state.NavigationWaypointIndex >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        Vector3 waypoint = state.NavigationWaypoints[state.NavigationWaypointIndex];
        float waypointDistance = HorizontalDistance(bot.Position, waypoint);
        if (waypointDistance < 1.0f || waypointDistance > Mathf.Max(1.0f, behavior.NavMeshStuckNudgeMaxDistance))
        {
            return false;
        }

        float sampleDistance = Mathf.Clamp(behavior.FacilityNavMeshSampleDistance, 0.75f, 2.0f);
        if (!NavMesh.SamplePosition(bot.Position, out NavMeshHit currentHit, sampleDistance, NavMesh.AllAreas))
        {
            return false;
        }

        float maxVerticalDrift = Mathf.Max(1.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        if (Mathf.Abs(currentHit.position.y - bot.Position.y) > maxVerticalDrift)
        {
            return false;
        }

        Vector3 direction = waypoint - currentHit.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float step = Mathf.Clamp(behavior.NavMeshStuckNudgeStep, 0.1f, behavior.NavMeshCornerMoveMaxStep);
        Vector3 desired = currentHit.position + (direction.normalized * step);
        if (!NavMesh.SamplePosition(desired, out NavMeshHit stepHit, Mathf.Max(0.75f, step + 0.5f), NavMesh.AllAreas))
        {
            return false;
        }

        if (Mathf.Abs(stepHit.position.y - currentHit.position.y) > 1.0f)
        {
            return false;
        }

        NavMeshPath validationPath = new();
        if (!NavMesh.CalculatePath(currentHit.position, stepHit.position, NavMesh.AllAreas, validationPath)
            || validationPath.status == NavMeshPathStatus.PathInvalid
            || HorizontalDistance(currentHit.position, stepHit.position) > step + 0.35f)
        {
            return false;
        }

        bot.Position = ApplyDirectNavPlayerOffset(stepHit.position, behavior);
        state.HasDirectNavigationSafePosition = true;
        state.LastDirectNavigationSafePosition = stepHit.position;
        state.LastDirectNavigationMoveTick = Environment.TickCount;
        state.LastMoveIntentLabel = "navmesh-stuck-nudge";
        state.LastMoveIntentTick = state.LastDirectNavigationMoveTick;
        if (unchecked(nowTick - state.LastNavMeshStuckNudgeTick) > NavMeshStuckNudgeLoopWindowMs)
        {
            state.NavMeshStuckNudgeLoopCount = 0;
        }

        state.NavMeshStuckNudgeLoopCount++;
        state.LastNavMeshStuckNudgeTick = nowTick;
        logNavDebug(
            bot,
            state,
            $"navmesh-stuck-nudge count={state.NavMeshStuckNudgeLoopCount} from=({currentHit.position.x:F1},{currentHit.position.y:F1},{currentHit.position.z:F1}) to=({stepHit.position.x:F1},{stepHit.position.y:F1},{stepHit.position.z:F1}) wp=({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1}) dist={waypointDistance:F2}");
        return true;
    }

    private static void RecordMoveIntent(ManagedBotState state, string moveLabel, int nowTick)
    {
        state.LastMoveIntentLabel = string.IsNullOrWhiteSpace(moveLabel) ? "none" : moveLabel;
        state.LastMoveIntentTick = nowTick;
    }

    private static void ClearMoveIntent(ManagedBotState state)
    {
        state.LastMoveIntentLabel = "none";
        state.LastMoveIntentTick = 0;
        state.ForwardStallSinceTick = 0;
        state.ForwardBlockedUntilTick = 0;
    }

    private static void StopWithoutTarget(ManagedBotState state, int nowTick)
    {
        if (state.AiState != BotAiState.Chase)
        {
            state.AiState = BotAiState.Chase;
            state.AiStateEnteredTick = nowTick;
        }

        state.LastStateSummary = "idle";
        state.NextStrafeFlipTick = 0;
        state.ReactiveStrafeUntilTick = 0;
        state.CampUntilTick = 0;
        state.NavigationWaypoints.Clear();
        state.NavigationWaypointIndex = 0;
        state.LastMoveUsedNavigation = false;
        state.AStarFallbackActive = false;
        state.ForwardBlockedUntilTick = 0;
        state.LeftBlockedUntilTick = 0;
        state.RightBlockedUntilTick = 0;
        ClearMoveIntent(state);
    }

    private static void MaybeTriggerForwardRecoveryJump(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        int nowTick,
        bool allowJump)
    {
        if (state.LastMoveIntentLabel != "forward"
            || state.ForwardStallSinceTick == 0
            || unchecked(nowTick - state.ForwardStallSinceTick) < behavior.ForwardStuckJumpThresholdMs
            || unchecked(nowTick - state.NextForwardJumpTick) < 0)
        {
            return;
        }

        if (state.ForwardBlockedUntilTick == 0 || unchecked(nowTick - state.ForwardBlockedUntilTick) >= 0)
        {
            int direction = state.LastBlockedMoveLabel == "left" ? 1 : -1;
            state.StrafeDirection = direction;
            state.LastBlockedMoveLabel = direction >= 0 ? "right" : "left";
            state.BlockedMoveRepeatCount++;
            state.ForwardBlockedUntilTick = nowTick + Math.Max(ForwardRecoverySidestepMinMs, behavior.UnstuckDurationMs);
        }

        state.NextForwardJumpTick = nowTick + Math.Max(MinimumForwardJumpIntervalMs, behavior.ForwardStuckJumpIntervalMs);
        if (allowJump && behavior.JumpActionNames is { Length: > 0 })
        {
            int jumpBursts = Math.Max(1, behavior.ForwardStuckJumpBurstCount);
            for (int i = 0; i < jumpBursts; i++)
            {
                tryInvokeDummyActions(bot, behavior.JumpActionNames);
            }
        }

        TryApplyForwardRecoverySidestep(bot, state, behavior, tryInvokeDummyActions, nowTick);
    }

    private static void TriggerMovementStallJumpIfReady(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick)
    {
        int thresholdMs = Math.Max(0, behavior.ForwardStuckJumpThresholdMs);
        if (state.ForwardStallSinceTick == 0
            || unchecked(nowTick - state.ForwardStallSinceTick) < thresholdMs)
        {
            return;
        }

        TriggerStuckJumpIfReady(bot, state, behavior, tryInvokeDummyActions, logNavDebug, nowTick);
    }

    private static void TriggerStuckJumpIfReady(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        Action<Player, ManagedBotState, string> logNavDebug,
        int nowTick,
        int? intervalOverrideMs = null,
        int burstMultiplier = 1,
        string logReason = "stuck-jump")
    {
        if (behavior.JumpActionNames is not { Length: > 0 }
            || unchecked(nowTick - state.NextForwardJumpTick) < 0)
        {
            return;
        }

        int intervalMs = intervalOverrideMs.HasValue
            ? Math.Max(MinimumForwardJumpIntervalMs, intervalOverrideMs.Value)
            : Math.Max(MinimumForwardJumpIntervalMs, behavior.ForwardStuckJumpIntervalMs);
        state.NextForwardJumpTick = nowTick + intervalMs;
        int jumpBursts = Math.Max(1, behavior.ForwardStuckJumpBurstCount) * Math.Max(1, burstMultiplier);
        for (int i = 0; i < jumpBursts; i++)
        {
            tryInvokeDummyActions(bot, behavior.JumpActionNames);
        }

        logNavDebug(
            bot,
            state,
            $"{logReason} ticks={state.StuckTicks} move={state.LastMoveIntentLabel} pos=({bot.Position.x:F1},{bot.Position.y:F1},{bot.Position.z:F1}) bursts={jumpBursts} nextMs={intervalMs}");
    }

    private static bool IsElevatedPropStuckCandidate(Vector3 botPosition, ManagedBotState state)
    {
        if (state.NavigationWaypoints.Count == 0
            || state.NavigationWaypointIndex < 0
            || state.NavigationWaypointIndex >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        for (int i = state.NavigationWaypointIndex; i < state.NavigationWaypoints.Count; i++)
        {
            if (state.NavigationWaypoints[i].y < botPosition.y - 0.6f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryApplyForwardRecoverySidestep(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        int nowTick)
    {
        if (unchecked(state.ForwardBlockedUntilTick - nowTick) <= 0)
        {
            return false;
        }

        string[] primaryActions = state.StrafeDirection >= 0
            ? behavior.WalkRightActionNames
            : behavior.WalkLeftActionNames;
        string primaryLabel = state.StrafeDirection >= 0 ? "right" : "left";
        if (primaryActions != null && primaryActions.Length > 0 && tryInvokeDummyActions(bot, primaryActions))
        {
            RecordMoveIntent(state, primaryLabel, nowTick);
            return true;
        }

        string[] fallbackActions = state.StrafeDirection >= 0
            ? behavior.WalkLeftActionNames
            : behavior.WalkRightActionNames;
        string fallbackLabel = state.StrafeDirection >= 0 ? "left" : "right";
        if (fallbackActions != null && fallbackActions.Length > 0 && tryInvokeDummyActions(bot, fallbackActions))
        {
            state.StrafeDirection *= -1;
            state.LastBlockedMoveLabel = fallbackLabel;
            RecordMoveIntent(state, fallbackLabel, nowTick);
            return true;
        }

        return false;
    }

    private static bool ShouldApplyChaseStrafe(ManagedBotState state, BotTargetSelection? target, int nowTick)
    {
        return IsReactiveStrafeActive(state, nowTick);
    }

    private static bool IsReactiveStrafeActive(ManagedBotState state, int nowTick)
    {
        return unchecked(state.ReactiveStrafeUntilTick - nowTick) > 0;
    }

    private static float GetCampExitDistance(BotBehaviorDefinition behavior)
    {
        return Mathf.Max(behavior.PreferredRange, behavior.PreferredRange + Mathf.Abs(behavior.RangeTolerance));
    }

    private static bool TryMoveForwardOnNavigationPath(
        Player bot,
        Vector3 desiredDirection,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        out string moveLabel,
        float maxStepDistance)
    {
        moveLabel = "none";
        desiredDirection.y = 0f;
        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 forward = yawRotation * Vector3.forward;
        Vector3 normalized = desiredDirection.normalized;
        float maxAngle = Mathf.Clamp(behavior.NavMeshForwardMoveMaxAngleDegrees, 5f, 175f);
        float forwardDot = Vector3.Dot(normalized, forward);
        if (forwardDot < Mathf.Cos(maxAngle * Mathf.Deg2Rad))
        {
            return false;
        }

        string[] boundedActions = SelectWalkActionsForDistance(behavior.WalkForwardActionNames, maxStepDistance);
        if (boundedActions.Length == 0 || !tryInvokeDummyActions(bot, boundedActions))
        {
            return false;
        }

        ApplyForwardMoveBurst(bot, "forward", boundedActions, behavior, tryInvokeDummyActions);
        moveLabel = "forward";
        return true;
    }

    private static bool TryMoveTowardDirection(
        Player bot,
        Vector3 desiredDirection,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        bool allowBackward,
        out string moveLabel,
        float maxStepDistance = float.PositiveInfinity)
    {
        moveLabel = "none";
        desiredDirection.y = 0f;
        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 forward = yawRotation * Vector3.forward;
        Vector3 right = yawRotation * Vector3.right;
        Vector3 normalized = desiredDirection.normalized;

        List<(float Score, string[] Actions, string Label)> candidates = new()
        {
            (Vector3.Dot(normalized, forward), behavior.WalkForwardActionNames, "forward"),
            (Vector3.Dot(normalized, right), behavior.WalkRightActionNames, "right"),
            (Vector3.Dot(normalized, -right), behavior.WalkLeftActionNames, "left"),
        };

        if (allowBackward)
        {
            candidates.Add((Vector3.Dot(normalized, -forward), behavior.WalkBackwardActionNames, "back"));
        }

        foreach ((float _, string[] actions, string label) in candidates
            .OrderByDescending(candidate => candidate.Score))
        {
            string[] boundedActions = SelectWalkActionsForDistance(actions, maxStepDistance);
            if (boundedActions.Length > 0 && tryInvokeDummyActions(bot, boundedActions))
            {
                ApplyForwardMoveBurst(bot, label, boundedActions, behavior, tryInvokeDummyActions);
                moveLabel = label;
                return true;
            }
        }

        return false;
    }

    private static string[] SelectWalkActionsForDistance(string[]? actions, float maxStepDistance)
    {
        if (actions == null || actions.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (float.IsInfinity(maxStepDistance) || maxStepDistance <= 0f)
        {
            return actions;
        }

        float cappedDistance = Mathf.Max(0.05f, maxStepDistance);
        string[] bounded = actions
            .Where(action => ExtractWalkStepMeters(action) <= cappedDistance + 0.05f)
            .ToArray();
        return bounded.Length > 0 ? bounded : new[] { actions[actions.Length - 1] };
    }

    private static float ExtractWalkStepMeters(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return float.PositiveInfinity;
        }

        int meterIndex = actionName.LastIndexOf('m');
        if (meterIndex <= 0)
        {
            return float.PositiveInfinity;
        }

        int start = meterIndex - 1;
        while (start >= 0 && (char.IsDigit(actionName[start]) || actionName[start] == '.'))
        {
            start--;
        }

        string value = actionName.Substring(start + 1, meterIndex - start - 1);
        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float meters)
            ? meters
            : float.PositiveInfinity;
    }

    private static void ApplyForwardMoveBurst(
        Player bot,
        string moveLabel,
        string[] actions,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions)
    {
        if (!string.Equals(moveLabel, "forward", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int burstCount = behavior.BotForwardMoveBurstCount;
        burstCount = Math.Max(1, burstCount);
        for (int i = 1; i < burstCount; i++)
        {
            tryInvokeDummyActions(bot, actions);
        }
    }

    private static void ApplyStrafeBurst(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        System.Random random,
        bool useScpForwardStrafe)
    {
        if (useScpForwardStrafe)
        {
            ApplyScpForwardStrafe(bot, state, behavior, tryInvokeDummyActions);
            return;
        }

        int burstCount = random.Next(StrafeBurstMin, StrafeBurstMax + 1);
        for (int i = 0; i < burstCount; i++)
        {
            bool usePrimaryDirection = random.Next(0, 100) < 75;
            bool strafeRight = usePrimaryDirection
                ? state.StrafeDirection >= 0
                : state.StrafeDirection < 0;
            string[] actions = strafeRight
                ? behavior.WalkRightActionNames
                : behavior.WalkLeftActionNames;

            if (actions == null || actions.Length == 0)
            {
                continue;
            }

            tryInvokeDummyActions(bot, actions);
        }
    }

    private static void ApplySafeStrafeBurst(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions,
        bool useScpForwardStrafe)
    {
        if (useScpForwardStrafe)
        {
            ApplyScpForwardStrafe(bot, state, behavior, tryInvokeDummyActions);
            return;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Vector3 right = Quaternion.Euler(0f, movementYaw, 0f) * Vector3.right;
        Vector3 strafeDirection = state.StrafeDirection >= 0 ? right : -right;
        string[] actions = state.StrafeDirection >= 0
            ? behavior.WalkRightActionNames
            : behavior.WalkLeftActionNames;

        float clearStep = GetClearStepDistance(bot, strafeDirection, behavior);
        if (clearStep <= 0f)
        {
            return;
        }

        string[] boundedActions = SelectWalkActionsForDistance(actions, clearStep * GetCloseRetreatSpeedScale(behavior));
        if (boundedActions.Length > 0)
        {
            tryInvokeDummyActions(bot, boundedActions);
        }
    }

    private static void ApplyScpForwardStrafe(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyActions)
    {
        if (behavior.WalkForwardActionNames != null && behavior.WalkForwardActionNames.Length > 0)
        {
            tryInvokeDummyActions(bot, behavior.WalkForwardActionNames);
        }

        string[] sideActions = state.StrafeDirection >= 0
            ? behavior.WalkRightActionNames
            : behavior.WalkLeftActionNames;
        if (sideActions != null && sideActions.Length > 0)
        {
            tryInvokeDummyActions(bot, sideActions);
        }
    }

    private static int GetLoadedAmmo(FirearmItem firearm)
    {
        return firearm.StoredAmmo + firearm.ChamberedAmmo;
    }

    private static BotBehaviorDefinition ResolveActiveBehaviorProfile(
        ManagedBotState state,
        BotTargetSelection? target,
        BotBehaviorDefinition behavior)
    {
        if (state.AiState == BotAiState.Camp)
        {
            return CreateCampBehaviorProfile(behavior);
        }

        if (ShouldUseFarTargetAimProfile(target, behavior))
        {
            return CreateFarTargetAimProfile(behavior);
        }

        return behavior;
    }

    private static bool ShouldUseFarTargetAimProfile(BotTargetSelection? target, BotBehaviorDefinition behavior)
    {
        if (!behavior.EnableFarTargetAimAssist || target?.HasLineOfSight != true)
        {
            return false;
        }

        float farDistance = behavior.FarTargetAimDistance > 0f
            ? behavior.FarTargetAimDistance
            : behavior.PreferredRange + behavior.RangeTolerance;
        return target.Distance >= Mathf.Max(behavior.PreferredRange + behavior.RangeTolerance, farDistance);
    }

    private static BotBehaviorDefinition CreateFarTargetAimProfile(BotBehaviorDefinition behavior)
    {
        BotBehaviorDefinition profile = CopyBehavior(behavior);
        profile.MaxHorizontalAimActionsPerTick = Math.Max(
            behavior.MaxHorizontalAimActionsPerTick,
            behavior.FarTargetMaxHorizontalAimActionsPerTick);
        profile.MaxVerticalAimActionsPerTick = Math.Max(
            behavior.MaxVerticalAimActionsPerTick,
            behavior.FarTargetMaxVerticalAimActionsPerTick);
        profile.HorizontalAimDeadzoneDegrees = Mathf.Min(
            behavior.HorizontalAimDeadzoneDegrees,
            Mathf.Max(0.1f, behavior.FarTargetHorizontalAimDeadzoneDegrees));
        profile.VerticalAimDeadzoneDegrees = Mathf.Min(
            behavior.VerticalAimDeadzoneDegrees,
            Mathf.Max(0.1f, behavior.FarTargetVerticalAimDeadzoneDegrees));
        profile.RealisticAimSettleMs = behavior.FarTargetRealisticAimSettleMs > 0
            ? Math.Min(behavior.RealisticAimSettleMs, behavior.FarTargetRealisticAimSettleMs)
            : behavior.RealisticAimSettleMs;
        return profile;
    }

    private static BotBehaviorDefinition CreateCampBehaviorProfile(BotBehaviorDefinition behavior)
    {
        BotBehaviorDefinition profile = CopyBehavior(behavior);
        profile.RealisticAimSettleMs = Math.Max(1, behavior.RealisticAimSettleMs / 2);
        profile.MinShotIntervalMs = Math.Max(1, behavior.MinShotIntervalMs / 2);
        profile.MaxHorizontalAimActionsPerTick = Math.Max(1, behavior.MaxHorizontalAimActionsPerTick * 2);
        profile.MaxVerticalAimActionsPerTick = Math.Max(1, behavior.MaxVerticalAimActionsPerTick * 2);
        profile.HorizontalAimDeadzoneDegrees = Mathf.Max(0.1f, behavior.HorizontalAimDeadzoneDegrees * 0.5f);
        profile.VerticalAimDeadzoneDegrees = Mathf.Max(0.1f, behavior.VerticalAimDeadzoneDegrees * 0.5f);
        return profile;
    }

    private static BotBehaviorDefinition CopyBehavior(BotBehaviorDefinition behavior)
    {
        return new BotBehaviorDefinition
        {
            AiMode = behavior.AiMode,
            EnableCombatActions = behavior.EnableCombatActions,
            EnableStepMovement = behavior.EnableStepMovement,
            EnableObstacleNavigation = behavior.EnableObstacleNavigation,
            UseFacilityNavMesh = behavior.UseFacilityNavMesh,
            UseFacilitySurfaceNavMesh = behavior.UseFacilitySurfaceNavMesh,
            FacilityNavMeshSampleDistance = behavior.FacilityNavMeshSampleDistance,
            FacilityRuntimeNavMeshEnabled = behavior.FacilityRuntimeNavMeshEnabled,
            FacilitySurfaceRuntimeNavMeshEnabled = behavior.FacilitySurfaceRuntimeNavMeshEnabled,
            FacilityRuntimeNavMeshAgentRadius = behavior.FacilityRuntimeNavMeshAgentRadius,
            FacilityRuntimeNavMeshAgentHeight = behavior.FacilityRuntimeNavMeshAgentHeight,
            FacilityRuntimeNavMeshAgentMaxSlope = behavior.FacilityRuntimeNavMeshAgentMaxSlope,
            FacilityRuntimeNavMeshAgentClimb = behavior.FacilityRuntimeNavMeshAgentClimb,
            FacilityRuntimeNavMeshUseRenderMeshes = behavior.FacilityRuntimeNavMeshUseRenderMeshes,
            FacilityRuntimeNavMeshUseRoomTemplates = behavior.FacilityRuntimeNavMeshUseRoomTemplates,
            FacilityRuntimeNavMeshBoundsPadding = behavior.FacilityRuntimeNavMeshBoundsPadding,
            FacilityRuntimeNavMeshMinRegionArea = behavior.FacilityRuntimeNavMeshMinRegionArea,
            FacilityRuntimeNavMeshIgnoreDoors = behavior.FacilityRuntimeNavMeshIgnoreDoors,
            FacilityRuntimeNavMeshLogBuild = behavior.FacilityRuntimeNavMeshLogBuild,
            FacilityRuntimeNavMeshCreateOffMeshLinks = behavior.FacilityRuntimeNavMeshCreateOffMeshLinks,
            FacilityRuntimeNavMeshMaxOffMeshLinks = behavior.FacilityRuntimeNavMeshMaxOffMeshLinks,
            FacilityRuntimeNavMeshOffMeshLinkSearchRadius = behavior.FacilityRuntimeNavMeshOffMeshLinkSearchRadius,
            FacilityRuntimeNavMeshOffMeshLinkMaxVerticalDelta = behavior.FacilityRuntimeNavMeshOffMeshLinkMaxVerticalDelta,
            FacilityRuntimeNavMeshOffMeshLinkSampleSpacing = behavior.FacilityRuntimeNavMeshOffMeshLinkSampleSpacing,
            FacilityRuntimeNavMeshOffMeshLinkSampleDistance = behavior.FacilityRuntimeNavMeshOffMeshLinkSampleDistance,
            FacilityRuntimeNavMeshOffMeshLinkWidth = behavior.FacilityRuntimeNavMeshOffMeshLinkWidth,
            FacilityRuntimeNavMeshOffMeshLinkCostModifier = behavior.FacilityRuntimeNavMeshOffMeshLinkCostModifier,
            VisualizeFacilityNavMesh = behavior.VisualizeFacilityNavMesh,
            FacilityRuntimeNavMeshMaxDebugEdges = behavior.FacilityRuntimeNavMeshMaxDebugEdges,
            FacilityRuntimeNavMeshDebugEdgeWidth = behavior.FacilityRuntimeNavMeshDebugEdgeWidth,
            FacilityRuntimeNavMeshDebugHeightOffset = behavior.FacilityRuntimeNavMeshDebugHeightOffset,
            VisualizeFacilityNavMeshSamples = behavior.VisualizeFacilityNavMeshSamples,
            FacilityRuntimeNavMeshMaxDebugSamples = behavior.FacilityRuntimeNavMeshMaxDebugSamples,
            FacilityRuntimeNavMeshDebugSampleSpacing = behavior.FacilityRuntimeNavMeshDebugSampleSpacing,
            FacilityRuntimeNavMeshDebugSampleRadius = behavior.FacilityRuntimeNavMeshDebugSampleRadius,
            FacilityRuntimeNavMeshDebugSampleDistance = behavior.FacilityRuntimeNavMeshDebugSampleDistance,
            FacilityRuntimeNavMeshDebugSampleSize = behavior.FacilityRuntimeNavMeshDebugSampleSize,
            UseFacilityDummyFollowFallback = behavior.UseFacilityDummyFollowFallback,
            FacilityNavMeshDirectPositionControl = behavior.FacilityNavMeshDirectPositionControl,
            FacilityNavMeshDirectPositionMaxStep = behavior.FacilityNavMeshDirectPositionMaxStep,
            FacilityNavMeshDirectPositionVerticalOffset = behavior.FacilityNavMeshDirectPositionVerticalOffset,
            FacilityNavMeshDirectPositionMaxDropPerStep = behavior.FacilityNavMeshDirectPositionMaxDropPerStep,
            FacilityNavMeshDirectPositionBridgeDistance = behavior.FacilityNavMeshDirectPositionBridgeDistance,
            FacilityDummyFollowMaxDistance = behavior.FacilityDummyFollowMaxDistance,
            FacilityDummyFollowMinDistance = behavior.FacilityDummyFollowMinDistance,
            FacilityDummyFollowSpeed = behavior.FacilityDummyFollowSpeed,
            FacilityDummyFollowSpeedScp939 = behavior.FacilityDummyFollowSpeedScp939,
            FacilityDummyFollowSpeedScp3114 = behavior.FacilityDummyFollowSpeedScp3114,
            FacilityDummyFollowSpeedScp049 = behavior.FacilityDummyFollowSpeedScp049,
            FacilityDummyFollowSpeedScp106 = behavior.FacilityDummyFollowSpeedScp106,
            FacilityDummyFollowDoorSlowSpeed = behavior.FacilityDummyFollowDoorSlowSpeed,
            EnableBotDoorOpening = behavior.EnableBotDoorOpening,
            BotDoorOpenRadius = behavior.BotDoorOpenRadius,
            BotForceOpenUnlockedDoors = behavior.BotForceOpenUnlockedDoors,
            BotWaitAtClosedDoors = behavior.BotWaitAtClosedDoors,
            BotWaitAtClosedDoorsOnlyHcz = behavior.BotWaitAtClosedDoorsOnlyHcz,
            BotClosedDoorStopRadius = behavior.BotClosedDoorStopRadius,
            EnableVerticalAim = behavior.EnableVerticalAim,
            TargetAimHeightOffset = behavior.TargetAimHeightOffset,
            EnableGlobalVisionFallback = behavior.EnableGlobalVisionFallback,
            RealisticSightMemoryMs = behavior.RealisticSightMemoryMs,
            RealisticReacquireDelayMs = behavior.RealisticReacquireDelayMs,
            RealisticInitialYawOffsetMaxDegrees = behavior.RealisticInitialYawOffsetMaxDegrees,
            RealisticInitialPitchOffsetMaxDegrees = behavior.RealisticInitialPitchOffsetMaxDegrees,
            RealisticAimSettleMs = behavior.RealisticAimSettleMs,
            RealisticReloadLockOffsetMaxDegrees = behavior.RealisticReloadLockOffsetMaxDegrees,
            RealisticHeadAimHeightOffset = behavior.RealisticHeadAimHeightOffset,
            RealisticLosDebugLogging = behavior.RealisticLosDebugLogging,
            MaxVerticalAimDegrees = behavior.MaxVerticalAimDegrees,
            EnableFarTargetAimAssist = behavior.EnableFarTargetAimAssist,
            FarTargetAimDistance = behavior.FarTargetAimDistance,
            FarTargetMaxHorizontalAimActionsPerTick = behavior.FarTargetMaxHorizontalAimActionsPerTick,
            FarTargetMaxVerticalAimActionsPerTick = behavior.FarTargetMaxVerticalAimActionsPerTick,
            FarTargetHorizontalAimDeadzoneDegrees = behavior.FarTargetHorizontalAimDeadzoneDegrees,
            FarTargetVerticalAimDeadzoneDegrees = behavior.FarTargetVerticalAimDeadzoneDegrees,
            FarTargetRealisticAimSettleMs = behavior.FarTargetRealisticAimSettleMs,
            RefillAmmoBetweenBursts = behavior.RefillAmmoBetweenBursts,
            KeepMagazineFilled = behavior.KeepMagazineFilled,
            UseZoomWhileShooting = behavior.UseZoomWhileShooting,
            UseZoomForFarTargets = behavior.UseZoomForFarTargets,
            FarTargetZoomDistance = behavior.FarTargetZoomDistance,
            ScpAttackRange = behavior.ScpAttackRange,
            EnableOrbitMovement = behavior.EnableOrbitMovement,
            OrbitRetreatDistance = behavior.OrbitRetreatDistance,
            OrbitRetreatBias = behavior.OrbitRetreatBias,
            ThinkIntervalMinMs = behavior.ThinkIntervalMinMs,
            ThinkIntervalMaxMs = behavior.ThinkIntervalMaxMs,
            MinShotIntervalMs = behavior.MinShotIntervalMs,
            MinReloadAttemptIntervalMs = behavior.MinReloadAttemptIntervalMs,
            ShootReleaseDelayMs = behavior.ShootReleaseDelayMs,
            DebugLogIntervalMs = behavior.DebugLogIntervalMs,
            UnstuckDurationMs = behavior.UnstuckDurationMs,
            ReactiveStrafeDurationMs = behavior.ReactiveStrafeDurationMs,
            ReactiveStrafeCooldownMs = behavior.ReactiveStrafeCooldownMs,
            BotForwardMoveBurstCount = behavior.BotForwardMoveBurstCount,
            DoorSlowForwardMoveBurstCount = behavior.DoorSlowForwardMoveBurstCount,
            BotDoorSlowRadius = behavior.BotDoorSlowRadius,
            StuckTickThreshold = behavior.StuckTickThreshold,
            NavWaypointReachDistance = behavior.NavWaypointReachDistance,
            NavRecomputeIntervalMs = behavior.NavRecomputeIntervalMs,
            NavPathFailedCooldownMs = behavior.NavPathFailedCooldownMs,
            NavProbeDistance = behavior.NavProbeDistance,
            NavLateralProbeCount = behavior.NavLateralProbeCount,
            NavTargetMoveRecomputeDistance = behavior.NavTargetMoveRecomputeDistance,
            NavMeshCornerMoveMaxStep = behavior.NavMeshCornerMoveMaxStep,
            NavMeshForwardMoveMaxAngleDegrees = behavior.NavMeshForwardMoveMaxAngleDegrees,
            NavMeshStuckNudgeEnabled = behavior.NavMeshStuckNudgeEnabled,
            NavMeshStuckNudgeStep = behavior.NavMeshStuckNudgeStep,
            NavMeshStuckNudgeMaxDistance = behavior.NavMeshStuckNudgeMaxDistance,
            NavMeshForwardClipEnabled = behavior.NavMeshForwardClipEnabled,
            NavMeshForwardClipStep = behavior.NavMeshForwardClipStep,
            NavMeshRoomCenterTeleportEnabled = behavior.NavMeshRoomCenterTeleportEnabled,
            NavMeshRoomCenterTeleportRecoveryCount = behavior.NavMeshRoomCenterTeleportRecoveryCount,
            NavMeshRoomCenterTeleportCooldownMs = behavior.NavMeshRoomCenterTeleportCooldownMs,
            NavMeshRoomCenterTeleportSampleDistance = behavior.NavMeshRoomCenterTeleportSampleDistance,
            NavMeshStuckDoorTeleportEnabled = behavior.NavMeshStuckDoorTeleportEnabled,
            NavMeshStuckDoorTeleportStuckMs = behavior.NavMeshStuckDoorTeleportStuckMs,
            NavMeshStuckDoorTeleportCooldownMs = behavior.NavMeshStuckDoorTeleportCooldownMs,
            NavMeshStuckDoorTeleportSampleDistance = behavior.NavMeshStuckDoorTeleportSampleDistance,
            NavMeshLocalDetourEnabled = behavior.NavMeshLocalDetourEnabled,
            NavMeshLocalDetourForwardDistance = behavior.NavMeshLocalDetourForwardDistance,
            NavMeshLocalDetourLateralDistance = behavior.NavMeshLocalDetourLateralDistance,
            NavMeshLocalDetourMaxWaypointDistance = behavior.NavMeshLocalDetourMaxWaypointDistance,
            LongRangeRandomRoomTeleportEnabled = behavior.LongRangeRandomRoomTeleportEnabled,
            LongRangeRandomRoomTeleportDistance = behavior.LongRangeRandomRoomTeleportDistance,
            LongRangeRandomRoomTeleportCooldownMs = behavior.LongRangeRandomRoomTeleportCooldownMs,
            LongRangeRandomRoomTeleportSampleDistance = behavior.LongRangeRandomRoomTeleportSampleDistance,
            GlobalVisionMaxVerticalDelta = behavior.GlobalVisionMaxVerticalDelta,
            NavDebugLogging = behavior.NavDebugLogging,
            NavigationExecutionLogIntervalMs = behavior.NavigationExecutionLogIntervalMs,
            EnableAStarFallbackNavigation = behavior.EnableAStarFallbackNavigation,
            AStarFallbackTriggerRadius = behavior.AStarFallbackTriggerRadius,
            AStarFallbackTriggerMs = behavior.AStarFallbackTriggerMs,
            AStarGridStep = behavior.AStarGridStep,
            AStarSearchPadding = behavior.AStarSearchPadding,
            AStarMaxNodeCount = behavior.AStarMaxNodeCount,
            LinearMoveTickThreshold = behavior.LinearMoveTickThreshold,
            RandomStrafeAfterLinearChancePercent = behavior.RandomStrafeAfterLinearChancePercent,
            StrafeDirectionChangeChancePercent = behavior.StrafeDirectionChangeChancePercent,
            EnableAdaptiveCloseRangeStrafing = behavior.EnableAdaptiveCloseRangeStrafing,
            CloseRangeStrafeDistance = behavior.CloseRangeStrafeDistance,
            VeryCloseRangeStrafeDistance = behavior.VeryCloseRangeStrafeDistance,
            CloseRangeStrafeRepeatCount = behavior.CloseRangeStrafeRepeatCount,
            VeryCloseRangeStrafeRepeatCount = behavior.VeryCloseRangeStrafeRepeatCount,
            EnableAdaptiveCloseRangeRetreat = behavior.EnableAdaptiveCloseRangeRetreat,
            CloseRetreatSpeedScale = behavior.CloseRetreatSpeedScale,
            RetreatStartDistanceBuffer = behavior.RetreatStartDistanceBuffer,
            CloseRangeRetreatRepeatCount = behavior.CloseRangeRetreatRepeatCount,
            VeryCloseRangeRetreatRepeatCount = behavior.VeryCloseRangeRetreatRepeatCount,
            EnableNoTargetPatrol = behavior.EnableNoTargetPatrol,
            NoTargetPatrolMinDistance = behavior.NoTargetPatrolMinDistance,
            NoTargetPatrolMaxDistance = behavior.NoTargetPatrolMaxDistance,
            NoTargetPatrolReachDistance = behavior.NoTargetPatrolReachDistance,
            NoTargetPatrolRefreshMs = behavior.NoTargetPatrolRefreshMs,
            PreferredRange = behavior.PreferredRange,
            RangeTolerance = behavior.RangeTolerance,
            StuckDistanceThreshold = behavior.StuckDistanceThreshold,
            NearbyBotAvoidanceRadius = behavior.NearbyBotAvoidanceRadius,
            ForwardStuckJumpThresholdMs = behavior.ForwardStuckJumpThresholdMs,
            ForwardStuckJumpIntervalMs = behavior.ForwardStuckJumpIntervalMs,
            ForwardStuckJumpBurstCount = behavior.ForwardStuckJumpBurstCount,
            MaxHorizontalAimActionsPerTick = behavior.MaxHorizontalAimActionsPerTick,
            MaxVerticalAimActionsPerTick = behavior.MaxVerticalAimActionsPerTick,
            HorizontalAimDeadzoneDegrees = behavior.HorizontalAimDeadzoneDegrees,
            VerticalAimDeadzoneDegrees = behavior.VerticalAimDeadzoneDegrees,
            WalkForwardActionNames = behavior.WalkForwardActionNames,
            WalkBackwardActionNames = behavior.WalkBackwardActionNames,
            WalkLeftActionNames = behavior.WalkLeftActionNames,
            WalkRightActionNames = behavior.WalkRightActionNames,
            JumpActionNames = behavior.JumpActionNames,
            LookHorizontalPositiveActionNames = behavior.LookHorizontalPositiveActionNames,
            LookHorizontalNegativeActionNames = behavior.LookHorizontalNegativeActionNames,
            LookVerticalPositiveActionNames = behavior.LookVerticalPositiveActionNames,
            LookVerticalNegativeActionNames = behavior.LookVerticalNegativeActionNames,
            ShootPressActionName = behavior.ShootPressActionName,
            ShootReleaseActionName = behavior.ShootReleaseActionName,
            AlternateShootPressActionName = behavior.AlternateShootPressActionName,
            ReloadActionName = behavior.ReloadActionName,
            ZoomActionName = behavior.ZoomActionName,
            ZoomReleaseActionName = behavior.ZoomReleaseActionName,
        };
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

    private static void NoNavLog(Player bot, ManagedBotState state, string message)
    {
    }

    private static void NoAimLog(Player bot, ManagedBotState state, string message)
    {
    }

    private static void NoAimDebug(Player bot, ManagedBotState state, Player target, float yaw, float pitch, Vector3 direction)
    {
    }
}
