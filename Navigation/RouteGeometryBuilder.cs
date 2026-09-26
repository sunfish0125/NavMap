using System.Numerics;

using ETS2LA.Game.Data;
using ETS2LA.Game.SDK;

using NavMap.Map;

using TruckLib;
using TruckLib.ScsMap;

namespace NavMap.Navigation;

/// <summary>
///  Converts the active NavigationData route into drawable world-space geometry
///  for the currently visible area only, per PLAN.md Phase 4/6's evaluation
///  order:
///   1. existing ETS2LA helper for junction geometry (ParsedPrefab.GetPathsFromNodeToNode)
///   2. route node UIDs walked to connected Road/Prefab segments - generalized
///      (Phase 6) into a bounded depth-first search with backtracking so a
///      route can pass through more than one chained prefab (interchanges,
///      roundabouts, ramps) between two consecutive route entries, not just a
///      single direct hop.
///   3. a direct straight line between two route nodes, only as a last resort
///      when no connected geometry could be traced (visually approximate, not
///      proven correct - see RESEARCH.md section 6).
/// </summary>
public sealed class RouteGeometryBuilder
{
    private const float RefreshDistanceMeters = 40f;
    private const float RoadSampleStepMeters = 10f;
    private const float PrefabSampleStepMeters = 4f;

    // Bounds for the Phase 6 bounded DFS - keeps worst-case cost small
    // regardless of map topology (see Walk()'s doc comment).
    private const int MaxHopsPerConnection = 40;
    private const float MaxHopDistanceMeters = 600f;
    private const int MaxPrefabPathExpansions = 200;

    private NavigationData? cachedRoute;
    private MapData? cachedMap;
    private Vector3 cachedCenter = new(float.NaN, float.NaN, float.NaN);
    private float cachedRadius = float.NaN;
    private List<Vector3> points = new();
    private bool usedFallbackSegment;
    private Vector3? destinationPosition;

    public IReadOnlyList<Vector3> Points => points;

    /// <summary>
    ///  True if the last build had to fall back to a straight line for at least
    ///  one gap between consecutive route nodes (tier 3). Surfaced for the
    ///  Phase 3 diagnostics text, not for the user-facing map itself.
    /// </summary>
    public bool UsedFallbackSegment => usedFallbackSegment;

    /// <summary>
    ///  World position of the route's final node, if it could be resolved in
    ///  MapData - regardless of whether it's currently within the visible
    ///  radius. Used for Phase 8's destination marker/off-screen indicator.
    /// </summary>
    public Vector3? DestinationPosition => destinationPosition;

    /// <param name="routeDistanceMeters">
    ///  GameTelemetryData.truckFloat.routeDistance (TruckPoseState.RouteDistanceMeters)
    ///  - an independent cross-check for "is a route actually active". See the
    ///  0-or-less branch below for why NavigationData's own entries aren't
    ///  trusted alone for this.
    /// </param>
    public void Refresh(NavigationRouteState routeState, Vector3 truckPosition, float viewRadiusMeters, float routeDistanceMeters)
    {
        NavigationData? route = routeState.Latest;
        MapData? map = MapDataSource.TryGetCurrentMap();

        bool routeChanged = !ReferenceEquals(route, cachedRoute);
        bool mapChanged = !ReferenceEquals(map, cachedMap);
        bool radiusChanged = cachedRadius != viewRadiusMeters;
        bool movedEnough = float.IsNaN(cachedCenter.X)
            || Vector3.DistanceSquared(cachedCenter, truckPosition) >= RefreshDistanceMeters * RefreshDistanceMeters;

        if (!routeChanged && !mapChanged && !radiusChanged && !movedEnough)
            return;

        cachedRoute = route;
        cachedMap = map;
        cachedRadius = viewRadiusMeters;
        cachedCenter = truckPosition;

        if (routeDistanceMeters <= 0f)
        {
            // Navigation has ended in-game (job finished/cancelled, or none
            // was ever set). NavigationData's entries have been observed to
            // keep reporting the previous route indefinitely instead of
            // clearing when this happens, so this cross-checks against the
            // standard SCS telemetry's own route-distance field - a separate,
            // already-proven-live channel - rather than trusting entries alone.
            points = new List<Vector3>();
            usedFallbackSegment = false;
            destinationPosition = null;
            return;
        }

        try
        {
            (List<Vector3> newPoints, bool newUsedFallback) = Build(routeState, map, truckPosition, viewRadiusMeters);
            points = newPoints;
            usedFallbackSegment = newUsedFallback;
            destinationPosition = ResolveDestination(routeState, map);
        }
        catch
        {
            // A prefab descriptor can be transiently unavailable while map data
            // is still loading (same defensive pattern as SignalHUD/NearbyMapCache).
            // Keep whatever route we already had instead of clearing it to
            // empty - clearing here made the route line visibly disappear for
            // several seconds (until the next refresh, gated by
            // RefreshDistanceMeters) whenever this happened while driving.
        }
    }

