using System.Numerics;

using ETS2LA.Backend.Events;
using ETS2LA.Game.Data;
using ETS2LA.Game.SDK;
using ETS2LA.Overlay;
using ETS2LA.Overlay.AR;

using Hexa.NET.ImGui;

using NavMap.Map;
using NavMap.Navigation;

using TruckLib.ScsMap;

using SignalSemaphore = ETS2LA.Game.SDK.Semaphore;

namespace NavMap.Signals;

/// <summary>
///  Traffic signal HUD, ported from the standalone SignalHUD plugin. Picks the
///  traffic light most likely to control the truck and marks it in 3D AR with
///  its remaining time and distance (see SignalViewSettings.ArMarkerEnabled).
///  SignalHUD's plain Overlay window was intentionally not ported.
///
///  Kept independent of the map: it owns its own semaphore subscription and
///  AR callback, and only reads NavigationRouteState. Its AR names differ from
///  the standalone SignalHUD plugin's so both can be enabled at the same time
///  without unregistering each other.
/// </summary>
public sealed class SignalHudFeature
{
    private const float MaximumSignalDistance = 100f;
    private const float RouteSignalMatchDistance = 35f;
    private const float RouteCacheRefreshDistance = 25f;
    private const string ArRendererName = "NavMap Signal AR";
    private const string ArWindowName = "NavMapSignalMarker";

    private readonly SignalViewSettings settings;
    private readonly NavigationRouteState routeState;

    private bool arRendererRegistered;
    private Action<SemaphoreData>? semaphoreHandler;
    private SemaphoreData? latestSemaphoreData;
    private SignalCandidate? selectedSignal;
    private readonly List<Vector3> routeSignalPositions = new();
    private Vector3 lastRouteCachePosition = new(float.NaN);
    private NavigationData? cachedRouteData;
    private MapData? cachedMap;

    public SignalHudFeature(SignalViewSettings settings, NavigationRouteState routeState)
    {
        this.settings = settings;
        this.routeState = routeState;
    }

    /// <summary>
    ///  Exposed for the Adjustments page's diagnostics section.
    /// </summary>
    public bool HasSignal => selectedSignal is not null;
    public int RouteSignalCount => routeSignalPositions.Count;

    public void Start()
    {
        semaphoreHandler = OnSemaphores;
        Events.Current.Subscribe(SemaphoreProvider.Current.EventString, semaphoreHandler);

        OverlayHandler.Current.AR.RegisterRenderCallback(new ARRenderCallback
        {
            Definition = new ARRendererDefinition
            {
                Name = ArRendererName,
                Alpha = 1f
            },
            Render3D = RenderSignalAr
        });
        arRendererRegistered = true;
    }

    public void Stop()
    {
        if (semaphoreHandler != null)
        {
            Events.Current.Unsubscribe(SemaphoreProvider.Current.EventString, semaphoreHandler);
            semaphoreHandler = null;
        }

        if (arRendererRegistered)
        {
            OverlayHandler.Current.AR.UnregisterRenderCallback(ArRendererName);
            arRendererRegistered = false;
        }

        latestSemaphoreData = null;
        selectedSignal = null;
        routeSignalPositions.Clear();
        lastRouteCachePosition = new Vector3(float.NaN);
        cachedRouteData = null;
        cachedMap = null;
    }

    public void Tick()
    {
        CameraData truck = CameraProvider.Current.GetCurrentData();
        UpdateRouteSignalPositions(truck.truckPosition);
        selectedSignal = SelectSignal(latestSemaphoreData, truck, routeSignalPositions);
    }

    private void OnSemaphores(SemaphoreData data)
    {
        latestSemaphoreData = data;
    }

    private void UpdateRouteSignalPositions(Vector3 truckPosition)
    {
        MapData? map = MapDataSource.TryGetCurrentMap();
        NavigationData? navigation = routeState.Latest;
        bool refreshDue = !ReferenceEquals(map, cachedMap)
            || !ReferenceEquals(navigation, cachedRouteData)
            || float.IsNaN(lastRouteCachePosition.X)
            || Vector3.DistanceSquared(lastRouteCachePosition, truckPosition)
                >= RouteCacheRefreshDistance * RouteCacheRefreshDistance;
        if (!refreshDue)
            return;

        cachedMap = map;
        cachedRouteData = navigation;
        lastRouteCachePosition = truckPosition;
        routeSignalPositions.Clear();
        if (map is null || navigation is null)
            return;

        var prefabs = new Dictionary<ulong, Prefab>();
        IReadOnlyList<Node> nearbyNodes = map.Nodes.Within(
            truckPosition.X - MaximumSignalDistance,
            truckPosition.Z - MaximumSignalDistance,
            truckPosition.X + MaximumSignalDistance,
            truckPosition.Z + MaximumSignalDistance);

        foreach (Node node in nearbyNodes)
        {
            if (node.ForwardItem is Prefab forwardPrefab)
                prefabs.TryAdd(forwardPrefab.Uid, forwardPrefab);
            if (node.BackwardItem is Prefab backwardPrefab)
                prefabs.TryAdd(backwardPrefab.Uid, backwardPrefab);
        }

        foreach (Prefab prefab in prefabs.Values)
        {
            try
            {
                List<Node> routeNodes = prefab.Nodes
                    .OfType<Node>()
                    .Select(node => new { Node = node, Index = navigation.IndexFor(node.Uid) })
                    .Where(entry => entry.Index >= 0)
                    .OrderBy(entry => entry.Index)
                    .Select(entry => entry.Node)
                    .ToList();
                if (routeNodes.Count < 2)
                    continue;

                ParsedPrefab parsedPrefab = new(prefab);
                (List<PrefabPath> bestPaths, _) = parsedPrefab.GetPathsFromNodeToNode(
                    routeNodes[0], routeNodes[1], truckPosition);
                PrefabPath? routePath = bestPaths.FirstOrDefault();
                if (routePath is null)
                    continue;

                foreach (ParsedSemaphore semaphore in routePath.GetSemaphores())
                    routeSignalPositions.Add(semaphore.GetWorldOrientedPoint().Position);
            }
            catch
            {
                // Map descriptors can be unavailable while map data is loading. Fall back to direction-only matching.
            }
        }
    }

