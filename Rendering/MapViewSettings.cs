namespace NavMap.Rendering;

/// <summary>
///  Runtime-adjustable map zoom/background opacity. Live, in-memory state used
///  by MapRenderer every frame; NavMapPlugin loads/saves the persisted subset
///  of this (see NavMapSettings) via ETS2LA.Settings.SettingsHandler, so this
///  class itself stays free of any JSON/serialization concerns. Zoom is a
///  small set of discrete render-distance levels (meters shown edge-to-edge)
///  rather than a continuous slider + a separate render distance control, so
///  on-screen UI is just two buttons instead of sliders that were taking up
///  too much of the window.
/// </summary>
public sealed class MapViewSettings
{
    private static readonly float[] LevelsMeters = { 500f, 1000f, 1500f, 2000f };
    private const int DefaultLevelIndex = 2;

    private int levelIndex = DefaultLevelIndex;

    /// <summary>
    ///  Radius (meters) around the truck that is queried/cached from MapData,
    ///  and also exactly what's shown edge-to-edge on screen.
    /// </summary>
    public float RenderDistanceMeters => LevelsMeters[levelIndex];

    public bool CanZoomIn => levelIndex > 0;
    public bool CanZoomOut => levelIndex < LevelsMeters.Length - 1;

    /// <summary>
    ///  Raw zoom level index (0 = most zoomed in .. LevelsMeters.Length-1 =
    ///  most zoomed out). Only meant for NavMapSettings load/save round-tripping
    ///  - use ZoomIn()/ZoomOut() for the +/- buttons.
    /// </summary>
    public int ZoomLevelIndex
    {
        get => levelIndex;
        set => levelIndex = Math.Clamp(value, 0, LevelsMeters.Length - 1);
    }

    public void ZoomIn()
    {
        if (CanZoomIn)
            levelIndex--;
    }

    public void ZoomOut()
    {
        if (CanZoomOut)
            levelIndex++;
    }

    /// <summary>
    ///  Opacity of the NavMap window's own background fill, as a 0-100
    ///  percentage. Configured from the plugin's Adjustments page (see
    ///  UI/Adjustments.razor) and read by MapRenderer on the Overlay render
    ///  thread - see RESEARCH.md section 9 for why that cross-thread read is
    ///  fine without locking (a single primitive field, same pattern as
    ///  RenderDistanceMeters/the zoom level).
    /// </summary>
    public float BackgroundOpacityPercent = 90f;

    /// <summary>
    ///  When true, NavMapPlugin closes its Overlay window while the sim is
    ///  paused (TruckPoseState.IsPaused) instead of leaving it open showing
    ///  the "waiting for telemetry" status message. Off by default to keep
    ///  existing behavior unless the user opts in from the Adjustments page.
    /// </summary>
    public bool HideOverlayWhenPaused;
}
