using System;
using CommandSystem;
using ICommand = CommandSystem.ICommand;

namespace ScpslPluginStarter;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class PublicChatCommand : ICommand
{
    public string Command => "publicchat";

    public string[] Aliases => new[] { "pc" };

    public string Description => "Sends a public text chat message.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        return NativeTextChatService.TrySend(TextChatChannel.Public, arguments, sender, out response);
    }
}

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class ProximityChatCommand : ICommand
{
    public string Command => "proximitychat";

    public string[] Aliases => new[] { "c" };

    public string Description => "Sends a nearby text chat message.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        return NativeTextChatService.TrySend(TextChatChannel.Proximity, arguments, sender, out response);
    }
}

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class RadioChatCommand : ICommand
{
    public string Command => "radiochat";

    public string[] Aliases => new[] { "rc" };

    public string Description => "Sends a radio text chat message.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        return NativeTextChatService.TrySend(TextChatChannel.Radio, arguments, sender, out response);
    }
}

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class TeamChatCommand : ICommand
{
    public string Command => "teamchat";

    public string[] Aliases => new[] { "tc" };

    public string Description => "Sends a team text chat message.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        return NativeTextChatService.TrySend(TextChatChannel.Team, arguments, sender, out response);
    }
}
