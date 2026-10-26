using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Commands.Handlers;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Network;
using ACE.Server.Pathfinding;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Patrol;

namespace ACE.Server.Commands.DeveloperCommands;

/// <summary>
/// The outdoor navmesh (Pathfinding.OutdoorNavMesh): builds tiles, and draws routes with marker objects
/// </summary>
public class NavMeshCommand
{
    // a torch: easy to see, and gone again after MarkerLifespan seconds
    private const uint DefaultMarkerWcid = 293;
    private const int MarkerLifespan = 60;

    // markers along each leg this far apart, as well as on the corners
    private const float MarkerSpacing = 8.0f;

    // where @navmesh route goes without an appraised target, set by @navmesh mark (per player)
    private static readonly Dictionary<uint, Position> marks = new();

    [CommandHandler(
        "navmesh",
        AccessLevel.Developer,
        CommandHandlerFlag.RequiresWorld,
        0,
        "Outdoor navmesh pathfinding.",
        "route [wide] [wcid] - draws a route from you to your appraised target (or your mark) with markers\n"
            + "mark - remembers where you stand, for route to go to with nothing appraised\n"
            + "build [radius] [wide] - builds the tiles of the landblocks around you (radius 1 = 3x3), ignoring the cache\n"
            + "info - the tile you're in: loaded, walkable area, build stats\n"
            + "unload - takes every tile out of the navmesh (the disk cache stays)"
    )]
    public static void HandleNavMesh(Session session, params string[] parameters)
    {
        var player = session.Player;
        var command = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "route";
        var width = parameters.Any(p => p.Equals("wide", StringComparison.OrdinalIgnoreCase)) ? AgentWidth.Wide : AgentWidth.Narrow;

        if (player.Location.Indoors)
        {
            CommandHandlerHelper.WriteOutputInfo(session, "The outdoor navmesh only covers outdoors.");
            return;
        }

        var here = player.Location.ToGlobal();
        var landblock = OutdoorNavMesh.LandblockAt(here);

        switch (command)
        {
            case "mark":
                marks[player.Guid.Full] = new Position(player.Location);
                CommandHandlerHelper.WriteOutputInfo(session, $"Route mark set at {player.Location.ToLOCString()}.");
                return;

            case "build":
                Build(session, landblock, parameters.Length > 1 && int.TryParse(parameters[1], out var radius) ? Math.Clamp(radius, 0, 3) : 1, width);
                return;

            case "info":
                Info(session, landblock);
                return;

            case "unload":
                OutdoorNavMesh.UnloadAll();
                CommandHandlerHelper.WriteOutputInfo(session, "Navmesh tiles unloaded.");
                return;

            case "route":
                Route(session, width, parameters);
                return;

            default:
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: @navmesh route|mark|build|info|unload (see @acehelp navmesh)");
                return;
        }
    }

