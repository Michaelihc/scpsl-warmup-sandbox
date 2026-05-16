using System;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class FacilityWaypointFollower : MonoBehaviour
{
    private Player? _bot;
    private ManagedBotState? _state;
    private Func<BotBehaviorDefinition>? _behaviorProvider;
    private Action<Player, ManagedBotState, string>? _logNavDebug;

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
        if (_bot == null
            || _state == null
            || _behaviorProvider == null
            || _bot.IsDestroyed
            || _bot.ReferenceHub == null
            || _bot.Role == RoleTypeId.Spectator
            || !NetworkServer.active)
        {
            Destroy(this);
            return;
        }

        BotBehaviorDefinition behavior = _behaviorProvider();
        if (!behavior.UseFacilityRoomGraphNavigation
            || !_state.LastNavigationReason.StartsWith("room-graph", StringComparison.Ordinal)
            || _state.NavigationWaypointIndex < 0
            || _state.NavigationWaypointIndex >= _state.NavigationWaypoints.Count)
        {
            Destroy(this);
            return;
        }

        ReferenceHub hub = _bot.ReferenceHub;
        if (!(hub.roleManager.CurrentRole is IFpcRole { FpcModule: var fpcModule }))
        {
            Destroy(this);
            return;
        }

        Vector3 position = hub.transform.position;
        Vector3 waypoint = _state.NavigationWaypoints[_state.NavigationWaypointIndex];
        Vector3 direction = waypoint - position;
        float distance = direction.magnitude;
        float minDistance = Mathf.Clamp(behavior.NavWaypointReachDistance * 0.55f, 0.15f, 0.75f);
        if (distance < minDistance)
        {
            _state.LastMoveIntentLabel = "facility-node-follow-wait";
            _state.LastMoveIntentTick = Environment.TickCount;
            return;
        }

        float maxDistance = Mathf.Max(20.0f, behavior.FacilityDummyFollowMaxDistance);
        if (distance > maxDistance)
        {
            fpcModule.ServerOverridePosition(waypoint);
        }
        else
        {
            Vector3 step = Time.deltaTime * GetFacilityFollowSpeed(_bot, behavior) * direction.normalized;
            fpcModule.Motor.ReceivedPosition = new RelativePosition(position + step);
            fpcModule.MouseLook.LookAtDirection(direction);
        }

        _state.LastMoveIntentLabel = "facility-node-follow";
        _state.LastMoveIntentTick = Environment.TickCount;
    }

    private static float GetFacilityFollowSpeed(Player bot, BotBehaviorDefinition behavior)
    {
        return Mathf.Max(
            0.1f,
            bot.Role switch
            {
                RoleTypeId.Scp939 => behavior.FacilityDummyFollowSpeedScp939,
                RoleTypeId.Scp3114 => behavior.FacilityDummyFollowSpeedScp3114,
                RoleTypeId.Scp049 => behavior.FacilityDummyFollowSpeedScp049,
                RoleTypeId.Scp106 => behavior.FacilityDummyFollowSpeedScp106,
                _ => behavior.FacilityDummyFollowSpeed,
            });
    }

    private void OnDestroy()
    {
        if (_state != null
            && (string.Equals(_state.LastMoveIntentLabel, "facility-node-follow", StringComparison.Ordinal)
                || string.Equals(_state.LastMoveIntentLabel, "facility-node-follow-wait", StringComparison.Ordinal)))
        {
            _state.LastMoveIntentLabel = "none";
        }
    }
}
