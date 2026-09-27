using System.Numerics;

using ETS2LA.Backend.Events;
using ETS2LA.Game.SDK;
using ETS2LA.Overlay;
using ETS2LA.Overlay.AR;

using Hexa.NET.ImGui;

using SignalSemaphore = ETS2LA.Game.SDK.Semaphore;

namespace NavMap.Signals;

/// <summary>
///  Traffic signal AR markers, originally ported from the standalone SignalHUD
///  plugin. Marks every traffic light within MaximumSignalDistance at its own
///  position with its color, remaining time and distance (see
///  SignalViewSettings.ArMarkerEnabled).
///
///  SignalHUD picked a single "relevant" signal from the truck's heading and
///  route, but that choice often came late or not at all, so every nearby
///  signal is marked instead and the driver picks out the relevant one.
///
///  Markers are drawn straight onto the AR background draw list at each
///  signal's projected screen position, rather than through
///  ARRenderer.BeginWindow/EndWindow: those share a single render texture
///  that is only sampled after all callbacks ran, so several windows in one
///  frame would all show the last one's content.
///
///  Kept independent of the map: it owns its own semaphore subscription and
///  AR callback. Its AR name differs from the standalone SignalHUD plugin's
///  so both can be enabled at the same time without unregistering each other.
/// </summary>
public sealed class SignalHudFeature
{
    private const float MaximumSignalDistance = 150f;
    private const float MarkerHeightAboveSignal = 2.5f;

    // Markers shrink linearly with distance: full size up to
    // FullSizeDistance, down to MinimumMarkerScale at MaximumSignalDistance.
    // Not true 1/distance perspective, which would make the readout
    // unreadably small well before the far end of the range.
    private const float FullSizeDistance = 20f;
    private const float MinimumMarkerScale = 0.4f;
    private const string ArRendererName = "NavMap Signal AR";

    private readonly SignalViewSettings settings;

    private bool arRendererRegistered;
    private Action<SemaphoreData>? semaphoreHandler;
    private SignalSemaphore[] latestSemaphores = Array.Empty<SignalSemaphore>();
    private readonly List<SignalMarker> markers = new();
    private SignalDiagnostics diagnostics = SignalDiagnostics.None;

    public SignalHudFeature(SignalViewSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>
    ///  Exposed for the Adjustments page's diagnostics section, to tell apart
    ///  "the game isn't sending the signal yet" from "the marker isn't drawn".
    /// </summary>
    public SignalDiagnostics Diagnostics => diagnostics;

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

        latestSemaphores = Array.Empty<SignalSemaphore>();
        markers.Clear();
        diagnostics = SignalDiagnostics.None;
    }

    /// <summary>
    ///  Only refreshes the diagnostics - the markers themselves are collected
    ///  every frame in RenderSignalAr, so they follow the 60Hz semaphore data
    ///  rather than the plugin's TickRate.
    /// </summary>
    public void Tick()
    {
        Vector3 truckPosition = CameraProvider.Current.GetCurrentData().truckPosition;
        int trafficLights = 0;
        int inRange = 0;
        float? nearest = null;
        foreach (SignalSemaphore semaphore in latestSemaphores)
        {
            if (semaphore.type != SemaphoreType.TRAFFICLIGHT)
                continue;

            trafficLights++;
            float distance = Vector3.Distance(semaphore.GetWorldCoordinates(), truckPosition);
            if (distance <= MaximumSignalDistance)
                inRange++;
            if (nearest is null || distance < nearest)
                nearest = distance;
        }

        diagnostics = new SignalDiagnostics(trafficLights, inRange, nearest);
    }

    private void OnSemaphores(SemaphoreData data)
    {
        // SemaphoreProvider reuses the same SemaphoreData instance and swaps in
        // a new array on every update, so keep the array itself.
        latestSemaphores = data.semaphores;
    }

