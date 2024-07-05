namespace ACE.Server.Network.GameEvent.Events;

public class GameEventFellowshipDisband : GameEventMessage
{
    public GameEventFellowshipDisband(Session session)
        : base(GameEventType.FellowshipDisband, GameMessageGroup.UIQueue, session, 4)
    {
        // empty base
    }
}
