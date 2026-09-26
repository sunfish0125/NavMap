using System.Numerics;

namespace NavMap.Map;

/// <summary>
///  World (X/Z ground plane, see RESEARCH.md section 7) to screen transform for
///  a heading-up, truck-centered 2D map. No latitude/longitude involved.
/// </summary>
public readonly struct MapProjection
{
    private readonly Vector3 center;
    private readonly float headingRadians;
    private readonly float metersPerPixel;
    private readonly Vector2 screenCenter;
    private readonly float cosH;
    private readonly float sinH;

    public MapProjection(Vector3 truckPosition, float headingRadians, float metersPerPixel, Vector2 screenCenter)
    {
        center = truckPosition;
        this.headingRadians = headingRadians;
        this.metersPerPixel = MathF.Max(metersPerPixel, 0.001f);
        this.screenCenter = screenCenter;
        cosH = MathF.Cos(headingRadians);
        sinH = MathF.Sin(headingRadians);
    }

    /// <summary>
    ///  world point -> subtract truck position -> rotate by truck heading so
    ///  truck-forward becomes screen-up -> scale by zoom -> screen coordinates.
    ///
    ///  Derivation: TruckPoseState.HeadingRadians is no longer derived from
    ///  truckPlacement.rotation.X (that field didn't produce a consistent
    ///  rotation across two live tests, so it was abandoned rather than keep
    ///  guessing at its encoding - see TruckPoseState.ComputeHeadingFromMovement's
    ///  doc comment). It's now atan2 of the truck's own recent position delta,
    ///  using the exact same (sin H, cos H) = forward-in-(X,Z) convention this
    ///  method assumes - so "rotate by heading maps forward to screen-up" is
    ///  true by construction here, not just empirically observed: driving in a
    ///  straight line is, by definition, moving along the heading this class
    ///  was given, for any encoding quirks the game's own turn-fraction field
    ///  might have had.
    ///
    ///  The X axis is then negated: ParsedRoad.InterpolateLane (ETS2LA.Game/Data/
    ///  Classes.cs) offsets a curve point by `normal * -(offset)` where `normal =
    ///  Transform(UnitX, rotation)` and its own doc comment states positive
    ///  offset means "to the right" - i.e. -normal (not +normal = UnitX) is
    ///  right, so local +X is left in this engine's convention. Applying that to
    ///  the truck's own rotation, the truck's true right-hand direction works
    ///  out to world (-cos(heading), sin(heading)) in (X,Z), which without this
    ///  negation would land on screen-left instead of screen-right. This part is
    ///  still only source-derived - not yet independently confirmed live the
    ///  way forward/up now has been, so it's still worth a sanity check (e.g.
    ///  turn right and confirm roads on the map sweep to the right of the
    ///  fixed-up truck marker).
    /// </summary>
    public Vector2 WorldToScreen(Vector3 worldPosition)
    {
        float relX = worldPosition.X - center.X;
        float relZ = worldPosition.Z - center.Z;

        float rotatedX = relX * cosH - relZ * sinH;
        float rotatedZ = relX * sinH + relZ * cosH;

        float screenX = screenCenter.X - rotatedX / metersPerPixel;
        float screenY = screenCenter.Y - rotatedZ / metersPerPixel;
        return new Vector2(screenX, screenY);
    }
}
