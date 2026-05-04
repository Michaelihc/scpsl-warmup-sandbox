using System;
using System.Collections.Generic;
using System.Linq;
using CommandSystem;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace ScpslPluginStarter;

internal enum TextChatChannel
{
    Public,
    Proximity,
    Radio,
    Team,
}

internal static class NativeTextChatService
{
    public static bool TrySend(
        TextChatChannel channel,
        ArraySegment<string> arguments,
        ICommandSender sender,
        out string response)
    {
        WarmupSandboxPlugin? plugin = WarmupSandboxPlugin.Instance;
        if (plugin == null)
        {
            response = WarmupLocalization.T("WarmupSandbox is not loaded.", "WarmupSandbox 未加载。");
            return false;
        }

        TextChatConfig config = plugin.Config.TextChat;
        if (!config.Enabled)
        {
            response = WarmupLocalization.T("Text chat is disabled on this server.", "本服务器已关闭文本聊天。");
            return false;
        }

        if (!IsChannelEnabled(config, channel))
        {
            response = WarmupLocalization.T("That chat channel is disabled.", "该聊天频道已关闭。");
            return false;
        }

        if (!Player.TryGet(sender, out Player? speaker) || speaker == null || !IsRealPlayer(speaker))
        {
            response = WarmupLocalization.T("Only players can use text chat.", "只有玩家可以使用文本聊天。");
            return false;
        }

        if (arguments.Count == 0)
        {
            response = BuildUsage(channel);
            return false;
        }

        string message = Sanitize(JoinArguments(arguments), config.MaxMessageLength);
        if (string.IsNullOrWhiteSpace(message))
        {
            response = WarmupLocalization.T("Message is empty.", "消息为空。");
            return false;
        }

        if (!TryGetRecipients(speaker, channel, config, out List<Player> recipients, out response))
        {
            return false;
        }

        string hint = BuildHint(speaker, channel, message);
        foreach (Player recipient in recipients)
        {
            recipient.SendHint(hint, Mathf.Max(1f, config.HintDurationSeconds));
        }

        response = config.ShowSenderConsoleResponse
            ? WarmupLocalization.T(
                $"Sent to {GetChannelName(channel)} chat ({recipients.Count}): {message}",
                $"已发送到 {GetChannelName(channel)} 聊天（{recipients.Count}）：{message}")
            : string.Empty;
        return true;
    }

    private static bool IsChannelEnabled(TextChatConfig config, TextChatChannel channel)
    {
        return channel switch
        {
            TextChatChannel.Public => config.AllowPublicChat,
            TextChatChannel.Proximity => config.AllowProximityChat,
            TextChatChannel.Radio => config.AllowRadioChat,
            TextChatChannel.Team => config.AllowTeamChat,
            _ => false,
        };
    }

    private static bool TryGetRecipients(
        Player speaker,
        TextChatChannel channel,
        TextChatConfig config,
        out List<Player> recipients,
        out string response)
    {
        IEnumerable<Player> candidates = Player.ReadyList.Where(IsRealPlayer);
        bool speakerAlive = IsAlive(speaker);

        switch (channel)
        {
            case TextChatChannel.Public:
                if (!speakerAlive && !config.AllowSpectatorsChat)
                {
                    recipients = new List<Player>();
                    response = WarmupLocalization.T("Spectators cannot use public chat.", "观察者不能使用公共聊天。");
                    return false;
                }

                recipients = candidates
                    .Where(candidate => config.AllowSpectatorsChat || IsAlive(candidate))
                    .Where(candidate => config.AllowScpAndHumanPublicChat || IsSameChatGroup(speaker, candidate))
                    .ToList();
                break;

            case TextChatChannel.Proximity:
                if (!speakerAlive)
                {
                    recipients = new List<Player>();
                    response = WarmupLocalization.T("Spectators cannot use proximity chat.", "观察者不能使用近距离聊天。");
                    return false;
                }

                float maxDistanceSqr = Mathf.Max(0f, config.ProximityChatDistance);
                maxDistanceSqr *= maxDistanceSqr;
                recipients = candidates
                    .Where(IsAlive)
                    .Where(candidate => config.AllowScpAndHumanProximityChat || IsSameChatGroup(speaker, candidate))
                    .Where(candidate => (candidate.Position - speaker.Position).sqrMagnitude <= maxDistanceSqr)
                    .ToList();
                break;

            case TextChatChannel.Radio:
                if (!speakerAlive)
                {
                    recipients = new List<Player>();
                    response = WarmupLocalization.T("Spectators cannot use radio chat.", "观察者不能使用无线电聊天。");
                    return false;
                }

                if (!HasUsableRadio(speaker))
                {
                    recipients = new List<Player>();
                    response = WarmupLocalization.T("You need a usable radio to use radio chat.", "你需要可用的无线电才能使用无线电聊天。");
                    return false;
                }

                recipients = candidates
                    .Where(IsAlive)
                    .Where(candidate => candidate.Team != Team.SCPs)
                    .Where(candidate => IsSameChatGroup(speaker, candidate))
                    .Where(HasUsableRadio)
                    .ToList();
                break;

            case TextChatChannel.Team:
                recipients = candidates
                    .Where(candidate => IsSameChatGroup(speaker, candidate))
                    .Where(candidate => config.AllowSpectatorsChat || IsAlive(candidate))
                    .ToList();
                break;

            default:
                recipients = new List<Player>();
                response = WarmupLocalization.T("Unknown chat channel.", "未知聊天频道。");
                return false;
        }

        if (recipients.Count == 0)
        {
            response = WarmupLocalization.T("No players can receive that message.", "没有玩家可以接收该消息。");
            return false;
        }

        response = string.Empty;
        return true;
    }

