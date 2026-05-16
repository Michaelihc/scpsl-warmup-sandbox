using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace ScpslPluginStarter.RepkinsNavigation;

// Movement adapter based on repkins/scpsl-bot-plugin FpcBotPlayer + FpcMove.
internal static class RepkinsFpcMovementRegistry
{
    private const int IntentExpiryMs = 1000;
    private static readonly Dictionary<ReferenceHub, BotMovementState> StatesByHub = new();

    public static bool TryResolveSteeringTarget(
        Player bot,
        ManagedBotState state,
        Vector3 goalPosition,
        BotBehaviorDefinition behavior,
        Action<Player, ManagedBotState, string> logNavDebug,
        out Vector3 steeringTarget)
    {
        steeringTarget = goalPosition;
        if (!behavior.UseRepkinsFacilityNavigation
            || !TryGetState(bot, out BotMovementState movementState)
            || !RepkinsNavigationSystem.Instance.TryEnsureLoaded(message => logNavDebug(bot, state, message)))
        {
            return false;
        }

        try
        {
            steeringTarget = movementState.Navigator.GetPositionTowards(goalPosition);
            if (!IsFinite(steeringTarget))
            {
                logNavDebug(bot, state, $"repkins-navmesh-invalid target={FormatVector(goalPosition)}");
                ResetNavigator(state.PlayerId);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logNavDebug(bot, state, $"repkins-navmesh-failed error={ex.GetType().Name}:{ex.Message}");
            ResetNavigator(state.PlayerId);
            return false;
        }
    }

    public static bool TryApplyMove(
        Player bot,
        ManagedBotState state,
        Vector3 moveTarget,
        int nowTick,
        out string moveLabel)
    {
        moveLabel = "none";
        if (!TryGetState(bot, out BotMovementState movementState)
            || !TryGetFpcModule(bot, out FirstPersonMovementModule fpcModule))
        {
            return false;
        }

        Vector3 relativePosition = moveTarget - bot.ReferenceHub.PlayerCameraReference.position;
        Vector3 relativeHorizontalPosition = Vector3.ProjectOnPlane(relativePosition, Vector3.up);
        if (relativeHorizontalPosition.sqrMagnitude < 0.0001f)
        {
            movementState.DesiredLocalDirection = Vector3.zero;
            movementState.Active = true;
            movementState.LastIntentTick = nowTick;
            moveLabel = "repkins-fpc-wait";
            return true;
        }

        Vector3 directionToTarget = relativeHorizontalPosition.normalized;
        Vector3 playerDirection = fpcModule.transform.forward;
        movementState.DesiredLocalDirection = Vector3.Dot(playerDirection, directionToTarget) < 0f
            ? fpcModule.transform.InverseTransformDirection(directionToTarget)
            : Vector3.forward;
        movementState.Active = true;
        movementState.LastIntentTick = nowTick;
        state.LastMoveIntentLabel = "repkins-fpc-follow";
        state.LastMoveIntentTick = nowTick;
        moveLabel = "repkins-fpc-follow";
        return true;
    }

    public static bool TryGetDesiredWorldMove(ReferenceHub hub, FirstPersonMovementModule fpcModule, out Vector3 desiredWorldMove)
    {
        desiredWorldMove = Vector3.zero;
        if (!StatesByHub.TryGetValue(hub, out BotMovementState movementState) || !movementState.IsActive)
        {
            return false;
        }

        desiredWorldMove = fpcModule.transform.TransformDirection(movementState.DesiredLocalDirection);
        return true;
    }

    public static bool IsActiveBot(ReferenceHub hub)
    {
        return StatesByHub.TryGetValue(hub, out BotMovementState movementState) && movementState.IsActive;
    }

    public static void Disable(Player bot)
    {
        if (bot.ReferenceHub != null && StatesByHub.TryGetValue(bot.ReferenceHub, out BotMovementState movementState))
        {
            movementState.Active = false;
            movementState.DesiredLocalDirection = Vector3.zero;
        }
    }

    public static void ResetNavigator(int playerId)
    {
        foreach (KeyValuePair<ReferenceHub, BotMovementState> entry in StatesByHub.ToArray())
        {
            if (entry.Value.PlayerId == playerId)
            {
                StatesByHub.Remove(entry.Key);
            }
        }
    }

    public static void ClearAll()
    {
        StatesByHub.Clear();
    }

    private static bool TryGetState(Player bot, out BotMovementState movementState)
    {
        movementState = null!;
        if (bot.ReferenceHub == null || !TryGetFpcModule(bot, out _))
        {
            return false;
        }

        if (!StatesByHub.TryGetValue(bot.ReferenceHub, out movementState))
        {
            movementState = new BotMovementState(bot.PlayerId, new RepkinsFpcBotNavigator(bot));
            StatesByHub[bot.ReferenceHub] = movementState;
        }

        return true;
    }

    private static bool TryGetFpcModule(Player bot, out FirstPersonMovementModule fpcModule)
    {
        if (bot.ReferenceHub?.roleManager?.CurrentRole is IFpcRole { FpcModule: var module })
        {
            fpcModule = module;
            return true;
        }

        fpcModule = null!;
        return false;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !(float.IsNaN(value.x)
            || float.IsNaN(value.y)
            || float.IsNaN(value.z)
            || float.IsInfinity(value.x)
            || float.IsInfinity(value.y)
            || float.IsInfinity(value.z));
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F1},{value.y:F1},{value.z:F1})";
    }

    private sealed class BotMovementState
    {
        public BotMovementState(int playerId, RepkinsFpcBotNavigator navigator)
        {
            PlayerId = playerId;
            Navigator = navigator;
        }

        public int PlayerId { get; }

        public RepkinsFpcBotNavigator Navigator { get; }

        public Vector3 DesiredLocalDirection { get; set; }

        public bool Active { get; set; }

        public int LastIntentTick { get; set; }

        public bool IsActive => Active
            && unchecked(Environment.TickCount - LastIntentTick) <= IntentExpiryMs;
    }
}
