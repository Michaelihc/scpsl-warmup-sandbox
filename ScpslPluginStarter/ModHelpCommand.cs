using System;
using CommandSystem;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class ModHelpCommand : ICommand
{
    public string Command => "modhelp";

    public string[] Aliases => Array.Empty<string>();

    public string Description => "Lists the commands exposed by the warmup mod.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response =
            "Warmup mod commands:\n" +
            "warmup status\n" +
            "warmup start\n" +
            "warmup restart\n" +
            "warmup roundrestart\n" +
            "warmup stop\n" +
            "warmup save\n" +
            "warmup difficulty <easy|normal|hard|hardest>\n" +
            "warmup set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|keepmagfilled> <value>\n" +
            "Aliases: warmup, ws, warmupsandbox";
        return true;
    }
}
