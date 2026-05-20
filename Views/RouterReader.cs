using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a read-only hot-path view over router page bytes.<br/>
/// The view does not own bytes and performs no file I/O or allocation.<br/>
/// </summary>
internal readonly ref struct RouterReader
{
    private readonly ReadOnlySpan<byte> bytes;

    public RouterReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
    }

    public uint Magic => RouterLayout.ReadMagic(bytes);

    public ushort FormatVersion => RouterLayout.ReadFormatVersion(bytes);

    public ushort HeaderSize => RouterLayout.ReadHeaderSize(bytes);

    public uint Flags => RouterLayout.ReadFlags(bytes);

    public byte PrefixByteCount => RouterLayout.ReadPrefixByteCount(bytes);

    public ushort KeyDepth => RouterLayout.ReadKeyDepth(bytes);

    public ushort RouteCount => RouterLayout.ReadRouteCount(bytes);

    public ushort MaxRouteCount => RouterLayout.ReadMaxRouteCount(bytes);

    public ushort AllocationClassId => RouterLayout.ReadAllocationClassId(bytes);

    public bool HasDirectIndex => (Flags & RouterLayout.DirectIndexFlag) != 0;

    public bool IsArenaMember => (Flags & RouterLayout.ArenaMemberFlag) != 0;

    public int ArenaBaseDelta => RouterLayout.ReadArenaBaseDelta(bytes);

    public int ArenaLength
    {
        get
        {
            ushort pageSize = ArenaRouterPageSize;
            ushort pageCount = ArenaRouterPageCount;
            if ((ArenaFlags & RouterLayout.ArenaLengthFromPageCountFlag) != 0)
            {
                return checked(pageSize * pageCount);
            }

            return RouterLayout.ReadArenaLength(bytes);
        }
    }

    public ushort ArenaRouterPageSize => RouterLayout.ReadArenaRouterPageSize(bytes);

    public ushort ArenaRouterPageIndex => RouterLayout.ReadArenaRouterPageIndex(bytes);

    public ushort ArenaRouterPageCount => RouterLayout.ReadArenaRouterPageCount(bytes);

    public uint ArenaFlags => RouterLayout.ReadArenaFlags(bytes);

    public bool IsValid =>
        Magic == RouterLayout.Magic &&
        FormatVersion == RouterLayout.FormatVersion &&
        HeaderSize == RouterLayout.HeaderSize &&
        RouteCount <= MaxRouteCount &&
        (PrefixByteCount <= 1 || RouterLayout.RoutesOffset + RouterLayout.GetMultiByteRouteStorageLength(MaxRouteCount, PrefixByteCount) <= RouterLayout.Size) &&
        (!IsArenaMember || (ArenaLength >= RouterLayout.Size && ArenaRouterPageSize == RouterLayout.Size && ArenaRouterPageCount > 0 && ArenaRouterPageIndex < ArenaRouterPageCount)) &&
        RouterLayout.GetRouteOffset(RouteCount == 0 ? 0 : RouteCount - 1) + RouterLayout.RouteSlotSize <= RouterLayout.Size;

    /// <summary>
    /// Reads a direct-index route target for a one-byte router.<br/>
    /// The caller is expected to check `HasDirectIndex` before using this path.<br/>
    /// Offset zero means the route exists but has no target yet.<br/>
    /// </summary>
    /// <param name="prefixByte">The prefix byte used as direct route index.</param>
    /// <returns>The route target offset, or zero when unset.</returns>
    public long GetDirectTarget(byte prefixByte)
    {
        ReadOnlySpan<byte> route = RouterLayout.GetRoute(bytes, prefixByte);
        return RouterLayout.ReadRouteTargetOffset(route);
    }

    /// <summary>
    /// Finds the route target for a one-byte prefix using the router's persisted route ranges.<br/>
    /// Direct-index routers use O(1) route lookup; compressed routers use linear range search for this first slice.<br/>
    /// </summary>
    /// <param name="prefixByte">The prefix byte to route.</param>
    /// <returns>The target offset, or zero when no route target is set.</returns>
    public long FindTarget(byte prefixByte)
    {
        return FindTarget(prefixByte, out _);
    }

    /// <summary>
    /// Finds the route target for a one-byte prefix and returns the selected route index.<br/>
    /// Direct-index routers return the prefix byte as the route index; compressed routers return the matching range slot index.<br/>
    /// </summary>
    /// <param name="prefixByte">The prefix byte to route.</param>
    /// <param name="routeIndex">The selected route index, or -1 when no route matched.</param>
    /// <returns>The target offset, or zero when no route target is set.</returns>
    public long FindTarget(byte prefixByte, out int routeIndex)
    {
        if (HasDirectIndex)
        {
            routeIndex = prefixByte;
            return GetDirectTarget(prefixByte);
        }

        ReadOnlySpan<byte> localBytes = bytes;
        int routeCount = RouteCount;
        for (int i = 0; i < routeCount; i++)
        {
            ReadOnlySpan<byte> route = RouterLayout.GetRoute(localBytes, i);
            byte start = RouterLayout.ReadRoutePrefixStart(route);
            byte end = RouterLayout.ReadRoutePrefixEnd(route);
            if (prefixByte >= start && prefixByte <= end)
            {
                routeIndex = i;
                return RouterLayout.ReadRouteTargetOffset(route);
            }
        }

        routeIndex = -1;
        return 0;
    }

    /// <summary>
    /// Finds the route target for this router using the raw key bytes and returns the selected route index.<br/>
    /// One-byte routers use the existing prefix-byte path; multi-byte compressed routers compare an exact stem and final-byte range.<br/>
    /// Missing key bytes route as zero to match the existing `VS8` short-key routing rule.<br/>
    /// </summary>
    /// <param name="key">The raw key bytes to route.</param>
    /// <param name="keyDepth">The expected first key byte depth consumed by this router.</param>
    /// <param name="routeIndex">The selected route index, or -1 when no route matched.</param>
    /// <returns>The target offset, or zero when no route target is set.</returns>
    public long FindTarget(ReadOnlySpan<byte> key, int keyDepth, out int routeIndex)
    {
        if (PrefixByteCount == 1)
        {
            return FindTarget(GetKeyByteOrZero(key, keyDepth), out routeIndex);
        }

        if (keyDepth != KeyDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "The supplied key depth must match the router key depth.");
        }

        ReadOnlySpan<byte> localBytes = bytes;
        int routeCount = RouteCount;
        int maxRouteCount = MaxRouteCount;
        int prefixByteCount = PrefixByteCount;
        int stemLength = prefixByteCount - 1;
        byte finalByte = GetKeyByteOrZero(key, keyDepth + stemLength);
        for (int i = 0; i < routeCount; i++)
        {
            ReadOnlySpan<byte> stem = localBytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(maxRouteCount, prefixByteCount, i), stemLength);
            if (!StemMatchesKey(stem, key, keyDepth))
            {
                continue;
            }

            ReadOnlySpan<byte> route = RouterLayout.GetRoute(localBytes, i);
            byte start = RouterLayout.ReadRoutePrefixStart(route);
            byte end = RouterLayout.ReadRoutePrefixEnd(route);
            if (finalByte >= start && finalByte <= end)
            {
                routeIndex = i;
                return RouterLayout.ReadRouteTargetOffset(route);
            }
        }

        routeIndex = -1;
        return 0;
    }

    /// <summary>
    /// Reads the target offset from a route slot by index.<br/>
    /// The method is intentionally prefix-agnostic so callers can scan route-limited compressed routers without re-materializing route keys.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based route slot index.</param>
    /// <returns>The target offset persisted in the route slot.</returns>
    public long GetRouteTargetAt(int routeIndex)
    {
        ReadOnlySpan<byte> route = RouterLayout.GetRoute(bytes, routeIndex);
        return RouterLayout.ReadRouteTargetOffset(route);
    }

    private static bool StemMatchesKey(ReadOnlySpan<byte> stem, ReadOnlySpan<byte> key, int keyDepth)
    {
        for (int i = 0; i < stem.Length; i++)
        {
            if (stem[i] != GetKeyByteOrZero(key, keyDepth + i))
            {
                return false;
            }
        }

        return true;
    }

    private static byte GetKeyByteOrZero(ReadOnlySpan<byte> key, int index)
    {
        return index >= 0 && index < key.Length
            ? key[index]
            : (byte)0;
    }
}
