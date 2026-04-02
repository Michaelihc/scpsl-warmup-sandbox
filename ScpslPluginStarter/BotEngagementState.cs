using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BotEngagementState
{
    public int TargetPlayerId { get; set; } = -1;

    public int LastSeenTick { get; set; }

    public int VisibleSinceTick { get; set; }

    public int ReactionReadyTick { get; set; }

    public bool IsTargetVisible { get; set; }

    public Vector3 LastKnownAimPoint { get; set; }

    public float InitialYawOffset { get; set; }

    public float InitialPitchOffset { get; set; }

    public bool HasPostReloadLock { get; set; }

    public float ReloadLockYawOffset { get; set; }

    public float ReloadLockPitchOffset { get; set; }

    public void Reset()
    {
        TargetPlayerId = -1;
        LastSeenTick = 0;
        VisibleSinceTick = 0;
        ReactionReadyTick = 0;
        IsTargetVisible = false;
        LastKnownAimPoint = default;
        InitialYawOffset = 0f;
        InitialPitchOffset = 0f;
        HasPostReloadLock = false;
        ReloadLockYawOffset = 0f;
        ReloadLockPitchOffset = 0f;
    }
}