    private static Vector3? ResolveDestination(NavigationRouteState routeState, MapData? map)
    {
        if (map == null)
            return null;

        IReadOnlyList<NavigationEntry> entries = routeState.GetValidEntries();
        if (entries.Count == 0)
            return null;

        NavigationEntry last = entries[^1];
        if (map.Nodes.TryGetValue(last.nodeUid, out INode? node) && node is Node concreteNode)
            return concreteNode.Position;

        return null;
    }

    private static (List<Vector3> Points, bool UsedFallback) Build(
        NavigationRouteState routeState, MapData? map, Vector3 truckPosition, float viewRadiusMeters)
    {
        var result = new List<Vector3>();
        if (map == null)
            return (result, false);

        IReadOnlyList<NavigationEntry> entries = routeState.GetValidEntries();
        if (entries.Count == 0)
            return (result, false);

        List<Node> resolved = ResolveVisibleRouteNodes(map, entries, truckPosition, viewRadiusMeters);
        if (resolved.Count == 0)
            return (result, false);

        bool usedFallback = false;
        result.Add(resolved[0].Position);
        for (int i = 0; i < resolved.Count - 1; i++)
        {
            List<Vector3>? segment = TryConnect(resolved[i], resolved[i + 1], truckPosition);
            if (segment != null)
            {
                result.AddRange(segment);
            }
            else
            {
                usedFallback = true;
                result.Add(resolved[i + 1].Position);
            }
        }

        return (result, usedFallback);
    }

    /// <summary>
    ///  Resolves route nodeUids to actual map Nodes, stopping once the route
    ///  leaves the (padded) visible radius - the route can span the whole map,
    ///  we only need to draw the part that's on screen. The search radius
    ///  tracks the current zoom level exactly (viewRadiusMeters), so at a
    ///  2000m zoom level the route is reconstructed out to 2000m too.
    /// </summary>
    private static List<Node> ResolveVisibleRouteNodes(
        MapData map, IReadOnlyList<NavigationEntry> entries, Vector3 truckPosition, float viewRadiusMeters)
    {
        float boundRadiusSq = MathF.Pow(viewRadiusMeters * 1.5f, 2);
        var resolved = new List<Node>();

        foreach (NavigationEntry entry in entries)
        {
            if (!map.Nodes.TryGetValue(entry.nodeUid, out INode? node) || node is not Node concreteNode)
                continue;

            if (Vector3.DistanceSquared(concreteNode.Position, truckPosition) > boundRadiusSq)
            {
                if (resolved.Count > 0)
                    break; // left the visible area again after having entered it

                continue; // haven't reached the visible area yet
            }

            resolved.Add(concreteNode);
        }

        return resolved;
    }

    /// <returns>
    ///  Points strictly AFTER `a` (not including a.Position) leading to
    ///  b.Position, or null if no connection was found within the bounds.
    /// </returns>
    private static List<Vector3>? TryConnect(Node a, Node b, Vector3 truckPosition)
    {
        if (a.Uid == b.Uid)
            return new List<Vector3>();

        var budget = new WalkBudget();
        var visited = new HashSet<ulong> { a.Uid };

        return Walk(a, b, a.ForwardItem, visited, 0, 0f, truckPosition, budget)
            ?? Walk(a, b, a.BackwardItem, visited, 0, 0f, truckPosition, budget);
    }

    private sealed class WalkBudget
    {
        public int PrefabPathExpansionsRemaining = MaxPrefabPathExpansions;
    }

