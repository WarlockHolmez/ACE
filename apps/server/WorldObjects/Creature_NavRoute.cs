using System;
using System.Collections.Generic;
using System.Numerics;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pathfinding;
using ACE.Server.WorldObjects.Patrol;

namespace ACE.Server.WorldObjects;

/// <summary>
/// A chase that's stuck on a building, a wall or a tree runs a route round it on the outdoor navmesh (Pathfinding.OutdoorNavMesh),
/// following its corners the way ClassicACE's Monster_Navigation.ContinueRoute does
/// </summary>
partial class Creature
{
    private List<Position> navRoute;
    private int navRouteIndex;
    private WorldObject navRouteTarget;
    private Vector3 navRouteTargetGlobal;
    private double navRouteRepathTime;
    private double navRouteNextMoveTime;
    private float navRouteBestDist;
    private double navRouteBestTime;

    // a corner counts as reached this close, or when the MoveTo to it ends this close
    private const float NavRouteCornerReached = 1.0f;
    private const float NavRouteCornerArrived = 2.0f;

    // no closer to the next corner for this long, and the route is given up
    private const double NavRouteStuckSeconds = 4.0;

    // the target moving this far from the end of the route finds a new route, at most this often
    private const float NavRouteRepathDistance = 4.0f;
    private const double NavRouteRepathSeconds = 2.0;

    // a route has to end this close to the target to be any use
    private const float NavRouteEndDistance = 3.0f;

    // creatures with collision spheres wider than this use the wide navmesh
    private const float NavRouteNarrowRadius = 0.55f;

    public bool IsNavRouting => navRoute != null;

    /// <summary>
    /// Starts a route to the attack target round whatever the chase is stuck on. False if there's no route that helps (or its tiles are
    /// still being built), and the caller falls back to blipping.
    /// </summary>
    private bool TryStartNavRoute(double currentUnixTime)
    {
        if (!PropertyManager.GetBool("town_event_navmesh").Item || AttackTarget?.Location == null || Location.Indoors || AttackTarget.Location.Indoors)
        {
            return false;
        }

        var start = Location.ToGlobal();
        var end = AttackTarget.Location.ToGlobal();

        var route = OutdoorNavMesh.FindRoute(start, end, GetNavAgentWidth());

        // a straight line is what the chase was already running, and it's stuck
        if (route.Result != NavRouteResult.Found || route.Corners.Count < 3)
        {
            return false;
        }

        var last = route.Corners[^1];
        if (Vector2.Distance(new Vector2(last.X, last.Y), new Vector2(end.X, end.Y)) > NavRouteEndDistance)
        {
            return false;
        }

        // the first corner is where the creature stands
        navRoute = new List<Position>();
        for (var i = 1; i < route.Corners.Count; i++)
        {
            navRoute.Add(PatrolComponent.BuildOutdoorPosition(Location, route.Corners[i].X - start.X, route.Corners[i].Y - start.Y, InstanceId));
        }

        navRouteIndex = 0;
        navRouteTarget = AttackTarget;
        navRouteTargetGlobal = end;
        navRouteRepathTime = currentUnixTime + NavRouteRepathSeconds;
        navRouteBestDist = float.MaxValue;
        navRouteBestTime = currentUnixTime;

        MoveToNavCorner(currentUnixTime);
        return true;
    }

    internal enum NavRouteState
    {
        /// <summary>
        /// Not on a route
        /// </summary>
        None,

        /// <summary>
        /// On a route and getting on with it: the normal chase waits
        /// </summary>
        Following,

        /// <summary>
        /// Round the obstacle: the normal chase takes over for the last stretch
        /// </summary>
        Finished,

        /// <summary>
        /// Stuck on the route too, and given up
        /// </summary>
        GaveUp,
    }

    /// <summary>
    /// Runs the creature on along its route
    /// </summary>
    private NavRouteState NavRouteUpdate(double currentUnixTime)
    {
        if (navRoute == null)
        {
            return NavRouteState.None;
        }

        if (AttackTarget != navRouteTarget || AttackTarget?.Location == null)
        {
            EndNavRoute();
            return NavRouteState.None;
        }

        // the target has moved off: a new route from here, now and then
        if (currentUnixTime >= navRouteRepathTime && Vector3.Distance(AttackTarget.Location.ToGlobal(), navRouteTargetGlobal) > NavRouteRepathDistance)
        {
            EndNavRoute();
            return TryStartNavRoute(currentUnixTime) ? NavRouteState.Following : NavRouteState.Finished;
        }

        var corner = navRoute[navRouteIndex];
        var dist = Location.Distance2D(corner);

        if (dist <= NavRouteCornerReached || (!IsMoving && dist <= NavRouteCornerArrived))
        {
            navRouteIndex++;

            if (navRouteIndex >= navRoute.Count)
            {
                EndNavRoute();
                return NavRouteState.Finished;
            }

            navRouteBestDist = float.MaxValue;
            navRouteBestTime = currentUnixTime;
            MoveToNavCorner(currentUnixTime);
            return NavRouteState.Following;
        }

        if (dist < navRouteBestDist - 0.5f)
        {
            navRouteBestDist = dist;
            navRouteBestTime = currentUnixTime;
        }
        else if (currentUnixTime - navRouteBestTime >= NavRouteStuckSeconds)
        {
            EndNavRoute();
            return NavRouteState.GaveUp;
        }

        // reissued when a fight or a collision dropped the move, and now and then anyway
        if (!IsMoving || currentUnixTime >= navRouteNextMoveTime)
        {
            MoveToNavCorner(currentUnixTime);
        }

        return NavRouteState.Following;
    }

    /// <summary>
    /// Drops the route, and the move to its next corner (the creature is in range of its target, or has none)
    /// </summary>
    private void StopNavRoute()
    {
        if (navRoute == null)
        {
            return;
        }

        EndNavRoute();

        if (PhysicsObj?.MovementManager?.MoveToManager != null)
        {
            CancelMoveTo();
        }
    }

    private void MoveToNavCorner(double currentUnixTime)
    {
        var corner = navRoute[navRouteIndex];

        var moveTo = new Position(corner);
        var dir = corner.ToGlobal() - Location.ToGlobal();
        dir.Z = 0;
        if (dir.LengthSquared() > 0.01f)
        {
            moveTo.Rotate(dir);
        }

        MoveTo(moveTo, GetRunRate(), true, null, null);
        IsMoving = true;

        navRouteNextMoveTime = currentUnixTime + 3.0;
    }

    private void EndNavRoute()
    {
        navRoute = null;
        navRouteTarget = null;
    }

    /// <summary>
    /// The navmesh this creature fits: the narrow one keeps far enough from walls for a person-sized collision sphere
    /// </summary>
    private AgentWidth GetNavAgentWidth()
    {
        var radius = 0.0f;
        var spheres = PhysicsObj?.PartArray?.GetSphere();

        if (spheres != null)
        {
            foreach (var sphere in spheres)
            {
                radius = Math.Max(radius, (new Vector2(sphere.Center.X, sphere.Center.Y).Length() + sphere.Radius) * PhysicsObj.Scale);
            }
        }

        return radius > NavRouteNarrowRadius ? AgentWidth.Wide : AgentWidth.Narrow;
    }
}
