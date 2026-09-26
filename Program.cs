using ETS2LA.Overlay;
using ETS2LA.Settings;
using ETS2LA.Shared;

using Hexa.NET.ImGui;

using NavMap.Map;
using NavMap.Navigation;
using NavMap.Rendering;
using NavMap.Telemetry;

namespace NavMap;

public class NavMapPlugin : Plugin
{
    /// <summary>
    ///  Live instance of this plugin while enabled, so the Adjustments Razor
    ///  page (a separate Blazor UI running in the same ETS2LA process, see
    ///  ETS2LA.UI/Pages/Plugins/Manager.razor's "hasSettingsPage" check) can
    ///  reach ViewSettings. Follows the same static-instance-exposure pattern
    ///  as the DrivingAnalyst sibling plugin's CurrentRuntime.
    /// </summary>
    public static NavMapPlugin? Current { get; private set; }

    private const string SettingsFileName = "NavMapSettings.json";

    private WindowDefinition mapWindow;
    private bool windowRegistered;
    private bool windowVisible = true;
    private float lastAppliedWindowAlpha = -1f;

    private readonly TruckPoseState pose = new();
    private readonly NavigationRouteState routeState = new();
    private readonly NearbyMapCache nearbyCache = new();
    private readonly RouteGeometryBuilder routeBuilder = new();
    private readonly MapViewSettings viewSettings = new();
    private readonly SettingsHandler settingsHandler = new();

    private float lastSavedOpacity;
    private int lastSavedZoomLevel;
    private bool lastSavedHideWhenPaused;

    public MapViewSettings ViewSettings => viewSettings;

    /// <summary>
    ///  Exposed for the Adjustments page's diagnostics section (see
    ///  UI/Adjustments.razor) - this used to be drawn directly on the map, but
    ///  was moved off the Overlay to keep it uncluttered.
    /// </summary>
    public TruckPoseState Pose => pose;
    public NavigationRouteState RouteState => routeState;
    public NearbyMapCache NearbyCache => nearbyCache;
    public RouteGeometryBuilder RouteBuilder => routeBuilder;
    public bool MapReady => MapDataSource.TryGetCurrentMap() != null;

    public override void Init()
    {
        NavMapSettings persisted = settingsHandler.Load<NavMapSettings>(SettingsFileName);
        viewSettings.BackgroundOpacityPercent = persisted.BackgroundOpacityPercent;
        viewSettings.ZoomLevelIndex = persisted.ZoomLevelIndex;
        viewSettings.HideOverlayWhenPaused = persisted.HideOverlayWhenPaused;
        lastSavedOpacity = viewSettings.BackgroundOpacityPercent;
        lastSavedZoomLevel = viewSettings.ZoomLevelIndex;
        lastSavedHideWhenPaused = viewSettings.HideOverlayWhenPaused;

        base.Init();
    }

    public override float TickRate => 10f;

    public override PluginInformation Info => new()
    {
        Id = "sunfish.navmap",
        Version = "0.1.0",
        Name = "NavMap",
        Description = "Custom navigation map using the game's existing navigation route.",
        AuthorName = "Sunfish",
        Dependencies = new List<string>()
    };

    public override void OnEnable()
    {
        base.OnEnable();

        mapWindow = new WindowDefinition
        {
            Title = "NavMap",
            Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNavInputs
        };

        ApplyWindowAlphaIfChanged();

        OverlayHandler.Current.RegisterWindow(mapWindow, RenderMap);
        windowRegistered = true;
        windowVisible = true; // RegisterWindow creates a new window open by default

        pose.Start();
        routeState.Start();

        Current = this;
    }

    public override void Tick()
    {
        pose.ExpireIfStale();
        ApplyWindowAlphaIfChanged();
        UpdateWindowVisibility();
        SaveViewSettingsIfChanged();

        if (pose.IsLive)
        {
            nearbyCache.Refresh(pose.Position, viewSettings.RenderDistanceMeters);
            routeBuilder.Refresh(routeState, pose.Position, viewSettings.RenderDistanceMeters, pose.RouteDistanceMeters);
        }
    }

