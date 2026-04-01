using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class WarmupCommand : ICommand
{
    public string Command => "warmup";

    public string[] Aliases => new[] { "ws", "warmupsandbox" };

    public string Description => "Controls the WarmupSandbox plugin at runtime.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        if (plugin == null)
        {
            response = "WarmupSandbox is not loaded.";
            return false;
        }

        if (arguments.Count == 0)
        {
            response = BuildHelp();
            return true;
        }

        string subcommand = GetArgument(arguments, 0).ToLowerInvariant();
        switch (subcommand)
        {
            case "status":
                response = plugin.BuildStatus();
                return true;

            case "start":
                return plugin.StartRoundIfNeeded(out response);

            case "restart":
                return plugin.RestartWarmupFromCommand(ensureRoundStarted: true, out response);

            case "roundrestart":
                return plugin.RestartRound(out response);

            case "stop":
                return plugin.StopWarmup(out response);

            case "save":
                return plugin.SaveCurrentConfig(out response);

            case "difficulty":
                if (arguments.Count < 2)
                {
                    response = "Usage: warmup difficulty <easy|normal|hard|hardest>";
                    return false;
                }

                return plugin.ApplyDifficultyPreset(GetArgument(arguments, 1), out response);

            case "set":
                if (arguments.Count < 3)
                {
                    response = "Usage: warmup set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|keepmagfilled> <value>";
                    return false;
                }

                if (!plugin.UpdateSetting(GetArgument(arguments, 1), GetArgument(arguments, 2), out response))
                {
                    return false;
                }

                plugin.SaveCurrentConfig(out _);
                return true;

            default:
                response = BuildHelp();
                return false;
        }
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array![arguments.Offset + index];
    }

    private static string BuildHelp()
    {
        return "warmup status | start | restart | roundrestart | stop | save | difficulty <easy|normal|hard|hardest> | set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|keepmagfilled> <value>";
    }
}
