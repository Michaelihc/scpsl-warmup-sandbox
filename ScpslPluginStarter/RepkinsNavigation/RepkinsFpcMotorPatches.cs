using System.Reflection;
using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using UnityEngine;

namespace ScpslPluginStarter.RepkinsNavigation;

// Ported from repkins/scpsl-bot-plugin (SCPSLBot.AI.FirstPersonControl.Movement.FpcMotorPatches).
[HarmonyPatch(typeof(FpcMotor))]
internal static class RepkinsFpcMotorPatches
{
    private static readonly FieldInfo MainModuleField = AccessTools.Field(typeof(FpcMotor), "MainModule");
    private static readonly MethodInfo HubGetter = AccessTools.PropertyGetter(typeof(FirstPersonMovementModule), "Hub");

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(FpcMotor), "DesiredMove"),
            prefix: new HarmonyMethod(typeof(RepkinsFpcMotorPatches), nameof(GetBotDesiredMoveIfBot)));
        harmony.Patch(
            AccessTools.Method(typeof(FpcMotor), "GetFrameMove"),
            prefix: new HarmonyMethod(typeof(RepkinsFpcMotorPatches), nameof(AssignNewReceivedPositionIfBot)));
    }

    [HarmonyPatch("DesiredMove", MethodType.Getter)]
    [HarmonyPrefix]
    public static bool GetBotDesiredMoveIfBot(FpcMotor __instance, ref Vector3 __result)
    {
        if (!TryGetFpcModuleAndHub(__instance, out FirstPersonMovementModule fpcModule, out ReferenceHub hub))
        {
            return true;
        }

        if (!RepkinsFpcMovementRegistry.TryGetDesiredWorldMove(hub, fpcModule, out Vector3 desiredWorldMove))
        {
            return true;
        }

        __result = desiredWorldMove;
        return false;
    }

    [HarmonyPatch("GetFrameMove")]
    [HarmonyPrefix]
    public static bool AssignNewReceivedPositionIfBot(FpcMotor __instance)
    {
        if (TryGetFpcModuleAndHub(__instance, out FirstPersonMovementModule fpcModule, out ReferenceHub hub)
            && RepkinsFpcMovementRegistry.IsActiveBot(hub))
        {
            __instance.ReceivedPosition = new RelativePosition(fpcModule.Position + __instance.MoveDirection);
        }

        return true;
    }

    private static bool TryGetFpcModuleAndHub(
        FpcMotor motor,
        out FirstPersonMovementModule fpcModule,
        out ReferenceHub hub)
    {
        fpcModule = MainModuleField.GetValue(motor) as FirstPersonMovementModule;
        hub = fpcModule == null ? null! : HubGetter.Invoke(fpcModule, null) as ReferenceHub;
        return fpcModule != null && hub != null;
    }
}
