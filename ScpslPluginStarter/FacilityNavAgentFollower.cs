using System;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;
using UnityEngine.AI;

namespace ScpslPluginStarter;

internal sealed class FacilityNavAgentFollower : MonoBehaviour
{
    private const float UpdateInterval = 0.04f;
    private Player? _bot;
    private ManagedBotState? _state;
    private Func<BotBehaviorDefinition>? _behaviorProvider;
    private Action<Player, ManagedBotState, string>? _logNavDebug;
    private float _nextUpdateTime;

    public void Init(
        Player bot,
        ManagedBotState state,
        Func<BotBehaviorDefinition> behaviorProvider,
        Action<Player, ManagedBotState, string> logNavDebug)
    {
        _bot = bot;
        _state = state;
        _behaviorProvider = behaviorProvider;
        _logNavDebug = logNavDebug;
    }

    private void Update()
    {
        if (Time.time < _nextUpdateTime)
        {
            return;
        }

        _nextUpdateTime = Time.time + UpdateInterval;
        if (_bot == null
            || _state == null
            || _behaviorProvider == null
            || _bot.IsDestroyed
            || _bot.Role == RoleTypeId.Spectator)
        {
            Destroy(this);
            return;
        }

        BotBehaviorDefinition behavior = _behaviorProvider();
        if (!behavior.FacilityNavMeshDirectPositionControl)
        {
            return;
        }

        if (!_state.LastNavigationReason.StartsWith("navmesh-agent", StringComparison.Ordinal))
        {
            return;
        }

        NavMeshAgent? agent = _state.NavigationAgent;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
        {
            return;
        }

        Vector3 currentPlayerPosition = _bot.Position;
        float verticalOffset = Mathf.Max(0f, behavior.FacilityNavMeshDirectPositionVerticalOffset);
        float currentSampleDistance = Mathf.Clamp(behavior.FacilityNavMeshSampleDistance, 0.75f, 2.0f);
        bool sampledCurrent = NavMesh.SamplePosition(
            currentPlayerPosition - (Vector3.up * verticalOffset),
            out NavMeshHit currentHit,
            currentSampleDistance,
            NavMesh.AllAreas);

        float maxVerticalDrift = Mathf.Max(1.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        if (!sampledCurrent || Mathf.Abs((currentHit.position.y + verticalOffset) - currentPlayerPosition.y) > maxVerticalDrift)
        {
            RecoverToSafePosition(agent, behavior, sampledCurrent ? "vertical-drift" : "off-navmesh");
            return;
        }

        Vector3 currentNavPosition = currentHit.position;
        _state.HasDirectNavigationSafePosition = true;
        _state.LastDirectNavigationSafePosition = currentNavPosition;

        Vector3 agentPosition = agent.nextPosition;
        Vector3 delta = agentPosition - currentNavPosition;
        float horizontalDistance = new Vector2(delta.x, delta.z).magnitude;
        if (horizontalDistance < 0.04f && Mathf.Abs(delta.y) < 0.08f)
        {
            return;
        }

        float speed = Mathf.Max(0.1f, behavior.FacilityDummyFollowSpeed);
        float maxStep = Mathf.Max(
            0.05f,
            behavior.FacilityNavMeshDirectPositionMaxStep > 0f
                ? behavior.FacilityNavMeshDirectPositionMaxStep
                : speed * UpdateInterval);
        float step = Mathf.Min(maxStep, speed * UpdateInterval);
        Vector3 candidate = Vector3.MoveTowards(currentNavPosition, agentPosition, step);

        if (!TryValidateNextNavPosition(currentNavPosition, candidate, behavior, step, out Vector3 nextNavPosition, out string reason))
        {
            RecoverToSafePosition(agent, behavior, reason);
            return;
        }

        _bot.Position = nextNavPosition + (Vector3.up * verticalOffset);
        _state.HasDirectNavigationSafePosition = true;
        _state.LastDirectNavigationSafePosition = nextNavPosition;
        _state.LastMoveIntentLabel = "navmesh-agent-follow";
        _state.LastMoveIntentTick = Environment.TickCount;
        _state.LastNavigationReason = "navmesh-agent-follow";
    }

    private bool TryValidateNextNavPosition(
        Vector3 currentNavPosition,
        Vector3 candidate,
        BotBehaviorDefinition behavior,
        float step,
        out Vector3 nextNavPosition,
        out string reason)
    {
        nextNavPosition = currentNavPosition;
        reason = "none";
        float bridgeDistance = Mathf.Max(0.4f, behavior.FacilityNavMeshDirectPositionBridgeDistance);
        float sampleDistance = Mathf.Clamp(step + bridgeDistance, 0.75f, bridgeDistance + 0.75f);
        if (!NavMesh.SamplePosition(candidate, out NavMeshHit nextHit, sampleDistance, NavMesh.AllAreas))
        {
            reason = "next-off-navmesh";
            return false;
        }

        Vector3 sampledDelta = nextHit.position - currentNavPosition;
        sampledDelta.y = 0f;
        if (sampledDelta.magnitude > step + bridgeDistance)
        {
            reason = "sample-snap-too-far";
            return false;
        }

        float maxDrop = Mathf.Max(0.05f, behavior.FacilityNavMeshDirectPositionMaxDropPerStep);
        if (nextHit.position.y < currentNavPosition.y - maxDrop)
        {
            reason = "downward-jump";
            return false;
        }

        if (Mathf.Abs(nextHit.position.y - currentNavPosition.y) > Mathf.Max(2.0f, behavior.FacilityRuntimeNavMeshAgentHeight + 0.5f))
        {
            reason = "vertical-snap";
            return false;
        }

        if (NavMesh.Raycast(currentNavPosition, nextHit.position, out _, NavMesh.AllAreas)
            && sampledDelta.magnitude > bridgeDistance)
        {
            reason = "edge";
            return false;
        }

        nextNavPosition = nextHit.position;
        return true;
    }

    private void RecoverToSafePosition(NavMeshAgent agent, BotBehaviorDefinition behavior, string reason)
    {
        if (_bot == null || _state == null)
        {
            return;
        }

        if (!_state.HasDirectNavigationSafePosition)
        {
            return;
        }

        Vector3 safe = _state.LastDirectNavigationSafePosition;
        float verticalOffset = Mathf.Max(0f, behavior.FacilityNavMeshDirectPositionVerticalOffset);
        _bot.Position = safe + (Vector3.up * verticalOffset);
        if (agent.enabled && agent.isOnNavMesh)
        {
            agent.Warp(safe);
            agent.ResetPath();
            agent.isStopped = false;
        }

        _state.LastNavigationRecomputeTick = 0;
        _state.LastDirectNavigationMoveTick = Environment.TickCount;
        _logNavDebug?.Invoke(
            _bot,
            _state,
            $"agent-follow-recover reason={reason} safe=({safe.x:F1},{safe.y:F1},{safe.z:F1})");
    }
}
