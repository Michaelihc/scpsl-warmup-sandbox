using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BotAimService
{
    public void AimAt(
        Player bot,
        ManagedBotState state,
        BotTargetSelection target,
        BotBehaviorDefinition behavior,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, string> logAimStep,
        Action<Player, ManagedBotState, Player, float, float, Vector3> logAimDebug)
    {
        Vector3 eyeOrigin = GetEyeOrigin(bot, behavior.TargetAimHeightOffset);
        state.LastEyeOrigin = eyeOrigin;
        state.LastTorsoAimPoint = target.TorsoAimPoint;
        state.LastHeadAimPoint = target.HeadAimPoint;
        Vector3 aimPoint = GetDesiredAimPoint(bot, state, target, behavior);
        state.LastComputedAimPoint = aimPoint;
        Vector3 direction = aimPoint - eyeOrigin;
        Vector3 flatDirection = new(direction.x, 0f, direction.z);
        if (flatDirection.sqrMagnitude < 0.01f)
        {
            return;
        }

        float yaw = Mathf.Atan2(flatDirection.x, flatDirection.z) * Mathf.Rad2Deg;
        float pitch = 0f;
        if (behavior.EnableVerticalAim)
        {
            float flatDistance = flatDirection.magnitude;
            pitch = Mathf.Atan2(direction.y, Mathf.Max(flatDistance, 0.01f)) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, -behavior.MaxVerticalAimDegrees, behavior.MaxVerticalAimDegrees);
        }

        state.LastDesiredYaw = yaw;
        state.LastDesiredPitch = pitch;
        ApplyAimActions(bot, state, yaw, pitch, behavior, tryInvokeDummyAction, logAimStep);
        logAimDebug(bot, state, target.Target, yaw, pitch, direction);
    }

    public bool IsAimAligned(Player bot, ManagedBotState state, BotBehaviorDefinition behavior)
    {
        Vector2 currentAim = GetCurrentAim(bot);
        float yawDelta = Mathf.Abs(Mathf.DeltaAngle(currentAim.x, state.LastDesiredYaw));
        float pitchDelta = behavior.EnableVerticalAim
            ? Mathf.Abs(state.LastDesiredPitch - currentAim.y)
            : 0f;
        float yawTolerance = Mathf.Max(behavior.HorizontalAimDeadzoneDegrees * 2f, 1f);
        float pitchTolerance = Mathf.Max(behavior.VerticalAimDeadzoneDegrees * 2f, 0.75f);
        return yawDelta <= yawTolerance && pitchDelta <= pitchTolerance;
    }

    private static Vector3 GetDesiredAimPoint(Player bot, ManagedBotState state, BotTargetSelection target, BotBehaviorDefinition behavior)
    {
        if (!BotTargetingService.IsRealisticEnabledFor(bot, behavior))
        {
            state.LastAimMode = "classic";
            state.LastAimSettleProgress = 1f;
            state.LastAimYawOffset = 0f;
            state.LastAimPitchOffset = 0f;
            state.LastBaseAimPoint = target.AimPoint;
            return target.AimPoint;
        }

        if (state.Engagement.HasPostReloadLock && target.HasLineOfSight)
        {
            state.LastAimMode = "reload-lock";
            state.LastAimSettleProgress = 1f;
            state.LastAimYawOffset = state.Engagement.ReloadLockYawOffset;
            state.LastAimPitchOffset = state.Engagement.ReloadLockPitchOffset;
            state.LastBaseAimPoint = target.HeadAimPoint;
            return ApplyOffsetToPoint(bot, target.HeadAimPoint, state.Engagement.ReloadLockYawOffset, state.Engagement.ReloadLockPitchOffset, behavior.TargetAimHeightOffset);
        }

        if (!target.HasLineOfSight && target.IsRememberedTarget)
        {
            state.LastAimMode = "remembered";
            state.LastAimSettleProgress = 1f;
            state.LastAimYawOffset = 0f;
            state.LastAimPitchOffset = 0f;
            state.LastBaseAimPoint = state.Engagement.LastKnownAimPoint;
            return state.Engagement.LastKnownAimPoint;
        }

        float settleProgress = 1f;
        if (behavior.RealisticAimSettleMs > 0)
        {
            settleProgress = Mathf.Clamp01((Environment.TickCount - state.Engagement.VisibleSinceTick) / (float)behavior.RealisticAimSettleMs);
        }

        Vector3 baseAimPoint = new(
            Mathf.Lerp(target.TorsoAimPoint.x, target.HeadAimPoint.x, settleProgress),
            target.TorsoAimPoint.y,
            Mathf.Lerp(target.TorsoAimPoint.z, target.HeadAimPoint.z, settleProgress));
        float yawOffset = state.Engagement.InitialYawOffset * (1f - settleProgress);
        float pitchOffset = state.Engagement.InitialPitchOffset * (1f - settleProgress);
        state.LastAimMode = "realistic-track";
        state.LastAimSettleProgress = settleProgress;
        state.LastAimYawOffset = yawOffset;
        state.LastAimPitchOffset = pitchOffset;
        state.LastBaseAimPoint = baseAimPoint;
        return ApplyOffsetToPoint(bot, baseAimPoint, yawOffset, pitchOffset, behavior.TargetAimHeightOffset);
    }

    private static Vector3 ApplyOffsetToPoint(Player bot, Vector3 baseAimPoint, float yawOffsetDegrees, float pitchOffsetDegrees, float fallbackHeight)
    {
        if (Mathf.Abs(yawOffsetDegrees) < 0.01f && Mathf.Abs(pitchOffsetDegrees) < 0.01f)
        {
            return baseAimPoint;
        }

        Vector3 origin = GetEyeOrigin(bot, fallbackHeight);
        Vector3 direction = baseAimPoint - origin;
        if (direction.sqrMagnitude < 0.01f)
        {
            return baseAimPoint;
        }

        Quaternion yawRotation = Quaternion.AngleAxis(yawOffsetDegrees, Vector3.up);
        Vector3 horizontalAxis = Vector3.Cross(direction.normalized, Vector3.up);
        if (horizontalAxis.sqrMagnitude < 0.0001f)
        {
            horizontalAxis = Vector3.right;
        }

        Quaternion pitchRotation = Quaternion.AngleAxis(-pitchOffsetDegrees, horizontalAxis.normalized);
        Vector3 rotatedDirection = yawRotation * (pitchRotation * direction);
        return origin + rotatedDirection;
    }

    private static Vector3 GetEyeOrigin(Player player, float fallbackHeight)
    {
        if (player.Camera != null)
        {
            return player.Camera.position;
        }

        return player.Position + Vector3.up * fallbackHeight;
    }

    private void ApplyAimActions(
        Player bot,
        ManagedBotState state,
        float desiredYaw,
        float desiredPitch,
        BotBehaviorDefinition behavior,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, string> logAimStep)
    {
        Vector2 rawLookBefore = bot.LookRotation;
        Vector2 currentAim = GetCurrentAim(bot);
        state.LastPitchWasSanitized = false;
        state.LastSanitizedPitch = currentAim.y;

        // Firing/recoil can occasionally report a vertical look value far outside the bot's
        // intended combat envelope. Clamp those spikes back into the legal aim envelope
        // so the bot still issues corrective vertical actions instead of freezing pitch.
        if (behavior.EnableVerticalAim && Mathf.Abs(currentAim.y) > behavior.MaxVerticalAimDegrees + 10f)
        {
            currentAim.y = Mathf.Clamp(currentAim.y, -behavior.MaxVerticalAimDegrees, behavior.MaxVerticalAimDegrees);
            state.LastPitchWasSanitized = true;
            state.LastSanitizedPitch = currentAim.y;
        }

        float yawDelta = Mathf.DeltaAngle(currentAim.x, desiredYaw);
        float pitchDelta = desiredPitch - currentAim.y;
        state.LastRawPitchBeforeAim = rawLookBefore.x;
        state.LastRawYawBeforeAim = rawLookBefore.y;
        state.LastYawDelta = yawDelta;
        state.LastPitchDelta = pitchDelta;

        state.LastHorizontalAimActions = ApplyAimAxisActions(
            bot,
            state,
            "horizontal",
            yawDelta,
            behavior.HorizontalAimDeadzoneDegrees,
            behavior.MaxHorizontalAimActionsPerTick,
            behavior.LookHorizontalPositiveActionNames,
            behavior.LookHorizontalNegativeActionNames,
            tryInvokeDummyAction,
            logAimStep);

        if (!behavior.EnableVerticalAim)
        {
            state.LastVerticalAimActions = "disabled";
            logAimStep(bot, state, "axis=vertical disabled=True");
            return;
        }

        state.LastVerticalAimRetriedInverted = false;
        state.LastVerticalAimActions = ApplyVerticalAimActions(
            bot,
            state,
            pitchDelta,
            behavior,
            tryInvokeDummyAction,
            logAimStep);
    }

    private static Vector2 GetCurrentAim(Player bot)
    {
        Vector2 rawLook = bot.LookRotation;
        float pitch = NormalizeSignedAngle(rawLook.x);
        float yaw = NormalizeSignedAngle(rawLook.y);
        return new Vector2(yaw, pitch);
    }

    private static float NormalizeSignedAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle <= -180f)
        {
            angle += 360f;
        }

        return angle;
    }

    private string ApplyVerticalAimActions(
        Player bot,
        ManagedBotState state,
        float pitchDelta,
        BotBehaviorDefinition behavior,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, string> logAimStep)
    {
        string[] primaryPositive = state.VerticalAimDirectionInverted
            ? behavior.LookVerticalNegativeActionNames
            : behavior.LookVerticalPositiveActionNames;
        string[] primaryNegative = state.VerticalAimDirectionInverted
            ? behavior.LookVerticalPositiveActionNames
            : behavior.LookVerticalNegativeActionNames;

        string actions = ApplyAimAxisActions(
            bot,
            state,
            "vertical",
            pitchDelta,
            behavior.VerticalAimDeadzoneDegrees,
            behavior.MaxVerticalAimActionsPerTick,
            primaryPositive,
            primaryNegative,
            tryInvokeDummyAction,
            logAimStep);

        if (actions != "none" || Mathf.Abs(pitchDelta) <= behavior.VerticalAimDeadzoneDegrees)
        {
            return actions;
        }

        state.VerticalAimDirectionInverted = !state.VerticalAimDirectionInverted;
        state.LastVerticalAimRetriedInverted = true;
        logAimStep(
            bot,
            state,
            $"axis=vertical retryInvert=True pitchDelta={pitchDelta:F1} deadzone={behavior.VerticalAimDeadzoneDegrees:F1} invertNow={state.VerticalAimDirectionInverted}");

        string[] retryPositive = state.VerticalAimDirectionInverted
            ? behavior.LookVerticalNegativeActionNames
            : behavior.LookVerticalPositiveActionNames;
        string[] retryNegative = state.VerticalAimDirectionInverted
            ? behavior.LookVerticalPositiveActionNames
            : behavior.LookVerticalNegativeActionNames;

        string retryActions = ApplyAimAxisActions(
            bot,
            state,
            "vertical",
            pitchDelta,
            behavior.VerticalAimDeadzoneDegrees,
            behavior.MaxVerticalAimActionsPerTick,
            retryPositive,
            retryNegative,
            tryInvokeDummyAction,
            logAimStep);

        if (retryActions == "none")
        {
            state.VerticalAimDirectionInverted = !state.VerticalAimDirectionInverted;
            logAimStep(bot, state, $"axis=vertical retryInvertFailed revertInvert={state.VerticalAimDirectionInverted}");
            return actions;
        }

        return $"invert:{retryActions}";
    }

    private static string ApplyAimAxisActions(
        Player bot,
        ManagedBotState state,
        string axisName,
        float delta,
        float deadzone,
        int maxActions,
        string[] positiveActionNames,
        string[] negativeActionNames,
        Func<Player, string, bool> tryInvokeDummyAction,
        Action<Player, ManagedBotState, string> logAimStep)
    {
        if (maxActions <= 0 || Mathf.Abs(delta) <= deadzone)
        {
            logAimStep(
                bot,
                state,
                $"axis={axisName} skipped=True delta={delta:F1} deadzone={deadzone:F1} maxActions={maxActions}");
            return "none";
        }

        string[] actionNames = delta >= 0f ? positiveActionNames : negativeActionNames;
        float remaining = Mathf.Abs(delta);
        int used = 0;
        List<string> usedActions = new();

        string[] orderedActions = actionNames ?? Array.Empty<string>();
        while (used < maxActions && remaining > deadzone)
        {
            string? fallbackAction = null;
            float fallbackStep = float.MaxValue;
            bool invoked = false;

            foreach (string actionName in orderedActions)
            {
                float step = ExtractAimStepDegrees(actionName);
                if (step > remaining + deadzone)
                {
                    if (step < fallbackStep)
                    {
                        fallbackAction = actionName;
                        fallbackStep = step;
                    }

                    continue;
                }

                Vector2 rawBefore = bot.LookRotation;
                Vector2 aimBefore = GetCurrentAim(bot);
                bool invokedAction = tryInvokeDummyAction(bot, actionName);
                Vector2 rawAfter = bot.LookRotation;
                Vector2 aimAfter = GetCurrentAim(bot);
                logAimStep(
                    bot,
                    state,
                    $"axis={axisName} action={actionName} step={step:F1} remainingBefore={remaining:F1} " +
                    $"delta={delta:F1} invoked={invokedAction} rawBefore=({rawBefore.x:F1},{rawBefore.y:F1}) " +
                    $"rawAfter=({rawAfter.x:F1},{rawAfter.y:F1}) aimBefore=({aimBefore.x:F1},{aimBefore.y:F1}) " +
                    $"aimAfter=({aimAfter.x:F1},{aimAfter.y:F1}) invert={state.VerticalAimDirectionInverted}");
                if (!invokedAction)
                {
                    continue;
                }

                usedActions.Add(actionName);
                remaining -= step;
                used++;
                invoked = true;
                break;
            }

            if (!invoked
                && !string.IsNullOrWhiteSpace(fallbackAction)
                && fallbackStep < float.MaxValue
                )
            {
                Vector2 rawBefore = bot.LookRotation;
                Vector2 aimBefore = GetCurrentAim(bot);
                bool invokedFallback = tryInvokeDummyAction(bot, fallbackAction);
                Vector2 rawAfter = bot.LookRotation;
                Vector2 aimAfter = GetCurrentAim(bot);
                logAimStep(
                    bot,
                    state,
                    $"axis={axisName} fallbackAction={fallbackAction} step={fallbackStep:F1} remainingBefore={remaining:F1} " +
                    $"delta={delta:F1} invoked={invokedFallback} rawBefore=({rawBefore.x:F1},{rawBefore.y:F1}) " +
                    $"rawAfter=({rawAfter.x:F1},{rawAfter.y:F1}) aimBefore=({aimBefore.x:F1},{aimBefore.y:F1}) " +
                    $"aimAfter=({aimAfter.x:F1},{aimAfter.y:F1}) invert={state.VerticalAimDirectionInverted}");
                if (!invokedFallback)
                {
                    continue;
                }

                usedActions.Add(fallbackAction);
                remaining -= fallbackStep;
                used++;
                invoked = true;
            }

            if (!invoked)
            {
                logAimStep(
                    bot,
                    state,
                    $"axis={axisName} abort=True remaining={remaining:F1} delta={delta:F1} tried={string.Join(",", orderedActions)}");
                break;
            }
        }

        return usedActions.Count == 0 ? "none" : string.Join(",", usedActions);
    }

    private static float ExtractAimStepDegrees(string actionName)
    {
        int signIndex = Math.Max(actionName.LastIndexOf('+'), actionName.LastIndexOf('-'));
        if (signIndex < 0 || signIndex >= actionName.Length - 1)
        {
            return 1f;
        }

        string suffix = actionName.Substring(signIndex + 1);
        return float.TryParse(suffix, out float value) ? value : 1f;
    }
}
