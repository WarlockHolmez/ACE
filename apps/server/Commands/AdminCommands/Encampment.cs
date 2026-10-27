using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Server.Commands.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Commands.AdminCommands;

/// <summary>
/// Inspects and drives the enemy encampments' camp controllers ("Hearts") for testing and live tuning.
/// A Heart is any creature carrying the EncampHeart quest, which it stamps on itself when it spawns.
/// Its whole state lives in its own quest sheet (world-db: Encampments/*/... War Camp Heart).
/// </summary>
public class Encampment
{
    private const string HeartQuest = "EncampHeart";
    private const string AlarmQuest = "EncampAlarm";
    private const string RoutTicksQuest = "EncampRoutTicks";
    private const int AlarmMax = 100;

    // every quest a Heart keeps; @encamp reset erases them all
    private static readonly string[] StateQuests =
    {
        AlarmQuest,
        "EncampActive",
        "EncampLocked",
        "EncampRouted",
        RoutTicksQuest,
        "EncampStage1",
        "EncampStage2",
        "EncampStage3",
    };

    [CommandHandler(
        "encamp",
        AccessLevel.Developer,
        CommandHandlerFlag.None,
        0,
        "Shows or sets the state of the enemy encampments loaded right now.",
        "[status | alarm <0-100> | reset]\n"
            + "status (default) lists every loaded camp's Alarm and state.\n"
            + "alarm <n> sets the nearest camp's Alarm and fires any event that Alarm has reached, as if a kill had "
            + "just landed (n >= 100 brings out the boss).\n"
            + "reset clears the nearest camp back to Garrisoned. Spawned event mobs, bosses and chests stay where "
            + "they are."
    )]
    public static void HandleEncamp(Session session, params string[] parameters)
    {
        var action = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

        if (action == "status")
        {
            ShowStatus(session);
            return;
        }

        var player = session?.Player;

        if (player == null)
        {
            CommandHandlerHelper.WriteOutputInfo(
                session,
                "alarm and reset act on the camp nearest you, so they need a character in game."
            );
            return;
        }

        var heart = FindHearts()
            .Where(h => IsSameOrAdjacentLandblock(h, player))
            .OrderBy(h => h.Location.SquaredDistanceTo(player.Location))
            .FirstOrDefault();

        if (heart == null)
        {
            CommandHandlerHelper.WriteOutputInfo(
                session,
                "There's no encampment on your landblock or the ones next to it."
            );
            return;
        }

        switch (action)
        {
            case "alarm":
                if (parameters.Length < 2 || !int.TryParse(parameters[1], out var alarm) || alarm < 0)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, "Usage: @encamp alarm <0-100>");
                    return;
                }

                heart.QuestManager.SetQuestCompletions(AlarmQuest, alarm);
                // counts as fresh activity, so the next heartbeat doesn't decay it straight away
                heart.QuestManager.Stamp("EncampActive");
                // the same ladder a kill runs; it fires whatever stage this Alarm has reached
                heart.EmoteManager.ExecuteEmoteSet(EmoteCategory.GotoSet, "CheckAlarm", null, true);
                break;

            case "reset":
                foreach (var quest in StateQuests)
                {
                    heart.QuestManager.Erase(quest);
                }
                break;

            default:
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: @encamp [status | alarm <0-100> | reset]");
                return;
        }

        PlayerManager.BroadcastToAuditChannel(
            player,
            $"{player.Name} ran @encamp {string.Join(" ", parameters)} on {Describe(heart)}"
        );

        CommandHandlerHelper.WriteOutputInfo(session, Describe(heart));
    }

    private static void ShowStatus(Session session)
    {
        var hearts = FindHearts();

        if (hearts.Count == 0)
        {
            CommandHandlerHelper.WriteOutputInfo(
                session,
                "No encampments are loaded. A camp loads with its landblock."
            );
            return;
        }

        foreach (var heart in hearts.OrderBy(h => h.Location.Landblock))
        {
            CommandHandlerHelper.WriteOutputInfo(session, Describe(heart));
        }
    }

    private static List<Creature> FindHearts()
    {
        return LandblockManager
            .GetLoadedLandblocks()
            .SelectMany(lb => lb.GetAllWorldObjectsForDiagnostics())
            .OfType<Creature>()
            .Where(c => !c.IsDestroyed && c.QuestManager.HasQuest(HeartQuest))
            .ToList();
    }

    private static bool IsSameOrAdjacentLandblock(WorldObject a, WorldObject b)
    {
        // LandblockX/Y are unsigned
        var dx = Math.Abs((int)a.Location.LandblockX - (int)b.Location.LandblockX);
        var dy = Math.Abs((int)a.Location.LandblockY - (int)b.Location.LandblockY);
        return dx <= 1 && dy <= 1;
    }

    private static int Solves(Creature heart, string quest) =>
        heart.QuestManager.GetQuest(quest)?.NumTimesCompleted ?? 0;

    private static string Describe(Creature heart)
    {
        var alarm = Solves(heart, AlarmQuest);

        string state;
        if (heart.QuestManager.HasQuest("EncampRouted"))
        {
            state = $"Routed ({Solves(heart, RoutTicksQuest)} heartbeats into the rout)";
        }
        else if (heart.QuestManager.HasQuest("EncampLocked"))
        {
            state = "boss is out";
        }
        else
        {
            var fired = Enumerable.Range(1, 3).Where(n => heart.QuestManager.HasQuest($"EncampStage{n}")).ToList();
            state = fired.Count == 0 ? "Garrisoned" : $"events fired: {string.Join(", ", fired)}";
        }

        var active = heart.QuestManager.HasQuest("EncampActive") ? "" : ", no kills since the last heartbeat";

        return $"{heart.Name} at {heart.Location.ToLOCString()}: Alarm {alarm}/{AlarmMax}, {state}{active}.";
    }
}
