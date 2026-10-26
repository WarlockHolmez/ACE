# Outdoor navmesh

Town event guards and attackers chase in straight lines, so they get stuck on buildings, walls and trees. Until now a chase that made no headway for 7 seconds blipped the creature next to its target. With `town_event_navmesh` on, the creature first tries a route round the obstacle on an outdoor navmesh, and blips only if there's no route, or it gets stuck on the route as well.

This is a spike. It's based on ClassicACE's pathfinding (bDekaru/ClassicACE, `Source/ACE.Server/Pathfinding/`, commit 73ae37f), which builds DotRecast navmeshes for dungeons only. The outdoor geometry, the tiling across landblocks and the scenery are new.

The code is in `apps/server/Pathfinding/`:

- `OutdoorGeometry` reads a landblock's collision shapes from the dats.
- `OutdoorTileGeometry` is the Recast input for one tile.
- `OutdoorNavMesh` builds, caches and queries the tiles.

`Creature_NavRoute` follows a route.

## How it works

- **One tile per landblock**, in one Detour navmesh per agent width, in global coordinates (192 m tiles, origin 0,0). Routes cross landblock edges.
- **Geometry** is what server physics collides with, built the same way `Physics.Common.Landblock` builds it:
  - **Terrain:** the landblock's terrain polygons. A landblock that's all deep water has none.
  - **Buildings:** each building's first part, as server physics uses it. The inside of each building is cut out of the mesh: its walls at person height are drawn on a 0.3 m grid and thickened to seal gaps up to 3.6 m, and whatever the outside can't reach is blocked. This stops routes running in one door and out another over the terrain under the floor.
  - **Statics and scenery:** `LandblockInfo.Objects`, and the trees and rocks from `make_land_scenes()` (split out of `get_land_scenes()` for this). Each is made as a `PhysicsObj` with the server's placement frame and scale. Its collision shape is the same as in `PhysicsObj.FindObjCollisions`: physics polygons if it has a BSP, otherwise its cylspheres (as 12-sided prisms), otherwise its spheres. Nothing is added to a cell or to LScape, so building tiles is safe off-thread.
  - **Neighbours:** a tile also takes its neighbours' shapes that reach within its Recast border.
- **Settings:**

  | Setting | Narrow | Wide |
  |---|---|---|
  | Cell size | 0.3 m | 0.3 m |
  | Cell height | 0.1 m | 0.1 m |
  | Agent radius | 0.8 m | 1.5 m |
  | Agent height | 1.8 m | 2.4 m |
  | Max climb | 0.6 m | 0.6 m |
  | Max slope | 48° | 48° |

  48° is physics' `FloorZ`. A creature whose collision spheres reach past 0.55 m uses the wide mesh. The narrow radius is a guard's 0.48 m sphere plus a cell: at 0.6 m, physics caught on building corners.
- **Disk cache:** one file per landblock and width, `Pathfinding/Meshes/Outdoors/<landblock>_<width>.navtile` next to the server. Each file is keyed on the settings, the cell and portal dat iterations, and `CacheFormatVersion`. Bump `CacheFormatVersion` when `OutdoorGeometry` changes, or delete the folder.
- **Tiles are built on demand.** A route through tiles that aren't loaded yet queues them in the background (two at a time), and the chase blips that time.
- **Following a route:** the creature runs to each corner with `MoveTo`. A corner counts as reached within 1 m, or within 2 m once the move ends. It repaths if the target moves 4 m (at most every 2 s). It gives up if it makes no progress toward a corner for 4 s, and then blips. A route that's a straight line, or that ends more than 3 m from the target, isn't used.

## Commands

| Command | What it does |
| --- | --- |
| `@navmesh route [wide] [wcid]` | Draws a route from you to your appraised target (or your mark) with torches (or the wcid) that last 60 s |
| `@navmesh mark` | Remembers where you stand, as the route's end when nothing is appraised |
| `@navmesh build [radius] [wide]` | Rebuilds the tiles around you, ignoring the cache (radius 1 is 3×3), and reports each tile |
| `@navmesh info` | Whether your tile is loaded, its walkable area and build stats |
| `@navmesh unload` | Takes every tile out of the navmesh (the cache stays) |

## Measured (offline harness, server physics, Holtburg and five other towns)

- **Matches physics:** random routes within 150 m of six town centres were walked corner to corner with `PhysicsObj.transition`, as a human-sized creature. 1755 of 1756 reached the end. The same start and end points in a straight line were blocked 24–69% of the time. The scenery placed per landblock matches world-db's clothdump exactly.
- **Build:** 0.4–1.0 s per tile, about 0.45 s on average over 252 tiles. Each build allocates 90–160 MB of short-lived garbage (the 640×640 heightfield).
- **Size:** a tile is 5–90 KB on disk (about 54 KB on average) and about 130 KB in memory. Loading Holtburg's 9 tiles from the cache takes about 0.1 s.
- **Queries:** a route takes 0.1–0.3 ms (median) and 0.6–3 ms (95th percentile), for routes of about 165 m. The first query on a thread can take up to 50 ms.
- **All towns:** 28 of the 30 guarded towns, at 3×3 tiles each (252 tiles), build in 113 s on one thread and cache to 13.5 MB per width.

## Not done yet

- **Hook-up:** it's in `Creature_TownEvent.CheckChaseStuck` and `ResetChaseStuck`, which aren't on main yet (town events are still uncommitted). It's untested in game.
- **Prebuilding:** a town's tiles could be built when an attack starts, or at startup for all guarded towns.
- **Open buildings and statics:** a building with gaps wider than 3.6 m keeps its inside on the mesh, and so does an enclosed static, such as a pen or a stall.
- **Things the mesh doesn't know about:** bridges and other overhangs are only as good as Recast's spans. Water is only handled for landblocks that are all deep water. Doors and other world objects, such as creatures, aren't in the mesh.
- **Recast's console warnings:** DotRecast writes "Dangling!" to stderr for some detail-mesh triangles. It's harmless, but noisy while tiles build.
