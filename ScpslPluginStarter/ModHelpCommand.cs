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
        response = WarmupLocalization.T(
            "Warmup commands:\n" +
            ".loadout - human presets\n" +
            ".loadout <number|name> - choose human preset\n" +
            ".loadout <173|939|106|049|3114|096> - temporary SCP practice role\n" +
            ".bots setcount <count> - change bot count with cooldown\n" +
            "Open Server Specific Settings for the GUI (personal actions: 10s cooldown; global Apply buttons: shared cooldown)\n" +
            "Admin-only extras: bots start/restart/stop/save/map/difficulty/aimode/language/set <key> <value>",
            "热身命令：\n" +
            ".loadout - 查看人类预设\n" +
            ".loadout <编号|名称> - 选择人类预设\n" +
            ".loadout <173|939|106|049|3114|096> - 临时切换为 SCP 练习角色\n" +
            ".bots setcount <数量> - 带冷却修改机器人数量\n" +
            "打开服务器专属设置（Server Specific Settings）使用 GUI（个人按钮 10 秒冷却；全局应用按钮共享冷却）\n" +
            "管理员额外命令：bots start/restart/stop/save/map/difficulty/aimode/language/set <键> <值>");
        return true;
    }
}
