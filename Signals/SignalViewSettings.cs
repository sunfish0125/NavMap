namespace NavMap.Signals;

/// <summary>
///  Runtime-adjustable traffic signal AR marker settings. Same shape as
///  Rendering.MapViewSettings: plain primitive fields written by the
///  Adjustments page and read by the AR render thread without locking, with
///  NavMapPlugin loading/saving the persisted copy via NavMapSettings.
/// </summary>
public sealed class SignalViewSettings
{
    /// <summary>
    ///  Whether AR markers are drawn on nearby traffic lights.
    /// </summary>
    public bool ArMarkerEnabled = true;

    /// <summary>
    ///  Opacity of the AR marker's black background, as a 0-100 percentage.
    /// </summary>
    public float ArBackgroundOpacityPercent = 80f;

    /// <summary>
    ///  AR marker font size, as a 50-300 percentage of the Overlay's base font size.
    /// </summary>
    public float ArFontScalePercent = 100f;
}
