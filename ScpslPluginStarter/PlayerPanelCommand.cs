using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class PlayerPanelCommand : ICommand
{
    public string Command => "panel";

    public string[] Aliases => new[] { "adminpanel", "menu" };

    public string Description => WarmupLocalization.T(
        "Opens and uses the warmup player command panel.",
        "打开并使用热身玩家控制台。");

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
            return plugin.TryOpenPlayerPanel(player, out response);
        }

        return plugin.TryExecutePlayerPanelCommand(player, arguments, out response);
    }
}
