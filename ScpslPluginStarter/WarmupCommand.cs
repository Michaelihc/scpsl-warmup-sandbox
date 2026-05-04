using System;
using System.Linq;
using CommandSystem;
using LabApi.Features.Wrappers;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class WarmupCommand : ICommand
{
    public string Command => "bots";

    public string[] Aliases => new[] { "bot", "warmup", "ws", "warmupsandbox" };

    public string Description => WarmupLocalization.T(
        "Controls the WarmupSandbox plugin at runtime.",
        "运行时控制 WarmupSandbox 插件。");

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        if (plugin == null)
        {
            response = WarmupLocalization.T("WarmupSandbox is not loaded.", "WarmupSandbox 未加载。");
            return false;
        }

        if (arguments.Count == 0)
        {
            response = BuildHelp();
            return true;
        }

        string subcommand = GetArgument(arguments, 0).ToLowerInvariant();
        bool isPlayerSender = Player.TryGet(sender, out Player player);
        bool isPrivilegedSender = IsPrivilegedSender(sender);
        if (isPlayerSender && !isPrivilegedSender)
        {
            response = BuildPlayerHelp();
            return false;
        }

        switch (subcommand)
        {
            case "status":
                response = plugin.BuildStatus();
                return true;

            case "playtime":
                int limit = 10;
                if (arguments.Count >= 2 && (!int.TryParse(GetArgument(arguments, 1), out limit) || limit <= 0))
                {
                    response = WarmupLocalization.T(
                        "Usage: bots playtime [limit]",
                        "用法：bots playtime [数量]");
                    return false;
                }

                response = plugin.BuildPlaytimeReport(limit);
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

            case "updatewarning":
            case "updaterestart":
                return BroadcastLiveUpdateWarning(plugin, arguments, out response);

            case "setcount":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots setcount <count>",
                        "用法：bots setcount <数量>");
                    return false;
                }

                return UpdateSettingAndSave(plugin, "bots", GetArgument(arguments, 1), out response);

            case "set939speed":
                return UpdateSpeedAndSave(plugin, "939speed", arguments, out response);

            case "set3114speed":
                return UpdateSpeedAndSave(plugin, "3114speed", arguments, out response);

            case "set049speed":
                return UpdateSpeedAndSave(plugin, "049speed", arguments, out response);

            case "set106speed":
                return UpdateSpeedAndSave(plugin, "106speed", arguments, out response);

            case "setspeed":
                return UpdateSpeedAndSave(plugin, "speed", arguments, out response);

            case "setretreatspeed":
            case "setbackoffspeed":
            case "setcloseretreatspeed":
            case "retreatspeed":
            case "backoffspeed":
                return UpdateRetreatSpeedAndSave(plugin, arguments, out response);

            case "difficulty":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots difficulty <easy|normal|hard|hardest>",
                        "用法：bots difficulty <easy|normal|hard|hardest>");
                    return false;
                }

                return ApplyDifficultyAndSave(plugin, GetArgument(arguments, 1), out response);

            case "aimode":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots aimode <classic|realistic>",
                        "用法：bots aimode <classic|realistic>");
                    return false;
                }

                return ApplyAiModeAndSave(plugin, GetArgument(arguments, 1), out response);

            case "language":
            case "lang":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots language <en|cn>",
                        "用法：bots language <en|cn>");
                    return false;
                }

                return SetLanguageAndSave(plugin, GetArgument(arguments, 1), out response);

            case "mode":
            case "bombmode":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots mode <standard|bomb>",
                        "用法：bots mode <standard|bomb>");
                    return false;
                }

                return SetRoundModeAndSave(plugin, GetArgument(arguments, 1), out response);

            case "bomb":
                return SetRoundModeAndSave(plugin, "bomb", out response);

            case "standard":
            case "facility":
                return SetRoundModeAndSave(plugin, "standard", out response);

            case "map":
            case "dust2":
                if (arguments.Count < 2)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots map <bomb|standard|true|false>",
                        "用法：bots map <bomb|standard|true|false>");
                    return false;
                }

                string mapValue = GetArgument(arguments, 1);
                if (mapValue.Equals("bomb", StringComparison.OrdinalIgnoreCase))
                {
                    return SetRoundModeAndSave(plugin, "bomb", out response);
                }

                if (mapValue.Equals("standard", StringComparison.OrdinalIgnoreCase)
                    || mapValue.Equals("facility", StringComparison.OrdinalIgnoreCase))
                {
                    return SetRoundModeAndSave(plugin, "standard", out response);
                }

                if (!bool.TryParse(mapValue, out bool mapEnabled))
                {
                    response = WarmupLocalization.T(
                        "Usage: bots map <bomb|standard|true|false>",
                        "用法：bots map <bomb|standard|true|false>");
                    return false;
                }

                return SetDust2MapAndSave(plugin, mapEnabled, out response);

            case "set":
                if (arguments.Count == 2 && int.TryParse(GetArgument(arguments, 1), out _))
                {
                    return UpdateSettingAndSave(plugin, "bots", GetArgument(arguments, 1), out response);
                }

                if (isPlayerSender && !isPrivilegedSender)
                {
                    response = BuildPlayerHelp();
                    return false;
                }

                if (arguments.Count < 3)
                {
                    response = WarmupLocalization.T(
                        "Usage: bots set <count> OR bots set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|mode|map|keepmagfilled|aimode|language|retreatspeed|speed|939speed|3114speed|049speed|106speed> <value>",
                        "用法：bots set <数量> 或 bots set <bots|humanrespawn|botrespawn|humanrole|botrole|forceroundstart|suppressroundend|mode|map|keepmagfilled|aimode|language|retreatspeed|speed|939speed|3114speed|049speed|106speed> <值>");
                    return false;
                }

                return UpdateSettingAndSave(plugin, GetArgument(arguments, 1), GetArgument(arguments, 2), out response);

            default:
                response = isPlayerSender && !isPrivilegedSender ? BuildPlayerHelp() : BuildHelp();
                return false;
        }
    }

    private static string GetArgument(ArraySegment<string> arguments, int index)
    {
        return arguments.Array![arguments.Offset + index]!;
    }

    private static bool UpdateSpeedAndSave(WarmupSandboxPlugin plugin, string key, ArraySegment<string> arguments, out string response)
    {
        if (arguments.Count < 2)
        {
            response = WarmupLocalization.T(
                $"Usage: bots set{key} <speed>",
                $"用法：bots set{key} <速度>");
            return false;
        }

        return UpdateSettingAndSave(plugin, key, GetArgument(arguments, 1), out response);
    }

    private static bool UpdateRetreatSpeedAndSave(WarmupSandboxPlugin plugin, ArraySegment<string> arguments, out string response)
    {
        if (arguments.Count < 2)
        {
            response = WarmupLocalization.T(
                "Usage: bots setretreatspeed <scale>",
                "用法：bots setretreatspeed <倍率>");
            return false;
        }

        return UpdateSettingAndSave(plugin, "retreatspeed", GetArgument(arguments, 1), out response);
    }

    private static bool BroadcastLiveUpdateWarning(WarmupSandboxPlugin plugin, ArraySegment<string> arguments, out string response)
    {
        int seconds = 30;
        int messageStartIndex = 1;
        if (arguments.Count >= 2 && int.TryParse(GetArgument(arguments, 1), out int parsedSeconds))
        {
            seconds = Math.Max(1, parsedSeconds);
            messageStartIndex = 2;
        }

        string message = JoinArguments(arguments, messageStartIndex);
        return plugin.BroadcastLiveUpdateWarning(seconds, message, out response);
    }

    private static string JoinArguments(ArraySegment<string> arguments, int startIndex)
    {
        if (arguments.Array == null || startIndex >= arguments.Count)
        {
            return string.Empty;
        }

        return string.Join(" ", arguments.Array.Skip(arguments.Offset + startIndex).Take(arguments.Count - startIndex));
    }

    private static bool UpdateSettingAndSave(WarmupSandboxPlugin plugin, string key, string value, out string response)
    {
        if (!plugin.UpdateSetting(key, value, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static bool ApplyDifficultyAndSave(WarmupSandboxPlugin plugin, string value, out string response)
    {
        if (!plugin.ApplyDifficultyPreset(value, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static bool ApplyAiModeAndSave(WarmupSandboxPlugin plugin, string value, out string response)
    {
        if (!plugin.ApplyAiMode(value, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static bool SetLanguageAndSave(WarmupSandboxPlugin plugin, string value, out string response)
    {
        if (!plugin.SetLanguage(value, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static bool SetRoundModeAndSave(WarmupSandboxPlugin plugin, string value, out string response)
    {
        if (!plugin.SetRoundMode(value, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static bool SetDust2MapAndSave(WarmupSandboxPlugin plugin, bool enabled, out string response)
    {
        if (!plugin.SetDust2MapEnabled(enabled, out response))
        {
            return false;
        }

        plugin.SaveCurrentConfig(out _);
        return true;
    }

    private static string BuildHelp()
    {
        return WarmupLocalization.T(
            "bots status | playtime [limit] | updatewarning [seconds] [message] | start | restart | roundrestart | stop | save | set <count> | setcount <count> | set939speed <speed> | set3114speed <speed> | set049speed <speed> | set106speed <speed> | setspeed <speed> | setretreatspeed <scale> | map <bomb|standard|true|false> | difficulty <easy|normal|hard|hardest> | aimode <classic|realistic> | language <en|cn> | set retreatspeed <scale> | set <key> <value>",
            "bots status | playtime [limit] | updatewarning [秒数] [消息] | start | restart | roundrestart | stop | save | set <数量> | setcount <数量> | set939speed <速度> | set3114speed <速度> | set049speed <速度> | set106speed <速度> | setspeed <速度> | setretreatspeed <倍率> | map <bomb|standard|true|false> | difficulty <easy|normal|hard|hardest> | aimode <classic|realistic> | language <en|cn> | set retreatspeed <倍率> | set <键> <值>");
    }

    private static string BuildPlayerHelp()
    {
        return WarmupLocalization.T(
            "Player warmup text commands are disabled on this server.",
            "本服务器已关闭玩家热身文本命令。");
    }

    private static bool IsPrivilegedSender(ICommandSender sender)
    {
        return sender is CommandSender commandSender
            && (commandSender.FullPermissions || commandSender.Permissions != 0UL);
    }
}
