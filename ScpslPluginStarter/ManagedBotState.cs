namespace ScpslPluginStarter;

internal sealed class ManagedBotState
{
    public ManagedBotState(int playerId, string nickname)
    {
        PlayerId = playerId;
        Nickname = nickname;
    }

    public int PlayerId { get; }

    public string Nickname { get; }

    public int BrainToken { get; set; }

    public bool SpawnSetupCompleted { get; set; }

    public int StrafeDirection { get; set; } = 1;

    public int LastShotTick { get; set; }

    public int LastReloadAttemptTick { get; set; }

    public int LastDebugTick { get; set; }

    public int LastAimDebugTick { get; set; }

    public UnityEngine.Vector3 LastPosition { get; set; }

    public float LastDesiredYaw { get; set; }

    public float LastDesiredPitch { get; set; }

    public string LastHorizontalAimActions { get; set; } = "none";

    public string LastVerticalAimActions { get; set; } = "none";

    public float LastYawDelta { get; set; }

    public float LastPitchDelta { get; set; }

    public float LastRawPitchBeforeAim { get; set; }

    public float LastRawYawBeforeAim { get; set; }

    public int StuckTicks { get; set; }

    public int UnstuckUntilTick { get; set; }

    public int ConsecutiveLinearMoves { get; set; }
}
