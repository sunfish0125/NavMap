using System.Numerics;

using Hexa.NET.ImGui;

using NavMap.Map;
using NavMap.Navigation;
using NavMap.Telemetry;

namespace NavMap.Rendering;

/// <summary>
///  Draws the map into whatever ImGui window is currently active. Must only be
///  called from the Overlay render thread (i.e. from the window Render callback
///  registered with OverlayHandler) - see RESEARCH.md section 9.
///
///  Visual order (background -> range rings -> roads -> active route -> truck
///  marker -> destination indicator) and clipping to the map area follow
///  PLAN.md's Rendering Requirements; road styling and the destination
///  indicator are Phase 8 polish on top of that. Diagnostics text used to be
///  drawn on top of the map itself but was moved to the plugin's Adjustments
///  page (UI/Adjustments.razor) so it doesn't clutter the Overlay.
/// </summary>
public static class MapRenderer
{
    private const byte BackgroundR = 18;
    private const byte BackgroundG = 18;
    private const byte BackgroundB = 22;
    private static readonly uint RangeRingColor = PackColor(255, 255, 255, 20);
    private static readonly uint RoadMinorColor = PackColor(70, 120, 190, 170);
    private static readonly uint RoadColor = PackColor(90, 165, 235, 205);
    private static readonly uint RoadMajorColor = PackColor(130, 195, 255, 235);
    private static readonly uint RouteOutlineColor = PackColor(10, 35, 20, 255);
    private static readonly uint RouteColor = PackColor(60, 210, 100, 255);
    private static readonly uint TruckFillColor = PackColor(255, 210, 60, 255);
    private static readonly uint TruckOutlineColor = PackColor(20, 20, 20, 255);
    private static readonly uint DestinationColor = PackColor(255, 90, 90, 255);
    private static readonly uint CompanyIconColor = PackColor(230, 160, 60, 255);
    private static readonly uint GasStationIconColor = PackColor(80, 210, 110, 255);
    private static readonly uint ServiceIconColor = PackColor(90, 190, 220, 255);
    private static readonly uint GarageIconColor = PackColor(180, 120, 230, 255);
    private static readonly uint IconOutlineColor = PackColor(20, 20, 20, 220);
    private static readonly uint StatusTextColor = PackColor(230, 230, 230, 255);
    private static readonly uint CompassBackgroundColor = PackColor(20, 20, 20, 130);
    private static readonly uint CompassNeedleColor = PackColor(235, 235, 235, 255);
    private static readonly uint CompassOutlineColor = PackColor(20, 20, 20, 200);

    private const float RoadLineThickness = 1.4f;
    private const float RouteLineThickness = 4f;
    private const float TruckMarkerRadius = 7f;
    private const float DestinationMarkerRadius = 6f;
    private const float PoiIconRadius = 6f;
    private const float EdgeIndicatorMargin = 14f;
    private const float CompassRadius = 13f;
    private const float CompassMarginX = 22f;
    private const float CompassMarginY = 22f;

    public static void Render(
        TruckPoseState pose,
        NearbyMapCache nearbyCache,
        RouteGeometryBuilder routeBuilder,
        MapViewSettings settings,
        bool mapReady)
    {
        DrawZoomControls(settings);

        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        if (size.X < 2f || size.Y < 2f)
            return;

        Vector2 min = origin;
        Vector2 max = origin + size;
        Vector2 screenCenter = origin + size * 0.5f;

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);

        byte backgroundAlpha = (byte)Math.Clamp(settings.BackgroundOpacityPercent / 100f * 255f, 0f, 255f);
        drawList.AddRectFilled(min, max, PackColor(BackgroundR, BackgroundG, BackgroundB, backgroundAlpha));

