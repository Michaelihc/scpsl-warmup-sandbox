using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class LimitedRemoteAdminCommand : ICommand
{
    public string Command => "ra";

    public string[] Aliases => new[] { "panel", "adminpanel" };

    public string Description => WarmupLocalization.T(
        "Opens the limited warmup Remote Admin panel.",
        "打开热身插件的受限 Remote Admin 面板。");

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

        return plugin.TryOpenLimitedRemoteAdmin(player, out response);
    }
}
