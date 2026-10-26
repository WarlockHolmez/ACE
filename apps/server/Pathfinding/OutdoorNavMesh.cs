using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ACE.DatLoader;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Toolset.Builder;
using Serilog;

namespace ACE.Server.Pathfinding;

/// <summary>
/// How much room a creature needs: the navmesh keeps this far from walls, so a wide creature gets a mesh of its own
/// </summary>
public enum AgentWidth
{
    Narrow,
    Wide,
}

/// <summary>
/// The Recast settings an outdoor tile is built with
/// </summary>
public sealed record OutdoorNavSettings(
    float CellSize,
    float CellHeight,
    float AgentHeight,
    float AgentRadius,
    float AgentMaxClimb,
    float AgentMaxSlope,
    float EdgeMaxLen,
    float EdgeMaxError,
    int MinRegionSize,
    int MergedRegionSize,
    float DetailSampleDist,
    float DetailSampleMaxError,
    RcPartition Partition
)
{
    /// <summary>
    /// Changes whenever a setting does, so tiles cached with other settings are rebuilt
    /// </summary>
    public uint Hash()
    {
        var hash = 2166136261;
        foreach (var b in Encoding.ASCII.GetBytes(ToString()))
        {
            hash = (hash ^ b) * 16777619;
        }
        return hash;
    }
}

public enum NavRouteResult
{
    Found,
    Partial,
    NotReady,
    NoStartPoly,
    NoEndPoly,
    NoPath,
    TooFar,
}

/// <summary>
/// A route across the outdoor navmesh: the corners to run to, in global coordinates (x east, y north, z up)
/// </summary>
public sealed class NavRoute
{
    public NavRouteResult Result;
    public readonly List<Vector3> Corners = new();
    public float Length;
    public int Polys;
    public double QueryMs;
}

public sealed record NavTileStats(
    ushort Landblock,
    AgentWidth Width,
    bool FromCache,
    int InputTriangles,
    int Buildings,
    int Statics,
    int Scenery,
    int Blockers,
    int Polys,
    int Verts,
    int DataBytes,
    double GeometryMs,
    double BuildMs,
    long AllocatedBytes
);

/// <summary>
/// Outdoor pathfinding on a tiled Detour navmesh: one tile per landblock, in global coordinates, so routes run across
/// landblock edges. Tiles are built from the same collision shapes server physics uses (OutdoorGeometry) and cached on disk
/// per landblock and agent width.<para />
/// Based on ClassicACE's dungeon pathfinding (ACE.Server/Pathfinding, which builds one untiled mesh per dungeon landblock).
/// </summary>
public static class OutdoorNavMesh
{
    private static readonly ILogger _log = Log.ForContext(typeof(OutdoorNavMesh));

    private const int VertsPerPoly = 6;

    private const int CacheMagic = 0x544E4341; // "ACNT"
    // bump when OutdoorGeometry changes what goes into a tile: cached tiles are only rebuilt by this, new settings or new dats
    private const int CacheFormatVersion = 1;

    // routes are only looked for between points this many landblocks apart (or closer)
    private const int MaxRouteLandblocks = 2;

    private const int MaxCorners = 128;

    /// <summary>
    /// Physics walks up anything flatter than PhysicsGlobals.FloorZ (about 48 degrees)
    /// </summary>
    private const float MaxSlope = 48.0f;

    public static readonly IReadOnlyDictionary<AgentWidth, OutdoorNavSettings> Settings = new Dictionary<AgentWidth, OutdoorNavSettings>
    {
        [AgentWidth.Narrow] = new(0.3f, 0.1f, 1.8f, 0.8f, 0.6f, MaxSlope, 12.0f, 1.3f, 8, 20, 6.0f, 1.0f, RcPartition.WATERSHED),
        [AgentWidth.Wide] = new(0.3f, 0.1f, 2.4f, 1.5f, 0.6f, MaxSlope, 12.0f, 1.3f, 8, 20, 6.0f, 1.0f, RcPartition.WATERSHED),
    };

    /// <summary>
    /// Where built tiles are kept, one file per landblock and agent width
    /// </summary>
    public static string CacheDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Pathfinding", "Meshes", "Outdoors");

    private sealed class MeshSet
    {
        public readonly DtNavMesh Mesh = new();
        public readonly ReaderWriterLockSlim Lock = new();

        /// <summary>
        /// Landblocks whose tile is in the mesh (or has nothing walkable, a value of false)
        /// </summary>
        public readonly ConcurrentDictionary<ushort, bool> Loaded = new();
        public readonly ConcurrentDictionary<ushort, Lazy<Task>> Building = new();
    }

