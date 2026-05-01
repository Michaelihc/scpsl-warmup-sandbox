using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using UnityEngine;
using UnityEngine.AI;

namespace ScpslPluginStarter;

internal sealed class BotNavigationService
{
    private const float NodeClearanceRadius = 0.4f;
    private const float PathClearanceRadius = 0.34f;
    private const float PathClearanceLateralOffset = 0.26f;
    private const float WaypointLinkDistance = 8.75f;
    private const float WaypointGoalReachDistance = 1.2f;
    private const float TightWaypointCompletionDistance = 1.45f;
    private const float StuckWaypointSoftAdvanceDistance = 2.4f;
    private const float MaxWaypointVerticalDelta = 3.25f;
    private const float AgentProxyWarpDistance = 2.0f;
    private static readonly float[] AgentPlacementVerticalOffsets = { 0.0f, 0.05f, 0.25f, 0.5f };
    private const int MaxAnchorCandidates = 16;
    private const int MaxFallbackAnchorCandidates = 6;
    private const float ForcedWaypointMatchDistance = 1.35f;
    private const float GateBridgeSoftAdvanceDistance = 4.25f;
    private static readonly Vector3 GateBridgeWorldPoint = new(-5.795f, 28.395f, -20.178f);
    private static readonly Vector3[] GateBridgeForcedNeighbors =
    {
        new(-7.30f, 28.83f, -17.91f),
        new(-6.00f, 28.83f, -24.00f),
    };

    private readonly List<Vector3> _arenaWaypoints = new();
    private List<int>[]? _arenaWaypointLinks;

    public void SetArenaWaypoints(IEnumerable<Vector3> waypoints)
    {
        _arenaWaypoints.Clear();
        _arenaWaypointLinks = null;

        if (waypoints == null)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Vector3 waypoint in waypoints)
        {
            Vector3 flat = waypoint;
            string key = $"{flat.x:F3}|{flat.y:F3}|{flat.z:F3}";
            if (seen.Add(key))
            {
                _arenaWaypoints.Add(flat);
            }
        }

