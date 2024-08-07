using ACE.Common.Extensions;

namespace ACE.Server.Network.GameAction.Actions;

public static class GameActionFellowshipCreate
{
    [GameAction(GameActionType.FellowshipCreate)]
    public static void Handle(ClientMessage message, Session session)
    {
        var fellowshipName = message.Payload.ReadString16L();
        var shareXp = message.Payload.ReadUInt32() > 0;

        session.Player.FellowshipCreate(fellowshipName, shareXp);
    }
}