        if (!mapReady || !pose.IsLive)
        {
            DrawStatusMessage(drawList, min, max, mapReady, pose.IsLive);
        }
        else
        {
            float metersPerPixel = (settings.RenderDistanceMeters * 2f) / MathF.Max(MathF.Min(size.X, size.Y), 1f);
            MapProjection projection = new(pose.Position, pose.HeadingRadians, metersPerPixel, screenCenter);

            DrawRangeRings(drawList, screenCenter, settings.RenderDistanceMeters, metersPerPixel);

            foreach (RoadSegment segment in nearbyCache.Segments)
            {
                (uint color, float thickness) = GetRoadStyle(segment.LaneCount);
                DrawPolyline(drawList, projection, segment.Points, color, thickness);
            }

            foreach (MapIcon icon in nearbyCache.Icons)
                DrawIcon(drawList, projection, icon);

            DrawPolyline(drawList, projection, routeBuilder.Points, RouteOutlineColor, RouteLineThickness + 2f);
            DrawPolyline(drawList, projection, routeBuilder.Points, RouteColor, RouteLineThickness);

            DrawTruckMarker(drawList, screenCenter);
            DrawDestinationIndicator(drawList, projection, screenCenter, min, max, routeBuilder.DestinationPosition);
            DrawNorthIndicator(drawList, min, max, pose.HeadingRadians);
        }

