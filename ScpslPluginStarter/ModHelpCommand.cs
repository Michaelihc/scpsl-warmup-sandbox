using System;
using CommandSystem;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class ModHelpCommand : ICommand
{
    public string Command => "modhelp";

    public string[] Aliases => new[] { "help", "warmuphelp" };

    public string Description => WarmupLocalization.T(
        "Lists the commands exposed by the warmup mod.",
        "列出热身插件提供的命令。");

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        bool playerPanelEnabled = plugin?.Config.PlayerPanelEnabled ?? false;

        response = WarmupLocalization.T(
            "Warmup commands:\n" +
            "Player text commands are disabled.\n" +
            (playerPanelEnabled
                ? "Use the GUI in Server Specific Settings for player controls.\n"
                : "Player GUI controls are disabled on this server.\n") +
            "Admin commands: bots status/start/restart/stop/save/map/difficulty/aimode/language/set <key> <value>",
            "热身命令：\n" +
            "玩家文本命令已关闭。\n" +
            (playerPanelEnabled
                ? "请使用服务器专属设置中的图形界面进行玩家控制。\n"
                : "本服务器已关闭玩家图形界面控制。\n") +
            "管理员命令：bots status/start/restart/stop/save/map/difficulty/aimode/language/set <键> <值>");
        return true;
    }
}
