using System;
using System.Collections.Generic;
using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace ACE.Server.Pathfinding;

/// <summary>
/// The input Recast builds one outdoor tile from: the triangles of a landblock and the edges of its neighbours, in Recast's
/// axes (x east, y up, z north), and the building insides to cut away
/// </summary>
internal sealed class OutdoorTileGeometry : IInputGeomProvider
{
    private readonly RcTriMesh mesh;
    private readonly List<RcConvexVolume> volumes = new();
    private readonly RcVec3f boundsMin;
    private readonly RcVec3f boundsMax;

    public int TriangleCount { get; }

    /// <summary>
    /// Gathers the shapes of a tile: all of its own, and those of its neighbours that reach within margin of its edge
    /// </summary>
    public OutdoorTileGeometry(ushort landblock, IReadOnlyList<LandblockShapes> shapes, float margin)
    {
        var origin = OutdoorGeometry.BlockOrigin(landblock);
        var min = new Vector2(origin.X - margin, origin.Y - margin);
        var max = new Vector2(origin.X + Physics.Common.LandDefs.BlockLength + margin, origin.Y + Physics.Common.LandDefs.BlockLength + margin);

        var verts = new List<float>();
        var tris = new List<int>();
        var minY = float.MaxValue;
        var maxY = float.MinValue;

        foreach (var landblockShapes in shapes)
        {
            foreach (var group in landblockShapes.Groups)
            {
                if (!group.Overlaps2D(min, max))
                {
                    continue;
                }

                for (var i = 0; i < group.Triangles.Count; i += 3)
                {
                    // x, y, z -> x, z, y mirrors the triangle, so the corners go in backwards to keep it facing the same way
                    AddVertex(verts, group.Triangles[i]);
                    AddVertex(verts, group.Triangles[i + 2]);
                    AddVertex(verts, group.Triangles[i + 1]);

                    var first = verts.Count / 3 - 3;
                    tris.Add(first);
                    tris.Add(first + 1);
                    tris.Add(first + 2);
                }

                minY = MathF.Min(minY, group.Min.Z);
                maxY = MathF.Max(maxY, group.Max.Z);
            }

            foreach (var blocker in landblockShapes.Blockers)
            {
                if (blocker.Max.X < min.X || blocker.Min.X > max.X || blocker.Max.Y < min.Y || blocker.Min.Y > max.Y)
                {
                    continue;
                }

                volumes.Add(new RcConvexVolume
                {
                    verts = new[]
                    {
                        blocker.Min.X, blocker.MinZ, blocker.Min.Y,
                        blocker.Max.X, blocker.MinZ, blocker.Min.Y,
                        blocker.Max.X, blocker.MinZ, blocker.Max.Y,
                        blocker.Min.X, blocker.MinZ, blocker.Max.Y,
                    },
                    hmin = blocker.MinZ,
                    hmax = blocker.MaxZ,
                    areaMod = new RcAreaModification(RcRecast.RC_NULL_AREA),
                });
            }
        }

        TriangleCount = tris.Count / 3;

        if (TriangleCount == 0)
        {
            minY = 0;
            maxY = 1;
        }

        boundsMin = new RcVec3f(min.X, minY - 1, min.Y);
        boundsMax = new RcVec3f(max.X, maxY + 1, max.Y);

        mesh = new RcTriMesh(verts.ToArray(), tris.ToArray());
    }

    private static void AddVertex(List<float> verts, Vector3 v)
    {
        verts.Add(v.X);
        verts.Add(v.Z);
        verts.Add(v.Y);
    }

    public RcTriMesh GetMesh()
    {
        return mesh;
    }

    public RcVec3f GetMeshBoundsMin()
    {
        return boundsMin;
    }

    public RcVec3f GetMeshBoundsMax()
    {
        return boundsMax;
    }

    public IEnumerable<RcTriMesh> Meshes()
    {
        yield return mesh;
    }

    public void AddConvexVolume(RcConvexVolume convexVolume)
    {
        volumes.Add(convexVolume);
    }

    public IList<RcConvexVolume> ConvexVolumes()
    {
        return volumes;
    }

    public List<RcOffMeshConnection> GetOffMeshConnections()
    {
        return new List<RcOffMeshConnection>();
    }

    public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) { }

    public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) { }
}
