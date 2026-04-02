using System;
using System.Linq;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter;

internal sealed class BotCombatService
{
    public FirearmItem? EnsureFirearmEquipped(Player player)
    {
        if (player.CurrentItem is FirearmItem currentFirearm)
        {
            return currentFirearm;
        }

        FirearmItem? firstFirearm = player.Items.OfType<FirearmItem>().FirstOrDefault();
        if (firstFirearm != null)
        {
            player.CurrentItem = firstFirearm;
        }

        return firstFirearm;
    }

    public bool CanShoot(
        Player bot,
        ManagedBotState state,
        BotTargetSelection target,
        BotBehaviorDefinition behavior,
        int nowTick,
        out string? reason)
    {
        reason = null;
        if (!BotTargetingService.IsRealisticEnabledFor(bot, behavior))
        {
            return true;
        }

        if (!target.HasLineOfSight)
        {
            reason = target.IsRememberedTarget
                ? $"remembering target out of sight lastSeen={state.Engagement.LastSeenTick}"
                : $"target blocked headLos={target.HeadHasLineOfSight} torsoLos={target.TorsoHasLineOfSight}";
            return false;
        }

        if (unchecked(nowTick - state.Engagement.ReactionReadyTick) < 0)
        {
            reason = $"reaction {(state.Engagement.ReactionReadyTick - nowTick)}ms";
            return false;
        }

        return true;
    }

    public void OnReloaded(ManagedBotState state, BotBehaviorDefinition behavior, System.Random random)
    {
        if (state.Engagement.TargetPlayerId < 0)
        {
            return;
        }

        state.Engagement.HasPostReloadLock = true;
        state.Engagement.ReloadLockYawOffset = RandomOffset(random, behavior.RealisticReloadLockOffsetMaxDegrees);
        state.Engagement.ReloadLockPitchOffset = 0f;
    }

    private static float RandomOffset(System.Random random, float maxAbsDegrees)
    {
        if (maxAbsDegrees <= 0f)
        {
            return 0f;
        }

        return (float)((random.NextDouble() * 2d) - 1d) * maxAbsDegrees;
    }
}
