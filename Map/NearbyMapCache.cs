using System.Numerics;

using ETS2LA.Game.Data;
using ETS2LA.Game.PpdFiles;
using ETS2LA.Game.Utils;

using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Models.Ppd;

namespace NavMap.Map;

/// <summary>
///  One drawable polyline in world space: one lane of a plain road, one
///  prefab NavCurve, or (fallback only) a road's bare centerline. LaneCount
///  is the road's total lane count when known (both per-lane segments and the
///  fallback centerline carry it) and null for prefab curves and unresolved
///  road templates - used by MapRenderer for major/minor road color coding.
/// </summary>
public sealed class RoadSegment
{
    public required IReadOnlyList<Vector3> Points { get; init; }
    public int? LaneCount { get; init; }
}

/// <summary>
///  Category for a point-of-interest marker on the map. GasStation is split
///  out from the rest of TruckLib.ScsMap.ServiceType because it's the most
///  navigation-relevant one; the other service spawn point types (service
///  station, truck dealer, parking, recruitment, weigh station) share one
///  generic marker to keep this lightweight.
/// </summary>
public enum MapIconKind
{
    Company,
    GasStation,
    Service,
    Garage,
}

public sealed class MapIcon
{
    public required Vector3 Position { get; init; }
    public required MapIconKind Kind { get; init; }
}

/// <summary>
///  Caches nearby road/prefab geometry around the truck, refreshed only when the
///  map identity changes, the render distance setting changes, or the truck has
///  moved far enough - not every frame and not every tick. Mirrors the
///  Nodes.Within() + node.ForwardItem/BackwardItem dedupe pattern already
///  proven in ETS2LA.ML/Vision and SignalHUD (see RESEARCH.md section 2).
/// </summary>
public sealed class NearbyMapCache
{
    private const float RefreshDistanceMeters = 60f;
    private const float RoadSampleStepMeters = 8f;
    private const float PrefabSampleStepMeters = 4f;

    // MapViewSettings' render distance is user-configurable up to 2000m; at
    // that radius, geometry generation (especially walking every NavCurve of
    // every nearby Prefab) can involve enough allocation/CPU work per refresh
    // to be visible as a brief stutter, most noticeably near large
    // interchanges. Sample coarser than the base step sizes above once the
    // view radius exceeds this reference (the original Phase 2 default) -
    // imperceptible at a zoomed-out scale, and keeps cost roughly proportional
    // to what's actually visible rather than growing with the full query area.
    private const float ReferenceViewRadiusMeters = 400f;

    private MapData? cachedMap;
    private Vector3 cachedCenter = new(float.NaN, float.NaN, float.NaN);
    private float cachedRadius = float.NaN;
    private List<RoadSegment> segments = new();
    private List<MapIcon> icons = new();

    public IReadOnlyList<RoadSegment> Segments => segments;

    /// <summary>
    ///  Company/gas station/service/garage markers. Only populated when the
    ///  underlying map item survived ETS2LA's DataFidelity filtering - these
    ///  item types are dropped entirely unless DataFidelity is set to Extreme
    ///  (see ETS2LA.Game/Data/Classes.cs's MapData.PostProcessItem and
    ///  IgnoredItemTypes), so this list is expected to stay empty at the
    ///  default (Medium) setting. That's a user-configurable ETS2LA-wide
    ///  setting, not something NavMap controls or overrides.
    /// </summary>
    public IReadOnlyList<MapIcon> Icons => icons;

    /// <summary>
    ///  Refresh the cache if the map object changed, the render distance
    ///  changed, or the truck moved more than RefreshDistanceMeters since the
    ///  last refresh. Cheap to call every tick.
    /// </summary>
    public void Refresh(Vector3 truckPosition, float viewRadiusMeters)
    {
        MapData? map = MapDataSource.TryGetCurrentMap();
        bool mapChanged = !ReferenceEquals(map, cachedMap);
        bool radiusChanged = cachedRadius != viewRadiusMeters;
        bool movedEnough = float.IsNaN(cachedCenter.X)
            || Vector3.DistanceSquared(cachedCenter, truckPosition) >= RefreshDistanceMeters * RefreshDistanceMeters;

        if (!mapChanged && !radiusChanged && !movedEnough)
            return;

        cachedMap = map;
        cachedCenter = truckPosition;
        cachedRadius = viewRadiusMeters;

        try
        {
            (segments, icons) = Build(map, truckPosition, viewRadiusMeters);
        }
        catch
        {
            // Map descriptors can be transiently unavailable while loading
            // (matches the same defensive pattern used in SignalHUD). Keep
            // whatever geometry we had rather than crashing the overlay.
        }
    }

