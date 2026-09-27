namespace NavMap.Rendering;

/// <summary>
///  The subset of MapViewSettings persisted across restarts, via
///  ETS2LA.Settings.SettingsHandler (JSON at %APPDATA%\ETS2LA\NavMapSettings.json
///  - same mechanism/location SignalHUD and ETS2LA's own DataSettings/
///  StateSettings use). Kept as a separate plain-field POCO rather than
///  persisting MapViewSettings directly, since that class exposes computed
///  properties (RenderDistanceMeters, CanZoomIn/Out) backed by a private zoom
///  level index that doesn't round-trip through JSON.
/// </summary>
public sealed class NavMapSettings
{
    public float BackgroundOpacityPercent = 90f;
    public int ZoomLevelIndex = 2;
    public bool HideOverlayWhenPaused;

    // Traffic signal AR marker (see Signals.SignalViewSettings). Missing from
    // settings files saved by older versions, so these defaults apply there.
    public bool SignalArMarkerEnabled = true;
    public float SignalArBackgroundOpacityPercent = 80f;
    public float SignalArFontScalePercent = 100f;
}
