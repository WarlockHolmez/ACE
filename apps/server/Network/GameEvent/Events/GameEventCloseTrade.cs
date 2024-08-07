using ACE.Entity.Enum;

namespace ACE.Server.Network.GameEvent.Events;

public class GameEventCloseTrade : GameEventMessage
{
    public GameEventCloseTrade(Session session, EndTradeReason endTradeReason)
        : base(GameEventType.CloseTrade, GameMessageGroup.UIQueue, session, 8)
    {
        Writer.Write((uint)endTradeReason);
    }
}
