using System;
using System.Collections.Generic;
using System.Numerics;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;
using Serilog;
using PhysicsLandblock = ACE.Server.Physics.Common.Landblock;
using PhysicsPosition = ACE.Server.Physics.Common.Position;

namespace ACE.Server.Pathfinding;

/// <summary>
/// One object's collision triangles, in global coordinates (x east, y north, z up, 192 m to a landblock)
/// </summary>
internal sealed class ShapeGroup
{
    public readonly string Kind;
    public readonly uint Id;

    /// <summary>
    /// Three corners to a triangle
    /// </summary>
    public readonly List<Vector3> Triangles = new();

    public Vector3 Min = new(float.MaxValue);
    public Vector3 Max = new(float.MinValue);

    public ShapeGroup(string kind, uint id)
    {
        Kind = kind;
        Id = id;
    }

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        Triangles.Add(a);
        Triangles.Add(b);
        Triangles.Add(c);

        Min = Vector3.Min(Min, Vector3.Min(a, Vector3.Min(b, c)));
        Max = Vector3.Max(Max, Vector3.Max(a, Vector3.Max(b, c)));
    }

    public bool Overlaps2D(Vector2 min, Vector2 max)
    {
        return Triangles.Count > 0 && Max.X >= min.X && Min.X <= max.X && Max.Y >= min.Y && Min.Y <= max.Y;
    }
}

/// <summary>
/// A box the navmesh is cut away from (the inside of a building), in global coordinates
/// </summary>
internal readonly record struct BlockerBox(Vector2 Min, Vector2 Max, float MinZ, float MaxZ);

/// <summary>
/// The collision shapes of one outdoor landblock, the same ones server physics collides against
/// </summary>
internal sealed class LandblockShapes
{
    public readonly ushort Landblock;

    public readonly List<ShapeGroup> Groups = new();
    public readonly List<BlockerBox> Blockers = new();

    public int Buildings;
    public int Statics;
    public int Scenery;
    public int Triangles;

    /// <summary>
    /// A landblock that's all deep water: physics won't let anything walk on it, so it has no terrain here
    /// </summary>
    public bool EntirelyWater;

    public LandblockShapes(ushort landblock)
    {
        Landblock = landblock;
    }
}

/// <summary>
/// Reads the outdoor collision geometry of a landblock from the dats, the way server physics builds it
/// (Physics.Common.Landblock: terrain polygons, init_buildings, init_static_objs, get_land_scenes), for the outdoor navmesh.<para />
/// Each object is made as a PhysicsObj with the same placement frame, scale and collision shape the server uses: physics polygons
/// (its BSP) if any part has them, otherwise its cylspheres, otherwise its spheres. Nothing here is added to a cell or to LScape,
/// so it's safe to run off-thread in a live server.
/// </summary>
internal static class OutdoorGeometry
{
    private static readonly ILogger _log = Log.ForContext(typeof(OutdoorGeometry));

    private const int PrismSides = 12;

    // building insides are found on a grid this fine, with doorways up to twice this wide closed off
    private const float InteriorGridSize = 0.3f;
    private const float InteriorDoorwayHalfWidth = 1.8f;

    // the band above a building's floor whose walls count as its outline: a person's height, above any skirting or step
    private const float InteriorBandLow = 0.3f;
    private const float InteriorBandHigh = 2.0f;

    public static LandblockShapes Load(ushort landblock)
    {
        var shapes = new LandblockShapes(landblock);
        var origin = BlockOrigin(landblock);

        var cellLandblock = DatManager.CellDat.ReadFromDat<CellLandblock>((uint)landblock << 16 | 0xFFFF);
        if (cellLandblock == null)
        {
            return shapes;
        }

        // a stand-alone copy, never put in LScape: its cells are only used to tell scenery where the buildings are
        var physicsLandblock = new PhysicsLandblock(cellLandblock);

        LoadTerrain(shapes, physicsLandblock, origin);

        try
        {
            physicsLandblock.init_buildings();
        }
        catch (Exception e)
        {
            _log.Warning(e, "Navmesh: landblock {Landblock:X4} buildings not loaded", landblock);
        }

        LoadBuildings(shapes, physicsLandblock, origin);
        LoadStatics(shapes, physicsLandblock);
        LoadScenery(shapes, physicsLandblock, origin);

        foreach (var group in shapes.Groups)
        {
            shapes.Triangles += group.Triangles.Count / 3;
        }

        return shapes;
    }

    public static Vector3 BlockOrigin(ushort landblock)
    {
        return new Vector3((landblock >> 8) * LandDefs.BlockLength, (landblock & 0xFF) * LandDefs.BlockLength, 0);
    }

