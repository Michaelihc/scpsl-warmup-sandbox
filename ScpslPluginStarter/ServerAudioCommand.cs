using System;
using System.IO;
using System.Linq;
using Cassie;
using CommandSystem;
using LabApi.Features.Wrappers;
using Utils;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class ServerAudioCommand : ICommand
{
    public string Command => "serveraudio";

    public string[] Aliases => new[] { "saudio", "sa", "allsay", "music" };

    public string Description => WarmupLocalization.T(
        "Sends serverwide admin messages, CASSIE speech, or WAV music.",
        "发送全服管理员消息、CASSIE 语音或 WAV 音乐。");

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
            response = BuildHelp(plugin.Config);
            return true;
        }

        string subcommand = arguments.At(0).ToLowerInvariant();
        switch (subcommand)
        {
            case "help":
            case "?":
                response = BuildHelp(plugin.Config);
                return true;

            case "say":
            case "broadcast":
            case "bc":
                return SendTextBroadcast(arguments, 1, sender, out response);

            case "speak":
            case "cassie":
            case "tts":
                return SendCassie(arguments, 1, sender, silent: false, out response);

            case "silent":
            case "cassiesilent":
                return SendCassie(arguments, 1, sender, silent: true, out response);

            case "play":
            case "wav":
                return PlayWav(plugin.Config, arguments, 1, sender, out response);

            case "stop":
                if (!HasAudioPermission(sender, out response))
                {
                    return false;
                }

                return ServerAudioPlaybackService.Stop(out response);

            case "status":
                response = ServerAudioPlaybackService.IsPlaying
                    ? WarmupLocalization.T("Server audio is currently playing.", "全服音频正在播放。")
                    : WarmupLocalization.T("No server audio is playing.", "当前没有全服音频在播放。");
                return true;
        }

        if (LooksLikeWavPath(arguments.At(0)))
        {
            return PlayWav(plugin.Config, arguments, 0, sender, out response);
        }

        return SendTextBroadcast(arguments, 0, sender, out response);
    }

    private static bool SendTextBroadcast(
        ArraySegment<string> arguments,
        int startIndex,
        ICommandSender sender,
        out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.Broadcasting, out response))
        {
            return false;
        }

        ushort durationSeconds = 8;
        if (arguments.Count > startIndex
            && ushort.TryParse(arguments.At(startIndex), out ushort parsedDuration)
            && parsedDuration > 0)
        {
            durationSeconds = parsedDuration;
            startIndex++;
        }

        if (arguments.Count <= startIndex)
        {
            response = WarmupLocalization.T(
                "Usage: serveraudio say [seconds] <message>",
                "用法：serveraudio say [秒数] <消息>");
            return false;
        }

        string message = RAUtils.FormatArguments(arguments, startIndex).Trim();
        string broadcast = $"<size=28><color=#00ffff><b>管理员广播</b></color></size>\n<size=24>{message}</size>";
        int count = 0;
        foreach (Player player in Player.List)
        {
            if (player.IsHost || player.IsDestroyed)
            {
                continue;
            }

            player.ClearBroadcasts();
            player.SendBroadcast(broadcast, durationSeconds, global::Broadcast.BroadcastFlags.Normal, true);
            count++;
        }

        ServerLogs.AddLog(
            ServerLogs.Modules.Administrative,
            $"{sender.LogName} sent a serverwide admin broadcast: {message}",
            ServerLogs.ServerLogType.RemoteAdminActivity_Misc);
        response = WarmupLocalization.T(
            $"Broadcast sent to {count} players.",
            $"已向 {count} 名玩家发送广播。");
        return true;
    }

    private static bool SendCassie(
        ArraySegment<string> arguments,
        int startIndex,
        ICommandSender sender,
        bool silent,
        out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.Announcer, out response))
        {
            return false;
        }

        if (arguments.Count <= startIndex)
        {
            response = WarmupLocalization.T(
                "Usage: serveraudio speak <cassie message>",
                "用法：serveraudio speak <CASSIE 消息>");
            return false;
        }

        string message = RAUtils.FormatArguments(arguments, startIndex).ToUpperInvariant();
        new CassieAnnouncement(new CassieTtsPayload(message, autoGenerateSubtitles: true, playBackground: !silent), 0f, 0f).AddToQueue();
        ServerLogs.AddLog(
            ServerLogs.Modules.Administrative,
            $"{sender.LogName} started a serverwide CASSIE announcement: {message}",
            ServerLogs.ServerLogType.RemoteAdminActivity_GameChanging);
        response = silent
            ? WarmupLocalization.T("Silent CASSIE announcement sent.", "无提示音 CASSIE 广播已发送。")
            : WarmupLocalization.T("CASSIE announcement sent.", "CASSIE 广播已发送。");
        return true;
    }

    private static bool PlayWav(
        PluginConfig config,
        ArraySegment<string> arguments,
        int startIndex,
        ICommandSender sender,
        out string response)
    {
        if (!HasAudioPermission(sender, out response))
        {
            return false;
        }

        if (arguments.Count <= startIndex)
        {
            response = WarmupLocalization.T(
                "Usage: serveraudio play <file.wav> [volume]",
                "用法：serveraudio play <文件.wav> [音量]");
            return false;
        }

        string fileName = arguments.At(startIndex);
        float? volume = null;
        if (arguments.Count > startIndex + 1 && float.TryParse(arguments.At(startIndex + 1), out float parsedVolume))
        {
            volume = parsedVolume;
        }

        bool started = ServerAudioPlaybackService.TryStartWav(config, fileName, sender, volume, out response);
        if (started)
        {
            ServerLogs.AddLog(
                ServerLogs.Modules.Administrative,
                $"{sender.LogName} started serverwide WAV playback: {fileName}",
                ServerLogs.ServerLogType.RemoteAdminActivity_GameChanging);
        }

        return started;
    }

    private static bool HasAudioPermission(ICommandSender sender, out string response)
    {
        if (sender.CheckPermission(PlayerPermissions.Broadcasting, out _)
            || sender.CheckPermission(PlayerPermissions.Announcer, out _))
        {
            response = string.Empty;
            return true;
        }

        response = WarmupLocalization.T(
            "Missing permission: Broadcasting or Announcer.",
            "缺少权限：Broadcasting 或 Announcer。");
        return false;
    }

    private static bool LooksLikeWavPath(string value)
    {
        return value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(value).Equals(".wav", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHelp(PluginConfig config)
    {
        return WarmupLocalization.T(
            "serveraudio say [seconds] <message> | speak <cassie message> | silent <cassie message> | play <file.wav> [volume] | stop | status. Alias: sa/music. WAV folder: LabAPI configs/*/WarmupSandbox/" + config.ServerAudio.AudioDirectoryName,
            "serveraudio say [秒数] <消息> | speak <CASSIE 消息> | silent <CASSIE 消息> | play <文件.wav> [音量] | stop | status。别名：sa/music。WAV 目录：LabAPI configs/*/WarmupSandbox/" + config.ServerAudio.AudioDirectoryName);
    }
}