    private static readonly Dictionary<AgentWidth, MeshSet> meshes = new();

    private static readonly ConcurrentDictionary<ushort, Lazy<LandblockShapes>> shapesCache = new();

    // one or two tiles at a time in the background, so building a town's worth never takes over the server
    private static readonly SemaphoreSlim buildSlots = new(2);

    private static readonly ConcurrentDictionary<(ushort, AgentWidth), NavTileStats> lastStats = new();

    [ThreadStatic]
    private static Dictionary<AgentWidth, DtNavMeshQuery> queries;

    private static int? cellIteration;
    private static int? portalIteration;

    static OutdoorNavMesh()
    {
        foreach (var (width, settings) in Settings)
        {
            var set = new MeshSet();
            var tileWidth = TileCells(settings) * settings.CellSize;

            set.Mesh.Init(
                new DtNavMeshParams
                {
                    orig = RcVec3f.Zero,
                    tileWidth = tileWidth,
                    tileHeight = tileWidth,
                    maxTiles = 4096,
                    maxPolys = 1 << DtDetour.DT_POLY_BITS,
                },
                VertsPerPoly
            );

            meshes[width] = set;
        }
    }

    private static int TileCells(OutdoorNavSettings settings)
    {
        return (int)MathF.Round(Physics.Common.LandDefs.BlockLength / settings.CellSize);
    }

    public static ushort LandblockAt(Vector3 global)
    {
        var x = (int)MathF.Floor(global.X / Physics.Common.LandDefs.BlockLength);
        var y = (int)MathF.Floor(global.Y / Physics.Common.LandDefs.BlockLength);
        return (ushort)(Math.Clamp(x, 0, 0xFE) << 8 | Math.Clamp(y, 0, 0xFE));
    }

    public static bool IsLoaded(ushort landblock, AgentWidth width)
    {
        return meshes[width].Loaded.ContainsKey(landblock);
    }

    public static NavTileStats GetStats(ushort landblock, AgentWidth width)
    {
        return lastStats.TryGetValue((landblock, width), out var stats) ? stats : null;
    }