    private static (List<RoadSegment> Segments, List<MapIcon> Icons) Build(MapData? map, Vector3 center, float viewRadiusMeters)
    {
        var segmentResult = new List<RoadSegment>();
        var iconResult = new List<MapIcon>();
        if (map == null)
            return (segmentResult, iconResult);

        float sampleScale = MathF.Max(viewRadiusMeters / ReferenceViewRadiusMeters, 1f);

        IReadOnlyList<Node> nearbyNodes = map.Nodes.Within(
            center.X - viewRadiusMeters, center.Z - viewRadiusMeters,
            center.X + viewRadiusMeters, center.Z + viewRadiusMeters);

        var roads = new Dictionary<ulong, Road>();
        var prefabs = new Dictionary<ulong, Prefab>();
        foreach (Node node in nearbyNodes)
        {
            if (node.ForwardItem is Road forwardRoad)
                roads.TryAdd(forwardRoad.Uid, forwardRoad);
            if (node.BackwardItem is Road backwardRoad)
                roads.TryAdd(backwardRoad.Uid, backwardRoad);
            if (node.ForwardItem is Prefab forwardPrefab)
                prefabs.TryAdd(forwardPrefab.Uid, forwardPrefab);
            if (node.BackwardItem is Prefab backwardPrefab)
                prefabs.TryAdd(backwardPrefab.Uid, backwardPrefab);
        }

        foreach (Road road in roads.Values)
            AddRoadSegments(segmentResult, road, sampleScale);

        foreach (Prefab prefab in prefabs.Values)
        {
            foreach (List<Vector3> curvePoints in SamplePrefabCurves(prefab, sampleScale))
            {
                if (curvePoints.Count >= 2)
                    segmentResult.Add(new RoadSegment { Points = curvePoints, LaneCount = null });
            }

            foreach (IMapItem slaveItem in prefab.SlaveItems)
            {
                MapIcon? icon = TryCreateIcon(slaveItem, center, viewRadiusMeters);
                if (icon != null)
                    iconResult.Add(icon);
            }
        }

        return (segmentResult, iconResult);
    }

    /// <summary>
    ///  Company/Service/Garage are TruckLib.ScsMap.PrefabSlaveItem subclasses
    ///  reachable via Prefab.SlaveItems - they aren't attached to the main
    ///  Node graph the same way roads/prefabs are, so they can't be found via
    ///  Nodes.Within() directly. Below DataFidelity.Extreme, ETS2LA drops these
    ///  item types during parsing, so the slave item reference stays an
    ///  unresolved placeholder here and this returns null - not an error.
    /// </summary>
    private static MapIcon? TryCreateIcon(IMapItem slaveItem, Vector3 center, float viewRadiusMeters)
    {
        MapIconKind kind;
        INode node;
        switch (slaveItem)
        {
            case Company company:
                kind = MapIconKind.Company;
                node = company.Node;
                break;
            case Service service:
                kind = service.ServiceType == ServiceType.GasStation ? MapIconKind.GasStation : MapIconKind.Service;
                node = service.Node;
                break;
            case Garage garage:
                kind = MapIconKind.Garage;
                node = garage.Node;
                break;
            default:
                return null;
        }

        if (node is not Node concreteNode)
            return null;

        if (Vector3.DistanceSquared(concreteNode.Position, center) > viewRadiusMeters * viewRadiusMeters)
            return null;

        return new MapIcon { Position = concreteNode.Position, Kind = kind };
    }

