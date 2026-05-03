using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

public sealed class LoadoutCommand : ICommand
{
    public string Command => "loadout";

    public string[] Aliases => new[] { "ld", "kit" };

    public string Description => WarmupLocalization.T(
        "Shows and selects your warmup human spawn preset.",
        "显示并选择你的热身人类出生预设。");

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        if (plugin == null)
        {
            response = WarmupLocalization.T("WarmupSandbox is not loaded.", "WarmupSandbox 未加载。");
            return false;
        }

        if (!Player.TryGet(sender, out Player player))
        {
            response = WarmupLocalization.T("Only players can use this command.", "只有玩家可以使用此命令。");
            return false;
        }

        if (arguments.Count == 0)
        {
            response = plugin.BuildLoadoutMenu(player);
            player.SendHint(response, 8f);
            return true;
        }

        string selector = GetArgument(arguments, 0);
        return plugin.TrySelectHumanLoadout(player, selector, applyNow: true, out response);
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array![arguments.Offset + index]!;
    }
}
