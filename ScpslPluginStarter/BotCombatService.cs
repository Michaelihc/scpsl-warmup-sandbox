using System;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles;

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
        if (!target.HasLineOfSight)
        {
            reason = "no-los";
            return false;
        }

        return true;
    }

    public bool CanAttack(
        Player bot,
        ManagedBotState state,
        BotTargetSelection target,
        BotBehaviorDefinition behavior,
        FirearmItem? firearm,
        int nowTick,
        out string? reason)
    {
        if (firearm != null)
        {
            return CanShoot(bot, state, target, behavior, nowTick, out reason);
        }

        reason = null;
        if (!IsSupportedScpAttacker(bot.Role))
        {
            reason = "no-weapon";
            return false;
        }

        if (!target.HasLineOfSight)
        {
            reason = "no-los";
            return false;
        }

        float range = Math.Max(0.5f, behavior.ScpAttackRange);
        if (target.Distance > range)
        {
            reason = $"out-of-range {target.Distance:F1}>{range:F1}";
            return false;
        }

        return true;
    }

    public static bool IsSupportedScpAttacker(RoleTypeId role)
    {
        return role == RoleTypeId.Scp049
            || role == RoleTypeId.Scp939
            || role == RoleTypeId.Scp3114
            || role == RoleTypeId.Scp0492
            || role == RoleTypeId.Scp106;
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
