using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class LoadoutCommand : ICommand
{
    public string Command => "loadout";

    public string[] Aliases => new[] { "ld", "kit" };

    public string Description => "Shows and selects your warmup human loadout preset.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        if (plugin == null)
        {
            response = "WarmupSandbox is not loaded.";
            return false;
        }

        if (!Player.TryGet(sender, out Player player))
        {
            response = "Only players can use this command.";
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
        return arguments.Array![arguments.Offset + index];
    }
}