        BuildArenaWaypointGraph();
    }

    public void ClearArenaWaypoints()
    {
        _arenaWaypoints.Clear();
        _arenaWaypointLinks = null;
    }

    public int GetArenaWaypointCount()
    {
        return _arenaWaypoints.Count;
    }

    public bool IsMoveDirectionClear(Player bot, Vector3 direction, float distance, IEnumerable<Player> players)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f || distance <= 0.05f)
        {
            return true;
        }

        Vector3 end = bot.Position + (direction.normalized * distance);
        end.y = bot.Position.y;
        return IsPathClear(bot.Position, end, players);
    }

    public Vector3 ResolveMoveTarget(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        bool useRuntimeNavMesh,
        float runtimeNavMeshSampleDistance,
        bool useSwiftStyleNavMeshPath,
        bool stopOnNavigationFailure,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        state.LastMoveUsedNavigation = false;
        if (!behavior.EnableObstacleNavigation)
        {
            logNavDebug(bot, state, $"skip reason=obstacle-navigation-disabled target={FormatWaypoint(targetPosition)}");
            ClearPath(state, targetPosition, "disabled");
            return targetPosition;
        }

        if (useRuntimeNavMesh
            && TryResolveNavMeshMoveTarget(bot, state, targetPosition, behavior, runtimeNavMeshSampleDistance, useSwiftStyleNavMeshPath, logNavDebug, out Vector3 navMeshTarget))
        {
            return navMeshTarget;
        }

        logNavDebug(
            bot,
            state,
            $"fallback reason={(useRuntimeNavMesh ? "navmesh-path-failed" : "navmesh-unavailable")} stop={stopOnNavigationFailure} " +
            $"bot={FormatWaypoint(bot.Position)} target={FormatWaypoint(targetPosition)} sample={runtimeNavMeshSampleDistance:F1}");
        ClearPath(state, targetPosition, useRuntimeNavMesh ? "navmesh-path-failed" : "navmesh-unavailable");
        return stopOnNavigationFailure ? bot.Position : targetPosition;
    }

    private static float GetNavigationTargetDelta(Vector3 previousTarget, Vector3 currentTarget, bool useFull3D)
    {
        return useFull3D
            ? Vector3.Distance(previousTarget, currentTarget)
            : HorizontalDistance(previousTarget, currentTarget);
    }

    public bool HasActivePath(ManagedBotState state)
    {
        return HasActiveWaypoint(state);
    }

    public void ForceRepath(ManagedBotState state, string reason, Vector3? warpPosition = null)
    {
        if (state.NavigationAgent != null)
        {
            NavMeshAgent agent = state.NavigationAgent;
            if (warpPosition.HasValue)
            {
                Vector3 position = warpPosition.Value;
                if ((!agent.enabled || !agent.isOnNavMesh)
                    && !TryEnableAgentAtPosition(agent, position))
                {
                    state.DestroyNavigationAgent();
                }
                else if (agent.enabled && agent.isOnNavMesh)
                {
                    if (agent.Warp(position))
                    {
                        agent.nextPosition = position;
                        agent.transform.position = position;
                        agent.isStopped = false;
                        agent.ResetPath();
                    }
                    else
                    {
                        state.DestroyNavigationAgent();
                    }
                }
            }
            else if (agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
            }
        }

        state.NavigationWaypoints.Clear();
        state.NavigationWaypointIndex = 0;
        state.LastNavigationTarget = default;
        state.LastNavigationRecomputeTick = 0;
        state.NavigationPathFailedUntilTick = 0;
        state.LastNavigationReason = reason;
        state.LastMoveUsedNavigation = false;
    }

    public bool TrySoftAdvanceStuckWaypoint(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (!HasActiveWaypoint(state)
            || state.NavigationWaypointIndex + 1 >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        Vector3 waypoint = state.NavigationWaypoints[state.NavigationWaypointIndex];
        float waypointDistance = HorizontalDistance(bot.Position, waypoint);
        float softAdvanceDistance = Mathf.Max(
            StuckWaypointSoftAdvanceDistance,
            behavior.NavWaypointReachDistance * 2.5f);
        if (waypointDistance > softAdvanceDistance)
        {
            return false;
        }

        state.NavigationWaypointIndex++;
        state.LastMoveUsedNavigation = true;
        state.LastNavigationReason = "stuck-skip-close-corner";
        logNavDebug(
            bot,
            state,
            $"stuck-waypoint-skip index={state.NavigationWaypointIndex - 1} point={FormatWaypoint(waypoint)} dist={waypointDistance:F2} next={FormatWaypoint(state.NavigationWaypoints[state.NavigationWaypointIndex])}");
        return true;
    }

    public bool TryInsertLocalDetourWaypoint(
        Player bot,
        ManagedBotState state,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        if (!behavior.NavMeshLocalDetourEnabled
            || !HasActiveWaypoint(state)
            || state.NavigationWaypointIndex < 0
            || state.NavigationWaypointIndex >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        Vector3 waypoint = state.NavigationWaypoints[state.NavigationWaypointIndex];
        float waypointDistance = HorizontalDistance(bot.Position, waypoint);
        if (waypointDistance < 1.5f
            || waypointDistance > Mathf.Max(2.0f, behavior.NavMeshLocalDetourMaxWaypointDistance))
        {
            return false;
        }

        float sampleDistance = Mathf.Clamp(behavior.FacilityNavMeshSampleDistance, 0.75f, 2.5f);
        if (!NavMesh.SamplePosition(bot.Position, out NavMeshHit currentHit, sampleDistance, NavMesh.AllAreas))
        {
            return false;
        }

        Vector3 forward = waypoint - currentHit.position;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        forward.Normalize();
        Vector3 right = new(forward.z, 0f, -forward.x);
        float forwardDistance = Mathf.Max(0.5f, behavior.NavMeshLocalDetourForwardDistance);
        float lateralDistance = Mathf.Max(0.5f, behavior.NavMeshLocalDetourLateralDistance);
        int preferredSide = state.StrafeDirection >= 0 ? 1 : -1;
        int[] sides = { preferredSide, -preferredSide };
        float[] lateralScales = { 1.0f, 1.5f, 2.25f, 3.0f, 0.65f };
        float[] forwardScales = { 0.75f, 1.25f, 0.0f, -0.5f, 1.75f, -1.0f };
        bool directSegmentClear = IsPathClear(bot.Position, waypoint, Array.Empty<Player>());
        bool foundCandidate = false;
        int bestSide = preferredSide;
        float bestScore = float.PositiveInfinity;
        Vector3 bestCandidate = default;

        foreach (int side in sides)
        {
            foreach (float lateralScale in lateralScales)
            {
                foreach (float forwardScale in forwardScales)
                {
                    Vector3 candidate = currentHit.position
                        + (forward * forwardDistance * forwardScale)
                        + (right * side * lateralDistance * lateralScale);
                    if (!NavMesh.SamplePosition(candidate, out NavMeshHit candidateHit, 2.25f, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    if (Mathf.Abs(candidateHit.position.y - currentHit.position.y) > MaxWaypointVerticalDelta)
                    {
                        continue;
                    }

                    float candidateDistance = HorizontalDistance(candidateHit.position, currentHit.position);
                    float candidateRemaining = HorizontalDistance(candidateHit.position, waypoint);
                    if (candidateDistance < 0.75f)
                    {
                        continue;
                    }

                    if (IsDuplicateDetourWaypoint(state, candidateHit.position))
                    {
                        continue;
                    }

                    NavMeshPath path = new();
                    if (!NavMesh.CalculatePath(currentHit.position, candidateHit.position, NavMesh.AllAreas, path)
                        || path.status != NavMeshPathStatus.PathComplete)
                    {
                        continue;
                    }

                    if (!IsPathClear(bot.Position, candidateHit.position, Array.Empty<Player>()))
                    {
                        continue;
                    }

                    NavMeshPath remainingPath = new();
                    if (!NavMesh.CalculatePath(candidateHit.position, waypoint, NavMesh.AllAreas, remainingPath)
                        || remainingPath.status == NavMeshPathStatus.PathInvalid)
                    {
                        continue;
                    }

                    bool clearsToWaypoint = IsPathClear(candidateHit.position, waypoint, Array.Empty<Player>());
                    float progress = waypointDistance - candidateRemaining;
                    float score = candidateDistance * 0.8f
                        + candidateRemaining
                        - (progress * 0.35f)
                        + (clearsToWaypoint ? -8.0f : 5.0f)
                        + (remainingPath.status == NavMeshPathStatus.PathComplete ? -2.0f : 4.0f)
                        + (side == preferredSide ? 0.0f : 1.0f);

                    if (directSegmentClear && progress < -1.5f)
                    {
                        score += 8.0f;
                    }

                    if (!foundCandidate || score < bestScore)
                    {
                        foundCandidate = true;
                        bestScore = score;
                        bestSide = side;
                        bestCandidate = candidateHit.position;
                    }
                }
            }
        }

        if (!foundCandidate)
        {
            return false;
        }

        state.NavigationWaypoints.Insert(state.NavigationWaypointIndex, bestCandidate);
        state.LastMoveUsedNavigation = true;
        state.LastNavigationReason = "local-detour";
        state.StrafeDirection = bestSide;
        logNavDebug(
            bot,
            state,
            $"local-detour-insert index={state.NavigationWaypointIndex} side={(bestSide >= 0 ? "right" : "left")} point={FormatWaypoint(bestCandidate)} wp={FormatWaypoint(waypoint)} wpDist={waypointDistance:F2} directClear={directSegmentClear} score={bestScore:F1}");
        return true;
    }

    private static bool IsDuplicateDetourWaypoint(ManagedBotState state, Vector3 candidate)
    {
        int start = Math.Max(0, state.NavigationWaypointIndex - 2);
        int end = Math.Min(state.NavigationWaypoints.Count - 1, state.NavigationWaypointIndex + 3);
        for (int i = start; i <= end; i++)
        {
            Vector3 existing = state.NavigationWaypoints[i];
            if (Mathf.Abs(existing.y - candidate.y) <= 0.5f
                && HorizontalDistance(existing, candidate) <= 0.6f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveNavMeshMoveTarget(
        Player bot,
        ManagedBotState state,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        float sampleDistance,
        bool useSwiftStyleNavMeshPath,
        Action<Player, ManagedBotState, string> logNavDebug,
        out Vector3 moveTarget)
    {
        moveTarget = targetPosition;
        int nowTick = Environment.TickCount;
        float safeSampleDistance = Mathf.Max(0.25f, sampleDistance);
        bool sampledStart = NavMesh.SamplePosition(bot.Position, out NavMeshHit startHit, safeSampleDistance, NavMesh.AllAreas);
        bool sampledTarget = NavMesh.SamplePosition(targetPosition, out NavMeshHit targetHit, safeSampleDistance, NavMesh.AllAreas);
        if (!sampledStart || !sampledTarget)
        {
            logNavDebug(
                bot,
                state,
                $"sample-failed start={sampledStart} target={sampledTarget} sample={safeSampleDistance:F1} " +
                $"bot={FormatWaypoint(bot.Position)} target={FormatWaypoint(targetPosition)}");
            return false;
        }

        if (useSwiftStyleNavMeshPath
            && TryResolveSwiftStyleNavMeshMoveTarget(
                bot,
                state,
                startHit.position,
                targetHit.position,
                behavior,
                nowTick,
                logNavDebug,
                out moveTarget))
        {
            return true;
        }

        if (!useSwiftStyleNavMeshPath
            && TryResolveNavMeshAgentMoveTarget(
                bot,
                state,
                startHit.position,
                targetHit.position,
                behavior,
                NavMeshAgentTypeUtility.DefaultAgentTypeId,
                nowTick,
                logNavDebug,
                out moveTarget))
        {
            return true;
        }

        logNavDebug(
            bot,
            state,
            $"{(useSwiftStyleNavMeshPath ? "swift-path-failed" : "agent-path-failed")} start={FormatWaypoint(startHit.position)} target={FormatWaypoint(targetHit.position)} rawBot={FormatWaypoint(bot.Position)}");
        ClearPath(state, targetPosition, useSwiftStyleNavMeshPath ? "navmesh-swift-path-failed" : "navmesh-agent-path-failed");
        return false;
    }

    private static bool TryResolveSwiftStyleNavMeshMoveTarget(
        Player bot,
        ManagedBotState state,
        Vector3 sampledStart,
        Vector3 sampledTarget,
        BotBehaviorDefinition behavior,
        int nowTick,
        Action<Player, ManagedBotState, string> logNavDebug,
        out Vector3 moveTarget)
    {
        moveTarget = sampledTarget;

        bool targetMoved = GetNavigationTargetDelta(
            state.LastNavigationTarget,
            sampledTarget,
            useFull3D: true) >= Mathf.Max(0.25f, behavior.NavTargetMoveRecomputeDistance);
        bool pathStale = unchecked(nowTick - state.LastNavigationRecomputeTick) >= Math.Max(100, behavior.NavRecomputeIntervalMs);
        if (!HasActiveWaypoint(state) || targetMoved || pathStale)
        {
            NavMeshPath path = new();
            if (!NavMesh.CalculatePath(sampledStart, sampledTarget, NavMesh.AllAreas, path)
                || path.status == NavMeshPathStatus.PathInvalid)
            {
                return false;
            }

            state.NavigationWaypoints.Clear();
            Vector3[] corners = path.corners ?? Array.Empty<Vector3>();
            for (int i = 0; i < corners.Length; i++)
            {
                if (i == 0 && HorizontalDistance(sampledStart, corners[i]) <= Mathf.Max(behavior.NavWaypointReachDistance, 0.75f))
                {
                    continue;
                }

                state.NavigationWaypoints.Add(corners[i]);
            }

            Vector3 lastWaypoint = state.NavigationWaypoints.Count > 0
                ? state.NavigationWaypoints[state.NavigationWaypoints.Count - 1]
                : sampledStart;
            bool targetAlreadyIncluded = HorizontalDistance(lastWaypoint, sampledTarget) <= 0.2f
                && Mathf.Abs(lastWaypoint.y - sampledTarget.y) <= 0.5f;
            bool canAppendTarget = path.status == NavMeshPathStatus.PathComplete
                || Mathf.Abs(lastWaypoint.y - sampledTarget.y) <= MaxWaypointVerticalDelta;

            if (!targetAlreadyIncluded && canAppendTarget)
            {
                state.NavigationWaypoints.Add(sampledTarget);
            }

            state.NavigationWaypointIndex = 0;
            state.LastNavigationTarget = sampledTarget;
            state.LastNavigationRecomputeTick = nowTick;
            state.NavigationPathFailedUntilTick = 0;
            state.LastMoveUsedNavigation = true;
            state.LastNavigationReason = path.status == NavMeshPathStatus.PathComplete
                ? "navmesh-swift-corners"
                : "navmesh-swift-corners-partial";
            logNavDebug(
                bot,
                state,
                $"swift-path reason={state.LastNavigationReason} corners={corners.Length} waypoints={state.NavigationWaypoints.Count} target={FormatWaypoint(sampledTarget)} path={FormatWaypoints(state.NavigationWaypoints)}");
        }

        AdvanceWaypoints(
            bot,
            state,
            Mathf.Max(behavior.NavWaypointReachDistance, 1.3f),
            sampledTarget,
            logNavDebug);
        if (!HasActiveWaypoint(state))
        {
            return false;
        }

        moveTarget = state.NavigationWaypoints[state.NavigationWaypointIndex];
        state.LastMoveUsedNavigation = true;
        return true;
    }

    private static bool TryResolveNavMeshAgentMoveTarget(
        Player bot,
        ManagedBotState state,
        Vector3 sampledStart,
        Vector3 sampledTarget,
        BotBehaviorDefinition behavior,
        int agentTypeId,
        int nowTick,
        Action<Player, ManagedBotState, string> logNavDebug,
        out Vector3 moveTarget)
    {
        moveTarget = sampledTarget;
        NavMeshAgent? agent = EnsureNavigationAgent(state, behavior, sampledStart);
        if (agent == null)
        {
            return false;
        }

        ConfigureNavigationAgent(agent, behavior);
        agent.agentTypeID = agentTypeId;

        if (!PlaceNavigationAgent(agent, sampledStart))
        {
            return false;
        }

        bool useContinuousAgentProxy = behavior.FacilityNavMeshDirectPositionControl;
        if (!useContinuousAgentProxy
            && (HorizontalDistance(agent.nextPosition, sampledStart) > AgentProxyWarpDistance
                || Mathf.Abs(agent.nextPosition.y - sampledStart.y) > MaxWaypointVerticalDelta))
        {
            if (!agent.Warp(sampledStart))
            {
                return false;
            }
        }
        else if (useContinuousAgentProxy
            && (HorizontalDistance(agent.nextPosition, sampledStart) > Mathf.Max(AgentProxyWarpDistance * 3f, behavior.FacilityDummyFollowSpeed)
                || Mathf.Abs(agent.nextPosition.y - sampledStart.y) > MaxWaypointVerticalDelta))
        {
            if (!agent.Warp(sampledStart))
            {
                return false;
            }
        }

        if (!useContinuousAgentProxy)
        {
            agent.nextPosition = sampledStart;
            agent.transform.position = sampledStart;
        }

        agent.isStopped = false;

        bool targetMoved = GetNavigationTargetDelta(
            state.LastNavigationTarget,
            sampledTarget,
            useFull3D: false) >= Mathf.Max(0.25f, behavior.NavTargetMoveRecomputeDistance);
        bool pathStale = unchecked(nowTick - state.LastNavigationRecomputeTick) >= Math.Max(100, behavior.NavRecomputeIntervalMs);
        if (!agent.hasPath || targetMoved || pathStale || agent.isPathStale)
        {
            if (!agent.SetDestination(sampledTarget))
            {
                return false;
            }

            state.LastNavigationTarget = sampledTarget;
            state.LastNavigationRecomputeTick = nowTick;
        }

        if (agent.pathPending && !agent.hasPath)
        {
            moveTarget = sampledStart;
            state.NavigationWaypoints.Clear();
            state.NavigationWaypoints.Add(moveTarget);
            state.NavigationWaypointIndex = 0;
            state.NavigationPathFailedUntilTick = 0;
            state.LastMoveUsedNavigation = true;
            state.LastNavigationReason = "navmesh-agent-pending";
            logNavDebug(
                bot,
                state,
                $"agent-pending target={FormatWaypoint(sampledTarget)} start={FormatWaypoint(sampledStart)}");
            return true;
        }

        if (!agent.pathPending && agent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            return false;
        }

        Vector3 steeringTarget = agent.steeringTarget;
        if (HorizontalDistance(sampledStart, steeringTarget) < Mathf.Max(behavior.NavWaypointReachDistance, 0.75f)
            && agent.desiredVelocity.sqrMagnitude > 0.01f)
        {
            steeringTarget = sampledStart + agent.desiredVelocity.normalized * Mathf.Max(behavior.NavWaypointReachDistance * 2f, 2.0f);
            if (NavMesh.SamplePosition(steeringTarget, out NavMeshHit velocityHit, 2.0f, NavMesh.AllAreas))
            {
                steeringTarget = velocityHit.position;
            }
        }

        if (float.IsNaN(steeringTarget.x)
            || float.IsNaN(steeringTarget.y)
            || float.IsNaN(steeringTarget.z)
            || HorizontalDistance(sampledStart, steeringTarget) < 0.15f)
        {
            steeringTarget = sampledTarget;
        }

        moveTarget = steeringTarget;
        state.NavigationWaypoints.Clear();
        state.NavigationWaypoints.Add(moveTarget);
        state.NavigationWaypointIndex = 0;
        state.NavigationPathFailedUntilTick = 0;
        state.LastMoveUsedNavigation = true;
        state.LastNavigationReason = agent.pathStatus == NavMeshPathStatus.PathComplete
            ? "navmesh-agent"
            : "navmesh-agent-partial";
        logNavDebug(
            bot,
            state,
            $"agent-steer reason={state.LastNavigationReason} pending={agent.pathPending} remaining={agent.remainingDistance:F1} target={FormatWaypoint(moveTarget)} dest={FormatWaypoint(sampledTarget)}");
        return true;
    }

    private static NavMeshAgent? EnsureNavigationAgent(ManagedBotState state, BotBehaviorDefinition behavior, Vector3 sampledStart)
    {
        if (state.NavigationAgent != null && state.NavigationAgentObject != null)
        {
            return state.NavigationAgent;
        }

        GameObject agentObject = new($"WarmupBotNavAgent_{state.PlayerId}");
        agentObject.hideFlags = HideFlags.HideAndDontSave;
        agentObject.SetActive(false);
        agentObject.transform.position = sampledStart;
        NavMeshAgent agent = agentObject.AddComponent<NavMeshAgent>();
        agent.enabled = false;
        ConfigureNavigationAgent(agent, behavior);
        agent.agentTypeID = NavMeshAgentTypeUtility.DefaultAgentTypeId;
        agentObject.SetActive(true);
        state.NavigationAgentObject = agentObject;
        state.NavigationAgent = agent;
        return agent;
    }

    private static bool PlaceNavigationAgent(NavMeshAgent agent, Vector3 sampledStart)
    {
        if (agent.isOnNavMesh)
        {
            return true;
        }

        foreach (float verticalOffset in AgentPlacementVerticalOffsets)
        {
            Vector3 candidate = sampledStart + (Vector3.up * verticalOffset);
            if (TryEnableAgentAtPosition(agent, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryEnableAgentAtPosition(NavMeshAgent agent, Vector3 position)
    {
        bool previousLogEnabled = Debug.unityLogger.logEnabled;
        try
        {
            Debug.unityLogger.logEnabled = false;
            if (agent.enabled)
            {
                agent.enabled = false;
            }

            agent.transform.position = position;
            agent.enabled = true;
            if (agent.isOnNavMesh)
            {
                return true;
            }

            if (agent.Warp(position) && agent.isOnNavMesh)
            {
                return true;
            }

            agent.enabled = false;
            return false;
        }
        finally
        {
            Debug.unityLogger.logEnabled = previousLogEnabled;
        }
    }

    private static void ConfigureNavigationAgent(NavMeshAgent agent, BotBehaviorDefinition behavior)
    {
        agent.baseOffset = 0f;
        agent.radius = Mathf.Max(0.05f, behavior.FacilityRuntimeNavMeshAgentRadius);
        agent.height = Mathf.Max(0.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        agent.speed = Mathf.Max(2.0f, behavior.FacilityDummyFollowSpeed);
        agent.acceleration = Mathf.Max(12.0f, agent.speed * 6.0f);
        agent.angularSpeed = 720.0f;
        agent.stoppingDistance = Mathf.Max(0.35f, behavior.NavWaypointReachDistance * 0.5f);
        agent.autoBraking = false;
        agent.autoRepath = true;
        agent.autoTraverseOffMeshLink = true;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.avoidancePriority = 40 + Math.Abs(agent.GetInstanceID() % 30);
        agent.areaMask = NavMesh.AllAreas;
        agent.updatePosition = true;
        agent.updateRotation = false;
        agent.updateUpAxis = true;
    }

    private void BuildArenaWaypointGraph()
    {
        if (_arenaWaypoints.Count == 0)
        {
            _arenaWaypointLinks = null;
            return;
        }

        Player[] noPlayers = Array.Empty<Player>();
        _arenaWaypointLinks = new List<int>[_arenaWaypoints.Count];
        for (int i = 0; i < _arenaWaypoints.Count; i++)
        {
            _arenaWaypointLinks[i] = new List<int>();
        }

        for (int i = 0; i < _arenaWaypoints.Count; i++)
        {
            for (int j = i + 1; j < _arenaWaypoints.Count; j++)
            {
                if (HorizontalDistance(_arenaWaypoints[i], _arenaWaypoints[j]) > WaypointLinkDistance)
                {
                    continue;
                }

                if (Mathf.Abs(_arenaWaypoints[i].y - _arenaWaypoints[j].y) > MaxWaypointVerticalDelta)
                {
                    continue;
                }

                if (!IsPathClear(_arenaWaypoints[i], _arenaWaypoints[j], noPlayers))
                {
                    continue;
                }

                _arenaWaypointLinks[i].Add(j);
                _arenaWaypointLinks[j].Add(i);
            }
        }

        ApplyForcedLinks();
    }

    private void ApplyForcedLinks()
    {
        if (_arenaWaypointLinks == null || _arenaWaypoints.Count == 0)
        {
            return;
        }

        int bridgeIndex = FindWaypointIndexNear(GateBridgeWorldPoint, ForcedWaypointMatchDistance);
        if (bridgeIndex < 0)
        {
            return;
        }

        foreach (Vector3 forcedNeighborPoint in GateBridgeForcedNeighbors)
        {
            int neighborIndex = FindWaypointIndexNear(forcedNeighborPoint, ForcedWaypointMatchDistance);
            if (neighborIndex < 0 || neighborIndex == bridgeIndex)
            {
                continue;
            }

            AddBidirectionalLink(bridgeIndex, neighborIndex);
        }

        // The bridge midpoint is a useful graph hint, but driving bots to stop on
        // that exact center node makes them snag on the mid-gate opening. Force a
        // direct side-to-side edge as well so A* prefers crossing the choke cleanly
        // instead of routing through the midpoint as an intermediate stop.
        if (GateBridgeForcedNeighbors.Length >= 2)
        {
            int firstNeighborIndex = FindWaypointIndexNear(GateBridgeForcedNeighbors[0], ForcedWaypointMatchDistance);
            int secondNeighborIndex = FindWaypointIndexNear(GateBridgeForcedNeighbors[1], ForcedWaypointMatchDistance);
            if (firstNeighborIndex >= 0
                && secondNeighborIndex >= 0
                && firstNeighborIndex != secondNeighborIndex)
            {
                AddBidirectionalLink(firstNeighborIndex, secondNeighborIndex);
            }
        }
    }

    private int FindWaypointIndexNear(Vector3 worldPoint, float maxDistance)
    {
        int bestIndex = -1;
        float bestDistance = maxDistance;
        for (int i = 0; i < _arenaWaypoints.Count; i++)
        {
            float distance = Vector3.Distance(_arenaWaypoints[i], worldPoint);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void AddBidirectionalLink(int firstIndex, int secondIndex)
    {
        if (_arenaWaypointLinks == null
            || firstIndex < 0
            || secondIndex < 0
            || firstIndex >= _arenaWaypointLinks.Length
            || secondIndex >= _arenaWaypointLinks.Length)
        {
            return;
        }

        if (!_arenaWaypointLinks[firstIndex].Contains(secondIndex))
        {
            _arenaWaypointLinks[firstIndex].Add(secondIndex);
        }

        if (!_arenaWaypointLinks[secondIndex].Contains(firstIndex))
        {
            _arenaWaypointLinks[secondIndex].Add(firstIndex);
        }
    }

    private bool TryBuildArenaWaypointPath(
        Vector3 botPosition,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        out List<Vector3> path,
        out string reason)
    {
        path = new List<Vector3>();
        reason = "waypoint-none";
        if (_arenaWaypoints.Count == 0 || _arenaWaypointLinks == null)
        {
            return false;
        }

        Player[] playerArray = players as Player[] ?? new List<Player>(players).ToArray();
        List<int> startCandidates = GetReachableWaypointCandidates(botPosition, playerArray);
        if (startCandidates.Count == 0)
        {
            return false;
        }

        HashSet<int> goalCandidates = new();
        for (int i = 0; i < _arenaWaypoints.Count; i++)
        {
            if (Vector3.Distance(_arenaWaypoints[i], targetPosition) <= WaypointGoalReachDistance)
            {
                goalCandidates.Add(i);
                continue;
            }

            if (Mathf.Abs(_arenaWaypoints[i].y - targetPosition.y) > MaxWaypointVerticalDelta)
            {
                continue;
            }

            if (IsPathClear(_arenaWaypoints[i], targetPosition, playerArray))
            {
                goalCandidates.Add(i);
            }
        }

        int waypointCount = _arenaWaypoints.Count;
        float[] gScore = Enumerable.Repeat(float.PositiveInfinity, waypointCount).ToArray();
        float[] fScore = Enumerable.Repeat(float.PositiveInfinity, waypointCount).ToArray();
        int[] cameFrom = Enumerable.Repeat(-1, waypointCount).ToArray();
        bool[] closed = new bool[waypointCount];
        List<int> open = new();
        int bestReachable = -1;
        float bestReachableDistance = float.PositiveInfinity;

        foreach (int startIndex in startCandidates)
        {
            float startCost = HorizontalDistance(botPosition, _arenaWaypoints[startIndex]);
            gScore[startIndex] = startCost;
            fScore[startIndex] = startCost + HorizontalDistance(_arenaWaypoints[startIndex], targetPosition);
            float targetDistance = Vector3.Distance(_arenaWaypoints[startIndex], targetPosition);
            if (targetDistance < bestReachableDistance)
            {
                bestReachableDistance = targetDistance;
                bestReachable = startIndex;
            }

            if (!open.Contains(startIndex))
            {
                open.Add(startIndex);
            }
        }

        while (open.Count > 0)
        {
            int current = open[0];
            int currentOpenIndex = 0;
            for (int i = 1; i < open.Count; i++)
            {
                int candidate = open[i];
                if (fScore[candidate] < fScore[current])
                {
                    current = candidate;
                    currentOpenIndex = i;
                }
            }

            open.RemoveAt(currentOpenIndex);
            if (goalCandidates.Contains(current))
            {
                path = ReconstructArenaWaypointPath(botPosition, playerArray, cameFrom, current, targetPosition, appendTarget: true);
                reason = "waypoint-graph";
                return path.Count > 0;
            }

            float currentDistance = Vector3.Distance(_arenaWaypoints[current], targetPosition);
            if (currentDistance < bestReachableDistance)
            {
                bestReachableDistance = currentDistance;
                bestReachable = current;
            }

            closed[current] = true;
            foreach (int neighbor in _arenaWaypointLinks[current])
            {
                if (closed[neighbor])
                {
                    continue;
                }

                float tentativeG = gScore[current] + HorizontalDistance(_arenaWaypoints[current], _arenaWaypoints[neighbor]);
                if (tentativeG >= gScore[neighbor])
                {
                    continue;
                }

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;
                fScore[neighbor] = tentativeG + HorizontalDistance(_arenaWaypoints[neighbor], targetPosition);
                if (!open.Contains(neighbor))
                {
                    open.Add(neighbor);
                }
            }
        }

        if (bestReachable >= 0)
        {
            path = ReconstructArenaWaypointPath(botPosition, playerArray, cameFrom, bestReachable, targetPosition, appendTarget: false);
            reason = "waypoint-partial";
            return path.Count > 0;
        }

        return false;
    }

    private List<int> GetReachableWaypointCandidates(Vector3 fromPosition, Player[] players)
    {
        List<int> reachable = _arenaWaypoints
            .Select((point, index) => new
            {
                Index = index,
                Distance = Vector3.Distance(fromPosition, point),
            })
            .OrderBy(candidate => candidate.Distance)
            .Take(MaxAnchorCandidates)
            .Where(candidate => Mathf.Abs(fromPosition.y - _arenaWaypoints[candidate.Index].y) <= MaxWaypointVerticalDelta)
            .Where(candidate => IsPathClear(fromPosition, _arenaWaypoints[candidate.Index], players))
            .Select(candidate => candidate.Index)
            .ToList();

        if (reachable.Count > 0)
        {
            return reachable;
        }

        // Fall back to the nearest vertical-compatible anchors even if the
        // direct segment is not perfectly clear. This keeps Dust2 bots attached
        // to the graph and lets later local/path recomputes make progress
        // instead of collapsing to a no-path freeze.
        return _arenaWaypoints
            .Select((point, index) => new
            {
                Index = index,
                Distance = Vector3.Distance(fromPosition, point),
            })
            .OrderBy(candidate => candidate.Distance)
            .Where(candidate => Mathf.Abs(fromPosition.y - _arenaWaypoints[candidate.Index].y) <= MaxWaypointVerticalDelta)
            .Take(MaxFallbackAnchorCandidates)
            .Select(candidate => candidate.Index)
            .ToList();
    }

    private List<Vector3> ReconstructArenaWaypointPath(
        Vector3 botPosition,
        Player[] players,
        int[] cameFrom,
        int current,
        Vector3 targetPosition,
        bool appendTarget)
    {
        List<Vector3> ordered = new();
        while (current >= 0)
        {
            ordered.Add(_arenaWaypoints[current]);
            current = cameFrom[current];
        }

        ordered.Reverse();
        if (appendTarget)
        {
            ordered.Add(targetPosition);
        }

        return SimplifyOrderedPath(botPosition, ordered, players);
    }

    private static bool TryBuildPath(
        Vector3 botPosition,
        Vector3 targetPosition,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        bool useAStarFallback,
        out List<Vector3> path,
        out string reason)
    {
        path = new List<Vector3>();
        reason = "none";

        Player[] playerArray = players as Player[] ?? new List<Player>(players).ToArray();
        if (useAStarFallback
            && TryBuildAStarPath(botPosition, targetPosition, playerArray, behavior, out path))
        {
            reason = "astar";
            return true;
        }

        List<Vector3> detourCandidates = GenerateDetourCandidates(botPosition, targetPosition, behavior.NavProbeDistance, behavior.NavLateralProbeCount);
        float bestSingleScore = float.MaxValue;
        Vector3 bestSingle = default;
        bool foundSingle = false;

        foreach (Vector3 candidate in detourCandidates)
        {
            if (!IsPathClear(botPosition, candidate, playerArray) || !IsPathClear(candidate, targetPosition, playerArray))
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
            if (!IsPathClear(botPosition, first, playerArray))
            {
                continue;
            }

            foreach (Vector3 second in approachCandidates)
            {
                if (!IsPathClear(first, second, playerArray) || !IsPathClear(second, targetPosition, playerArray))
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

    private static bool TryBuildAStarPath(
        Vector3 botPosition,
        Vector3 targetPosition,
        Player[] players,
        BotBehaviorDefinition behavior,
        out List<Vector3> path)
    {
        path = new List<Vector3>();

        Vector3 start = botPosition;
        Vector3 goal = targetPosition;
        start.y = botPosition.y;
        goal.y = botPosition.y;

        float spacing = Mathf.Max(0.75f, behavior.AStarGridStep);
        float padding = Mathf.Max(behavior.AStarSearchPadding, behavior.NavProbeDistance * 2f);
        float minX = Mathf.Min(start.x, goal.x) - padding;
        float maxX = Mathf.Max(start.x, goal.x) + padding;
        float minZ = Mathf.Min(start.z, goal.z) - padding;
        float maxZ = Mathf.Max(start.z, goal.z) + padding;

        int width = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / spacing) + 1);
        int height = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / spacing) + 1);
        int estimatedNodeCount = width * height;
        if (estimatedNodeCount > behavior.AStarMaxNodeCount && behavior.AStarMaxNodeCount > 0)
        {
            float scale = Mathf.Sqrt(estimatedNodeCount / (float)behavior.AStarMaxNodeCount);
            spacing *= scale;
            width = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / spacing) + 1);
            height = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / spacing) + 1);
            estimatedNodeCount = width * height;
            if (estimatedNodeCount > behavior.AStarMaxNodeCount * 2)
            {
                return false;
            }
        }

        int startX = Mathf.Clamp(Mathf.RoundToInt((start.x - minX) / spacing), 0, width - 1);
        int startZ = Mathf.Clamp(Mathf.RoundToInt((start.z - minZ) / spacing), 0, height - 1);
        int goalX = Mathf.Clamp(Mathf.RoundToInt((goal.x - minX) / spacing), 0, width - 1);
        int goalZ = Mathf.Clamp(Mathf.RoundToInt((goal.z - minZ) / spacing), 0, height - 1);
        int startIndex = startX + (startZ * width);
        int goalIndex = goalX + (goalZ * width);

        bool[] walkable = new bool[estimatedNodeCount];
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = x + (z * width);
                Vector3 point = GetGridPoint(minX, minZ, spacing, botPosition.y, x, z);
                walkable[index] = IsNodeWalkable(point, players) || index == startIndex || index == goalIndex;
            }
        }

        if (!walkable[startIndex] || !walkable[goalIndex])
        {
            return false;
        }

        float[] gScore = Enumerable.Repeat(float.PositiveInfinity, estimatedNodeCount).ToArray();
        float[] fScore = Enumerable.Repeat(float.PositiveInfinity, estimatedNodeCount).ToArray();
        int[] cameFrom = Enumerable.Repeat(-1, estimatedNodeCount).ToArray();
        bool[] closed = new bool[estimatedNodeCount];
        List<int> open = new() { startIndex };

        gScore[startIndex] = 0f;
        fScore[startIndex] = Heuristic(startX, startZ, goalX, goalZ, spacing);

        (int dx, int dz, float cost)[] directions =
        {
            (-1, 0, 1f),
            (1, 0, 1f),
            (0, -1, 1f),
            (0, 1, 1f),
            (-1, -1, 1.4142135f),
            (-1, 1, 1.4142135f),
            (1, -1, 1.4142135f),
            (1, 1, 1.4142135f),
        };

        while (open.Count > 0)
        {
            int current = open[0];
            int currentOpenIndex = 0;
            for (int i = 1; i < open.Count; i++)
            {
                int candidate = open[i];
                if (fScore[candidate] < fScore[current])
                {
                    current = candidate;
                    currentOpenIndex = i;
                }
            }

            if (current == goalIndex)
            {
                List<Vector3> rawPath = ReconstructPath(cameFrom, current, minX, minZ, spacing, botPosition.y, width);
                path = SimplifyPath(start, goal, rawPath, players);
                return path.Count > 0;
            }

            open.RemoveAt(currentOpenIndex);
            closed[current] = true;
            int currentX = current % width;
            int currentZ = current / width;
            Vector3 currentPoint = GetGridPoint(minX, minZ, spacing, botPosition.y, currentX, currentZ);

            foreach ((int dx, int dz, float cost) in directions)
            {
                int nextX = currentX + dx;
                int nextZ = currentZ + dz;
                if (nextX < 0 || nextX >= width || nextZ < 0 || nextZ >= height)
                {
                    continue;
                }

                int neighbor = nextX + (nextZ * width);
                if (closed[neighbor] || !walkable[neighbor])
                {
                    continue;
                }

                Vector3 neighborPoint = GetGridPoint(minX, minZ, spacing, botPosition.y, nextX, nextZ);
                if (!IsPathClear(currentPoint, neighborPoint, players))
                {
                    continue;
                }

                float tentativeG = gScore[current] + (cost * spacing);
                if (tentativeG >= gScore[neighbor])
                {
                    continue;
                }

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;
                fScore[neighbor] = tentativeG + Heuristic(nextX, nextZ, goalX, goalZ, spacing);
                if (!open.Contains(neighbor))
                {
                    open.Add(neighbor);
                }
            }
        }

        return false;
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

    private static Vector3 GetGridPoint(float minX, float minZ, float spacing, float y, int x, int z)
    {
        return new Vector3(minX + (x * spacing), y, minZ + (z * spacing));
    }

    private static float Heuristic(int x, int z, int goalX, int goalZ, float spacing)
    {
        return (Mathf.Abs(goalX - x) + Mathf.Abs(goalZ - z)) * spacing;
    }

    private static List<Vector3> ReconstructPath(
        int[] cameFrom,
        int current,
        float minX,
        float minZ,
        float spacing,
        float y,
        int width)
    {
        List<Vector3> path = new();
        while (current >= 0)
        {
            int x = current % width;
            int z = current / width;
            path.Add(GetGridPoint(minX, minZ, spacing, y, x, z));
            current = cameFrom[current];
        }

        path.Reverse();
        return path;
    }

    private static List<Vector3> SimplifyPath(Vector3 start, Vector3 goal, List<Vector3> rawPath, Player[] players)
    {
        List<Vector3> orderedPoints = new(rawPath);
        if (orderedPoints.Count > 0)
        {
            orderedPoints.RemoveAt(0);
        }

        orderedPoints.Add(goal);
        List<Vector3> simplified = new();
        Vector3 anchor = start;
        int index = 0;

        while (index < orderedPoints.Count)
        {
            int furthestReachable = index;
            for (int candidate = index; candidate < orderedPoints.Count; candidate++)
            {
                if (!IsPathClear(anchor, orderedPoints[candidate], players))
                {
                    break;
                }

                furthestReachable = candidate;
            }

            Vector3 nextPoint = orderedPoints[furthestReachable];
            simplified.Add(nextPoint);
            anchor = nextPoint;
            index = furthestReachable + 1;
        }

        return simplified;
    }

    private static List<Vector3> SimplifyOrderedPath(Vector3 start, List<Vector3> orderedPoints, Player[] players)
    {
        if (orderedPoints.Count == 0)
        {
            return new List<Vector3>();
        }

        List<Vector3> simplified = new();
        Vector3 anchor = start;
        int index = 0;

        while (index < orderedPoints.Count)
        {
            int furthestReachable = index;
            for (int candidate = index; candidate < orderedPoints.Count; candidate++)
            {
                if (!IsPathClear(anchor, orderedPoints[candidate], players))
                {
                    break;
                }

                furthestReachable = candidate;
            }

            Vector3 nextPoint = orderedPoints[furthestReachable];
            simplified.Add(nextPoint);
            anchor = nextPoint;
            index = furthestReachable + 1;
        }

        return simplified;
    }

    private static bool IsNodeWalkable(Vector3 point, Player[] players)
    {
        Vector3[] sampleHeights =
        {
            Vector3.up * 0.45f,
            Vector3.up * 1.0f,
            Vector3.up * 1.35f,
        };

        Vector3[] sampleOffsets =
        {
            Vector3.zero,
            Vector3.right * 0.18f,
            Vector3.left * 0.18f,
            Vector3.forward * 0.18f,
            Vector3.back * 0.18f,
        };

        foreach (Vector3 heightOffset in sampleHeights)
        {
            foreach (Vector3 lateralOffset in sampleOffsets)
            {
                Collider[] overlaps = Physics.OverlapSphere(point + heightOffset + lateralOffset, NodeClearanceRadius, ~0, QueryTriggerInteraction.Ignore);
                foreach (Collider collider in overlaps)
                {
                    Transform transform = collider.transform;
                    if (transform == null || IsIgnoredPlayerTransform(transform, players))
                    {
                        continue;
                    }

                    return false;
                }
            }
        }

        return true;
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
        Vector3 flatDirection = end - start;
        flatDirection.y = 0f;
        Vector3 right = flatDirection.sqrMagnitude < 0.001f
            ? Vector3.right
            : Vector3.Cross(Vector3.up, flatDirection.normalized);
        Vector3[] lateralOffsets =
        {
            Vector3.zero,
            right * PathClearanceLateralOffset,
            -right * PathClearanceLateralOffset,
        };

        foreach (Vector3 heightOffset in sampleOffsets)
        {
            foreach (Vector3 lateralOffset in lateralOffsets)
            {
                if (IsSegmentBlocked(start + heightOffset + lateralOffset, end + heightOffset + lateralOffset, playerArray))
                {
                    return false;
                }
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

        RaycastHit[] hits = Physics.SphereCastAll(start, PathClearanceRadius, direction.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
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
            float waypointDistance = HorizontalDistance(bot.Position, waypoint);
            if (ShouldSoftAdvanceGateBridge(state, waypoint, waypointDistance, bot.Position))
            {
                logNavDebug(
                    bot,
                    state,
                    $"waypoint-soft-advance index={state.NavigationWaypointIndex} point={FormatWaypoint(waypoint)} dist={waypointDistance:F2}");
                state.NavigationWaypointIndex++;
                continue;
            }

            float effectiveReachDistance = Mathf.Max(reachDistance, TightWaypointCompletionDistance);
            if (waypointDistance > effectiveReachDistance)
            {
                break;
            }

            logNavDebug(bot, state, $"waypoint-reached index={state.NavigationWaypointIndex} point={FormatWaypoint(waypoint)} dist={waypointDistance:F2}");
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

    private static bool ShouldSoftAdvanceGateBridge(ManagedBotState state, Vector3 waypoint, float waypointDistance, Vector3 botPosition)
    {
        if (!IsGateBridgeWaypoint(waypoint)
            || waypointDistance > GateBridgeSoftAdvanceDistance
            || state.NavigationWaypointIndex + 1 >= state.NavigationWaypoints.Count)
        {
            return false;
        }

        Vector3 nextWaypoint = state.NavigationWaypoints[state.NavigationWaypointIndex + 1];
        float nextDistance = HorizontalDistance(botPosition, nextWaypoint);
        return nextDistance <= waypointDistance + 2.25f;
    }

    private static bool IsGateBridgeWaypoint(Vector3 waypoint)
    {
        return Vector3.Distance(waypoint, GateBridgeWorldPoint) <= ForcedWaypointMatchDistance;
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
