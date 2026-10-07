using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Caches router header state after a router page is created or read.<br/>
/// This is an outer value snapshot and is not used as a hot byte view.<br/>
/// </summary>
/// <param name="Offset">The file offset where the router page starts.</param>
/// <param name="PrefixByteCount">The number of prefix bytes consumed by the router.</param>
/// <param name="KeyDepth">The key byte depth where routing begins.</param>
/// <param name="RouteCount">The persisted route count.</param>
/// <param name="MaxRouteCount">The configured maximum route count.</param>
/// <param name="AllocationClassId">The router allocation class identifier.</param>
/// <param name="HasDirectIndex">Whether direct index lookup is legal for this router.</param>
/// <param name="IsArenaMember">Whether the router declares router-arena membership metadata.</param>
/// <param name="ArenaBaseDelta">The signed byte delta from this router offset to the arena base offset.</param>
/// <param name="ArenaLength">The total decoded arena byte length.</param>
/// <param name="ArenaRouterPageSize">The router page size used inside the arena.</param>
/// <param name="ArenaRouterPageIndex">The zero-based router page index inside the arena.</param>
/// <param name="ArenaRouterPageCount">The total router page capacity inside the arena.</param>
/// <param name="ArenaFlags">Optional arena-specific flags.</param>
internal readonly record struct RouterSnapshot(
    long Offset,
    byte PrefixByteCount,
    ushort KeyDepth,
    ushort RouteCount,
    ushort MaxRouteCount,
    ushort AllocationClassId,
    bool HasDirectIndex,
    bool IsArenaMember,
    int ArenaBaseDelta,
    int ArenaLength,
    ushort ArenaRouterPageSize,
    ushort ArenaRouterPageIndex,
    ushort ArenaRouterPageCount,
    uint ArenaFlags)
{
    internal static RouterSnapshot FromReader(long offset, RouterReader reader)
    {
        return new RouterSnapshot(
            offset,
            reader.PrefixByteCount,
            reader.KeyDepth,
            reader.RouteCount,
            reader.MaxRouteCount,
            reader.AllocationClassId,
            reader.HasDirectIndex,
            reader.IsArenaMember,
            reader.ArenaBaseDelta,
            reader.ArenaLength,
            reader.ArenaRouterPageSize,
            reader.ArenaRouterPageIndex,
            reader.ArenaRouterPageCount,
            reader.ArenaFlags);
    }
}