    /// <summary>
    ///  Samples one polyline per lane (both sides) via ParsedRoad.InterpolateLane,
    ///  exactly like ETS2LA.ML/Vision/Meshes/Road.cs's VisionRoadUtils.ExtractLanes
    ///  (see RESEARCH.md section 8) - this is what makes plain road stretches look
    ///  consistent with how prefab junctions already render (one line per NavCurve/
    ///  lane) instead of a single centerline that only fans out at junctions.
    ///  Falls back to the plain centerline if the road's lane template can't be
    ///  resolved (e.g. an unrecognized/modded road type), so the road still shows
    ///  up as at least one line rather than disappearing.
    /// </summary>
    private static void AddRoadSegments(List<RoadSegment> result, Road road, float sampleScale)
    {
        ParsedRoad? parsedRoad = TryCreateParsedRoad(road);
        if (parsedRoad == null)
        {
            List<Vector3> centerline = SampleRoadCenterline(road, sampleScale);
            if (centerline.Count >= 2)
                result.Add(new RoadSegment { Points = centerline, LaneCount = null });
            return;
        }

        int totalLanes = parsedRoad.GetTotalLaneCount();
        bool addedAny = false;
        foreach (Side side in new[] { Side.Left, Side.Right })
        {
            int laneCount = parsedRoad.GetLaneCount(side);
            for (int laneIndex = 0; laneIndex < laneCount; laneIndex++)
            {
                List<Vector3> points = SampleLane(parsedRoad, side, laneIndex, sampleScale);
                if (points.Count < 2)
                    continue;

                result.Add(new RoadSegment { Points = points, LaneCount = totalLanes });
                addedAny = true;
            }
        }

        if (!addedAny)
        {
            // No lanes resolved on either side (unusual) - still show something.
            List<Vector3> centerline = SampleRoadCenterline(road, sampleScale);
            if (centerline.Count >= 2)
                result.Add(new RoadSegment { Points = centerline, LaneCount = totalLanes });
        }
    }

    private static ParsedRoad? TryCreateParsedRoad(Road road)
    {
        try
        {
            return new ParsedRoad(road);
        }
        catch
        {
            // Road template lookup can fail for unrecognized/modded road types.
            return null;
        }
    }

    private static List<Vector3> SampleLane(ParsedRoad parsedRoad, Side side, int laneIndex, float sampleScale)
    {
        var points = new List<Vector3>();
        float length = parsedRoad.Road.Length;
        if (length <= 0)
            return points;

        float step = Math.Clamp(RoadSampleStepMeters * sampleScale / length, 0.05f, 1f);
        for (float t = 0f; t < 1f; t += step)
            points.Add(parsedRoad.InterpolateLane(t, side, laneIndex).Position);

        points.Add(parsedRoad.InterpolateLane(1f, side, laneIndex).Position);
        return points;
    }

    private static List<Vector3> SampleRoadCenterline(Road road, float sampleScale)
    {
        var points = new List<Vector3>();
        float length = road.Length;
        if (length <= 0)
            return points;

        float step = Math.Clamp(RoadSampleStepMeters * sampleScale / length, 0.05f, 1f);
        for (float t = 0f; t < 1f; t += step)
            points.Add(road.InterpolateCurve(t).Position);

        points.Add(road.InterpolateCurve(1f).Position);
        return points;
    }

    /// <summary>
    ///  Walks every NavCurve in the prefab's descriptor, exactly like
    ///  ETS2LA.ML/Vision/Meshes/Road.cs's VisionRoadUtils.ExtractPrefabCurves
    ///  (see RESEARCH.md section 8) - the only proven-working code in this
    ///  workspace that reads TruckLib.Models.Ppd's PrefabDescriptor/NavCurve.
    /// </summary>
    private static IEnumerable<List<Vector3>> SamplePrefabCurves(Prefab prefab, float sampleScale)
    {
        if (PpdFileHandler.Current.GetPpdFile(prefab.Model.ToString()) is not PrefabDescriptor descriptor)
            yield break;

        int origin = prefab.Origin;
        Vector3 prefabStart = prefab.Nodes[0].Position - descriptor.Nodes[origin].Position;
        Vector3 prefabRotation = prefab.Nodes[0].Rotation.ToEuler()
            - MathEx.GetNodeRotation(descriptor.Nodes[origin].Direction).ToEuler();
        Matrix4x4 rotationMatrix = Matrix4x4.CreateRotationY(prefabRotation.Y, prefab.Nodes[0].Position);

        foreach (NavCurve curve in descriptor.NavCurves)
        {
            if (curve.Length <= 0)
                continue;

            var points = new List<Vector3>();
            float step = Math.Clamp(PrefabSampleStepMeters * sampleScale / curve.Length, 0.1f, 1f);
            for (float t = 0f; t < 1f; t += step)
            {
                Vector3 point = PrefabUtils.InterpolateNavCurve(curve, t);
                points.Add(Vector3.Transform(point + prefabStart, rotationMatrix));
            }

            Vector3 endPoint = PrefabUtils.InterpolateNavCurve(curve, 1f);
            points.Add(Vector3.Transform(endPoint + prefabStart, rotationMatrix));

            yield return points;
        }
    }
}