    private static void LoadTerrain(LandblockShapes shapes, PhysicsLandblock physicsLandblock, Vector3 origin)
    {
        if (physicsLandblock.WaterType == LandDefs.WaterType.EntirelyWater)
        {
            shapes.EntirelyWater = true;
            return;
        }

        var terrain = new ShapeGroup("terrain", (uint)shapes.Landblock << 16 | 0xFFFF);

        foreach (var polygon in physicsLandblock.Polygons)
        {
            if (polygon?.Vertices == null || polygon.Vertices.Count < 3)
            {
                continue;
            }

            var a = origin + polygon.Vertices[0].Origin;
            var b = origin + polygon.Vertices[1].Origin;
            var c = origin + polygon.Vertices[2].Origin;

            // facing up, so Recast sees it as floor
            if (Vector3.Cross(b - a, c - a).Z < 0)
            {
                (b, c) = (c, b);
            }

            terrain.AddTriangle(a, b, c);
        }

        shapes.Groups.Add(terrain);
    }

    private static void LoadBuildings(LandblockShapes shapes, PhysicsLandblock physicsLandblock, Vector3 origin)
    {
        foreach (var building in physicsLandblock.Buildings)
        {
            // server physics collides with a building through its first part only (BuildingObj.find_building_collisions)
            var part = building.PartArray?.Parts.Count > 0 ? building.PartArray.Parts[0] : null;
            if (part?.GfxObj?.PhysicsBSP == null)
            {
                continue;
            }

            var group = new ShapeGroup("building", building.ID);
            AddPartPolygons(group, part, origin);

            if (group.Triangles.Count == 0)
            {
                continue;
            }

            shapes.Groups.Add(group);
            shapes.Buildings++;

            AddBuildingInterior(shapes, group, origin.Z + part.Pos.Frame.Origin.Z);
        }
    }

    private static void LoadStatics(LandblockShapes shapes, PhysicsLandblock physicsLandblock)
    {
        if (physicsLandblock.Info == null)
        {
            return;
        }

        foreach (var info in physicsLandblock.Info.Objects)
        {
            // as in Landblock.init_static_objs(): objects placed past the edge of the landblock aren't made
            var position = new PhysicsPosition(physicsLandblock.ID, new AFrame(info.Frame));
            LandDefs.AdjustToOutside(position);

            if (GetCell(physicsLandblock, position.ObjCellID) == null)
            {
                continue;
            }

            var obj = PhysicsObj.makeObject(info.Id, 0, false);
            if (obj == null)
            {
                continue;
            }

            obj.set_initial_frame(position.Frame);

            var group = new ShapeGroup("static", info.Id);
            AddPhysicsObj(group, obj, BlockOrigin((ushort)(position.ObjCellID >> 16)));

            if (group.Triangles.Count > 0)
            {
                shapes.Groups.Add(group);
                shapes.Statics++;
            }
        }
    }

    private static void LoadScenery(LandblockShapes shapes, PhysicsLandblock physicsLandblock, Vector3 origin)
    {
        if (!PhysicsLandblock.UseSceneFiles)
        {
            return;
        }

        foreach (var (obj, _, _, scale) in physicsLandblock.make_land_scenes())
        {
            obj.SetScaleStatic(scale);

            var group = new ShapeGroup("scenery", obj.PartArray?.GetDataID() ?? 0);
            AddPhysicsObj(group, obj, origin);

            if (group.Triangles.Count > 0)
            {
                shapes.Groups.Add(group);
                shapes.Scenery++;
            }
        }
    }