    private static SignalCandidate? SelectSignal(
        SemaphoreData? data,
        CameraData truck,
        IReadOnlyList<Vector3> routeSignals)
    {
        if (data is null)
            return null;

        Vector3 forward = Vector3.Transform(Vector3.UnitZ, truck.truckRotation);
        forward.Y = 0f;
        if (forward.LengthSquared() < 0.001f)
            return null;

        forward = Vector3.Normalize(forward);
        SignalCandidate? best = null;
        float bestScore = float.NegativeInfinity;

        foreach (SignalSemaphore semaphore in data.semaphores)
        {
            if (semaphore.type != SemaphoreType.TRAFFICLIGHT)
                continue;

            Vector3 signalPosition = semaphore.GetWorldCoordinates();
            Vector3 offset = signalPosition - truck.truckPosition;
            float distance = offset.Length();
            Vector3 horizontalOffset = new(offset.X, 0f, offset.Z);
            float horizontalDistance = horizontalOffset.Length();
            if (distance > MaximumSignalDistance || horizontalDistance < 0.1f)
                continue;

            Vector3 direction = horizontalOffset / horizontalDistance;
            float alignment = Vector3.Dot(forward, direction);
            if (alignment <= 0f)
                continue;

            float lateralOffset = MathF.Abs(forward.X * horizontalOffset.Z - forward.Z * horizontalOffset.X);
            // A crossing-road signal may match the same prefab route. Keep route
            // matching as a small bonus, while distance remains the main criterion.
            float score = -distance + alignment * 5f - lateralOffset * 0.75f;
            SignalCandidate candidate = new(semaphore, distance);
            bool matchesRoute = routeSignals.Any(routeSignalPosition =>
                Vector3.DistanceSquared(routeSignalPosition, signalPosition)
                    <= RouteSignalMatchDistance * RouteSignalMatchDistance);
            if (matchesRoute)
                score += 5f;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private void RenderSignalAr()
    {
        if (!settings.ArMarkerEnabled)
            return;

        SignalCandidate? signal = selectedSignal;
        if (signal is null)
            return;

        Vector3 position = signal.Semaphore.GetWorldCoordinates();
        uint color = signal.Semaphore.GetColor();
        float backgroundOpacity = Math.Clamp(settings.ArBackgroundOpacityPercent / 100f, 0f, 1f);
        float fontScale = Math.Clamp(settings.ArFontScalePercent / 100f, 0.5f, 3f);
        uint backgroundColor = (uint)Math.Clamp(
            MathF.Round(backgroundOpacity * byte.MaxValue), 0f, byte.MaxValue);
        var ar = OverlayHandler.Current.AR;
        CameraData camera = CameraProvider.Current.GetCurrentData();
        ar.BeginWindow(
            ArWindowName,
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar,
            forceWidth: 140,
            forceHeight: 120);
        try
        {
            ImGui.PushFont(ImGui.GetFont(), ImGui.GetStyle().FontSizeBase * fontScale);
            try
            {
                ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                Vector2 windowPosition = ImGui.GetWindowPos();
                Vector2 markerCenter = windowPosition + new Vector2(70f, 34f);
                uint markerColor = ToImGuiColor(color);

                // The indicator and readout are drawn into one AR texture, so they
                // remain a single marker regardless of camera angle or distance.
                drawList.AddRectFilled(
                    windowPosition + new Vector2(10f, 4f),
                    windowPosition + new Vector2(130f, 116f),
                    ToImGuiColor(backgroundColor),
                    rounding: 12f);
                drawList.AddCircleFilled(markerCenter, 20f, ToImGuiColor(WithAlpha(color, 20)));
                drawList.AddCircleFilled(markerCenter, 14f, ToImGuiColor(WithAlpha(color, 45)));
                drawList.AddCircleFilled(markerCenter, 9f, ToImGuiColor(WithAlpha(color, 100)));
                drawList.AddCircleFilled(markerCenter, 6f, markerColor);

                string readout = $"{signal.Semaphore.time_remaining:0.0}s\n{signal.Distance:0}m";
                Vector2 readoutSize = ImGui.CalcTextSize(readout);
                drawList.AddText(
                    new Vector2(markerCenter.X - readoutSize.X / 2f, markerCenter.Y + 25f),
                    markerColor,
                    readout);
            }
            finally
            {
                ImGui.PopFont();
            }
        }
        finally
        {
            ar.EndWindow(
                new ARCoordinate(position + Vector3.UnitY * 2.5f, ARCoordinateCenter.World),
                camera.rotation,
                width: 3f,
                // With the current ETS2LA, EndWindow's default UVs put the
                // window's top edge at the bottom of the AR quad, drawing the
                // marker upside down.
                invertY: true);
        }
    }

    private static uint WithAlpha(uint color, byte alpha)
    {
        return (color & 0xFFFFFF00) | alpha;
    }

    private static uint ToImGuiColor(uint rgba)
    {
        return ((rgba & 0xFF000000) >> 24)
            | ((rgba & 0x00FF0000) >> 8)
            | ((rgba & 0x0000FF00) << 8)
            | ((rgba & 0x000000FF) << 24);
    }

    private sealed record SignalCandidate(SignalSemaphore Semaphore, float Distance);
}
