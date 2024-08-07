namespace ACE.Server.Network.GameAction.Actions;

public static class GameActionQueryItemMana
{
    [GameAction(GameActionType.QueryItemMana)]
    public static void Handle(ClientMessage message, Session session)
    {
        var objectGuid = message.Payload.ReadUInt32();

        session.Player.HandleActionQueryItemMana(objectGuid);
    }
}
