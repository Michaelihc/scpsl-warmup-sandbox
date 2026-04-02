using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BotMovementService
{
    private readonly BotNavigationService _navigationService = new();

    public bool MoveBot(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction,
        Func<int, int, int> next,
        Func<Player, bool> isManagedBot,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (!behavior.EnableStepMovement)
        {
            return false;
        }

        int nowTick = Environment.TickCount;
        if (state.StuckTicks >= behavior.StuckTickThreshold)
        {
            state.UnstuckUntilTick = nowTick + behavior.UnstuckDurationMs;
            state.StuckTicks = 0;
            state.StrafeDirection = next(0, 2) == 0 ? -1 : 1;
        }

        Vector3 moveTarget = _navigationService.ResolveMoveTarget(bot, state, targetPosition, players, behavior, logNavDebug);
        bool usingNavigation = _navigationService.HasActivePath(state);
        Vector3 toTarget = moveTarget - bot.Position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        float engagementDistance = HorizontalDistance(bot.Position, targetPosition);
        float retreatStartDistance = GetRetreatStartDistance(behavior);
        bool retreatRequired = engagementDistance < retreatStartDistance;
        bool crowded = IsCrowded(bot, players, behavior.NearbyBotAvoidanceRadius, isManagedBot);
        string moveState = retreatRequired
            ? "retreat"
            : engagementDistance > behavior.PreferredRange + behavior.RangeTolerance
                ? "chase"
                : "hold";
        logNavDebug(
            bot,
            state,
            $"move-state state={moveState} range={engagementDistance:F1} retreatAt={retreatStartDistance:F1} preferred={behavior.PreferredRange:F1} tolerance={behavior.RangeTolerance:F1} nav={usingNavigation} crowded={crowded}");
        if (nowTick < state.UnstuckUntilTick || (crowded && distance <= behavior.PreferredRange + behavior.RangeTolerance))
        {
            logNavDebug(bot, state, $"move-branch branch=unstuck retreatRequired={retreatRequired} crowded={crowded} unstuckUntil={state.UnstuckUntilTick}");
            return TryUnstuckMove(bot, state, behavior, engagementDistance, retreatRequired, tryInvokeDummyAction, next, logNavDebug);
        }

        if (next(0, 100) < behavior.StrafeDirectionChangeChancePercent)
        {
            state.StrafeDirection *= -1;
        }

        if (state.ConsecutiveLinearMoves >= behavior.LinearMoveTickThreshold
            && next(0, 100) < behavior.RandomStrafeAfterLinearChancePercent)
        {
            state.ConsecutiveLinearMoves = 0;
            return TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
        }

        if (usingNavigation)
        {
            bool moved = MoveTowardWaypoint(
                bot,
                state,
                moveTarget,
                behavior,
                tryInvokeDummyAction,
                next,
                logNavDebug,
                preferForwardPressure: false,
                engagementDistance: engagementDistance);
            state.ConsecutiveLinearMoves = moved ? state.ConsecutiveLinearMoves + 1 : 0;
            return moved;
        }

        if (distance > behavior.PreferredRange + behavior.RangeTolerance)
        {
            bool moved = tryInvokeDummyAction(bot, GetChaseForwardActionNames(behavior))
                || TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
            state.ConsecutiveLinearMoves = moved ? state.ConsecutiveLinearMoves + 1 : 0;
            return moved;
        }

        if (distance < retreatStartDistance)
        {
            logNavDebug(bot, state, $"move-branch branch=retreat range={engagementDistance:F1}");
            bool retreatMoved = TryInvokeRetreatAction(bot, behavior, engagementDistance, tryInvokeDummyAction)
                || TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
            state.ConsecutiveLinearMoves = retreatMoved ? state.ConsecutiveLinearMoves + 1 : 0;
            return retreatMoved;
        }

        state.ConsecutiveLinearMoves = 0;
        return TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
    }

    public bool PursueTargetDirectly(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction,
        Func<int, int, int> next,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (!behavior.EnableStepMovement)
        {
            return false;
        }

        int nowTick = Environment.TickCount;
        if (state.StuckTicks >= behavior.StuckTickThreshold)
        {
            state.UnstuckUntilTick = nowTick + behavior.UnstuckDurationMs;
            state.StuckTicks = 0;
            state.StrafeDirection = next(0, 2) == 0 ? -1 : 1;
        }

        Vector3 moveTarget = _navigationService.ResolveMoveTarget(bot, state, targetPosition, players, behavior, logNavDebug);
        Vector3 toTarget = moveTarget - bot.Position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        float engagementDistance = HorizontalDistance(bot.Position, targetPosition);
        float retreatStartDistance = GetRetreatStartDistance(behavior);
        bool retreatRequired = engagementDistance < retreatStartDistance;
        string moveState = retreatRequired ? "retreat" : "chase";
        logNavDebug(
            bot,
            state,
            $"move-state state={moveState} range={engagementDistance:F1} retreatAt={retreatStartDistance:F1} preferred={behavior.PreferredRange:F1} tolerance={behavior.RangeTolerance:F1} nav={_navigationService.HasActivePath(state)} pressure=True");
        if (nowTick < state.UnstuckUntilTick)
        {
            logNavDebug(bot, state, $"move-branch branch=unstuck retreatRequired={retreatRequired} crowded=False unstuckUntil={state.UnstuckUntilTick}");
            return TryUnstuckMove(bot, state, behavior, engagementDistance, retreatRequired, tryInvokeDummyAction, next, logNavDebug);
        }

        if (distance <= 0.6f)
        {
            state.ConsecutiveLinearMoves = 0;
            return false;
        }

        if (distance < retreatStartDistance)
        {
            logNavDebug(bot, state, $"move-branch branch=retreat range={engagementDistance:F1} pressure=True");
            bool retreatMoved = TryInvokeRetreatAction(bot, behavior, engagementDistance, tryInvokeDummyAction)
                || TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
            state.ConsecutiveLinearMoves = retreatMoved ? state.ConsecutiveLinearMoves + 1 : 0;
            return retreatMoved;
        }

        bool moved = MoveTowardWaypoint(
            bot,
            state,
            moveTarget,
            behavior,
            tryInvokeDummyAction,
            next,
            logNavDebug,
            preferForwardPressure: true,
            engagementDistance: engagementDistance);
        if (!moved)
        {
            moved = TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
        }

        state.ConsecutiveLinearMoves = moved ? state.ConsecutiveLinearMoves + 1 : 0;
        return moved;
    }

    public void UpdateStuckState(Player bot, ManagedBotState state, float stuckDistanceThreshold, bool movementExpected)
    {
        if (!movementExpected)
        {
            state.StuckTicks = 0;
            state.UnstuckUntilTick = 0;
            state.LastPosition = bot.Position;
            return;
        }

        Vector3 current = bot.Position;
        Vector3 previous = state.LastPosition;
        current.y = 0f;
        previous.y = 0f;

        float movedDistance = Vector3.Distance(current, previous);
        state.StuckTicks = movedDistance < stuckDistanceThreshold ? state.StuckTicks + 1 : 0;
        state.LastPosition = bot.Position;
    }

    private static bool IsCrowded(Player bot, IEnumerable<Player> players, float radius, Func<Player, bool> isManagedBot)
    {
        float radiusSquared = radius * radius;
        foreach (Player other in players)
        {
            if (other.PlayerId == bot.PlayerId || !isManagedBot(other) || other.IsDestroyed || other.Role == RoleTypeId.Spectator)
            {
                continue;
            }

            Vector3 offset = other.Position - bot.Position;
            offset.y = 0f;
            if (offset.sqrMagnitude <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryUnstuckMove(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        float engagementDistance,
        bool retreatRequired,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction,
        Func<int, int, int> next,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        int roll = next(0, 100);
        state.StrafeDirection = next(0, 2) == 0 ? -1 : 1;
        bool chaseRequired = engagementDistance > behavior.PreferredRange + behavior.RangeTolerance;

        if (retreatRequired)
        {
            logNavDebug(bot, state, $"move-action branch=unstuck mode=retreat-first roll={roll}");
            return roll < 55
                ? TryInvokeRetreatAction(bot, behavior, engagementDistance, tryInvokeDummyAction)
                    || TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next)
                : TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next)
                    || TryInvokeRetreatAction(bot, behavior, engagementDistance, tryInvokeDummyAction);
        }

        if (chaseRequired)
        {
            logNavDebug(bot, state, $"move-action branch=unstuck mode=chase-forward-only roll={roll}");
            return roll < 35
                ? TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next)
                    || tryInvokeDummyAction(bot, GetChaseForwardActionNames(behavior))
                : tryInvokeDummyAction(bot, GetChaseForwardActionNames(behavior))
                    || TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
        }

        logNavDebug(bot, state, $"move-action branch=unstuck mode=hold-strafe-only roll={roll}");
        return TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next)
            || tryInvokeDummyAction(bot, GetChaseForwardActionNames(behavior));
    }

    private static bool TryStrafeMove(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        float engagementDistance,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction,
        Func<int, int, int> next)
    {
        if (next(0, 100) < behavior.StrafeDirectionChangeChancePercent)
        {
            state.StrafeDirection *= -1;
        }

        string[] primaryActions = state.StrafeDirection >= 0
            ? behavior.WalkRightActionNames
            : behavior.WalkLeftActionNames;
        if (TryInvokeStrafeAction(bot, primaryActions, behavior, engagementDistance, tryInvokeDummyAction))
        {
            return true;
        }

        string[] fallbackActions = state.StrafeDirection >= 0
            ? behavior.WalkLeftActionNames
            : behavior.WalkRightActionNames;
        return TryInvokeStrafeAction(bot, fallbackActions, behavior, engagementDistance, tryInvokeDummyAction);
    }

    private static bool MoveTowardWaypoint(
        Player bot,
        ManagedBotState state,
        Vector3 waypoint,
        BotBehaviorDefinition behavior,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction,
        Func<int, int, int> next,
        Action<Player, ManagedBotState, string> logNavDebug,
        bool preferForwardPressure,
        float engagementDistance)
    {
        Vector3 toWaypoint = waypoint - bot.Position;
        toWaypoint.y = 0f;
        if (toWaypoint.sqrMagnitude < behavior.NavWaypointReachDistance * behavior.NavWaypointReachDistance)
        {
            state.ConsecutiveLinearMoves = 0;
            return false;
        }

        float movementYaw = NormalizeSignedAngle(bot.LookRotation.y);
        Quaternion yawRotation = Quaternion.Euler(0f, movementYaw, 0f);
        Vector3 forward = yawRotation * Vector3.forward;
        Vector3 right = yawRotation * Vector3.right;
        Vector3 direction = toWaypoint.normalized;
        float forwardDot = Vector3.Dot(direction, forward);
        float rightDot = Vector3.Dot(direction, right);

        List<string[]> orderedMoves = new();
        if (preferForwardPressure && forwardDot >= -0.1f)
        {
            orderedMoves.Add(GetChaseForwardActionNames(behavior));
            orderedMoves.Add(rightDot >= 0f ? behavior.WalkRightActionNames : behavior.WalkLeftActionNames);
        }
        else if (Mathf.Abs(rightDot) >= Mathf.Abs(forwardDot))
        {
            orderedMoves.Add(rightDot >= 0f ? behavior.WalkRightActionNames : behavior.WalkLeftActionNames);
            if (forwardDot >= 0.15f)
            {
                orderedMoves.Add(GetChaseForwardActionNames(behavior));
            }
            else if (forwardDot <= -0.15f)
            {
                orderedMoves.Add(behavior.WalkBackwardActionNames);
            }
        }
        else
        {
            if (forwardDot >= 0f)
            {
                orderedMoves.Add(GetChaseForwardActionNames(behavior));
                orderedMoves.Add(rightDot >= 0f ? behavior.WalkRightActionNames : behavior.WalkLeftActionNames);
            }
            else
            {
                orderedMoves.Add(behavior.WalkBackwardActionNames);
                orderedMoves.Add(rightDot >= 0f ? behavior.WalkRightActionNames : behavior.WalkLeftActionNames);
            }
        }

        orderedMoves.Add(TryGetOppositeLateralActions(rightDot, behavior));
        orderedMoves.Add(forwardDot >= 0f ? behavior.WalkBackwardActionNames : GetChaseForwardActionNames(behavior));

        foreach (string[] moveSet in orderedMoves)
        {
            if (moveSet.Length == 0)
            {
                continue;
            }

            if (tryInvokeDummyAction(bot, moveSet))
            {
                logNavDebug(
                    bot,
                    state,
                    $"move-step waypoint={FormatWaypoint(waypoint)} lookYaw={movementYaw:F1} forwardDot={forwardDot:F2} rightDot={rightDot:F2} pressure={preferForwardPressure} action={string.Join(",", moveSet)}");
                return true;
            }
        }

            return TryStrafeMove(bot, state, behavior, engagementDistance, tryInvokeDummyAction, next);
        }

    private static string[] TryGetOppositeLateralActions(float rightDot, BotBehaviorDefinition behavior)
    {
        return rightDot >= 0f ? behavior.WalkLeftActionNames : behavior.WalkRightActionNames;
    }

    private static bool TryInvokeStrafeAction(
        Player bot,
        string[] actionNames,
        BotBehaviorDefinition behavior,
        float engagementDistance,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction)
    {
        int repeatCount = GetStrafeRepeatCount(behavior, engagementDistance);
        string[] preferredActionNames = GetPreferredStrafeActionNames(actionNames, behavior, engagementDistance);
        for (int i = 0; i < repeatCount; i++)
        {
            if (!tryInvokeDummyAction(bot, preferredActionNames))
            {
                return i > 0;
            }
        }

        return true;
    }

    private static string[] GetPreferredStrafeActionNames(
        string[] actionNames,
        BotBehaviorDefinition behavior,
        float engagementDistance)
    {
        if (actionNames == null || actionNames.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (!behavior.EnableAdaptiveCloseRangeStrafing || behavior.AiMode != WarmupAiMode.Realistic)
        {
            return actionNames;
        }

        if (engagementDistance <= behavior.VeryCloseRangeStrafeDistance)
        {
            return TakeLeadingActions(actionNames, 1);
        }

        if (engagementDistance <= behavior.CloseRangeStrafeDistance)
        {
            return TakeLeadingActions(actionNames, 2);
        }

        return actionNames;
    }

    private static string[] TakeLeadingActions(string[] actionNames, int count)
    {
        if (actionNames == null || actionNames.Length == 0 || count <= 0)
        {
            return Array.Empty<string>();
        }

        if (actionNames.Length <= count)
        {
            return actionNames;
        }

        string[] result = new string[count];
        Array.Copy(actionNames, result, count);
        return result;
    }

    private static bool TryInvokeRetreatAction(
        Player bot,
        BotBehaviorDefinition behavior,
        float engagementDistance,
        Func<Player, IEnumerable<string>, bool> tryInvokeDummyAction)
    {
        int repeatCount = GetRetreatRepeatCount(behavior, engagementDistance);
        string[] actionNames = behavior.WalkBackwardActionNames;
        for (int i = 0; i < repeatCount; i++)
        {
            if (!tryInvokeDummyAction(bot, actionNames))
            {
                return i > 0;
            }
        }

        return true;
    }

    private static int GetStrafeRepeatCount(BotBehaviorDefinition behavior, float engagementDistance)
    {
        if (!behavior.EnableAdaptiveCloseRangeStrafing || behavior.AiMode != WarmupAiMode.Realistic)
        {
            return 1;
        }

        if (engagementDistance <= behavior.VeryCloseRangeStrafeDistance)
        {
            return Math.Max(1, behavior.VeryCloseRangeStrafeRepeatCount);
        }

        if (engagementDistance <= behavior.CloseRangeStrafeDistance)
        {
            return Math.Max(1, behavior.CloseRangeStrafeRepeatCount);
        }

        return 1;
    }

    private static int GetRetreatRepeatCount(BotBehaviorDefinition behavior, float engagementDistance)
    {
        if (!behavior.EnableAdaptiveCloseRangeRetreat || behavior.AiMode != WarmupAiMode.Realistic)
        {
            return 1;
        }

        if (engagementDistance <= behavior.VeryCloseRangeStrafeDistance)
        {
            return Math.Max(1, behavior.VeryCloseRangeRetreatRepeatCount);
        }

        if (engagementDistance <= behavior.CloseRangeStrafeDistance)
        {
            return Math.Max(1, behavior.CloseRangeRetreatRepeatCount);
        }

        return 1;
    }

    private static float GetRetreatStartDistance(BotBehaviorDefinition behavior)
    {
        return behavior.PreferredRange - behavior.RangeTolerance + behavior.RetreatStartDistanceBuffer;
    }

    private static string[] GetChaseForwardActionNames(BotBehaviorDefinition behavior)
    {
        if (behavior.WalkForwardActionNames == null || behavior.WalkForwardActionNames.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (behavior.WalkForwardActionNames.Length == 1)
        {
            return behavior.WalkForwardActionNames;
        }

        return new[]
        {
            behavior.WalkForwardActionNames[0],
            behavior.WalkForwardActionNames[1],
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

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static string FormatWaypoint(Vector3 waypoint)
    {
        return $"({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1})";
    }
}