    /// <summary>
    ///  Bounded depth-first search with backtracking, following Road hops
    ///  (always exactly one continuation) and Prefab hops (may branch to any
    ///  of the prefab's other boundary nodes, so multi-prefab interchanges and
    ///  roundabouts resolve correctly instead of dead-ending on the first
    ///  prefab that doesn't directly contain the target - PLAN.md Phase 6).
    ///  Bounded by hop count, cumulative travelled distance, and a shared
    ///  prefab-path-expansion budget so a pathological junction topology can't
    ///  blow up cost; a cache refresh only runs on movement/route-change
    ///  events, not every frame, so this bound is generous.
    /// </summary>
    private static List<Vector3>? Walk(
        Node current, Node target, IMapObject? nextItem, HashSet<ulong> visited,
        int hop, float traveled, Vector3 truckPosition, WalkBudget budget)
    {
        if (nextItem == null || hop >= MaxHopsPerConnection || traveled >= MaxHopDistanceMeters)
            return null;

        if (nextItem is Road road)
        {
            bool forwardDirection = road.Node.Uid == current.Uid;
            Node other = forwardDirection ? (Node)road.ForwardNode : (Node)road.Node;
            if (!visited.Add(other.Uid))
                return null; // would revisit a node already on this path - cycle guard

            var points = new List<Vector3>();
            AppendRoadPoints(points, road, forwardDirection);
            float newTraveled = traveled + road.Length;

            if (other.Uid == target.Uid)
                return points;

            IMapObject? continuation = ReferenceEquals(other.ForwardItem, road) ? other.BackwardItem : other.ForwardItem;
            List<Vector3>? rest = Walk(other, target, continuation, visited, hop + 1, newTraveled, truckPosition, budget);
            if (rest == null)
            {
                visited.Remove(other.Uid);
                return null;
            }

            points.AddRange(rest);
            return points;
        }

        if (nextItem is Prefab prefab)
        {
            List<Node> boundaryNodes;
            try
            {
                boundaryNodes = prefab.Nodes.OfType<Node>().ToList();
            }
            catch
            {
                return null;
            }

            // Direct boundary match first - the common case, and cheapest.
            Node? directExit = boundaryNodes.Find(n => n.Uid == target.Uid);
            if (directExit != null)
            {
                List<Vector3>? direct = TryPrefabPath(prefab, current, directExit, truckPosition, budget);
                if (direct != null)
                    return direct;
            }

            // Otherwise try transiting through the prefab to each of its
            // other boundary nodes and continuing the walk from there.
            foreach (Node exit in boundaryNodes)
            {
                if (exit.Uid == current.Uid || exit.Uid == target.Uid || !visited.Add(exit.Uid))
                    continue;

                List<Vector3>? throughPrefab = TryPrefabPath(prefab, current, exit, truckPosition, budget);
                if (throughPrefab == null)
                {
                    visited.Remove(exit.Uid);
                    continue;
                }

                IMapObject? continuation = ReferenceEquals(exit.ForwardItem, prefab) ? exit.BackwardItem : exit.ForwardItem;
                List<Vector3>? rest = Walk(exit, target, continuation, visited, hop + 1, traveled, truckPosition, budget);
                if (rest != null)
                {
                    throughPrefab.AddRange(rest);
                    return throughPrefab;
                }

                visited.Remove(exit.Uid);
            }

            return null;
        }

        return null; // dead end - no further item in this direction
    }

    private static List<Vector3>? TryPrefabPath(Prefab prefab, Node from, Node to, Vector3 truckPosition, WalkBudget budget)
    {
        if (budget.PrefabPathExpansionsRemaining-- <= 0)
            return null;

        try
        {
            ParsedPrefab parsedPrefab = new(prefab);
            (List<PrefabPath> bestPaths, List<PrefabPath> otherPaths) =
                parsedPrefab.GetPathsFromNodeToNode(from, to, truckPosition);
            PrefabPath? path = bestPaths.FirstOrDefault() ?? otherPaths.FirstOrDefault();
            if (path == null)
                return null;

            var points = new List<Vector3>();
            AppendPrefabPathPoints(points, path);
            return points;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendRoadPoints(List<Vector3> points, Road road, bool forwardDirection)
    {
        float length = road.Length;
        if (length <= 0)
            return;

        float step = Math.Clamp(RoadSampleStepMeters / length, 0.1f, 1f);
        if (forwardDirection)
        {
            for (float t = step; t < 1f; t += step)
                points.Add(road.InterpolateCurve(t).Position);
            points.Add(road.InterpolateCurve(1f).Position);
        }
        else
        {
            for (float t = 1f - step; t > 0f; t -= step)
                points.Add(road.InterpolateCurve(t).Position);
            points.Add(road.InterpolateCurve(0f).Position);
        }
    }

    private static void AppendPrefabPathPoints(List<Vector3> points, PrefabPath path)
    {
        float length = path.Length;
        if (length <= 0)
            return;

        float step = Math.Clamp(PrefabSampleStepMeters / length, 0.1f, 1f);
        for (float t = step; t < 1f; t += step)
        {
            OrientedPoint? point = path.InterpolateDist(t * length);
            if (point.HasValue)
                points.Add(point.Value.Position);
        }

        OrientedPoint? end = path.InterpolateDist(length);
        if (end.HasValue)
            points.Add(end.Value.Position);
    }
}
