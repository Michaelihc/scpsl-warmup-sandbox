using System;
using CommandSystem;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class ModHelpCommand : ICommand
{
    public string Command => "modhelp";

    public string[] Aliases => Array.Empty<string>();

    public string Description => WarmupLocalization.T(
        "Lists the commands exposed by the warmup mod.",
        "列出热身插件提供的命令。");

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = WarmupLocalization.T(
            "Warmup mod commands:\n" +
            "bots status\n" +
            "bots start\n" +
            "bots restart\n" +
            "bots roundrestart\n" +
            "bots stop\n" +
            "bots save\n" +
            "bots setcount <count>\n" +
            "bots set939speed <speed>\n" +
            "bots set3114speed <speed>\n" +
            "bots set049speed <speed>\n" +
            "bots set106speed <speed>\n" +
            "bots setspeed <speed>\n" +
            "bots setretreatspeed <scale>\n" +
            "bots map <bomb|standard|true|false>\n" +
            "bots difficulty <easy|normal|hard|hardest>\n" +
            "bots aimode <classic|realistic>\n" +
            "bots language <en|cn>\n" +
            "bots set retreatspeed <scale>\n" +
            "bots set <key> <value>\n" +
            "loadout\n" +
            "loadout <number|preset|role>\n" +
            "Aliases: bots, bot, warmup, ws, warmupsandbox | loadout, ld, kit",
            "热身插件命令：\n" +
            "bots status\n" +
            "bots start\n" +
            "bots restart\n" +
            "bots roundrestart\n" +
            "bots stop\n" +
            "bots save\n" +
            "bots setcount <数量>\n" +
            "bots set939speed <速度>\n" +
            "bots set3114speed <速度>\n" +
            "bots set049speed <速度>\n" +
            "bots set106speed <速度>\n" +
            "bots setspeed <速度>\n" +
            "bots setretreatspeed <倍率>\n" +
            "bots map <bomb|standard|true|false>\n" +
            "bots difficulty <easy|normal|hard|hardest>\n" +
            "bots aimode <classic|realistic>\n" +
            "bots language <en|cn>\n" +
            "bots set retreatspeed <倍率>\n" +
            "bots set <键> <值>\n" +
            "loadout\n" +
            "loadout <编号|预设|角色>\n" +
            "别名：bots, bot, warmup, ws, warmupsandbox | loadout, ld, kit");
        return true;
    }
}