    private static LandCell GetCell(PhysicsLandblock physicsLandblock, uint cellId)
    {
        try
        {
            return physicsLandblock.get_landcell(cellId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the shape physics collides an object with: PhysicsObj.FindObjCollisions uses its parts' BSPs if it has any,
    /// otherwise its cylspheres, otherwise its spheres
    /// </summary>
    private static void AddPhysicsObj(ShapeGroup group, PhysicsObj obj, Vector3 blockOrigin)
    {
        if (obj.PartArray == null || obj.State.HasFlag(PhysicsState.Ethereal))
        {
            return;
        }

        if (obj.State.HasFlag(PhysicsState.HasPhysicsBSP))
        {
            foreach (var part in obj.PartArray.Parts)
            {
                if (part?.GfxObj?.PhysicsBSP != null)
                {
                    AddPartPolygons(group, part, blockOrigin);
                }
            }
            return;
        }

        var frame = obj.Position.Frame;

        if (obj.PartArray.GetNumCylsphere() > 0)
        {
            foreach (var cylSphere in obj.PartArray.GetCylSphere())
            {
                var lowPoint = blockOrigin + frame.LocalToGlobal(cylSphere.LowPoint * obj.Scale);
                AddPrism(group, lowPoint, cylSphere.Radius * obj.Scale, cylSphere.Height * obj.Scale);
            }
            return;
        }

        if (obj.PartArray.GetNumSphere() == 0)
        {
            return;
        }

        foreach (var sphere in obj.PartArray.GetSphere())
        {
            var center = blockOrigin + frame.LocalToGlobal(sphere.Center * obj.Scale);
            var radius = sphere.Radius * obj.Scale;
            AddPrism(group, center - new Vector3(0, 0, radius), radius, radius * 2);
        }
    }

    /// <summary>
    /// A part's physics polygons, placed the way PhysicsPart.FindObjCollisions places them: its frame, scaled by GfxObjScale.Z
    /// </summary>
    private static void AddPartPolygons(ShapeGroup group, PhysicsPart part, Vector3 blockOrigin)
    {
        var gfxObj = DatManager.PortalDat.ReadFromDat<DatLoader.FileTypes.GfxObj>(part.GfxObj.ID);
        if (gfxObj?.PhysicsPolygons == null)
        {
            return;
        }

        var frame = part.Pos.Frame;
        var scale = part.GfxObjScale.Z;
        var points = new List<Vector3>();

        foreach (var polygon in gfxObj.PhysicsPolygons.Values)
        {
            points.Clear();
            foreach (var vertexId in polygon.VertexIds)
            {
                if (gfxObj.VertexArray.Vertices.TryGetValue((ushort)vertexId, out var vertex))
                {
                    points.Add(blockOrigin + frame.LocalToGlobal(vertex.Origin * scale));
                }
            }

            for (var i = 2; i < points.Count; i++)
            {
                group.AddTriangle(points[0], points[i - 1], points[i]);
            }
        }
    }

    /// <summary>
    /// An upright cylinder, as a closed prism
    /// </summary>
    private static void AddPrism(ShapeGroup group, Vector3 lowPoint, float radius, float height)
    {
        if (radius <= 0 || height <= 0)
        {
            return;
        }

        var top = lowPoint + new Vector3(0, 0, height);

        for (var i = 0; i < PrismSides; i++)
        {
            var a0 = MathF.Tau * i / PrismSides;
            var a1 = MathF.Tau * (i + 1) / PrismSides;
            var d0 = new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, 0);
            var d1 = new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, 0);

            group.AddTriangle(lowPoint + d0, lowPoint + d1, top + d1);
            group.AddTriangle(lowPoint + d0, top + d1, top + d0);
            group.AddTriangle(top, top + d0, top + d1);
            group.AddTriangle(lowPoint, lowPoint + d1, lowPoint + d0);
        }
    }

    /// <summary>
    /// Fills in a building, so the navmesh doesn't run in one door and out another across the terrain under its floor.<para />
    /// Its walls at a person's height are drawn on a grid and thickened until gaps up to a doorway wide are sealed. What the outside
    /// can't reach, shrunk back off the thickening outside the outer walls, becomes a set of boxes the navmesh is cut away from.
    /// (Closing the walls instead, dilate then erode, doesn't seal a gap in a wall one cell thick.)
    /// </summary>
    private static void AddBuildingInterior(LandblockShapes shapes, ShapeGroup shell, float floorZ)
    {
        var min = new Vector2(shell.Min.X, shell.Min.Y) - new Vector2(InteriorDoorwayHalfWidth * 2);
        var size = new Vector2(shell.Max.X, shell.Max.Y) - new Vector2(shell.Min.X, shell.Min.Y) + new Vector2(InteriorDoorwayHalfWidth * 4);

        var width = (int)MathF.Ceiling(size.X / InteriorGridSize);
        var height = (int)MathF.Ceiling(size.Y / InteriorGridSize);
        if (width <= 0 || height <= 0 || width * height > 1_000_000)
        {
            return;
        }

        var walls = new bool[width * height];
        var bandLow = floorZ + InteriorBandLow;
        var bandHigh = floorZ + InteriorBandHigh;
        var anyWall = false;

        for (var t = 0; t < shell.Triangles.Count; t += 3)
        {
            var a = shell.Triangles[t];
            var b = shell.Triangles[t + 1];
            var c = shell.Triangles[t + 2];

            if (MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < bandLow || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > bandHigh)
            {
                continue;
            }

            // sample the triangle finer than the grid, keeping the points inside the band
            var longest = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
            var steps = Math.Max(1, (int)MathF.Ceiling(longest / (InteriorGridSize * 0.5f)));

            for (var i = 0; i <= steps; i++)
            {
                for (var j = 0; j <= steps - i; j++)
                {
                    var p = a + (b - a) * i / steps + (c - a) * j / steps;
                    if (p.Z < bandLow || p.Z > bandHigh)
                    {
                        continue;
                    }

                    var x = (int)((p.X - min.X) / InteriorGridSize);
                    var y = (int)((p.Y - min.Y) / InteriorGridSize);
                    if (x >= 0 && y >= 0 && x < width && y < height)
                    {
                        walls[y * width + x] = true;
                        anyWall = true;
                    }
                }
            }
        }

        if (!anyWall)
        {
            return;
        }

        // thickened walls seal the doorways
        var radius = (int)MathF.Ceiling(InteriorDoorwayHalfWidth / InteriorGridSize);
        var sealedWalls = Dilate(walls, width, height, radius);

        // flood the outside in from the edge of the grid
        var outside = new bool[width * height];
        var stack = new Stack<int>();

        for (var x = 0; x < width; x++)
        {
            stack.Push(x);
            stack.Push((height - 1) * width + x);
        }
        for (var y = 0; y < height; y++)
        {
            stack.Push(y * width);
            stack.Push(y * width + width - 1);
        }

        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (outside[i] || sealedWalls[i])
            {
                continue;
            }

            outside[i] = true;

            var x = i % width;
            var y = i / width;
            if (x > 0)
            {
                stack.Push(i - 1);
            }
            if (x < width - 1)
            {
                stack.Push(i + 1);
            }
            if (y > 0)
            {
                stack.Push(i - width);
            }
            if (y < height - 1)
            {
                stack.Push(i + width);
            }
        }

        // what the outside couldn't reach, less the thickening outside the outer walls: the inside and the walls themselves
        var enclosed = new bool[width * height];
        for (var i = 0; i < enclosed.Length; i++)
        {
            enclosed[i] = !outside[i];
        }

        var inside = Erode(enclosed, width, height, radius);
        var blocked = new bool[width * height];
        for (var i = 0; i < blocked.Length; i++)
        {
            blocked[i] = inside[i] || (walls[i] && enclosed[i]);
        }

        // as rectangles: runs along each row, merged with the same run on the rows above
        var used = new bool[width * height];
        var minZ = shell.Min.Z - 1.0f;
        var maxZ = floorZ + InteriorBandHigh + 0.5f;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!blocked[y * width + x] || used[y * width + x])
                {
                    continue;
                }