    private static bool IsRealPlayer(Player player)
    {
        return !player.IsDestroyed
            && player.IsReady
            && player.IsPlayer
            && !player.IsNpc
            && !player.IsDummy
            && !player.IsHost;
    }

    private static bool IsAlive(Player player)
    {
        return player.Role != RoleTypeId.Spectator && player.Team != Team.Dead && player.IsAlive;
    }

    private static bool HasUsableRadio(Player player)
    {
        return player.Items
            .OfType<RadioItem>()
            .Any(radio => radio.IsUsable && radio.BatteryPercent > 0);
    }

    private static bool IsSameChatGroup(Player a, Player b)
    {
        if (!IsAlive(a) || !IsAlive(b))
        {
            return !IsAlive(a) && !IsAlive(b);
        }

        if (a.Team == Team.SCPs || b.Team == Team.SCPs)
        {
            return a.Team == b.Team;
        }

        if (IsFoundationHumanRole(a.Role) && IsFoundationHumanRole(b.Role))
        {
            return true;
        }

        if (IsChaosHumanRole(a.Role) && IsChaosHumanRole(b.Role))
        {
            return true;
        }

        return a.Team == b.Team;
    }

    private static bool IsFoundationHumanRole(RoleTypeId role)
    {
        return role is RoleTypeId.NtfCaptain
            or RoleTypeId.NtfPrivate
            or RoleTypeId.NtfSergeant
            or RoleTypeId.NtfSpecialist
            or RoleTypeId.FacilityGuard
            or RoleTypeId.Scientist;
    }

    private static bool IsChaosHumanRole(RoleTypeId role)
    {
        return role is RoleTypeId.ChaosConscript
            or RoleTypeId.ChaosMarauder
            or RoleTypeId.ChaosRepressor
            or RoleTypeId.ChaosRifleman
            or RoleTypeId.ClassD;
    }

    private static string BuildHint(Player speaker, TextChatChannel channel, string message)
    {
        string channelName = GetChannelName(channel).ToUpperInvariant();
        string channelColor = GetChannelColor(channel);
        string roleColor = speaker.Team == Team.SCPs ? "#ff595e" : speaker.IsChaos ? "#95d36a" : "#80d8ff";
        string name = Sanitize(speaker.DisplayName ?? speaker.Nickname, 64);
        return $"<align=left><size=22><color={channelColor}>[{channelName}]</color> <color={roleColor}>{name}</color>: {message}</size></align>";
    }

    private static string GetChannelName(TextChatChannel channel)
    {
        return channel switch
        {
            TextChatChannel.Public => "public",
            TextChatChannel.Proximity => "nearby",
            TextChatChannel.Radio => "radio",
            TextChatChannel.Team => "team",
            _ => "chat",
        };
    }

    private static string GetChannelColor(TextChatChannel channel)
    {
        return channel switch
        {
            TextChatChannel.Public => "#ffd166",
            TextChatChannel.Proximity => "#7dd3fc",
            TextChatChannel.Radio => "#a7f3d0",
            TextChatChannel.Team => "#c4b5fd",
            _ => "#ffffff",
        };
    }

    private static string BuildUsage(TextChatChannel channel)
    {
        return channel switch
        {
            TextChatChannel.Public => WarmupLocalization.T("Usage: .pc <message>", "用法：.pc <消息>"),
            TextChatChannel.Proximity => WarmupLocalization.T("Usage: .c <message>", "用法：.c <消息>"),
            TextChatChannel.Radio => WarmupLocalization.T("Usage: .rc <message>", "用法：.rc <消息>"),
            TextChatChannel.Team => WarmupLocalization.T("Usage: .tc <message>", "用法：.tc <消息>"),
            _ => WarmupLocalization.T("Usage: .pc <message>", "用法：.pc <消息>"),
        };
    }

    private static string JoinArguments(ArraySegment<string> arguments)
    {
        string[] source = arguments.Array ?? Array.Empty<string>();
        return string.Join(" ", source.Skip(arguments.Offset).Take(arguments.Count));
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed.Substring(0, maxLength);
        }

        return trimmed
            .Replace("<", string.Empty)
            .Replace(">", string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
