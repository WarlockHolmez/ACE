namespace ACE.Server.Network.GameEvent.Events;

public class GameEventUpdateHealth : GameEventMessage
{
    public GameEventUpdateHealth(Session session, uint objectid, float health)
        : base(GameEventType.UpdateHealth, GameMessageGroup.UIQueue, session, 12)
    {
        Writer.Write(objectid);
        Writer.Write(health);
    }
}
