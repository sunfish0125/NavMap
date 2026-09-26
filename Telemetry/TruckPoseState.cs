using System.Numerics;

using ETS2LA.Backend.Events;
using ETS2LA.Game.Telemetry;

namespace NavMap.Telemetry;

/// <summary>
///  Tracks the truck's latest world position/heading from GameTelemetry.
///  Subscribes/unsubscribes itself; callers should read <see cref="Position"/>,
///  <see cref="HeadingRadians"/> and <see cref="IsLive"/> from the render thread.
/// </summary>
public sealed class TruckPoseState
{
    // Telemetry updates at 60Hz; treat data older than this as no longer live
    // even if OnEnable forgot to unsubscribe cleanly or the provider stalls.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    // Below this much horizontal movement between ticks, keep the last known
    // heading instead of recomputing it - avoids the heading jittering from
    // GPS/float noise while parked or stopped at a light.
    private const float MinMovementMetersForHeading = 0.15f;

    /// <summary>
    ///  Position/heading/live-state as a single immutable snapshot, swapped in
    ///  one reference assignment. Position and HeadingRadians used to be two
    ///  separate mutable properties, which meant a reader on the Overlay render
    ///  thread could observe a torn combination (this tick's Position paired
    ///  with the previous tick's HeadingRadians, or vice versa) if it read them
    ///  right as the telemetry thread was mid-update - for one frame, the map
    ///  would then rotate to an angle that didn't match where the truck
    ///  actually was, making the road layout briefly look like it was pointing
    ///  the wrong way even though the fixed-up truck marker itself never
    ///  rotates. Reference reads/writes are atomic in .NET, so swapping one
    ///  Snapshot reference avoids that.
    /// </summary>
    private sealed record Snapshot(Vector3 Position, float HeadingRadians, bool IsLive, bool IsPaused, float RouteDistanceMeters);

    private static readonly Snapshot NotLive = new(Vector3.Zero, 0f, false, false, 0f);

    private Snapshot current = NotLive;
    private Vector3? previousPosition;
    private Action<GameTelemetryData>? handler;
    private DateTime lastUpdateUtc = DateTime.MinValue;

    public Vector3 Position => current.Position;
    public float HeadingRadians => current.HeadingRadians;
    public bool IsLive => current.IsLive;

    /// <summary>
    ///  True only while the SDK is connected AND the sim is paused - distinct
    ///  from IsLive being false, which also covers "not connected at all".
    ///  Used to optionally hide the NavMap window while paused (see
    ///  MapViewSettings.HideOverlayWhenPaused).
    /// </summary>
    public bool IsPaused => current.IsPaused;

    /// <summary>
    ///  GameTelemetryData.truckFloat.routeDistance - the standard SCS SDK
    ///  "distance to planned destination" field, from the same telemetry
    ///  channel already confirmed live (unlike NavigationProvider's separate
    ///  ETS2LARoute channel, see RESEARCH.md section 4/7). Used as an
    ///  independent signal for "is a route actually active", since
    ///  NavigationData's entries have been observed to keep showing the
    ///  previous route after navigation ends in-game rather than clearing.
    ///  0 (or less) is treated as "no active route".
    /// </summary>
    public float RouteDistanceMeters => current.RouteDistanceMeters;

    public void Start()
    {
        handler = OnTelemetry;
        Events.Current.Subscribe(GameTelemetry.Current.EventString, handler);
    }

    public void Stop()
    {
        if (handler != null)
        {
            Events.Current.Unsubscribe(GameTelemetry.Current.EventString, handler);
            handler = null;
        }

        current = NotLive;
        previousPosition = null;
    }

    /// <summary>
    ///  Call periodically from Tick() to expire IsLive if telemetry events stop
    ///  arriving entirely (e.g. the game process was killed).
    /// </summary>
    public void ExpireIfStale()
    {
        if (current.IsLive && DateTime.UtcNow - lastUpdateUtc > StaleAfter)
            current = current with { IsLive = false };
    }

    private void OnTelemetry(GameTelemetryData data)
    {
        lastUpdateUtc = DateTime.UtcNow;

        if (!data.sdkActive)
        {
            current = current with { IsLive = false, IsPaused = false };
            return;
        }

        if (data.paused)
        {
            current = current with { IsLive = false, IsPaused = true };
            return;
        }

        Vector3 newPosition = data.truckPlacement.coordinate.ToVector3();
        float headingRadians = current.HeadingRadians;

        // previousPosition is null right after Start() (or a game/profile
        // switch teleports the truck) - skip heading update for that one tick
        // rather than computing a heading from a bogus huge delta.
        if (previousPosition is Vector3 previous)
        {
            Vector3 delta = newPosition - previous;
            float horizontalDistance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            if (horizontalDistance >= MinMovementMetersForHeading)
                headingRadians = ComputeHeadingFromMovement(delta);
        }
        previousPosition = newPosition;

        current = new Snapshot(newPosition, headingRadians, true, false, data.truckFloat.routeDistance);
    }

    /// <summary>
    ///  Heading derived directly from the truck's own movement between telemetry
    ///  ticks, instead of from truckPlacement.rotation.X.
    ///
    ///  Two live tests showed rotation.X can't be trusted for this: driving
    ///  straight south (rotation.X=0.509) scrolled the map backwards (fixed
    ///  with a +180 degree correction), but a second straight-line test at a
    ///  different heading (rotation.X=0.5796) then scrolled the map sideways -
    ///  meaning the error isn't a constant phase offset, so the rotation.X to
    ///  angle conversion is wrong in some way that's not worth guessing at
    ///  further. Movement-based heading sidesteps the whole question: whatever
    ///  direction the truck is actually displacing toward between two samples
    ///  IS by definition the direction "forward" should mean for a heading-up
    ///  map, so driving in a straight line is guaranteed to scroll the map
    ///  top-to-bottom regardless of how rotation.X is encoded.
    ///
    ///  Trade-off: heading only updates while actually moving (see
    ///  MinMovementMetersForHeading) and reflects direction of travel rather
    ///  than which way the cab is pointed - most noticeable while reversing,
    ///  where the map will orient to "backwards is up" instead of matching the
    ///  cab's facing direction. Acceptable for a navigation map's purposes.
    /// </summary>
    private static float ComputeHeadingFromMovement(Vector3 delta)
    {
        float heading = MathF.Atan2(delta.X, delta.Z);
        if (heading < 0)
            heading += MathF.Tau;

        return heading;
    }
}
