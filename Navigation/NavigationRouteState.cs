using ETS2LA.Backend.Events;
using ETS2LA.Game.SDK;

namespace NavMap.Navigation;

/// <summary>
///  Tracks the latest route published by NavigationProvider. Does not do any
///  map/geometry work itself - see RouteGeometryBuilder for that.
/// </summary>
public sealed class NavigationRouteState
{
    private Action<NavigationData>? handler;
    private NavigationData? latest;
    private bool hasReceivedData;

    public void Start()
    {
        handler = OnNavigationData;
        Events.Current.Subscribe(NavigationProvider.Current.EventString, handler);
    }

    public void Stop()
    {
        if (handler != null)
        {
            Events.Current.Unsubscribe(NavigationProvider.Current.EventString, handler);
            handler = null;
        }

        latest = null;
        hasReceivedData = false;
    }

    private void OnNavigationData(NavigationData data)
    {
        latest = data;
        hasReceivedData = true;
    }

    /// <summary>
    ///  Identity of the latest NavigationData instance, for cheap "did the route
    ///  change" comparisons (ReferenceEquals) without diffing entries.
    /// </summary>
    public NavigationData? Latest => latest;

    /// <summary>
    ///  True once NavigationProvider has published at least one NavigationData
    ///  event, regardless of whether it contained a real route. NavigationProvider
    ///  reads a separate shared-memory channel ("Local\ETS2LARoute", an ETS2LA-
    ///  specific SDK extension, not the standard SCS telemetry mmap) and silently
    ///  returns without publishing if that mmap can't be opened at all - so
    ///  "route:0" alone can't tell apart "no active route" from "this SDK channel
    ///  never connected in the first place". This flag disambiguates that.
    /// </summary>
    public bool HasReceivedData => hasReceivedData;

    /// <summary>
    ///  Ordered, de-padded route entries: NavigationData.entries is always a fixed
    ///  6000-slot array. Per RESEARCH.md section 4, unused trailing slots are
    ///  assumed to read nodeUid == 0 - this has not been runtime-verified, so this
    ///  is a documented assumption, not a proven fact. Entry order is assumed to
    ///  run from closest-to-truck (index 0) toward the destination.
    /// </summary>
    public IReadOnlyList<NavigationEntry> GetValidEntries()
    {
        NavigationData? data = latest;
        if (data == null || data.entries.Length == 0)
            return Array.Empty<NavigationEntry>();

        var result = new List<NavigationEntry>();
        foreach (NavigationEntry entry in data.entries)
        {
            if (entry.nodeUid == 0)
                break;

            result.Add(entry);
        }

        return result;
    }

    public bool HasRoute => GetValidEntries().Count > 0;
}