    /// <summary>
    /// How much of a landblock's tile can be walked on, in square meters (a landblock is 36,864)
    /// </summary>
    public static float WalkableArea(ushort landblock, AgentWidth width)
    {
        var set = meshes[width];

        set.Lock.EnterReadLock();
        try
        {
            var tile = set.Mesh.GetTileByRef(set.Mesh.GetTileRefAt(landblock >> 8, landblock & 0xFF, 0));
            if (tile?.data == null)
            {
                return 0;
            }

            var area = 0.0f;
            foreach (var poly in tile.data.polys)
            {
                for (var i = 2; i < poly.vertCount; i++)
                {
                    var a = poly.verts[0] * 3;
                    var b = poly.verts[i - 1] * 3;
                    var c = poly.verts[i] * 3;
                    var v = tile.data.verts;
                    area += MathF.Abs((v[b] - v[a]) * (v[c + 2] - v[a + 2]) - (v[c] - v[a]) * (v[b + 2] - v[a + 2])) * 0.5f;
                }
            }
            return area;
        }
        finally
        {
            set.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// The polygons of a landblock's tile, in global coordinates, for drawing the mesh
    /// </summary>
    internal static List<Vector3[]> GetTilePolygons(ushort landblock, AgentWidth width)
    {
        var polygons = new List<Vector3[]>();
        var set = meshes[width];

        set.Lock.EnterReadLock();
        try
        {
            var tile = set.Mesh.GetTileByRef(set.Mesh.GetTileRefAt(landblock >> 8, landblock & 0xFF, 0));
            if (tile?.data == null)
            {
                return polygons;
            }

            var v = tile.data.verts;
            foreach (var poly in tile.data.polys)
            {
                var points = new Vector3[poly.vertCount];
                for (var i = 0; i < poly.vertCount; i++)
                {
                    var j = poly.verts[i] * 3;
                    points[i] = new Vector3(v[j], v[j + 2], v[j + 1]);
                }
                polygons.Add(points);
            }
        }
        finally
        {
            set.Lock.ExitReadLock();
        }

        return polygons;
    }

    /// <summary>
    /// Finds a route from start to end (global coordinates), or says why there isn't one. The tiles the route needs are built
    /// in the background the first time they're asked for, and NotReady comes back until they're done.
    /// </summary>
    public static NavRoute FindRoute(Vector3 start, Vector3 end, AgentWidth width, bool buildMissing = true)
    {
        var route = new NavRoute();
        var set = meshes[width];

        var startBlock = LandblockAt(start);
        var endBlock = LandblockAt(end);

        if (Math.Abs((startBlock >> 8) - (endBlock >> 8)) > MaxRouteLandblocks || Math.Abs((startBlock & 0xFF) - (endBlock & 0xFF)) > MaxRouteLandblocks)
        {
            route.Result = NavRouteResult.TooFar;
            return route;
        }

        var ready = true;
        foreach (var landblock in LandblocksBetween(startBlock, endBlock))
        {
            if (set.Loaded.ContainsKey(landblock))
            {
                continue;
            }

            ready = false;
            if (buildMissing)
            {
                QueueTile(landblock, width);
            }
        }

        if (!ready)
        {
            route.Result = NavRouteResult.NotReady;
            return route;
        }

        var stopwatch = Stopwatch.StartNew();

        set.Lock.EnterReadLock();
        try
        {
            Query(set, width, ToRecast(start), ToRecast(end), route);
        }
        finally
        {
            set.Lock.ExitReadLock();
        }

        route.QueryMs = stopwatch.Elapsed.TotalMilliseconds;
        return route;
    }

    private static void Query(MeshSet set, AgentWidth width, RcVec3f start, RcVec3f end, NavRoute route)
    {
        queries ??= new Dictionary<AgentWidth, DtNavMeshQuery>();
        if (!queries.TryGetValue(width, out var query))
        {
            query = new DtNavMeshQuery(set.Mesh);
            queries[width] = query;
        }

        var filter = new DtQueryDefaultFilter();
        var radius = Settings[width].AgentRadius;

        // the creature may be up against a wall, inside the margin the mesh keeps from it, and the target too
        query.FindNearestPoly(start, new RcVec3f(radius + 1.0f, 3.0f, radius + 1.0f), filter, out var startRef, out var startPt, out _);
        if (startRef == 0)
        {
            route.Result = NavRouteResult.NoStartPoly;
            return;
        }

        query.FindNearestPoly(end, new RcVec3f(radius + 2.0f, 4.0f, radius + 2.0f), filter, out var endRef, out var endPt, out _);
        if (endRef == 0)
        {
            route.Result = NavRouteResult.NoEndPoly;
            return;
        }

        var polys = new List<long>();
        var status = query.FindPath(startRef, endRef, startPt, endPt, filter, ref polys, DtFindPathOption.NoOption);
        if (status.Failed() || polys.Count == 0)
        {
            route.Result = NavRouteResult.NoPath;
            return;
        }

        var complete = polys[^1] == endRef;
        if (!complete)
        {
            // as close as the mesh gets
            query.ClosestPointOnPoly(polys[^1], endPt, out endPt, out _);
        }

        Span<DtStraightPath> straight = stackalloc DtStraightPath[MaxCorners];
        query.FindStraightPath(startPt, endPt, polys, polys.Count, straight, out var count, MaxCorners, 0);

        var previous = FromRecast(startPt);
        for (var i = 0; i < count; i++)
        {
            var corner = FromRecast(straight[i].pos);
            route.Length += Vector2.Distance(new Vector2(previous.X, previous.Y), new Vector2(corner.X, corner.Y));
            route.Corners.Add(corner);
            previous = corner;
        }

        route.Polys = polys.Count;
        route.Result = complete ? NavRouteResult.Found : NavRouteResult.Partial;
    }

    private static IEnumerable<ushort> LandblocksBetween(ushort a, ushort b)
    {
        var x0 = Math.Min(a >> 8, b >> 8);
        var x1 = Math.Max(a >> 8, b >> 8);
        var y0 = Math.Min(a & 0xFF, b & 0xFF);
        var y1 = Math.Max(a & 0xFF, b & 0xFF);

        for (var x = x0; x <= x1; x++)
        {
            for (var y = y0; y <= y1; y++)
            {
                yield return (ushort)(x << 8 | y);
            }
        }
    }

    /// <summary>
    /// Builds (or loads from the cache) a landblock's tile in the background, if it isn't already
    /// </summary>
    public static Task QueueTile(ushort landblock, AgentWidth width)
    {
        var set = meshes[width];
        if (set.Loaded.ContainsKey(landblock))
        {
            return Task.CompletedTask;
        }

        return set.Building.GetOrAdd(
            landblock,
            key =>
                new Lazy<Task>(() => Task.Run(async () =>
                {
                    await buildSlots.WaitAsync();
                    try
                    {
                        LoadTile(landblock, width, true);
                    }
                    catch (Exception e)
                    {
                        _log.Error(e, "Navmesh: building tile {Landblock:X4} ({Width}) failed", landblock, width);

                        // nothing walkable rather than asking again on every chase
                        set.Loaded[landblock] = false;
                    }
                    finally
                    {
                        buildSlots.Release();
                        set.Building.TryRemove(landblock, out _);
                    }
                }))
        ).Value;
    }

    /// <summary>
    /// Builds a landblock's tile now (or loads it from the cache) and puts it in the navmesh, replacing any it had
    /// </summary>
    public static NavTileStats LoadTile(ushort landblock, AgentWidth width, bool useCache)
    {
        var set = meshes[width];
        var settings = Settings[width];

        DtMeshData data = null;
        NavTileStats stats = null;

        if (useCache && TryReadCache(landblock, width, settings, out data, out var bytes))
        {
            stats = new NavTileStats(landblock, width, true, 0, 0, 0, 0, 0, data?.header.polyCount ?? 0, data?.header.vertCount ?? 0, bytes, 0, 0, 0);
        }
        else
        {
            (data, stats) = BuildTile(landblock, width);
            WriteCache(landblock, width, settings, data);
        }

        set.Lock.EnterWriteLock();
        try
        {
            var existing = set.Mesh.GetTileRefAt(landblock >> 8, landblock & 0xFF, 0);
            if (existing != 0)
            {
                set.Mesh.RemoveTile(existing);
            }

            if (data != null)
            {
                set.Mesh.AddTile(data, 0, 0, out _);
            }

            set.Loaded[landblock] = data != null;
        }
        finally
        {
            set.Lock.ExitWriteLock();
        }

        lastStats[(landblock, width)] = stats;
        return stats;
    }

    /// <summary>
    /// Takes every tile out of the navmesh and forgets the shapes read for them (the disk cache is left alone)
    /// </summary>
    public static void UnloadAll()
    {
        foreach (var set in meshes.Values)
        {
            set.Lock.EnterWriteLock();
            try
            {
                foreach (var landblock in set.Loaded.Keys)
                {
                    var tileRef = set.Mesh.GetTileRefAt(landblock >> 8, landblock & 0xFF, 0);
                    if (tileRef != 0)
                    {
                        set.Mesh.RemoveTile(tileRef);
                    }
                }
                set.Loaded.Clear();
            }
            finally
            {
                set.Lock.ExitWriteLock();
            }
        }

        shapesCache.Clear();
    }

    /// <summary>
    /// Builds one tile from the dats: the landblock's own shapes, and its neighbours' near enough to its edge to matter
    /// </summary>
    public static (DtMeshData data, NavTileStats stats) BuildTile(ushort landblock, AgentWidth width)
    {
        var settings = Settings[width];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        var shapes = new List<LandblockShapes>();
        var lbx = landblock >> 8;
        var lby = landblock & 0xFF;

        for (var x = lbx - 1; x <= lbx + 1; x++)
        {
            for (var y = lby - 1; y <= lby + 1; y++)
            {
                if (x >= 0 && y >= 0 && x <= 0xFE && y <= 0xFE)
                {
                    shapes.Add(GetShapes((ushort)(x << 8 | y)));
                }
            }
        }

        var own = GetShapes(landblock);

        var tileCells = TileCells(settings);
        var border = RcConfig.CalcBorder(settings.AgentRadius, settings.CellSize);
        var geometry = new OutdoorTileGeometry(landblock, shapes, border * settings.CellSize + 1.0f);

        var geometryMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();

        var cfg = new RcConfig(
            true,
            tileCells,
            tileCells,
            border,
            settings.Partition,
            settings.CellSize,
            settings.CellHeight,
            settings.AgentMaxSlope,
            settings.AgentHeight,
            settings.AgentRadius,
            settings.AgentMaxClimb,
            settings.MinRegionSize * settings.MinRegionSize * settings.CellSize * settings.CellSize,
            settings.MergedRegionSize * settings.MergedRegionSize * settings.CellSize * settings.CellSize,
            settings.EdgeMaxLen,
            settings.EdgeMaxError,
            VertsPerPoly,
            settings.DetailSampleDist,
            settings.DetailSampleMaxError,
            true,
            true,
            true,
            SampleAreaModifications.SAMPLE_AREAMOD_WALKABLE,
            true
        );

        var boundsMin = geometry.GetMeshBoundsMin();
        var boundsMax = geometry.GetMeshBoundsMax();
        var builderConfig = new RcBuilderConfig(cfg, new RcVec3f(0, boundsMin.Y, 0), new RcVec3f(0, boundsMax.Y, 0), lbx, lby);

        DtMeshData data = null;

        if (geometry.TriangleCount > 0)
        {
            var result = new RcBuilder().Build(geometry, builderConfig, false);

            if (result.Mesh.npolys > 0)
            {
                var createParams = DemoNavMeshBuilder.GetNavMeshCreateParams(
                    geometry,
                    settings.CellSize,
                    settings.CellHeight,
                    settings.AgentHeight,
                    settings.AgentRadius,
                    settings.AgentMaxClimb,
                    result
                );
                createParams.tileX = lbx;
                createParams.tileZ = lby;

                data = DtNavMeshBuilder.CreateNavMeshData(createParams);
                if (data != null)
                {
                    DemoNavMeshBuilder.UpdateAreaAndFlags(data);
                }
            }
        }

        var buildMs = stopwatch.Elapsed.TotalMilliseconds;

        var stats = new NavTileStats(
            landblock,
            width,
            false,
            geometry.TriangleCount,
            own.Buildings,
            own.Statics,
            own.Scenery,
            own.Blockers.Count,
            data?.header.polyCount ?? 0,
            data?.header.vertCount ?? 0,
            data != null ? Serialize(data).Length : 0,
            geometryMs,
            buildMs,
            GC.GetAllocatedBytesForCurrentThread() - allocated
        );

        return (data, stats);
    }

    internal static LandblockShapes GetShapes(ushort landblock)
    {
        // a town's worth of landblocks is plenty to keep: building a town reads each one up to nine times
        if (shapesCache.Count > 64)
        {
            shapesCache.Clear();
        }

        return shapesCache.GetOrAdd(landblock, lb => new Lazy<LandblockShapes>(() => OutdoorGeometry.Load(lb))).Value;
    }

    private static RcVec3f ToRecast(Vector3 v)
    {
        return new RcVec3f(v.X, v.Z, v.Y);
    }

    private static Vector3 FromRecast(RcVec3f v)
    {
        return new Vector3(v.X, v.Z, v.Y);
    }

    private static byte[] Serialize(DtMeshData data)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        writer.Flush();
        return stream.ToArray();
    }

    private static string CachePath(ushort landblock, AgentWidth width)
    {
        return Path.Combine(CacheDirectory, $"{landblock:X4}_{width}.navtile");
    }

    private static (int cell, int portal) DatIterations()
    {
        cellIteration ??= DatManager.CellDat?.Iteration ?? 0;
        portalIteration ??= DatManager.PortalDat?.Iteration ?? 0;
        return (cellIteration.Value, portalIteration.Value);
    }

    /// <summary>
    /// Reads a cached tile, if there is one built with these settings from these dats. An empty tile (nothing walkable) is cached
    /// too, and comes back as a null tile.
    /// </summary>
    private static bool TryReadCache(ushort landblock, AgentWidth width, OutdoorNavSettings settings, out DtMeshData data, out int bytes)
    {
        data = null;
        bytes = 0;
        var path = CachePath(landblock, width);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var (cell, portal) = DatIterations();
            if (reader.ReadInt32() != CacheMagic || reader.ReadInt32() != CacheFormatVersion || reader.ReadUInt32() != settings.Hash()
                || reader.ReadInt32() != cell || reader.ReadInt32() != portal)
            {
                return false;
            }

            var length = reader.ReadInt32();
            if (length > 0)
            {
                var meshBytes = reader.ReadBytes(length);
                bytes = meshBytes.Length;
                data = new DtMeshDataReader().Read(new RcByteBuffer(meshBytes), VertsPerPoly, false);
            }
            return true;
        }
        catch (Exception e)
        {
            _log.Warning(e, "Navmesh: cached tile {Path} unreadable, rebuilding", path);
            data = null;
            return false;
        }
    }

    private static void WriteCache(ushort landblock, AgentWidth width, OutdoorNavSettings settings, DtMeshData data)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var meshBytes = data != null ? Serialize(data) : Array.Empty<byte>();
            var (cell, portal) = DatIterations();

            var path = CachePath(landblock, width);
            var temp = path + ".tmp";

            using (var stream = File.Create(temp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheFormatVersion);
                writer.Write(settings.Hash());
                writer.Write(cell);
                writer.Write(portal);
                writer.Write(meshBytes.Length);
                writer.Write(meshBytes);
            }

            File.Move(temp, path, true);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Navmesh: couldn't cache tile {Landblock:X4} ({Width})", landblock, width);
        }
    }
}