    private static void Route(Session session, AgentWidth width, string[] parameters)
    {
        var player = session.Player;

        Position destination = null;
        var target = CommandHandlerHelper.GetLastAppraisedObject(session);
        if (target?.Location != null && target != player)
        {
            destination = target.Location;
        }
        else if (marks.TryGetValue(player.Guid.Full, out var mark))
        {
            destination = mark;
        }

        if (destination == null || destination.Indoors)
        {
            CommandHandlerHelper.WriteOutputInfo(session, "Appraise an outdoor target, or set a mark with @navmesh mark.");
            return;
        }

        var start = player.Location.ToGlobal();
        var route = OutdoorNavMesh.FindRoute(start, destination.ToGlobal(), width);

        if (route.Result == NavRouteResult.NotReady)
        {
            CommandHandlerHelper.WriteOutputInfo(session, "Building the navmesh tiles for this route in the background. Try again in a few seconds.");
            return;
        }

        if (route.Result is not (NavRouteResult.Found or NavRouteResult.Partial))
        {
            CommandHandlerHelper.WriteOutputInfo(session, $"No route: {route.Result}.");
            return;
        }

        var wcid = DefaultMarkerWcid;
        foreach (var p in parameters.Skip(1))
        {
            if (uint.TryParse(p, out var parsed))
            {
                wcid = parsed;
            }
        }

        // the corners, and markers along the legs between them
        var points = new List<(Vector3 pos, string name)>();
        for (var i = 0; i < route.Corners.Count; i++)
        {
            if (i > 0)
            {
                var from = route.Corners[i - 1];
                var to = route.Corners[i];
                var steps = (int)(Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y)) / MarkerSpacing);
                for (var s = 1; s <= steps; s++)
                {
                    var t = s / (float)(steps + 1);
                    points.Add((Vector3.Lerp(from, to, t), "Route"));
                }
            }
            points.Add((route.Corners[i], $"Route corner {i + 1} of {route.Corners.Count}"));
        }

        var placed = 0;
        foreach (var (pos, name) in points)
        {
            var marker = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (marker == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"No weenie {wcid} to mark the route with.");
                return;
            }

            marker.Name = name;
            marker.Location = PatrolComponent.BuildOutdoorPosition(player.Location, pos.X - start.X, pos.Y - start.Y, player.InstanceId);
            marker.Lifespan = MarkerLifespan;
            marker.Stuck = true;

            if (marker.EnterWorld())
            {
                placed++;
            }
        }

        CommandHandlerHelper.WriteOutputInfo(
            session,
            $"{route.Result} route ({width}): {route.Corners.Count} corners, {route.Length:F0} m over {route.Polys} polygons, found in {route.QueryMs:F2} ms. {placed} markers placed for {MarkerLifespan} s."
        );
    }

    private static void Build(Session session, ushort centre, int radius, AgentWidth width)
    {
        var landblocks = new List<ushort>();
        for (var x = (centre >> 8) - radius; x <= (centre >> 8) + radius; x++)
        {
            for (var y = (centre & 0xFF) - radius; y <= (centre & 0xFF) + radius; y++)
            {
                if (x >= 0 && y >= 0 && x <= 0xFE && y <= 0xFE)
                {
                    landblocks.Add((ushort)(x << 8 | y));
                }
            }
        }

        CommandHandlerHelper.WriteOutputInfo(session, $"Building {landblocks.Count} navmesh tiles ({width}) in the background...");

        // off the world thread: a tile takes about a second
        Task.Run(() =>
        {
            var started = DateTime.UtcNow;
            foreach (var landblock in landblocks)
            {
                var stats = OutdoorNavMesh.LoadTile(landblock, width, false);
                CommandHandlerHelper.WriteOutputInfo(session, Describe(stats));
            }
            CommandHandlerHelper.WriteOutputInfo(session, $"Built {landblocks.Count} tiles in {(DateTime.UtcNow - started).TotalSeconds:F1} s.");
        });
    }

    private static void Info(Session session, ushort landblock)
    {
        foreach (var width in Enum.GetValues<AgentWidth>())
        {
            if (!OutdoorNavMesh.IsLoaded(landblock, width))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"{landblock:X4} ({width}): not loaded.");
                continue;
            }

            var stats = OutdoorNavMesh.GetStats(landblock, width);
            CommandHandlerHelper.WriteOutputInfo(
                session,
                $"{landblock:X4} ({width}): walkable {OutdoorNavMesh.WalkableArea(landblock, width):F0} of 36864 m2. {(stats != null ? Describe(stats) : "")}"
            );
        }
    }

    private static string Describe(NavTileStats stats)
    {
        if (stats.FromCache)
        {
            return $"{stats.Landblock:X4}: from cache, {stats.Polys} polygons, {stats.DataBytes / 1024.0:F0} KB";
        }

        return $"{stats.Landblock:X4}: {stats.InputTriangles} triangles from {stats.Buildings} buildings, {stats.Statics} statics, {stats.Scenery} scenery; "
            + $"{stats.Polys} polygons, {stats.DataBytes / 1024.0:F0} KB; {stats.GeometryMs + stats.BuildMs:F0} ms";
    }
}
