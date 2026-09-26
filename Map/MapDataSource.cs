using ETS2LA.Game.Data;
using ETS2LA.State;

namespace NavMap.Map;

/// <summary>
///  Thin accessor for the currently parsed MapData. Returns null whenever the
///  game/map isn't ready yet - see RESEARCH.md section 1 for the null/lifetime
///  behavior of ApplicationState.Current.RunningGame / Installation.GetMapData().
/// </summary>
public static class MapDataSource
{
    public static MapData? TryGetCurrentMap() => ApplicationState.Current.RunningGame?.GetMapData();
}