        drawList.PopClipRect();
        ImGui.Dummy(size);
    }

    /// <summary>
    ///  NearbyMapCache now emits one segment per lane (see AddRoadSegments), so
    ///  lane count is already visible as "how many parallel lines" - thickness
    ///  is a fixed, thin per-lane stroke and only the color varies with the
    ///  road's total lane count (major/minor differentiation).
    /// </summary>
    private static (uint Color, float Thickness) GetRoadStyle(int? laneCount)
    {
        uint color = laneCount switch
        {
            >= 4 => RoadMajorColor,
            <= 1 => RoadMinorColor,
            _ => RoadColor,
        };
        return (color, RoadLineThickness);
    }

    /// <summary>
    ///  Compact +/- zoom control (a handful of discrete render-distance levels,
    ///  see MapViewSettings) instead of sliders - keeps the on-screen UI small.
    /// </summary>
    private static void DrawZoomControls(MapViewSettings settings)
    {
        ImGui.BeginDisabled(!settings.CanZoomOut);
        if (ImGui.Button("-##zoomOut"))
            settings.ZoomOut();
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.Text($"{settings.RenderDistanceMeters:0} m");
        ImGui.SameLine();

        ImGui.BeginDisabled(!settings.CanZoomIn);
        if (ImGui.Button("+##zoomIn"))
            settings.ZoomIn();
        ImGui.EndDisabled();
    }

    private static void DrawRangeRings(ImDrawListPtr drawList, Vector2 center, float renderDistanceMeters, float metersPerPixel)
    {
        foreach (float fraction in stackalloc float[] { 0.5f, 1f })
        {
            float radiusPixels = (renderDistanceMeters * fraction) / metersPerPixel;
            drawList.AddCircle(center, radiusPixels, RangeRingColor, 64, 1f);
        }
    }

    private static void DrawPolyline(
        ImDrawListPtr drawList, MapProjection projection, IReadOnlyList<Vector3> worldPoints, uint color, float thickness)
    {
        if (worldPoints.Count < 2)
            return;

        Vector2 previous = projection.WorldToScreen(worldPoints[0]);
        for (int i = 1; i < worldPoints.Count; i++)
        {
            Vector2 next = projection.WorldToScreen(worldPoints[i]);
            drawList.AddLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private static void DrawTruckMarker(ImDrawListPtr drawList, Vector2 center)
    {
        // The map is heading-up and truck-centered, so the marker always
        // points straight up regardless of the truck's actual heading.
        Vector2 tip = center + new Vector2(0f, -TruckMarkerRadius);
        Vector2 baseLeft = center + new Vector2(-TruckMarkerRadius * 0.75f, TruckMarkerRadius * 0.6f);
        Vector2 baseRight = center + new Vector2(TruckMarkerRadius * 0.75f, TruckMarkerRadius * 0.6f);

        drawList.AddTriangleFilled(tip, baseLeft, baseRight, TruckFillColor);
        drawList.AddTriangle(tip, baseLeft, baseRight, TruckOutlineColor, 1.5f);
    }

    /// <summary>
    ///  Draws a small shape per MapIconKind for companies/gas stations/service
    ///  points/garages. Only ever has anything to draw when ETS2LA's
    ///  DataFidelity is set to Extreme - see NearbyMapCache.Icons.
    /// </summary>
    private static void DrawIcon(ImDrawListPtr drawList, MapProjection projection, MapIcon icon)
    {
        Vector2 center = projection.WorldToScreen(icon.Position);

        switch (icon.Kind)
        {
            case MapIconKind.Company:
                Vector2 half = new(PoiIconRadius, PoiIconRadius);
                drawList.AddRectFilled(center - half, center + half, CompanyIconColor);
                drawList.AddRect(center - half, center + half, IconOutlineColor);
                break;

            case MapIconKind.GasStation:
                drawList.AddCircleFilled(center, PoiIconRadius, GasStationIconColor);
                drawList.AddCircle(center, PoiIconRadius, IconOutlineColor, 12, 1f);
                break;

            case MapIconKind.Service:
                drawList.AddCircleFilled(center, PoiIconRadius * 0.8f, ServiceIconColor);
                drawList.AddCircle(center, PoiIconRadius * 0.8f, IconOutlineColor, 12, 1f);
                break;

            case MapIconKind.Garage:
                Vector2 top = center + new Vector2(0f, -PoiIconRadius);
                Vector2 rightPoint = center + new Vector2(PoiIconRadius, 0f);
                Vector2 bottom = center + new Vector2(0f, PoiIconRadius);
                Vector2 leftPoint = center + new Vector2(-PoiIconRadius, 0f);
                drawList.AddQuadFilled(top, rightPoint, bottom, leftPoint, GarageIconColor);
                drawList.AddQuad(top, rightPoint, bottom, leftPoint, IconOutlineColor, 1f);
                break;
        }
    }

    /// <summary>
    ///  Draws a marker at the destination when it's within the visible map
    ///  area, or an arrow at the edge of the map pointing toward it otherwise
    ///  - standard GPS-app behavior for an off-screen destination.
    /// </summary>
    private static void DrawDestinationIndicator(
        ImDrawListPtr drawList, MapProjection projection, Vector2 screenCenter, Vector2 min, Vector2 max, Vector3? destination)
    {
        if (destination is not Vector3 destinationPosition)
            return;

        Vector2 destinationScreen = projection.WorldToScreen(destinationPosition);
        bool onScreen = destinationScreen.X >= min.X && destinationScreen.X <= max.X
            && destinationScreen.Y >= min.Y && destinationScreen.Y <= max.Y;

        if (onScreen)
        {
            drawList.AddCircleFilled(destinationScreen, DestinationMarkerRadius, DestinationColor);
            drawList.AddCircle(destinationScreen, DestinationMarkerRadius, TruckOutlineColor, 1.5f);
            return;
        }

        Vector2 direction = destinationScreen - screenCenter;
        if (direction.LengthSquared() < 0.01f)
            return;

        Vector2 half = (max - min) * 0.5f - new Vector2(EdgeIndicatorMargin, EdgeIndicatorMargin);
        float scaleX = direction.X != 0f ? half.X / MathF.Abs(direction.X) : float.MaxValue;
        float scaleY = direction.Y != 0f ? half.Y / MathF.Abs(direction.Y) : float.MaxValue;
        float scale = MathF.Min(scaleX, scaleY);
        Vector2 edgePoint = screenCenter + direction * scale;

        Vector2 normalizedDirection = Vector2.Normalize(direction);
        Vector2 perpendicular = new(-normalizedDirection.Y, normalizedDirection.X);
        const float arrowLength = 9f;
        const float arrowWidth = 7f;
        Vector2 tip = edgePoint + normalizedDirection * (arrowLength * 0.5f);
        Vector2 backCenter = edgePoint - normalizedDirection * (arrowLength * 0.5f);
        Vector2 left = backCenter + perpendicular * (arrowWidth * 0.5f);
        Vector2 right = backCenter - perpendicular * (arrowWidth * 0.5f);

        drawList.AddTriangleFilled(tip, left, right, DestinationColor);
    }

    /// <summary>
    ///  Small rotating compass needle in the map's top-right corner pointing
    ///  toward true north, since the map itself is heading-up and constantly
    ///  rotating.
    ///
    ///  ASSUMPTION, NOT VERIFIED IN LOCAL SOURCE: this treats world -Z as
    ///  north and +X as east. A full search of the local ETS2LA source tree
    ///  for "north" returned zero matches - nothing in this checkout states
    ///  which world axis is north, unlike every other coordinate fact this
    ///  plugin relies on (all of which were confirmed via RESEARCH.md before
    ///  use, per CLAUDE.md's Source of Truth rule). -Z-is-north/+X-is-east is
    ///  the long-standing convention among ETS2/ATS community mapping tools,
    ///  but that is third-party/memory-based knowledge, not something this
    ///  repository confirms. Please verify against an in-game screen that
    ///  shows a real compass/north reference; if this points the wrong way,
    ///  flip it by changing the sign of the "north" direction vector below
    ///  (northWorldDirection) rather than the rotation math itself, since the
    ///  rotation derivation (see MapProjection.WorldToScreen) is already
    ///  confirmed correct for forward/up.
    ///
    ///  Derivation: for a world direction vector d=(dx,dz), applying the same
    ///  heading rotation + axis conventions MapProjection.WorldToScreen uses
    ///  for points (but without the truck-position offset, since a direction
    ///  has no position) gives an on-screen direction. For north
    ///  d=(0,-1), that on-screen direction simplifies to
    ///  (-sin(heading), cos(heading)) - i.e. the needle's rotation angle
    ///  (measured clockwise from "pointing straight up") equals
    ///  heading + PI.
    /// </summary>
    private static void DrawNorthIndicator(ImDrawListPtr drawList, Vector2 min, Vector2 max, float headingRadians)
    {
        Vector2 center = new(max.X - CompassMarginX, min.Y + CompassMarginY);

        float needleAngle = headingRadians + MathF.PI;
        Vector2 direction = new(MathF.Sin(needleAngle), -MathF.Cos(needleAngle));
        Vector2 perpendicular = new(-direction.Y, direction.X);

        Vector2 tip = center + direction * CompassRadius;
        Vector2 baseLeft = center - direction * (CompassRadius * 0.6f) + perpendicular * (CompassRadius * 0.55f);
        Vector2 baseRight = center - direction * (CompassRadius * 0.6f) - perpendicular * (CompassRadius * 0.55f);

        drawList.AddCircleFilled(center, CompassRadius + 7f, CompassBackgroundColor);
        drawList.AddTriangleFilled(tip, baseLeft, baseRight, CompassNeedleColor);
        drawList.AddTriangle(tip, baseLeft, baseRight, CompassOutlineColor, 1f);

        const string label = "N";
        Vector2 labelSize = ImGui.CalcTextSize(label);
        Vector2 labelPos = tip + direction * (labelSize.Y * 0.5f + 2f) - labelSize * 0.5f;
        drawList.AddText(labelPos, CompassNeedleColor, label);
    }

    private static void DrawStatusMessage(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool mapReady, bool telemetryLive)
    {
        string message = (!mapReady, !telemetryLive) switch
        {
            (true, true) => "Waiting for game and telemetry...",
            (true, false) => "Waiting for map data to parse...",
            (false, true) => "Waiting for telemetry (ETS2/ATS running with SDK enabled?)...",
            _ => "Waiting for data...",
        };

        Vector2 textSize = ImGui.CalcTextSize(message);
        Vector2 center = min + (max - min) * 0.5f;
        drawList.AddText(center - textSize * 0.5f, StatusTextColor, message);
    }

    private static uint PackColor(byte r, byte g, byte b, byte a) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
}
