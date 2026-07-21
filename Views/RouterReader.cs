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

        if (routeCount > 0)
        {
            routeIndex = FindNearestMultiByteRoute(key, keyDepth, localBytes, routeCount, maxRouteCount, prefixByteCount, stemLength);
            return GetRouteTargetAt(routeIndex);
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

    /// <summary>
    /// Reads the inclusive starting prefix byte from a physical route slot.<br/>
    /// Count and aggregate walkers use this to iterate persisted route entries directly instead of re-querying every logical prefix value.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based route slot index.<br/></param>
    /// <returns>The inclusive prefix start byte stored in the route slot.<br/></returns>
    public byte GetRoutePrefixStartAt(int routeIndex)
    {
        ReadOnlySpan<byte> route = RouterLayout.GetRoute(bytes, routeIndex);
        return RouterLayout.ReadRoutePrefixStart(route);
    }

    /// <summary>
    /// Reads the inclusive ending prefix byte from a physical route slot.<br/>
    /// Count and aggregate walkers use this to preserve direct route-slot semantics without performing ordered prefix lookups.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based route slot index.<br/></param>
    /// <returns>The inclusive prefix end byte stored in the route slot.<br/></returns>
    public byte GetRoutePrefixEndAt(int routeIndex)
    {
        ReadOnlySpan<byte> route = RouterLayout.GetRoute(bytes, routeIndex);
        return RouterLayout.ReadRoutePrefixEnd(route);
    }

    /// <summary>
    /// Reads the persisted stem bytes for one compressed multi-byte route without allocating or decoding a synthetic key.<br/>
    /// The returned span aliases the router page and is valid only while this reader's source bytes remain valid.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based compressed route slot index.<br/></param>
    /// <returns>The exact persisted stem bytes preceding the route's final-byte interval.<br/></returns>
    public ReadOnlySpan<byte> GetMultiByteRouteStemAt(int routeIndex)
    {
        int prefixByteCount = PrefixByteCount;
        if (prefixByteCount <= 1)
            throw new InvalidOperationException("A one-byte router does not contain multi-byte route stems.");

        if ((uint)routeIndex >= RouteCount)
            throw new ArgumentOutOfRangeException(nameof(routeIndex), routeIndex, "The compressed route index is outside the persisted route count.");

        return bytes.Slice(
            RouterLayout.GetMultiByteRouteStemOffset(MaxRouteCount, prefixByteCount, routeIndex),
            prefixByteCount - 1);
    }

    /// <summary>
    /// Classifies one persisted compressed multi-byte route against the active edges of an inclusive variable-key range.<br/>
    /// The caller supplies the route indices selected for the lower and upper keys so boundary targets remain reachable even when a compressed route uses nearest-route fallback.<br/>
    /// Routes wholly outside an active edge are rejected before their child target is queued; interior routes retain neither edge and may traverse their contained subtree directly.<br/>
    /// This method performs no allocation and compares the persisted stem and final-byte interval directly from the router page.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based compressed route slot being classified.<br/></param>
    /// <param name="lowerKey">The inclusive raw lower key.<br/></param>
    /// <param name="upperKey">The inclusive raw upper key.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the index.<br/></param>
    /// <param name="lowerEdge">True when this router may contain the lower range boundary.<br/></param>
    /// <param name="upperEdge">True when this router may contain the upper range boundary.<br/></param>
    /// <param name="lowerRouteIndex">The route selected by the lower key, or `-1` when the lower edge is inactive.<br/></param>
    /// <param name="upperRouteIndex">The route selected by the upper key, or `-1` when the upper edge is inactive.<br/></param>
    /// <param name="targetOffset">Receives the persisted child target when the route can intersect the range.<br/></param>
    /// <param name="childLowerEdge">Receives whether the selected child still contains the lower boundary.<br/></param>
    /// <param name="childUpperEdge">Receives whether the selected child still contains the upper boundary.<br/></param>
    /// <returns><see langword="true"/> when the route can intersect the requested range and has a nonzero target; otherwise <see langword="false"/>.<br/></returns>
    public bool TrySelectMultiByteRangeRoute(
        int routeIndex,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxKeyLength,
        bool lowerEdge,
        bool upperEdge,
        int lowerRouteIndex,
        int upperRouteIndex,
        out long targetOffset,
        out bool childLowerEdge,
        out bool childUpperEdge)
    {
        targetOffset = GetRouteTargetAt(routeIndex);
        childLowerEdge = false;
        childUpperEdge = false;
        if (targetOffset == 0)
        {
            return false;
        }

        bool selectedByLower = lowerEdge && routeIndex == lowerRouteIndex;
        bool selectedByUpper = upperEdge && routeIndex == upperRouteIndex;
        int lowToLower = lowerEdge ? CompareMultiByteRouteBoundToKey(routeIndex, highBound: false, lowerKey, maxKeyLength) : 0;
        int highToLower = lowerEdge ? CompareMultiByteRouteBoundToKey(routeIndex, highBound: true, lowerKey, maxKeyLength) : 0;
        int lowToUpper = upperEdge ? CompareMultiByteRouteBoundToKey(routeIndex, highBound: false, upperKey, maxKeyLength) : 0;
        int highToUpper = upperEdge ? CompareMultiByteRouteBoundToKey(routeIndex, highBound: true, upperKey, maxKeyLength) : 0;
        if ((lowerEdge && highToLower < 0 && !selectedByLower) ||
            (upperEdge && lowToUpper > 0 && !selectedByUpper))
        {
            targetOffset = 0;
            return false;
        }

        childLowerEdge = selectedByLower && lowToLower < 0 && highToLower >= 0;
        childUpperEdge = selectedByUpper && lowToUpper <= 0 && highToUpper > 0;
        return true;
    }

    /// <summary>
    /// Compares one compressed route's low or high synthetic key bound against a raw key without materializing the bound.<br/>
    /// Bytes following the route prefix use zero for a low bound and `0xFF` for a high bound, matching the conservative variable-key range containment model.<br/>
    /// Parent bytes are omitted because an active edge already proves equality through <see cref="KeyDepth"/>; only the current compressed segment and suffix can change the result.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based compressed route slot being compared.<br/></param>
    /// <param name="highBound">True to compare the route's high synthetic bound; false to compare its low synthetic bound.<br/></param>
    /// <param name="key">The raw key boundary.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length accepted by the index.<br/></param>
    /// <returns>A negative value when the route bound is below the key, zero when equivalent under padded comparison, or a positive value when above the key.<br/></returns>
    private int CompareMultiByteRouteBoundToKey(int routeIndex, bool highBound, ReadOnlySpan<byte> key, int maxKeyLength)
    {
        int keyDepth = KeyDepth;
        int prefixByteCount = PrefixByteCount;
        int stemLength = prefixByteCount - 1;
        int routeDepth = keyDepth + stemLength;
        int routeLength = routeDepth + 1;
        int compareLength = Math.Max(Math.Max(routeLength, key.Length), maxKeyLength);
        ReadOnlySpan<byte> stem = bytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(MaxRouteCount, prefixByteCount, routeIndex), stemLength);
        byte routePrefix = highBound ? GetRoutePrefixEndAt(routeIndex) : GetRoutePrefixStartAt(routeIndex);
        byte fill = highBound ? byte.MaxValue : byte.MinValue;
        for (int depth = keyDepth; depth < compareLength; depth++)
        {
            byte routeByte;
            if (depth < routeDepth)
            {
                routeByte = stem[depth - keyDepth];
            }
            else if (depth == routeDepth)
            {
                routeByte = routePrefix;
            }
            else
            {
                routeByte = fill;
            }

            byte keyByte = depth < key.Length ? key[depth] : byte.MinValue;
            if (routeByte != keyByte)
            {
                return routeByte < keyByte ? -1 : 1;
            }
        }

        return 0;
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

    private static int FindNearestMultiByteRoute(
        ReadOnlySpan<byte> key,
        int keyDepth,
        ReadOnlySpan<byte> routerBytes,
        int routeCount,
        int maxRouteCount,
        int prefixByteCount,
        int stemLength)
    {
        int previous = 0;
        for (int i = 0; i < routeCount; i++)
        {
            ReadOnlySpan<byte> stem = routerBytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(maxRouteCount, prefixByteCount, i), stemLength);
            int comparison = CompareKeyToStem(key, keyDepth, stem);
            if (comparison < 0)
            {
                return i == 0 ? 0 : previous;
            }

            previous = i;
        }

        return previous;
    }

    private static int CompareKeyToStem(ReadOnlySpan<byte> key, int keyDepth, ReadOnlySpan<byte> stem)
    {
        for (int i = 0; i < stem.Length; i++)
        {
            byte keyByte = GetKeyByteOrZero(key, keyDepth + i);
            byte stemByte = stem[i];
            if (keyByte < stemByte)
            {
                return -1;
            }

            if (keyByte > stemByte)
            {
                return 1;
            }
        }

        return 0;
    }

    private static byte GetKeyByteOrZero(ReadOnlySpan<byte> key, int index)
    {
        return index >= 0 && index < key.Length
            ? key[index]
            : (byte)0;
    }
}
