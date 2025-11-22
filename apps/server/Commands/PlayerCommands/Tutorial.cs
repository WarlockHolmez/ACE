using ACE.Entity.Enum;
using ACE.Server.Commands.Handlers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Commands.PlayerCommands;

public class Tutorial
{
    // sort
    [CommandHandler("tutorial", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Allows players to enabled/disable the tutorial messages.", "on/off")]
    public static void HandleTutorialToggle(Session session, params string[] parameters)
    {
        if (parameters?.Length <= 0)
        {
            session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"@tutorial on - enables tutorial popup messages.\n" +
                    $"@tutorials off - disables tutorial popup messages.",
                    ChatMessageType.Broadcast
                )
            );

            return;
        }

        var player = session.Player;
        var setting = parameters[0];

        if (player is null)
        {
            return;
        }

        if (setting is "on")
        {
            player.QuestManager.Erase("SkipTutorial");

            session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"Tutorial popup messages are now enabled.",
                    ChatMessageType.Broadcast
                )
            );
        }
        else if (setting is "off")
        {
            player.QuestManager.Stamp("SkipTutorial");

            session.Network.EnqueueSend(
                new GameMessageSystemChat(
                    $"Tutorial popup messages are now disabled.",
                    ChatMessageType.Broadcast
                )
            );
        }
    }
}