                var x1 = x;
                while (x1 + 1 < width && blocked[y * width + x1 + 1] && !used[y * width + x1 + 1])
                {
                    x1++;
                }

                var y1 = y;
                while (y1 + 1 < height && RowFree(blocked, used, width, y1 + 1, x, x1))
                {
                    y1++;
                }

                for (var yy = y; yy <= y1; yy++)
                {
                    for (var xx = x; xx <= x1; xx++)
                    {
                        used[yy * width + xx] = true;
                    }
                }

                shapes.Blockers.Add(new BlockerBox(
                    min + new Vector2(x, y) * InteriorGridSize,
                    min + new Vector2(x1 + 1, y1 + 1) * InteriorGridSize,
                    minZ,
                    maxZ));
            }
        }
    }

    private static bool RowFree(bool[] blocked, bool[] used, int width, int y, int x0, int x1)
    {
        for (var x = x0; x <= x1; x++)
        {
            if (!blocked[y * width + x] || used[y * width + x])
            {
                return false;
            }
        }
        return true;
    }

    private static bool[] Dilate(bool[] grid, int width, int height, int radius)
    {
        var result = new bool[grid.Length];
        var radiusSq = radius * radius;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!grid[y * width + x])
                {
                    continue;
                }

                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = x + dx;
                        var yy = y + dy;
                        if (dx * dx + dy * dy <= radiusSq && xx >= 0 && yy >= 0 && xx < width && yy < height)
                        {
                            result[yy * width + xx] = true;
                        }
                    }
                }
            }
        }
        return result;
    }

    private static bool[] Erode(bool[] grid, int width, int height, int radius)
    {
        var inverse = new bool[grid.Length];
        for (var i = 0; i < grid.Length; i++)
        {
            inverse[i] = !grid[i];
        }

        var dilated = Dilate(inverse, width, height, radius);

        var result = new bool[grid.Length];
        for (var i = 0; i < grid.Length; i++)
        {
            result[i] = !dilated[i];
        }
        return result;
    }
}