    /// <summary>
    ///  Optionally closes the Overlay window while the sim is paused, instead
    ///  of leaving it open showing the "waiting for telemetry" status message -
    ///  see MapViewSettings.HideOverlayWhenPaused. Only calls Open/CloseWindow
    ///  on an actual state change, same guard pattern as SignalHUD's
    ///  show/hide-on-signal-detected logic.
    /// </summary>
    private void UpdateWindowVisibility()
    {
        bool shouldBeVisible = !(viewSettings.HideOverlayWhenPaused && pose.IsPaused);
        if (shouldBeVisible == windowVisible)
            return;

        windowVisible = shouldBeVisible;
        if (windowVisible)
            OverlayHandler.Current.OpenWindow(mapWindow.Title);
        else
            OverlayHandler.Current.CloseWindow(mapWindow.Title);
    }

    /// <summary>
    ///  The opacity slider lives on a separate Blazor UI page and the zoom
    ///  buttons live on the Overlay render thread, so rather than wiring an
    ///  explicit "on change" callback from each, Tick() just polls for
    ///  differences against what was last saved - same self-polling shape as
    ///  NearbyMapCache/RouteGeometryBuilder's cache-refresh checks.
    /// </summary>
    private void SaveViewSettingsIfChanged()
    {
        if (viewSettings.BackgroundOpacityPercent == lastSavedOpacity
            && viewSettings.ZoomLevelIndex == lastSavedZoomLevel
            && viewSettings.HideOverlayWhenPaused == lastSavedHideWhenPaused)
            return;

        lastSavedOpacity = viewSettings.BackgroundOpacityPercent;
        lastSavedZoomLevel = viewSettings.ZoomLevelIndex;
        lastSavedHideWhenPaused = viewSettings.HideOverlayWhenPaused;

        settingsHandler.Save(SettingsFileName, new NavMapSettings
        {
            BackgroundOpacityPercent = lastSavedOpacity,
            ZoomLevelIndex = lastSavedZoomLevel,
            HideOverlayWhenPaused = lastSavedHideWhenPaused
        });
    }

    /// <summary>
    ///  OverlayHandler.RegisterWindow copies WindowDefinition into its own
    ///  internal window list by value, so mutating mapWindow.Alpha alone does
    ///  nothing to the already-registered window - it has to be pushed again
    ///  via RegisterWindow. At 0% this makes the ImGui window itself (not just
    ///  our own drawn background rect) fully transparent, since ETS2LA's
    ///  Overlay.OnUIRender always calls ImGui.SetNextWindowBgAlpha from
    ///  WindowDefinition.Alpha (defaulting to 0.9 when unset) independent of
    ///  whatever MapRenderer draws inside the window.
    /// </summary>
    private void ApplyWindowAlphaIfChanged()
    {
        float currentAlpha = Math.Clamp(viewSettings.BackgroundOpacityPercent / 100f, 0f, 1f);
        if (currentAlpha == lastAppliedWindowAlpha)
            return;

        mapWindow.Alpha = currentAlpha;
        lastAppliedWindowAlpha = currentAlpha;

        if (windowRegistered)
            OverlayHandler.Current.RegisterWindow(mapWindow, RenderMap);
    }

    public override void OnDisable()
    {
        if (Current == this)
            Current = null;

        if (windowRegistered)
        {
            OverlayHandler.Current.UnregisterWindow(mapWindow);
            windowRegistered = false;
        }

        pose.Stop();
        routeState.Stop();

        base.OnDisable();
    }

    public override void Shutdown()
    {
        settingsHandler.Dispose();
        base.Shutdown();
    }

    private void RenderMap()
    {
        MapRenderer.Render(pose, nearbyCache, routeBuilder, viewSettings, MapReady);
    }
}
