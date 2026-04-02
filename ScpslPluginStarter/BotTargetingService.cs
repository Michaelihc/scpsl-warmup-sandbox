using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BotTargetingService
{
    public BotTargetSelection? SelectTarget(
        Player bot,
        ManagedBotState state,
        IEnumerable<Player> players,
        BotBehaviorDefinition behavior,
        System.Random random)
    {
        List<Player> hostiles = players
            .Where(candidate => AreHostile(bot, candidate))
            .ToList();

        if (hostiles.Count == 0)
        {
            state.Engagement.Reset();
            return null;
        }

        int nowTick = Environment.TickCount;
        bool useRealistic = IsRealisticEnabledFor(bot, behavior);
        BotTargetSelection? closestVisible = null;
        BotTargetSelection? closestAny = null;

        foreach (Player hostile in hostiles)
        {
            BotTargetSelection selection = BuildSelection(bot, hostile, behavior, useRealistic);
            if (closestAny == null || selection.Distance < closestAny.Distance)
            {
                closestAny = selection;
            }

            if (selection.HasLineOfSight && (closestVisible == null || selection.Distance < closestVisible.Distance))
            {
                closestVisible = selection;
            }
        }

        if (!useRealistic)
        {
            state.Engagement.Reset();
            return closestAny;
        }

        if (closestVisible != null)
        {
            UpdateVisibleEngagement(state, closestVisible, behavior, random, nowTick);
            return closestVisible;
        }

        Player? rememberedTarget = hostiles.FirstOrDefault(candidate => candidate.PlayerId == state.Engagement.TargetPlayerId);
        if (rememberedTarget != null && unchecked(nowTick - state.Engagement.LastSeenTick) <= behavior.RealisticSightMemoryMs)
        {
            BotTargetSelection selection = BuildSelection(bot, rememberedTarget, behavior, useRealistic);
            selection.HasLineOfSight = false;
            selection.IsRememberedTarget = true;
            selection.AimPoint = state.Engagement.LastKnownAimPoint;
            state.Engagement.IsTargetVisible = false;
            return selection;
        }

        state.Engagement.Reset();
        return closestAny;
    }

    public static bool IsRealisticEnabledFor(Player bot, BotBehaviorDefinition behavior)
    {
        return behavior.AiMode == WarmupAiMode.Realistic && IsHumanCombatant(bot);
    }

    public static bool IsCombatTarget(Player player)
    {
        if (player == null || player.IsDestroyed || player.Role == RoleTypeId.Spectator)
        {
            return false;
        }

        return player.Team != Team.Dead;
    }

    public static bool IsHumanCombatant(Player player)
    {
        return IsCombatTarget(player) && player.Team != Team.SCPs;
    }

    private static bool AreHostile(Player bot, Player candidate)
    {
        if (candidate.PlayerId == bot.PlayerId || !IsCombatTarget(candidate) || !IsCombatTarget(bot))
        {
            return false;
        }

        if (bot.Team == Team.SCPs)
        {
            return candidate.Team != Team.SCPs;
        }

        if (candidate.Team == Team.SCPs)
        {
            return true;
        }

        return candidate.Team != bot.Team;
    }

    private static void UpdateVisibleEngagement(
        ManagedBotState state,
        BotTargetSelection selection,
        BotBehaviorDefinition behavior,
        System.Random random,
        int nowTick)
    {
        bool switchedTarget = state.Engagement.TargetPlayerId != selection.Target.PlayerId;
        bool wasHidden = !switchedTarget && !state.Engagement.IsTargetVisible;
        if (switchedTarget)
        {
            state.Engagement.TargetPlayerId = selection.Target.PlayerId;
            state.Engagement.VisibleSinceTick = nowTick;
            state.Engagement.LastSeenTick = nowTick;
            state.Engagement.ReactionReadyTick = nowTick;
            state.Engagement.InitialYawOffset = RandomOffset(random, behavior.RealisticInitialYawOffsetMaxDegrees);
            state.Engagement.InitialPitchOffset = RandomOffset(random, behavior.RealisticInitialPitchOffsetMaxDegrees);
            state.Engagement.HasPostReloadLock = false;
            state.Engagement.ReloadLockYawOffset = 0f;
            state.Engagement.ReloadLockPitchOffset = 0f;
        }
        else if (wasHidden)
        {
            state.Engagement.VisibleSinceTick = nowTick;
            state.Engagement.ReactionReadyTick = nowTick + behavior.RealisticReacquireDelayMs;
        }

        state.Engagement.IsTargetVisible = true;
        state.Engagement.LastSeenTick = nowTick;
        state.Engagement.LastKnownAimPoint = selection.AimPoint;
    }

    private static float RandomOffset(System.Random random, float maxAbsDegrees)
    {
        if (maxAbsDegrees <= 0f)
        {
            return 0f;
        }

        return (float)((random.NextDouble() * 2d) - 1d) * maxAbsDegrees;
    }

    private static BotTargetSelection BuildSelection(Player bot, Player target, BotBehaviorDefinition behavior, bool useLos)
    {
        Vector3 headAimPoint = target.Camera != null
            ? target.Camera.position
            : target.Position + Vector3.up * behavior.RealisticHeadAimHeightOffset;
        Vector3 torsoAimPoint = target.Camera != null
            ? Vector3.Lerp(target.Position, headAimPoint, 0.55f)
            : target.Position + Vector3.up * behavior.TargetAimHeightOffset;
        Vector3 eyeOrigin = GetEyeOrigin(bot, behavior.TargetAimHeightOffset);

        bool headVisible = useLos && HasLineOfSight(bot, target, eyeOrigin, headAimPoint);
        bool torsoVisible = useLos && HasLineOfSight(bot, target, eyeOrigin, torsoAimPoint);
        bool hasLineOfSight = headVisible || torsoVisible;
        Vector3 aimPoint = headVisible ? headAimPoint : torsoAimPoint;
        float distance = Vector3.Distance(bot.Position, target.Position);

        return new BotTargetSelection(target, distance, torsoAimPoint, headAimPoint, aimPoint, hasLineOfSight)
        {
            HeadHasLineOfSight = headVisible,
            TorsoHasLineOfSight = torsoVisible,
        };
    }

    private static Vector3 GetEyeOrigin(Player player, float fallbackHeight)
    {
        if (player.Camera != null)
        {
            return player.Camera.position;
        }

        return player.Position + Vector3.up * fallbackHeight;
    }

    private static bool HasLineOfSight(Player source, Player target, Vector3 origin, Vector3 aimPoint)
    {
        Vector3 direction = aimPoint - origin;
        float distance = direction.magnitude;
        if (distance < 0.01f)
        {
            return true;
        }

        RaycastHit[] hits = Physics.RaycastAll(origin, direction.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        Transform sourceRoot = source.ReferenceHub.transform;
        Transform targetRoot = target.ReferenceHub.transform;
        bool sawBlockingCandidate = false;

        foreach (RaycastHit hit in hits)
        {
            Transform hitTransform = hit.transform;
            if (hitTransform == null)
            {
                continue;
            }

            if (hitTransform == sourceRoot || hitTransform.IsChildOf(sourceRoot))
            {
                continue;
            }

            sawBlockingCandidate = true;
            if (hitTransform == targetRoot || hitTransform.IsChildOf(targetRoot))
            {
                return true;
            }

            return false;
        }

        // Dummies/players do not always return a hit collider on this ray path.
        // If nothing except the source was hit, treat the path as clear rather than blocked.
        return !sawBlockingCandidate;
    }
}

internal sealed class BotTargetSelection
{
    public BotTargetSelection(Player target, float distance, Vector3 torsoAimPoint, Vector3 headAimPoint, Vector3 aimPoint, bool hasLineOfSight)
    {
        Target = target;
        Distance = distance;
        TorsoAimPoint = torsoAimPoint;
        HeadAimPoint = headAimPoint;
        AimPoint = aimPoint;
        HasLineOfSight = hasLineOfSight;
    }

    public Player Target { get; }

    public float Distance { get; }

    public Vector3 TorsoAimPoint { get; }

    public Vector3 HeadAimPoint { get; }

    public Vector3 AimPoint { get; set; }

    public bool HasLineOfSight { get; set; }

    public bool IsRememberedTarget { get; set; }

    public bool HeadHasLineOfSight { get; set; }

    public bool TorsoHasLineOfSight { get; set; }
}
