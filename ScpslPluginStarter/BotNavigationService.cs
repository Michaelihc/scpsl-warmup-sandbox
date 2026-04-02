using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BotNavigationService
{
    public Vector3 ResolveMoveTarget(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        targetPosition.y = bot.Position.y;
        state.LastMoveUsedNavigation = false;

        if (!behavior.EnableObstacleNavigation)
        {
            ClearPath(state, targetPosition, "disabled");
            return targetPosition;
        }

        int nowTick = Environment.TickCount;
        AdvanceWaypoints(bot, state, behavior.NavWaypointReachDistance, targetPosition, logNavDebug);
        bool hasActiveWaypoint = HasActiveWaypoint(state);
        bool directPathClear = IsPathClear(bot.Position, targetPosition, players);

        if (directPathClear)
        {
            if (hasActiveWaypoint)
            {
                logNavDebug(bot, state, "clear-path direct route restored, clearing detour path");
            }

            ClearPath(state, targetPosition, "direct");
            return targetPosition;
        }

        bool targetMoved = HorizontalDistance(state.LastNavigationTarget, targetPosition) >= behavior.NavTargetMoveRecomputeDistance;
        bool pathStale = unchecked(nowTick - state.LastNavigationRecomputeTick) >= behavior.NavRecomputeIntervalMs;
        bool waypointBlocked = hasActiveWaypoint && !IsPathClear(bot.Position, state.NavigationWaypoints[state.NavigationWaypointIndex], players);

        if (hasActiveWaypoint && !targetMoved && !pathStale && !waypointBlocked)
        {
            state.LastMoveUsedNavigation = true;
            state.LastNavigationReason = "cached";
            return state.NavigationWaypoints[state.NavigationWaypointIndex];
        }

        if (!hasActiveWaypoint && unchecked(state.NavigationPathFailedUntilTick - nowTick) > 0)
        {
            state.LastNavigationReason = "cooldown";
            return targetPosition;
        }

        if (TryBuildPath(bot.Position, targetPosition, players, behavior, out List<Vector3> path, out string reason))
        {
            state.NavigationWaypoints.Clear();
            state.NavigationWaypoints.AddRange(path);
            state.NavigationWaypointIndex = 0;
            state.LastNavigationTarget = targetPosition;
            state.LastNavigationRecomputeTick = nowTick;
            state.NavigationPathFailedUntilTick = 0;
            state.LastMoveUsedNavigation = true;
            state.LastNavigationReason = reason;
            logNavDebug(
                bot,
                state,
                $"path-selected reason={reason} waypoints={FormatWaypoints(state.NavigationWaypoints)} target=({targetPosition.x:F1},{targetPosition.y:F1},{targetPosition.z:F1})");
            return state.NavigationWaypoints[0];
        }

        state.LastNavigationTarget = targetPosition;
        state.LastNavigationRecomputeTick = nowTick;
        state.LastNavigationReason = hasActiveWaypoint ? "path-refresh-failed" : "path-failed";

        if (hasActiveWaypoint)
        {
            state.LastMoveUsedNavigation = true;
            logNavDebug(bot, state, $"path-refresh-failed keeping={FormatWaypoint(state.NavigationWaypoints[state.NavigationWaypointIndex])}");
            return state.NavigationWaypoints[state.NavigationWaypointIndex];
        }

        state.NavigationPathFailedUntilTick = nowTick + behavior.NavPathFailedCooldownMs;
        logNavDebug(bot, state, $"path-failed cooldownMs={behavior.NavPathFailedCooldownMs}");
        return targetPosition;
    }

    public bool HasActivePath(ManagedBotState state)
    {
        return HasActiveWaypoint(state);
    }

    private static bool TryBuildPath(
        Vector3 botPosition,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        out List<Vector3> path,
        out string reason)
    {
        path = new List<Vector3>();
        reason = "none";

        List<Vector3> detourCandidates = GenerateDetourCandidates(botPosition, targetPosition, behavior.NavProbeDistance, behavior.NavLateralProbeCount);
        float bestSingleScore = float.MaxValue;
        Vector3 bestSingle = default;
        bool foundSingle = false;

        foreach (Vector3 candidate in detourCandidates)
        {
            if (!IsPathClear(botPosition, candidate, players) || !IsPathClear(candidate, targetPosition, players))
            {
                continue;
            }

            float score = HorizontalDistance(botPosition, candidate) + HorizontalDistance(candidate, targetPosition);
            if (!foundSingle || score < bestSingleScore)
            {
                foundSingle = true;
                bestSingleScore = score;
                bestSingle = candidate;
            }
        }

        if (foundSingle)
        {
            path.Add(bestSingle);
            reason = "single-hop";
            return true;
        }

        List<Vector3> approachCandidates = GenerateApproachCandidates(botPosition, targetPosition, behavior.NavProbeDistance, behavior.NavLateralProbeCount);
        float bestDoubleScore = float.MaxValue;
        Vector3 bestFirst = default;
        Vector3 bestSecond = default;
        bool foundDouble = false;

        foreach (Vector3 first in detourCandidates)
        {
            if (!IsPathClear(botPosition, first, players))
            {
                continue;
            }

            foreach (Vector3 second in approachCandidates)
            {
                if (!IsPathClear(first, second, players) || !IsPathClear(second, targetPosition, players))
                {
                    continue;
                }

                float score = HorizontalDistance(botPosition, first)
                    + HorizontalDistance(first, second)
                    + HorizontalDistance(second, targetPosition);
                if (!foundDouble || score < bestDoubleScore)
                {
                    foundDouble = true;
                    bestDoubleScore = score;
                    bestFirst = first;
                    bestSecond = second;
                }
            }
        }

        if (!foundDouble)
        {
            return false;
        }

        path.Add(bestFirst);
        path.Add(bestSecond);
        reason = "double-hop";
        return true;
    }

    private static List<Vector3> GenerateDetourCandidates(Vector3 botPosition, Vector3 targetPosition, float probeDistance, int lateralProbeCount)
    {
        List<Vector3> candidates = new();
        Vector3 forward = targetPosition - botPosition;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        float step = Mathf.Max(0.8f, probeDistance);
        int count = Math.Max(1, lateralProbeCount);

        for (int ring = 1; ring <= count; ring++)
        {
            float lateral = step * ring;
            float nearForward = step * 0.5f * ring;
            float farForward = step * 0.9f * ring;
            foreach (int sign in new[] { -1, 1 })
            {
                Vector3 side = right * (sign * lateral);
                candidates.Add(botPosition + side);
                candidates.Add(botPosition + side + forward * nearForward);
                candidates.Add(botPosition + side + forward * farForward);
            }
        }

        return candidates;
    }

    private static List<Vector3> GenerateApproachCandidates(Vector3 botPosition, Vector3 targetPosition, float probeDistance, int lateralProbeCount)
    {
        List<Vector3> candidates = new();
        Vector3 forward = targetPosition - botPosition;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        float step = Mathf.Max(0.8f, probeDistance);
        int count = Math.Max(1, lateralProbeCount);

        for (int ring = 1; ring <= count; ring++)
        {
            float lateral = step * 0.7f * ring;
            float back = step * 0.7f * ring;
            foreach (int sign in new[] { -1, 1 })
            {
                Vector3 side = right * (sign * lateral);
                candidates.Add(targetPosition - forward * back + side);
                candidates.Add(targetPosition - forward * (back * 1.4f) + side);
            }
        }

        return candidates;
    }

    private static bool IsPathClear(Vector3 start, Vector3 end, IEnumerable<Player> players)
    {
        Vector3[] sampleOffsets =
        {
            Vector3.up * 0.25f,
            Vector3.up * 0.9f,
            Vector3.up * 1.35f,
        };

        Player[] playerArray = players as Player[] ?? new List<Player>(players).ToArray();
        foreach (Vector3 offset in sampleOffsets)
        {
            if (IsSegmentBlocked(start + offset, end + offset, playerArray))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSegmentBlocked(Vector3 start, Vector3 end, Player[] players)
    {
        Vector3 direction = end - start;
        float distance = direction.magnitude;
        if (distance < 0.05f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(start, direction.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

        foreach (RaycastHit hit in hits)
        {
            Transform hitTransform = hit.transform;
            if (hitTransform == null)
            {
                continue;
            }

            if (IsIgnoredPlayerTransform(hitTransform, players))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsIgnoredPlayerTransform(Transform hitTransform, IEnumerable<Player> players)
    {
        foreach (Player player in players)
        {
            if (player.IsDestroyed || player.ReferenceHub == null)
            {
                continue;
            }

            Transform root = player.ReferenceHub.transform;
            if (hitTransform == root || hitTransform.IsChildOf(root))
            {
                return true;
            }
        }

        return false;
    }

    private static void AdvanceWaypoints(
        Player bot,
        ManagedBotState state,
        float reachDistance,
        Vector3 targetPosition,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        while (HasActiveWaypoint(state))
        {
            Vector3 waypoint = state.NavigationWaypoints[state.NavigationWaypointIndex];
            if (HorizontalDistance(bot.Position, waypoint) > reachDistance)
            {
                break;
            }

            logNavDebug(bot, state, $"waypoint-reached index={state.NavigationWaypointIndex} point={FormatWaypoint(waypoint)}");
            state.NavigationWaypointIndex++;
        }

        if (!HasActiveWaypoint(state) && state.NavigationWaypoints.Count > 0)
        {
            ClearPath(state, targetPosition, "complete");
        }
    }

    private static bool HasActiveWaypoint(ManagedBotState state)
    {
        return state.NavigationWaypointIndex >= 0
            && state.NavigationWaypointIndex < state.NavigationWaypoints.Count;
    }

    private static void ClearPath(ManagedBotState state, Vector3 targetPosition, string reason)
    {
        state.NavigationWaypoints.Clear();
        state.NavigationWaypointIndex = 0;
        state.LastNavigationTarget = targetPosition;
        state.LastNavigationReason = reason;
        state.LastMoveUsedNavigation = false;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string FormatWaypoints(IEnumerable<Vector3> waypoints)
    {
        List<string> pieces = new();
        foreach (Vector3 waypoint in waypoints)
        {
            pieces.Add(FormatWaypoint(waypoint));
        }

        return pieces.Count == 0 ? "[]" : $"[{string.Join(";", pieces)}]";
    }

    private static string FormatWaypoint(Vector3 waypoint)
    {
        return $"({waypoint.x:F1},{waypoint.y:F1},{waypoint.z:F1})";
    }
}
