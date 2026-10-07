using LibraDex.Layouts;

namespace LibraDex.Views;

/// <summary>
/// Provides a writable hot-path view over router page bytes.<br/>
/// The view owns no bytes and performs no file I/O or allocation.<br/>
/// </summary>
internal ref struct RouterWriter
{
    private Span<byte> bytes;

    public RouterWriter(Span<byte> bytes)
    {
        this.bytes = bytes;
    }

    /// <summary>
    /// Initializes a one-byte root router as a fully expanded direct-index router.<br/>
    /// The router uses 256 self-range route slots from birth; each route target offset starts as zero.<br/>
    /// </summary>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.</param>
    public void InitializeRoot(ushort allocationClassId)
    {
        bytes.Clear();
        RouterLayout.WriteMagic(bytes, RouterLayout.Magic);
        RouterLayout.WriteFormatVersion(bytes, RouterLayout.FormatVersion);
        RouterLayout.WriteHeaderSize(bytes, RouterLayout.HeaderSize);
        RouterLayout.WriteFlags(bytes, RouterLayout.DirectIndexFlag);
        RouterLayout.WritePrefixByteCount(bytes, 1);
        RouterLayout.WriteKeyDepth(bytes, 0);
        RouterLayout.WriteRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteMaxRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteAllocationClassId(bytes, allocationClassId);

        for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
        {
            Span<byte> route = RouterLayout.GetRoute(bytes, i);
            byte prefix = (byte)i;
            RouterLayout.WriteRoutePrefixStart(route, prefix);
            RouterLayout.WriteRoutePrefixEnd(route, prefix);
            RouterLayout.WriteRouteTargetOffset(route, 0);
        }
    }

    /// <summary>
    /// Initializes a non-root one-byte router as a fully expanded direct-index router.<br/>
    /// The router uses 256 self-range route slots and allows direct indexing at the supplied key depth.<br/>
    /// </summary>
    /// <param name="keyDepth">The key byte depth where this router begins.</param>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.</param>
    /// <param name="targets">The 256 route target offsets indexed by prefix byte.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="targets"/> does not contain exactly 256 offsets.</exception>
    public void InitializeExpandedOneByte(ushort keyDepth, ushort allocationClassId, ReadOnlySpan<long> targets)
    {
        if (targets.Length != RouterLayout.MaxOneByteRouteCount)
        {
            throw new ArgumentException("Expanded one-byte routers require exactly 256 route targets.", nameof(targets));
        }

        bytes.Clear();
        RouterLayout.WriteMagic(bytes, RouterLayout.Magic);
        RouterLayout.WriteFormatVersion(bytes, RouterLayout.FormatVersion);
        RouterLayout.WriteHeaderSize(bytes, RouterLayout.HeaderSize);
        RouterLayout.WriteFlags(bytes, RouterLayout.DirectIndexFlag);
        RouterLayout.WritePrefixByteCount(bytes, 1);
        RouterLayout.WriteKeyDepth(bytes, keyDepth);
        RouterLayout.WriteRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteMaxRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteAllocationClassId(bytes, allocationClassId);

        for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
        {
            Span<byte> route = RouterLayout.GetRoute(bytes, i);
            byte prefix = (byte)i;
            RouterLayout.WriteRoutePrefixStart(route, prefix);
            RouterLayout.WriteRoutePrefixEnd(route, prefix);
            RouterLayout.WriteRouteTargetOffset(route, targets[i]);
        }
    }

    /// <summary>
    /// Initializes a fully expanded one-byte router whose 256 prefixes share one target.<br/>
    /// This avoids constructing a temporary 256-element target array for uniform fixed-key router chains.<br/>
    /// </summary>
    /// <param name="keyDepth">The encoded key byte depth owned by this router.<br/></param>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.<br/></param>
    /// <param name="targetOffset">The target offset written for every prefix.<br/></param>
    public void InitializeExpandedOneByteUniform(ushort keyDepth, ushort allocationClassId, long targetOffset)
    {
        InitializeExpandedOneByteSplit(keyDepth, allocationClassId, targetOffset, targetOffset, 0);
    }

    /// <summary>
    /// Initializes a fully expanded one-byte router with one contiguous left/right target boundary.<br/>
    /// Prefixes below <paramref name="rightPrefixByte"/> select the left target and later prefixes select the right target without a temporary target array.<br/>
    /// </summary>
    /// <param name="keyDepth">The encoded key byte depth owned by this router.<br/></param>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.<br/></param>
    /// <param name="leftTargetOffset">The target for prefixes below the boundary.<br/></param>
    /// <param name="rightTargetOffset">The target for prefixes at or above the boundary.<br/></param>
    /// <param name="rightPrefixByte">The first prefix routed to <paramref name="rightTargetOffset"/>.<br/></param>
    public void InitializeExpandedOneByteSplit(ushort keyDepth, ushort allocationClassId, long leftTargetOffset, long rightTargetOffset, byte rightPrefixByte)
    {
        bytes.Clear();
        RouterLayout.WriteMagic(bytes, RouterLayout.Magic);
        RouterLayout.WriteFormatVersion(bytes, RouterLayout.FormatVersion);
        RouterLayout.WriteHeaderSize(bytes, RouterLayout.HeaderSize);
        RouterLayout.WriteFlags(bytes, RouterLayout.DirectIndexFlag);
        RouterLayout.WritePrefixByteCount(bytes, 1);
        RouterLayout.WriteKeyDepth(bytes, keyDepth);
        RouterLayout.WriteRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteMaxRouteCount(bytes, RouterLayout.MaxOneByteRouteCount);
        RouterLayout.WriteAllocationClassId(bytes, allocationClassId);

        for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
        {
            Span<byte> route = RouterLayout.GetRoute(bytes, i);
            byte prefix = (byte)i;
            RouterLayout.WriteRoutePrefixStart(route, prefix);
            RouterLayout.WriteRoutePrefixEnd(route, prefix);
            RouterLayout.WriteRouteTargetOffset(route, prefix < rightPrefixByte ? leftTargetOffset : rightTargetOffset);
        }
    }

    /// <summary>
    /// Writes router-arena membership metadata into an already initialized router page.<br/>
    /// The arena base is persisted as a signed delta from this router's own file offset so a whole router block can later relocate without rewriting internal arena metadata.<br/>
    /// </summary>
    /// <param name="arenaBaseDelta">The signed byte delta from this router offset to the arena base offset.</param>
    /// <param name="arenaLength">The total arena byte length; `65536` is persisted as the zero sentinel in the 16-bit length field.</param>
    /// <param name="routerPageSize">The router page size used by the arena.</param>
    /// <param name="routerPageIndex">The zero-based router page index inside the arena.</param>
    /// <param name="routerPageCount">The total router page capacity inside the arena.</param>
    /// <param name="arenaFlags">Optional arena-specific flags.</param>
    public void WriteArenaMetadata(
        int arenaBaseDelta,
        int arenaLength,
        ushort routerPageSize,
        ushort routerPageIndex,
        ushort routerPageCount,
        uint arenaFlags)
    {
        int persistedArenaLength = arenaLength;
        if (arenaLength > ushort.MaxValue + 1)
        {
            if ((arenaFlags & RouterLayout.ArenaLengthFromPageCountFlag) == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arenaLength), arenaLength, "Router arena lengths above 64 KiB require page-count-derived arena length metadata.");
            }

            int pageCountDerivedLength = checked(routerPageSize * routerPageCount);
            if (pageCountDerivedLength != arenaLength)
            {
                throw new ArgumentOutOfRangeException(nameof(arenaLength), arenaLength, "Page-count-derived router arena metadata must match page size times page count.");
            }

            persistedArenaLength = ushort.MaxValue + 1;
        }

        RouterLayout.WriteFlags(bytes, RouterLayout.ReadFlags(bytes) | RouterLayout.ArenaMemberFlag);
        RouterLayout.WriteArenaBaseDelta(bytes, arenaBaseDelta);
        RouterLayout.WriteArenaLength(bytes, persistedArenaLength);
        RouterLayout.WriteArenaRouterPageSize(bytes, routerPageSize);
        RouterLayout.WriteArenaRouterPageIndex(bytes, routerPageIndex);
        RouterLayout.WriteArenaRouterPageCount(bytes, routerPageCount);
        RouterLayout.WriteArenaFlags(bytes, arenaFlags);
    }

    /// <summary>
    /// Initializes a compressed router with an explicit route list.<br/>
    /// This uses the same router page and 10-byte route slots as the root router, but does not set the direct-index flag.<br/>
    /// </summary>
    /// <param name="prefixByteCount">The number of key bytes consumed by this router.</param>
    /// <param name="keyDepth">The key byte depth where this router begins.</param>
    /// <param name="maxRouteCount">The configured maximum route count for this router.</param>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.</param>
    /// <param name="routes">The compressed route ranges to persist.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when route counts or prefix byte counts are invalid.</exception>
    /// <exception cref="ArgumentException">Thrown when a route has an invalid prefix range.</exception>
    public void InitializeCompressed(
        byte prefixByteCount,
        ushort keyDepth,
        ushort maxRouteCount,
        ushort allocationClassId,
        ReadOnlySpan<RouterRouteSnapshot> routes)
    {
        if (prefixByteCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixByteCount), prefixByteCount, "Router prefix byte count must be greater than zero.");
        }

        if (routes.Length > maxRouteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(routes), routes.Length, "Route count cannot exceed the router maximum route count.");
        }

        bytes.Clear();
        RouterLayout.WriteMagic(bytes, RouterLayout.Magic);
        RouterLayout.WriteFormatVersion(bytes, RouterLayout.FormatVersion);
        RouterLayout.WriteHeaderSize(bytes, RouterLayout.HeaderSize);
        RouterLayout.WriteFlags(bytes, 0);
        RouterLayout.WritePrefixByteCount(bytes, prefixByteCount);
        RouterLayout.WriteKeyDepth(bytes, keyDepth);
        RouterLayout.WriteRouteCount(bytes, checked((ushort)routes.Length));
        RouterLayout.WriteMaxRouteCount(bytes, maxRouteCount);
        RouterLayout.WriteAllocationClassId(bytes, allocationClassId);

        for (int i = 0; i < routes.Length; i++)
        {
            RouterRouteSnapshot route = routes[i];
            if (route.PrefixStart > route.PrefixEnd)
            {
                throw new ArgumentException("Route prefix start cannot be greater than prefix end.", nameof(routes));
            }

            if (i > 0)
            {
                RouterRouteSnapshot previous = routes[i - 1];
                if (route.PrefixStart <= previous.PrefixEnd)
                {
                    throw new ArgumentException("Compressed routes must be sorted and non-overlapping.", nameof(routes));
                }
            }

            WriteRoute(i, route.PrefixStart, route.PrefixEnd, route.TargetOffset);
        }
    }

    /// <summary>
    /// Initializes a compressed multi-byte router with route-limited exact stems and final-byte ranges.<br/>
    /// Each route stores the exact bytes for `prefixByteCount - 1` consumed bytes in the compact stem table after the configured route slots.<br/>
    /// The normal route slot start/end bytes remain the inclusive range for the final consumed key byte, preserving the shared route target layout.<br/>
    /// </summary>
    /// <param name="prefixByteCount">The number of consecutive key bytes consumed by this router.</param>
    /// <param name="keyDepth">The first key byte depth consumed by this router.</param>
    /// <param name="maxRouteCount">The configured maximum route count for this router.</param>
    /// <param name="allocationClassId">The allocation class identifier persisted in the router header.</param>
    /// <param name="routes">The multi-byte route definitions to persist.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when route counts, prefix byte counts, or stem storage size are invalid.</exception>
    /// <exception cref="ArgumentException">Thrown when route stems or route ordering are invalid.</exception>
    public void InitializeCompressedMultiByte(
        byte prefixByteCount,
        ushort keyDepth,
        ushort maxRouteCount,
        ushort allocationClassId,
        ReadOnlySpan<RouterMultiByteRouteSnapshot> routes)
    {
        if (prefixByteCount <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixByteCount), prefixByteCount, "Multi-byte routers require at least two consumed prefix bytes.");
        }

        if (routes.Length > maxRouteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(routes), routes.Length, "Route count cannot exceed the router maximum route count.");
        }

        int storageLength = RouterLayout.GetMultiByteRouteStorageLength(maxRouteCount, prefixByteCount);
        if (RouterLayout.RoutesOffset + storageLength > RouterLayout.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouteCount), maxRouteCount, "Multi-byte route stems do not fit in the router page.");
        }

        bytes.Clear();
        RouterLayout.WriteMagic(bytes, RouterLayout.Magic);
        RouterLayout.WriteFormatVersion(bytes, RouterLayout.FormatVersion);
        RouterLayout.WriteHeaderSize(bytes, RouterLayout.HeaderSize);
        RouterLayout.WriteFlags(bytes, 0);
        RouterLayout.WritePrefixByteCount(bytes, prefixByteCount);
        RouterLayout.WriteKeyDepth(bytes, keyDepth);
        RouterLayout.WriteRouteCount(bytes, checked((ushort)routes.Length));
        RouterLayout.WriteMaxRouteCount(bytes, maxRouteCount);
        RouterLayout.WriteAllocationClassId(bytes, allocationClassId);

        int stemLength = prefixByteCount - 1;
        for (int i = 0; i < routes.Length; i++)
        {
            RouterMultiByteRouteSnapshot route = routes[i];
            ReadOnlySpan<byte> stem = route.PrefixStem.Span;
            if (stem.Length != stemLength)
            {
                throw new ArgumentException("Multi-byte route stems must match prefixByteCount - 1.", nameof(routes));
            }

            if (route.PrefixStart > route.PrefixEnd)
            {
                throw new ArgumentException("Route prefix start cannot be greater than prefix end.", nameof(routes));
            }

            if (i > 0)
            {
                RouterMultiByteRouteSnapshot previous = routes[i - 1];
                int stemComparison = previous.PrefixStem.Span.SequenceCompareTo(stem);
                if (stemComparison > 0 ||
                    (stemComparison == 0 && route.PrefixStart <= previous.PrefixEnd))
                {
                    throw new ArgumentException("Multi-byte compressed routes must be sorted and non-overlapping.", nameof(routes));
                }
            }

            WriteRoute(i, route.PrefixStart, route.PrefixEnd, route.TargetOffset);
            stem.CopyTo(bytes.Slice(RouterLayout.GetMultiByteRouteStemOffset(maxRouteCount, prefixByteCount, i), stemLength));
        }
    }

    /// <summary>
    /// Writes a route slot at the supplied route index.<br/>
    /// This helper preserves the shared 10-byte route slot contract used by root, compressed, expanded, and future multi-byte routers.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based route index.</param>
    /// <param name="prefixStart">The inclusive route prefix start byte.</param>
    /// <param name="prefixEnd">The inclusive route prefix end byte.</param>
    /// <param name="targetOffset">The direct target file offset, or zero when unset.</param>
    public void WriteRoute(int routeIndex, byte prefixStart, byte prefixEnd, long targetOffset)
    {
        Span<byte> route = RouterLayout.GetRoute(bytes, routeIndex);
        RouterLayout.WriteRoutePrefixStart(route, prefixStart);
        RouterLayout.WriteRoutePrefixEnd(route, prefixEnd);
        RouterLayout.WriteRouteTargetOffset(route, targetOffset);
    }

    /// <summary>
    /// Repoints an existing route slot while preserving its persisted prefix range and any multi-byte stem bytes.<br/>
    /// This is used when a shelf under an already-routed path grows and only the target offset changes.<br/>
    /// </summary>
    /// <param name="routeIndex">The zero-based route index to repoint.</param>
    /// <param name="targetOffset">The new direct target file offset, or zero when clearing the route.</param>
    public void WriteRouteTarget(int routeIndex, long targetOffset)
    {
        Span<byte> route = RouterLayout.GetRoute(bytes, routeIndex);
        RouterLayout.WriteRouteTargetOffset(route, targetOffset);
    }
}