    private void RenderSignalAr()
    {
        if (!settings.ArMarkerEnabled)
            return;

        SignalSemaphore[] semaphores = latestSemaphores;
        if (semaphores.Length == 0)
            return;

        var ar = OverlayHandler.Current.AR;
        Vector3 truckPosition = CameraProvider.Current.GetCurrentData().truckPosition;
        int screenWidth = (int)OverlayHandler.Current.OverlayWidth;
        int screenHeight = (int)OverlayHandler.Current.OverlayHeight;

        markers.Clear();
        foreach (SignalSemaphore semaphore in semaphores)
        {
            if (semaphore.type != SemaphoreType.TRAFFICLIGHT)
                continue;

            Vector3 signalPosition = semaphore.GetWorldCoordinates();
            float distance = Vector3.Distance(signalPosition, truckPosition);
            if (distance > MaximumSignalDistance)
                continue;

            var anchor = new ARCoordinate(signalPosition + Vector3.UnitY * MarkerHeightAboveSignal, ARCoordinateCenter.World);
            // Null when the signal is behind the camera.
            Vector2? screenPosition = ar.WorldToScreen(ar.ARCoordinateToVector3(anchor), screenWidth, screenHeight);
            if (screenPosition is Vector2 position)
                markers.Add(new SignalMarker(semaphore, distance, position));
        }

        if (markers.Count == 0)
            return;

        // Farthest first, so nearer markers are drawn on top where they overlap.
        markers.Sort((a, b) => b.Distance.CompareTo(a.Distance));

        float backgroundOpacity = Math.Clamp(settings.ArBackgroundOpacityPercent / 100f, 0f, 1f);
        float fontScale = Math.Clamp(settings.ArFontScalePercent / 100f, 0.5f, 3f);
        uint backgroundColor = (uint)Math.Clamp(
            MathF.Round(backgroundOpacity * byte.MaxValue), 0f, byte.MaxValue);

        ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();
        float baseFontSize = ImGui.GetStyle().FontSizeBase * fontScale;
        foreach (SignalMarker marker in markers)
        {
            float scale = GetDistanceScale(marker.Distance);
            ImGui.PushFont(ImGui.GetFont(), baseFontSize * scale);
            try
            {
                DrawMarker(drawList, marker, backgroundColor, scale);
            }
            finally
            {
                ImGui.PopFont();
            }
        }
    }

    private static float GetDistanceScale(float distance)
    {
        float t = Math.Clamp(
            (distance - FullSizeDistance) / (MaximumSignalDistance - FullSizeDistance), 0f, 1f);
        return 1f - t * (1f - MinimumMarkerScale);
    }

    /// <summary>
    ///  Same look as SignalHUD's marker (glowing dot with the remaining time and
    ///  distance below it, on a rounded black background), centered on the
    ///  signal's screen position. The background grows to fit the text, since
    ///  the font size is adjustable. Every size is multiplied by scale (the
    ///  font is already pushed at the scaled size).
    /// </summary>
    private static void DrawMarker(ImDrawListPtr drawList, SignalMarker marker, uint backgroundColor, float scale)
    {
        uint color = marker.Semaphore.GetColor();
        uint markerColor = ToImGuiColor(color);
        Vector2 center = marker.ScreenPosition;

        string readout = $"{marker.Semaphore.time_remaining:0.0}s\n{marker.Distance:0}m";
        Vector2 readoutSize = ImGui.CalcTextSize(readout);
        Vector2 readoutPosition = new(center.X - readoutSize.X / 2f, center.Y + 25f * scale);

        float halfWidth = MathF.Max(30f * scale, readoutSize.X / 2f + 10f * scale);
        drawList.AddRectFilled(
            new Vector2(center.X - halfWidth, center.Y - 30f * scale),
            new Vector2(center.X + halfWidth, readoutPosition.Y + readoutSize.Y + 8f * scale),
            ToImGuiColor(backgroundColor),
            rounding: 12f * scale);
        drawList.AddCircleFilled(center, 20f * scale, ToImGuiColor(WithAlpha(color, 20)));
        drawList.AddCircleFilled(center, 14f * scale, ToImGuiColor(WithAlpha(color, 45)));
        drawList.AddCircleFilled(center, 9f * scale, ToImGuiColor(WithAlpha(color, 100)));
        drawList.AddCircleFilled(center, 6f * scale, markerColor);
        drawList.AddText(readoutPosition, markerColor, readout);
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

    private readonly record struct SignalMarker(SignalSemaphore Semaphore, float Distance, Vector2 ScreenPosition);
}

/// <param name="TrafficLights">Traffic lights in the latest semaphore data from the game.</param>
/// <param name="InRange">Of those, how many are within the marker distance.</param>
/// <param name="NearestMeters">Distance to the nearest one, or null if there are none.</param>
public sealed record SignalDiagnostics(int TrafficLights, int InRange, float? NearestMeters)
{
    public static readonly SignalDiagnostics None = new(0, 0, null);
}
