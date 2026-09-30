using System.Buffers;
using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Opens a forward-only `SS8-8` routed range reader for an inclusive encoded scalar-key range.<br/>
    /// The reader streams encoded keys and encoded identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperEncodedKey"/> sorts before <paramref name="lowerEncodedKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Scalar8Scalar8RangeReader OpenScalar8Scalar8RangeReader(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultScalar8Scalar8MaxRouterHops)
    {
        if (upperEncodedKey < lowerEncodedKey)
        {
            throw new ArgumentException("The upper SS8-8 encoded key must be greater than or equal to the lower encoded key.", nameof(upperEncodedKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SS8-8 range reader maximum router hop count must be positive.");
        }

        // Both directions discover shelves on demand. Count/ordinal callers can still explicitly
        // complete discovery through the reader; opening a stream must not retain every shelf.
        return new Scalar8Scalar8RangeReader(this, rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `SS8-8` identities in an inclusive encoded key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets retain the established range-plan count for exact slot-bound correctness, while fully covered middle targets use count-all metadata traversal.<br/>
    /// This avoids retaining every shelf image for public ordered criteria counts when the range spans multiple root prefixes.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed shelf profile for the index.<br/></param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountScalar8Scalar8IdentityRange(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        byte lowerPrefix = GetFixedReaderScalar8Prefix(lowerEncodedKey, keyDepth: 0);
        byte upperPrefix = GetFixedReaderScalar8Prefix(upperEncodedKey, keyDepth: 0);
        if (lowerPrefix == upperPrefix || lowerPrefix == byte.MaxValue || upperPrefix == byte.MinValue)
        {
            using Scalar8Scalar8RangeReader reader = OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(rootRouterOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SS8-8 range-count root router is invalid.");
        }

        if (!router.HasDirectIndex)
        {
            using Scalar8Scalar8RangeReader reader = OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        long lowerTarget = router.GetRouteTargetAt(lowerPrefix);
        long upperTarget = router.GetRouteTargetAt(upperPrefix);
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            long target = router.GetRouteTargetAt(prefix);
            if (target != 0 && (target == lowerTarget || target == upperTarget))
            {
                using Scalar8Scalar8RangeReader reader = OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
                return reader.Count;
            }
        }

        long count = 0;
        using (Scalar8Scalar8RangeReader lowerReader = OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, lowerEncodedKey, CreateScalar8PrefixUpperBound(lowerPrefix)))
        {
            count += lowerReader.Count;
        }

        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            count += CountScalar8Scalar8RouteTarget(router.GetRouteTargetAt(prefix), profile, visitedTargets, visitedRouters);
        }

        using (Scalar8Scalar8RangeReader upperReader = OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, CreateScalar8PrefixLowerBound(upperPrefix), upperEncodedKey))
        {
            count += upperReader.Count;
        }

        return count;
    }

    /// <summary>
    /// Builds a retained `SS8-8` range plan by walking only routed targets that can contain the inclusive encoded key range.<br/>
    /// The plan owns retained shelf buffers and slot extents, allowing cursor, count, skip, and future bulk-copy consumers to share the same traversal result.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A retained range plan owned by the caller.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperEncodedKey"/> sorts before <paramref name="lowerEncodedKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">Thrown when a routed target is invalid or the hop guard is exceeded.</exception>
    internal Scalar8Scalar8RangePlan BuildScalar8Scalar8RangePlan(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        int maxRouterHops = DefaultScalar8Scalar8MaxRouterHops)
    {
        if (upperEncodedKey < lowerEncodedKey)
        {
            throw new ArgumentException("The upper SS8-8 encoded key must be greater than or equal to the lower encoded key.", nameof(upperEncodedKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SS8-8 range plan maximum router hop count must be positive.");
        }

        Scalar8Scalar8RangePlan plan = new(this, profile);
        long[] pendingOffsets = ArrayPool<long>.Shared.Rent(8);
        int[] pendingHops = ArrayPool<int>.Shared.Rent(8);
        byte[] pendingFlags = ArrayPool<byte>.Shared.Rent(8);
        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedContextSet visitedRouters = RouteVisitedContextSet.Rent();
        int pendingCount = 0;

        try
        {
            byte lowerPrefix = GetFixedReaderScalar8Prefix(lowerEncodedKey, keyDepth: 0);
            byte upperPrefix = GetFixedReaderScalar8Prefix(upperEncodedKey, keyDepth: 0);
            for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
            {
                long targetOffset = FindRouterTarget(rootRouterOffset, (byte)prefix);
                if (targetOffset != 0)
                {
                    PushScalar8Scalar8RangePlanTarget(
                        ref pendingOffsets,
                        ref pendingHops,
                        ref pendingFlags,
                        ref pendingCount,
                        targetOffset,
                        maxRouterHops,
                        lowerEdge: prefix == lowerPrefix,
                        upperEdge: prefix == upperPrefix);
                }
            }

            Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
            while (pendingCount > 0)
            {
                PopScalar8Scalar8RangePlanTarget(
                    pendingOffsets,
                    pendingHops,
                    pendingFlags,
                    ref pendingCount,
                    out long targetOffset,
                    out int remainingHops,
                    out bool lowerEdge,
                    out bool upperEdge);

                if (remainingHops <= 0)
                {
                    throw new InvalidDataException("The routed SS8-8 range plan exceeded the configured router hop count.");
                }

                Scalar8Scalar8RouteTargetKind kind = ClassifyScalar8Scalar8RouteTarget(targetOffset);
                if (kind == Scalar8Scalar8RouteTargetKind.Shelf)
                {
                    if (!visitedShelves.Add(targetOffset))
                    {
                        continue;
                    }

                    byte[] shelfBytes = ReadScalar8Scalar8ShelfBytes(targetOffset, profile);
                    Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
                    plan.AddShelfRange(targetOffset, shelfBytes, lowerEncodedKey, upperEncodedKey);
                    if (shelf.IsValid && shelf.IsDuplicateRun && shelf.DuplicateRunNextOffset != 0)
                    {
                        PushScalar8Scalar8RangePlanTarget(
                            ref pendingOffsets,
                            ref pendingHops,
                            ref pendingFlags,
                            ref pendingCount,
                            shelf.DuplicateRunNextOffset,
                            remainingHops,
                            lowerEdge,
                            upperEdge);
                    }

                    continue;
                }

                if (kind == Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
                {
                    if (!visitedShelves.Add(targetOffset))
                    {
                        continue;
                    }

                    byte[] rootBytes = ReadTerminalIdentityRootBytes(targetOffset);
                    if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8 ||
                        TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Scalar8Scalar8Layout.KeySize)
                    {
                        throw new InvalidDataException("The routed SS8-8 range plan terminal identity root shape is invalid.");
                    }

                    ulong encodedKey = BinaryPrimitives.ReadUInt64BigEndian(
                        rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar8Scalar8Layout.KeySize));
                    if (encodedKey < lowerEncodedKey || encodedKey > upperEncodedKey)
                    {
                        continue;
                    }

                    int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                    long identityShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
                    while (identityShelfOffset != 0)
                    {
                        if (!visitedShelves.Add(identityShelfOffset))
                        {
                            break;
                        }

                        byte[] identityShelfBytes = ReadTerminalIdentity8ShelfBytes(identityShelfOffset, shelfExtentSize);
                        long nextOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(identityShelfBytes);
                        plan.AddTerminalIdentityShelfRange(targetOffset, identityShelfOffset, identityShelfBytes, encodedKey);
                        identityShelfOffset = nextOffset;
                    }

                    continue;
                }

                if (kind != Scalar8Scalar8RouteTargetKind.Router)
                {
                    throw new InvalidDataException("The routed SS8-8 range plan target is not a shelf, terminal identity root, or router.");
                }

                if (!visitedRouters.Add(targetOffset, lowerEdge, upperEdge))
                {
                    continue;
                }

                ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
                RouterReader router = new(routerBytes);
                if (!router.IsValid)
                {
                    throw new InvalidDataException("The routed SS8-8 range plan router is invalid.");
                }

                byte startPrefix = lowerEdge ? GetFixedReaderScalar8Prefix(lowerEncodedKey, router.KeyDepth) : (byte)0;
                byte endPrefix = upperEdge ? GetFixedReaderScalar8Prefix(upperEncodedKey, router.KeyDepth) : byte.MaxValue;
                for (int prefix = endPrefix; prefix >= startPrefix; prefix--)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0)
                    {
                        continue;
                    }

                    PushScalar8Scalar8RangePlanTarget(
                        ref pendingOffsets,
                        ref pendingHops,
                        ref pendingFlags,
                        ref pendingCount,
                        childTargetOffset,
                        remainingHops - 1,
                        lowerEdge && prefix == startPrefix,
                        upperEdge && prefix == endPrefix);
                }
            }

            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(pendingOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(pendingHops, clearArray: false);
            ArrayPool<byte>.Shared.Return(pendingFlags, clearArray: false);
        }
    }

    /// <summary>
    /// Reads and validates one `SS8-8` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `SS8-8` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadScalar8Scalar8ShelfBytes(long shelfOffset, Scalar8Scalar8Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        if (durabilityBatchActive)
            ReadScalar8Scalar8ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).CopyTo(shelfBytes);
        else
            kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed SS8-8 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Opens a forward-only `SS16-8` routed range reader for an inclusive encoded scalar-key range.<br/>
    /// The reader streams encoded key lanes and encoded identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lowerKeyHigh">The inclusive lower encoded sortable key high lane.</param>
    /// <param name="lowerKeyLow">The inclusive lower encoded sortable key low lane.</param>
    /// <param name="upperKeyHigh">The inclusive upper encoded sortable key high lane.</param>
    /// <param name="upperKeyLow">The inclusive upper encoded sortable key low lane.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key sorts before the lower key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Scalar16Scalar8RangeReader OpenScalar16Scalar8RangeReader(
        long rootRouterOffset,
        Scalar16Scalar8Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultScalar16Scalar8MaxRouterHops)
    {
        if (CompareFixedReaderScalar16Key(upperKeyHigh, upperKeyLow, lowerKeyHigh, lowerKeyLow) < 0)
        {
            throw new ArgumentException("The upper SS16-8 encoded key must be greater than or equal to the lower encoded key.", nameof(upperKeyHigh));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SS16-8 range reader maximum router hop count must be positive.");
        }

        return new Scalar16Scalar8RangeReader(this, rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `SS16-8` identities in an inclusive encoded key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets use the existing range reader for exact two-lane key bounds, while fully covered middle targets use route-target count metadata and session-local aggregate caching.<br/>
    /// This keeps public ordered criteria counts from materializing identity rows when the range spans multiple root prefixes.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed shelf profile for the index.<br/></param>
    /// <param name="lowerKeyHigh">The inclusive lower encoded sortable key high lane.<br/></param>
    /// <param name="lowerKeyLow">The inclusive lower encoded sortable key low lane.<br/></param>
    /// <param name="upperKeyHigh">The inclusive upper encoded sortable key high lane.<br/></param>
    /// <param name="upperKeyLow">The inclusive upper encoded sortable key low lane.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountScalar16Scalar8IdentityRange(
        long rootRouterOffset,
        Scalar16Scalar8Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow)
    {
        if (CompareFixedReaderScalar16Key(lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow) > 0)
        {
            return 0;
        }

        byte lowerPrefix = GetFixedReaderScalar16Prefix(lowerKeyHigh, lowerKeyLow, keyDepth: 0);
        byte upperPrefix = GetFixedReaderScalar16Prefix(upperKeyHigh, upperKeyLow, keyDepth: 0);
        if (lowerPrefix == upperPrefix || lowerPrefix == byte.MaxValue || upperPrefix == byte.MinValue)
        {
            using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
            return reader.Count;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(rootRouterOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SS16-8 range-count root router is invalid.");
        }

        if (!router.HasDirectIndex)
        {
            using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
            return reader.Count;
        }

        long lowerTarget = router.GetRouteTargetAt(lowerPrefix);
        long upperTarget = router.GetRouteTargetAt(upperPrefix);
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            long target = router.GetRouteTargetAt(prefix);
            if (target != 0 && (target == lowerTarget || target == upperTarget))
            {
                using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
                return reader.Count;
            }
        }

        long count = 0;
        CreateScalar16PrefixUpperBound(lowerPrefix, out ulong lowerBoundaryHigh, out ulong lowerBoundaryLow);
        using (Scalar16Scalar8RangeReader lowerReader = OpenScalar16Scalar8RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, lowerBoundaryHigh, lowerBoundaryLow))
        {
            count += lowerReader.Count;
        }

        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            count += CountOrdinaryFixedScalarRouteTarget(
                router.GetRouteTargetAt(prefix),
                profile.MaxItemCount,
                profile.SlotRegionOffset,
                Scalar16Scalar8Layout.Magic,
                "SS16-8",
                visitedTargets,
                visitedRouters);
        }

        CreateScalar16PrefixLowerBound(upperPrefix, out ulong upperBoundaryHigh, out ulong upperBoundaryLow);
        using (Scalar16Scalar8RangeReader upperReader = OpenScalar16Scalar8RangeReader(rootRouterOffset, profile, upperBoundaryHigh, upperBoundaryLow, upperKeyHigh, upperKeyLow))
        {
            count += upperReader.Count;
        }

        return count;
    }

    /// <summary>
    /// Opens a forward-only `SS8-16` routed range reader for an inclusive encoded scalar-key range.<br/>
    /// The reader streams encoded keys and encoded 16-byte identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="upperEncodedKey"/> sorts before <paramref name="lowerEncodedKey"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Scalar8Scalar16RangeReader OpenScalar8Scalar16RangeReader(
        long rootRouterOffset,
        Scalar8Scalar16Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultScalar8Scalar16MaxRouterHops)
    {
        if (upperEncodedKey < lowerEncodedKey)
        {
            throw new ArgumentException("The upper SS8-16 encoded key must be greater than or equal to the lower encoded key.", nameof(upperEncodedKey));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SS8-16 range reader maximum router hop count must be positive.");
        }

        return new Scalar8Scalar16RangeReader(this, rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `SS8-16` identities in an inclusive encoded key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets use the existing range reader for exact slot-bound correctness, while fully covered middle targets use ordinary fixed-scalar route metadata and volatile aggregate caching.<br/>
    /// This mirrors the `SS8-8` count primitive for widened identities without adding persisted route counters.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed shelf profile for the index.<br/></param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded sortable key.<br/></param>
    /// <param name="upperEncodedKey">The inclusive upper encoded sortable key.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountScalar8Scalar16IdentityRange(
        long rootRouterOffset,
        Scalar8Scalar16Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey)
    {
        if (lowerEncodedKey > upperEncodedKey)
        {
            return 0;
        }

        byte lowerPrefix = GetFixedReaderScalar8Prefix(lowerEncodedKey, keyDepth: 0);
        byte upperPrefix = GetFixedReaderScalar8Prefix(upperEncodedKey, keyDepth: 0);
        if (lowerPrefix == upperPrefix || lowerPrefix == byte.MaxValue || upperPrefix == byte.MinValue)
        {
            using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(rootRouterOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SS8-16 range-count root router is invalid.");
        }

        if (!router.HasDirectIndex)
        {
            using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
            return reader.Count;
        }

        long lowerTarget = router.GetRouteTargetAt(lowerPrefix);
        long upperTarget = router.GetRouteTargetAt(upperPrefix);
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            long target = router.GetRouteTargetAt(prefix);
            if (target != 0 && (target == lowerTarget || target == upperTarget))
            {
                using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey);
                return reader.Count;
            }
        }

        long count = 0;
        using (Scalar8Scalar16RangeReader lowerReader = OpenScalar8Scalar16RangeReader(rootRouterOffset, profile, lowerEncodedKey, CreateScalar8PrefixUpperBound(lowerPrefix)))
        {
            count += lowerReader.Count;
        }

        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            count += CountOrdinaryFixedScalarRouteTarget(
                router.GetRouteTargetAt(prefix),
                profile.MaxItemCount,
                profile.SlotRegionOffset,
                Scalar8Scalar16Layout.Magic,
                "SS8-16",
                visitedTargets,
                visitedRouters);
        }

        using (Scalar8Scalar16RangeReader upperReader = OpenScalar8Scalar16RangeReader(rootRouterOffset, profile, CreateScalar8PrefixLowerBound(upperPrefix), upperEncodedKey))
        {
            count += upperReader.Count;
        }

        return count;
    }

    /// <summary>
    /// Reads and validates one `SS16-8` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `SS16-8` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadScalar16Scalar8ShelfBytes(long shelfOffset, Scalar16Scalar8Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        if (durabilityBatchActive)
            ReadScalar16Scalar8ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).CopyTo(shelfBytes);
        else
            kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed SS16-8 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Reads and validates one `SS8-16` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `SS8-16` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadScalar8Scalar16ShelfBytes(long shelfOffset, Scalar8Scalar16Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        if (durabilityBatchActive)
            ReadScalar8Scalar16ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).CopyTo(shelfBytes);
        else
            kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed SS8-16 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Opens a forward-only `SS16-16` routed range reader for an inclusive encoded scalar-key range.<br/>
    /// The reader streams encoded key lanes and encoded 16-byte identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lowerKeyHigh">The inclusive lower encoded sortable key high lane.</param>
    /// <param name="lowerKeyLow">The inclusive lower encoded sortable key low lane.</param>
    /// <param name="upperKeyHigh">The inclusive upper encoded sortable key high lane.</param>
    /// <param name="upperKeyLow">The inclusive upper encoded sortable key low lane.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key sorts before the lower key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Scalar16Scalar16RangeReader OpenScalar16Scalar16RangeReader(
        long rootRouterOffset,
        Scalar16Scalar16Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultScalar16Scalar16MaxRouterHops)
    {
        if (CompareFixedReaderScalar16Key(upperKeyHigh, upperKeyLow, lowerKeyHigh, lowerKeyLow) < 0)
        {
            throw new ArgumentException("The upper SS16-16 encoded key must be greater than or equal to the lower encoded key.", nameof(upperKeyHigh));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The SS16-16 range reader maximum router hop count must be positive.");
        }

        return new Scalar16Scalar16RangeReader(this, rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `SS16-16` identities in an inclusive encoded key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets keep the range reader for exact two-lane key comparison, while fully covered middle targets use ordinary fixed-scalar route metadata and volatile aggregate caching.<br/>
    /// This gives widened-key/widened-identity public counts the same physical route-count behavior as the narrower scalar shapes.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed shelf profile for the index.<br/></param>
    /// <param name="lowerKeyHigh">The inclusive lower encoded sortable key high lane.<br/></param>
    /// <param name="lowerKeyLow">The inclusive lower encoded sortable key low lane.<br/></param>
    /// <param name="upperKeyHigh">The inclusive upper encoded sortable key high lane.<br/></param>
    /// <param name="upperKeyLow">The inclusive upper encoded sortable key low lane.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountScalar16Scalar16IdentityRange(
        long rootRouterOffset,
        Scalar16Scalar16Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow)
    {
        if (CompareFixedReaderScalar16Key(lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow) > 0)
        {
            return 0;
        }

        byte lowerPrefix = GetFixedReaderScalar16Prefix(lowerKeyHigh, lowerKeyLow, keyDepth: 0);
        byte upperPrefix = GetFixedReaderScalar16Prefix(upperKeyHigh, upperKeyLow, keyDepth: 0);
        if (lowerPrefix == upperPrefix || lowerPrefix == byte.MaxValue || upperPrefix == byte.MinValue)
        {
            using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
            return reader.Count;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(rootRouterOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed SS16-16 range-count root router is invalid.");
        }

        if (!router.HasDirectIndex)
        {
            using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
            return reader.Count;
        }

        long lowerTarget = router.GetRouteTargetAt(lowerPrefix);
        long upperTarget = router.GetRouteTargetAt(upperPrefix);
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            long target = router.GetRouteTargetAt(prefix);
            if (target != 0 && (target == lowerTarget || target == upperTarget))
            {
                using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, upperKeyHigh, upperKeyLow);
                return reader.Count;
            }
        }

        long count = 0;
        CreateScalar16PrefixUpperBound(lowerPrefix, out ulong lowerBoundaryHigh, out ulong lowerBoundaryLow);
        using (Scalar16Scalar16RangeReader lowerReader = OpenScalar16Scalar16RangeReader(rootRouterOffset, profile, lowerKeyHigh, lowerKeyLow, lowerBoundaryHigh, lowerBoundaryLow))
        {
            count += lowerReader.Count;
        }

        HashSet<long> visitedTargets = new();
        HashSet<long> visitedRouters = new();
        for (int prefix = lowerPrefix + 1; prefix < upperPrefix; prefix++)
        {
            count += CountOrdinaryFixedScalarRouteTarget(
                router.GetRouteTargetAt(prefix),
                profile.MaxItemCount,
                profile.SlotRegionOffset,
                Scalar16Scalar16Layout.Magic,
                "SS16-16",
                visitedTargets,
                visitedRouters);
        }

        CreateScalar16PrefixLowerBound(upperPrefix, out ulong upperBoundaryHigh, out ulong upperBoundaryLow);
        using (Scalar16Scalar16RangeReader upperReader = OpenScalar16Scalar16RangeReader(rootRouterOffset, profile, upperBoundaryHigh, upperBoundaryLow, upperKeyHigh, upperKeyLow))
        {
            count += upperReader.Count;
        }

        return count;
    }

    /// <summary>
    /// Reads and validates one `SS16-16` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `SS16-16` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadScalar16Scalar16ShelfBytes(long shelfOffset, Scalar16Scalar16Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        if (durabilityBatchActive)
            ReadScalar16Scalar16ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).CopyTo(shelfBytes);
        else
            kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Scalar16Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed SS16-16 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Opens a forward-only `FS32-8` routed range reader for an inclusive encoded fixed-key range.<br/>
    /// The reader streams encoded 32-byte key lanes and encoded identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.</param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.</param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.</param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.</param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.</param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.</param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.</param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key sorts before the lower key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Fixed32Scalar8RangeReader OpenFixed32Scalar8RangeReader(
        long rootRouterOffset,
        Fixed32Scalar8Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultFixed32Scalar8MaxRouterHops)
    {
        if (CompareFixedReaderFixed32Key(upper0, upper1, upper2, upper3, lower0, lower1, lower2, lower3) < 0)
        {
            throw new ArgumentException("The upper FS32-8 encoded key must be greater than or equal to the lower encoded key.", nameof(upper0));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The FS32-8 range reader maximum router hop count must be positive.");
        }

        return new Fixed32Scalar8RangeReader(this, rootRouterOffset, profile, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `FS32-8` identities in an inclusive encoded fixed32 key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets use the existing fixed32 range reader for exact four-lane comparison, while fully covered middle targets use free route-target aggregate counts.<br/>
    /// This avoids materializing identity rows for public ordered criteria counts over broad fixed32 extents.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed32 shelf profile for the index.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountFixed32Scalar8IdentityRange(
        long rootRouterOffset,
        Fixed32Scalar8Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3)
    {
        if (CompareFixedReaderFixed32Key(lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3) > 0)
        {
            return 0;
        }

        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedContextSet visitedRouters = RouteVisitedContextSet.Rent();
        return CountFixed32Scalar8IdentityRangeFromRouter(
            rootRouterOffset,
            rootRouterOffset,
            profile,
            lower0,
            lower1,
            lower2,
            lower3,
            upper0,
            upper1,
            upper2,
            upper3,
            lowerEdge: true,
            upperEdge: true,
            visitedShelves,
            visitedRouters);
    }

    /// <summary>
    /// Counts `FS32-8` identities below one router for an inclusive encoded fixed32 range.<br/>
    /// Fully-contained child routes reuse ordinary route-target count metadata, while boundary routes recurse or inspect only the affected shelf slots.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to count below.<br/></param>
    /// <param name="profile">The fixed32 scalar-8 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether this router is still on the lower range boundary.<br/></param>
    /// <param name="upperEdge">Whether this router is still on the upper range boundary.<br/></param>
    /// <param name="visitedTargets">The shelf offsets already counted by the current traversal.<br/></param>
    /// <param name="visitedRouters">The router offsets already counted by the current traversal.<br/></param>
    /// <returns>The number of `FS32-8` identities matched below the router.<br/></returns>
    private long CountFixed32Scalar8IdentityRangeFromRouter(
        long rootRouterOffset,
        long routerOffset,
        Fixed32Scalar8Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset, lowerEdge, upperEdge))
        {
            return 0;
        }

        if (TryGetPromotedDirectRouterKeyDepth(routerOffset, out ushort promotedKeyDepth))
        {
            return CountFixed32Scalar8IdentityRangeFromDirectRouter(
                rootRouterOffset,
                routerOffset,
                promotedKeyDepth,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed FS32-8 range-count router is invalid.");
        }

        int keyDepth = router.KeyDepth;
        byte startPrefix = lowerEdge ? GetFixedReaderFixed32Prefix(lower0, lower1, lower2, lower3, keyDepth) : byte.MinValue;
        byte endPrefix = upperEdge ? GetFixedReaderFixed32Prefix(upper0, upper1, upper2, upper3, keyDepth) : byte.MaxValue;
        long count = 0;
        if (router.HasDirectIndex)
        {
            _ = TryGetDirectRouterKeyDepth(routerOffset, out ushort directKeyDepth);
            return CountFixed32Scalar8IdentityRangeFromDirectRouter(
                rootRouterOffset,
                routerOffset,
                directKeyDepth,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            byte routeStart = router.GetRoutePrefixStartAt(routeIndex);
            byte routeEnd = router.GetRoutePrefixEndAt(routeIndex);
            if (routeEnd < startPrefix || routeStart > endPrefix)
            {
                continue;
            }

            byte overlapStart = routeStart < startPrefix ? startPrefix : routeStart;
            byte overlapEnd = routeEnd > endPrefix ? endPrefix : routeEnd;
            bool singlePrefixRun = overlapStart == overlapEnd;
            count += CountFixed32Scalar8IdentityRangeTarget(
                rootRouterOffset,
                router.GetRouteTargetAt(routeIndex),
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                singlePrefixRun && lowerEdge && overlapStart == startPrefix,
                singlePrefixRun && upperEdge && overlapEnd == endPrefix,
                visitedShelves,
                visitedRouters);
        }

        return count;
    }

    /// <summary>
    /// Counts an `FS32-8` range below one already-promoted direct router without copying or reparsing its persisted 4 KiB page.<br/>
    /// Contiguous prefixes that share one durable target are collapsed into one recursive visit while preserving the same clipped edge flags used by the range reader.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The owning index root used by the immutable shelf cache.<br/></param>
    /// <param name="routerOffset">The promoted direct router offset.<br/></param>
    /// <param name="keyDepth">The promoted router key depth.<br/></param>
    /// <param name="profile">The fixed32 scalar-8 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether the lower range bound still constrains this router.<br/></param>
    /// <param name="upperEdge">Whether the upper range bound still constrains this router.<br/></param>
    /// <param name="visitedShelves">The pooled set of physical shelves already counted.<br/></param>
    /// <param name="visitedRouters">The pooled set of router/edge contexts already visited.<br/></param>
    /// <returns>The exact number of matching identities below the direct router.<br/></returns>
    private long CountFixed32Scalar8IdentityRangeFromDirectRouter(
        long rootRouterOffset,
        long routerOffset,
        ushort keyDepth,
        Fixed32Scalar8Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        byte startPrefix = lowerEdge ? GetFixedReaderFixed32Prefix(lower0, lower1, lower2, lower3, keyDepth) : byte.MinValue;
        byte endPrefix = upperEdge ? GetFixedReaderFixed32Prefix(upper0, upper1, upper2, upper3, keyDepth) : byte.MaxValue;
        long count = 0;
        int prefix = startPrefix;
        while (prefix <= endPrefix)
        {
            long targetOffset = FindRouterTarget(routerOffset, (byte)prefix);
            if (targetOffset == 0)
            {
                prefix++;
                continue;
            }

            _ = TryGetDirectRouterTargetPrefixRange(routerOffset, (byte)prefix, targetOffset, out byte targetStart, out byte targetEnd);
            int runStart = Math.Max(prefix, targetStart);
            int runEnd = Math.Min(endPrefix, targetEnd);
            bool singlePrefixRun = runStart == runEnd;
            count += CountFixed32Scalar8IdentityRangeTarget(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                singlePrefixRun && lowerEdge && runStart == startPrefix,
                singlePrefixRun && upperEdge && runEnd == endPrefix,
                visitedShelves,
                visitedRouters);
            prefix = runEnd + 1;
        }

        return count;
    }

    /// <summary>
    /// Counts one `FS32-8` range target after deciding whether the target is fully contained or range-boundary limited.<br/>
    /// Contained targets use the cached ordinary route-target aggregate; boundary shelves count matching key slots only.<br/>
    /// </summary>
    /// <param name="targetOffset">The routed target offset, or zero when the route is empty.<br/></param>
    /// <param name="profile">The fixed32 scalar-8 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether the lower range boundary still applies to this target.<br/></param>
    /// <param name="upperEdge">Whether the upper range boundary still applies to this target.<br/></param>
    /// <param name="visitedTargets">The shelf offsets already counted by the current traversal.<br/></param>
    /// <param name="visitedRouters">The router offsets already counted by the current traversal.<br/></param>
    /// <returns>The count of matching `FS32-8` identities under the route target.<br/></returns>
    private long CountFixed32Scalar8IdentityRangeTarget(
        long rootRouterOffset,
        long targetOffset,
        Fixed32Scalar8Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        if (targetOffset == 0)
        {
            return 0;
        }

        if (TryGetFixed32Scalar8ReadShelf(rootRouterOffset, targetOffset, out byte[]? cachedShelfBytes))
        {
            if (!visitedShelves.Add(targetOffset))
            {
                return 0;
            }

            Fixed32Scalar8ReadOnly cachedShelf = new(cachedShelfBytes.AsSpan(0, profile.ShelfExtentSize), profile);
            return CountFixed32Scalar8ShelfKeyRangeNarrow(cachedShelf, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
        }

        if (TryGetPromotedDirectRouterKeyDepth(targetOffset, out _))
        {
            return CountFixed32Scalar8IdentityRangeFromRouter(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        if (magic == RouterLayout.Magic)
        {
            return CountFixed32Scalar8IdentityRangeFromRouter(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        if (magic != Fixed32Scalar8Layout.Magic)
        {
            throw new InvalidDataException($"FS32-8 range-count target at offset {targetOffset} does not contain a recognized LibraDex structure.");
        }

        if (!visitedShelves.Add(targetOffset))
        {
            return 0;
        }

        byte[] shelfBytes = ReadFixed32Scalar8ShelfBytes(rootRouterOffset, targetOffset, profile);
        Fixed32Scalar8ReadOnly shelf = new(shelfBytes.AsSpan(0, profile.ShelfExtentSize), profile);
        return CountFixed32Scalar8ShelfKeyRangeNarrow(shelf, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }

    /// <summary>
    /// Counts matching `FS32-8` slots inside one boundary shelf without copying identities.<br/>
    /// The shelf is searched by lower-bound key and then walked only until the upper key is exceeded.<br/>
    /// </summary>
    /// <param name="shelf">The validated immutable shelf image to inspect.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <returns>The number of shelf slots whose keys are inside the range.<br/></returns>
    private long CountFixed32Scalar8ShelfKeyRangeNarrow(
        Fixed32Scalar8ReadOnly shelf,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3)
    {
        int slotIndex = shelf.LowerBoundKey(lower0, lower1, lower2, lower3);
        int count = 0;
        for (int i = slotIndex; i < shelf.ItemCount; i++)
        {
            ulong key0 = shelf.ReadKeyPart0At(i);
            ulong key1 = shelf.ReadKeyPart1At(i);
            ulong key2 = shelf.ReadKeyPart2At(i);
            ulong key3 = shelf.ReadKeyPart3At(i);
            if (CompareFixedReaderFixed32Key(key0, key1, key2, key3, upper0, upper1, upper2, upper3) > 0)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private byte[] ReadFixed32Scalar8ShelfBytes(long shelfOffset, Fixed32Scalar8Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed FS32-8 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Reads and validates one `FS32-8` shelf through the session-owned immutable read cache.<br/>
    /// Fixed shelf projections are stack-only, so readers borrow retained bytes and recreate lightweight views over them as they advance.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>A borrowed immutable shelf byte array containing a valid `FS32-8` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadFixed32Scalar8ShelfBytes(long indexRootOffset, long shelfOffset, Fixed32Scalar8Profile profile)
    {
        if (durabilityBatchActive)
            return ReadFixed32Scalar8ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).ToArray();

        int cacheGeneration = fixed32Scalar8ReadCache.Generation;
        if (fixed32Scalar8ReadCache.TryGet(indexRootOffset, shelfOffset, out byte[]? cachedShelfBytes))
        {
            return cachedShelfBytes;
        }

        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed FS32-8 shelf bytes are invalid.");
        }

        return fixed32Scalar8ReadCache.Store(indexRootOffset, shelfOffset, shelfBytes, cacheGeneration);
    }

    /// <summary>
    /// Tries to borrow one already-retained `FS32-8` shelf without route-target classification or file I/O.<br/>
    /// The owning range reader treats the returned bytes as immutable and clones them before any cursor mutation.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root that owns the shelf.<br/></param>
    /// <param name="shelfOffset">The physical shelf offset to probe.<br/></param>
    /// <param name="shelfBytes">Receives the immutable retained shelf bytes on a cache hit.<br/></param>
    /// <returns><see langword="true"/> when the shelf is already retained for this index.<br/></returns>
    internal bool TryGetFixed32Scalar8ReadShelf(long indexRootOffset, long shelfOffset, out byte[] shelfBytes)
    {
        if (durabilityBatchActive)
        {
            shelfBytes = null!;
            return false;
        }

        return fixed32Scalar8ReadCache.TryGet(indexRootOffset, shelfOffset, out shelfBytes);
    }

    /// <summary>
    /// Opens a forward-only `FS32-16` routed range reader for an inclusive encoded fixed-key range.<br/>
    /// The reader streams encoded 32-byte key lanes and encoded 16-byte identities so callers can choose identity-only, key-only, or full tuple iteration without a materializing adapter.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.</param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.</param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.</param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.</param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.</param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.</param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.</param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.</param>
    /// <param name="maxRouterHops">The maximum number of router pages to follow.</param>
    /// <returns>A cursor positioned before the first matching row.</returns>
    /// <exception cref="ArgumentException">Thrown when the upper key sorts before the lower key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxRouterHops"/> is not positive.</exception>
    internal Fixed32Scalar16RangeReader OpenFixed32Scalar16RangeReader(
        long rootRouterOffset,
        Fixed32Scalar16Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = DefaultFixed32Scalar16MaxRouterHops)
    {
        if (CompareFixedReaderFixed32Key(upper0, upper1, upper2, upper3, lower0, lower1, lower2, lower3) < 0)
        {
            throw new ArgumentException("The upper FS32-16 encoded key must be greater than or equal to the lower encoded key.", nameof(upper0));
        }

        if (maxRouterHops <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The FS32-16 range reader maximum router hop count must be positive.");
        }

        return new Fixed32Scalar16RangeReader(this, rootRouterOffset, profile, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, direction, maxRouterHops);
    }

    /// <summary>
    /// Counts `FS32-16` identities in an inclusive encoded fixed32 key range through a count-only routed path.<br/>
    /// Boundary root-prefix targets use the existing fixed32 range reader for exact four-lane comparison, while fully covered middle targets use free route-target aggregate counts.<br/>
    /// This is the widened-identity counterpart to `FS32-8` and keeps count criteria on the same physical route semantics as retrieval.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset for the index.<br/></param>
    /// <param name="profile">The fixed32 shelf profile for the index.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <returns>The number of identities in the encoded key range.<br/></returns>
    internal long CountFixed32Scalar16IdentityRange(
        long rootRouterOffset,
        Fixed32Scalar16Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3)
    {
        if (CompareFixedReaderFixed32Key(lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3) > 0)
        {
            return 0;
        }

        using RouteVisitedOffsetSet visitedShelves = RouteVisitedOffsetSet.Rent();
        using RouteVisitedContextSet visitedRouters = RouteVisitedContextSet.Rent();
        return CountFixed32Scalar16IdentityRangeFromRouter(
            rootRouterOffset,
            rootRouterOffset,
            profile,
            lower0,
            lower1,
            lower2,
            lower3,
            upper0,
            upper1,
            upper2,
            upper3,
            lowerEdge: true,
            upperEdge: true,
            visitedShelves,
            visitedRouters);
    }

    /// <summary>
    /// Counts `FS32-16` identities below one router for an inclusive encoded fixed32 range.<br/>
    /// Fully-contained child routes reuse ordinary route-target count metadata, while boundary routes recurse or inspect only the affected shelf slots.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to count below.<br/></param>
    /// <param name="profile">The fixed32 scalar-16 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether this router is still on the lower range boundary.<br/></param>
    /// <param name="upperEdge">Whether this router is still on the upper range boundary.<br/></param>
    /// <param name="visitedTargets">The shelf offsets already counted by the current traversal.<br/></param>
    /// <param name="visitedRouters">The router offsets already counted by the current traversal.<br/></param>
    /// <returns>The number of `FS32-16` identities matched below the router.<br/></returns>
    private long CountFixed32Scalar16IdentityRangeFromRouter(
        long rootRouterOffset,
        long routerOffset,
        Fixed32Scalar16Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        if (!visitedRouters.Add(routerOffset, lowerEdge, upperEdge))
        {
            return 0;
        }

        if (TryGetPromotedDirectRouterKeyDepth(routerOffset, out ushort promotedKeyDepth))
        {
            return CountFixed32Scalar16IdentityRangeFromDirectRouter(
                rootRouterOffset,
                routerOffset,
                promotedKeyDepth,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
        {
            throw new InvalidDataException("The routed FS32-16 range-count router is invalid.");
        }

        int keyDepth = router.KeyDepth;
        byte startPrefix = lowerEdge ? GetFixedReaderFixed32Prefix(lower0, lower1, lower2, lower3, keyDepth) : byte.MinValue;
        byte endPrefix = upperEdge ? GetFixedReaderFixed32Prefix(upper0, upper1, upper2, upper3, keyDepth) : byte.MaxValue;
        long count = 0;
        if (router.HasDirectIndex)
        {
            _ = TryGetDirectRouterKeyDepth(routerOffset, out ushort directKeyDepth);
            return CountFixed32Scalar16IdentityRangeFromDirectRouter(
                rootRouterOffset,
                routerOffset,
                directKeyDepth,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
        {
            byte routeStart = router.GetRoutePrefixStartAt(routeIndex);
            byte routeEnd = router.GetRoutePrefixEndAt(routeIndex);
            if (routeEnd < startPrefix || routeStart > endPrefix)
            {
                continue;
            }

            byte overlapStart = routeStart < startPrefix ? startPrefix : routeStart;
            byte overlapEnd = routeEnd > endPrefix ? endPrefix : routeEnd;
            bool singlePrefixRun = overlapStart == overlapEnd;
            count += CountFixed32Scalar16IdentityRangeTarget(
                rootRouterOffset,
                router.GetRouteTargetAt(routeIndex),
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                singlePrefixRun && lowerEdge && overlapStart == startPrefix,
                singlePrefixRun && upperEdge && overlapEnd == endPrefix,
                visitedShelves,
                visitedRouters);
        }

        return count;
    }

    /// <summary>
    /// Counts an `FS32-16` range below one already-promoted direct router without copying or reparsing its persisted 4 KiB page.<br/>
    /// Contiguous prefixes that share one durable target are collapsed into one recursive visit while preserving exact lower/upper shelf bounds.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The owning index root used by the immutable shelf cache.<br/></param>
    /// <param name="routerOffset">The promoted direct router offset.<br/></param>
    /// <param name="keyDepth">The promoted router key depth.<br/></param>
    /// <param name="profile">The fixed32 scalar-16 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether the lower range bound still constrains this router.<br/></param>
    /// <param name="upperEdge">Whether the upper range bound still constrains this router.<br/></param>
    /// <param name="visitedShelves">The pooled set of physical shelves already counted.<br/></param>
    /// <param name="visitedRouters">The pooled set of router/edge contexts already visited.<br/></param>
    /// <returns>The exact number of matching identities below the direct router.<br/></returns>
    private long CountFixed32Scalar16IdentityRangeFromDirectRouter(
        long rootRouterOffset,
        long routerOffset,
        ushort keyDepth,
        Fixed32Scalar16Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        byte startPrefix = lowerEdge ? GetFixedReaderFixed32Prefix(lower0, lower1, lower2, lower3, keyDepth) : byte.MinValue;
        byte endPrefix = upperEdge ? GetFixedReaderFixed32Prefix(upper0, upper1, upper2, upper3, keyDepth) : byte.MaxValue;
        long count = 0;
        int prefix = startPrefix;
        while (prefix <= endPrefix)
        {
            long targetOffset = FindRouterTarget(routerOffset, (byte)prefix);
            if (targetOffset == 0)
            {
                prefix++;
                continue;
            }

            _ = TryGetDirectRouterTargetPrefixRange(routerOffset, (byte)prefix, targetOffset, out byte targetStart, out byte targetEnd);
            int runStart = Math.Max(prefix, targetStart);
            int runEnd = Math.Min(endPrefix, targetEnd);
            bool singlePrefixRun = runStart == runEnd;
            count += CountFixed32Scalar16IdentityRangeTarget(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                singlePrefixRun && lowerEdge && runStart == startPrefix,
                singlePrefixRun && upperEdge && runEnd == endPrefix,
                visitedShelves,
                visitedRouters);
            prefix = runEnd + 1;
        }

        return count;
    }

    /// <summary>
    /// Counts one `FS32-16` range target after deciding whether the target is fully contained or range-boundary limited.<br/>
    /// Contained targets use the cached ordinary route-target aggregate; boundary shelves count matching key slots only.<br/>
    /// </summary>
    /// <param name="targetOffset">The routed target offset, or zero when the route is empty.<br/></param>
    /// <param name="profile">The fixed32 scalar-16 shelf profile.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <param name="lowerEdge">Whether the lower range boundary still applies to this target.<br/></param>
    /// <param name="upperEdge">Whether the upper range boundary still applies to this target.<br/></param>
    /// <param name="visitedTargets">The shelf offsets already counted by the current traversal.<br/></param>
    /// <param name="visitedRouters">The router offsets already counted by the current traversal.<br/></param>
    /// <returns>The count of matching `FS32-16` identities under the route target.<br/></returns>
    private long CountFixed32Scalar16IdentityRangeTarget(
        long rootRouterOffset,
        long targetOffset,
        Fixed32Scalar16Profile profile,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3,
        bool lowerEdge,
        bool upperEdge,
        RouteVisitedOffsetSet visitedShelves,
        RouteVisitedContextSet visitedRouters)
    {
        if (targetOffset == 0)
        {
            return 0;
        }

        if (TryGetFixed32Scalar16ReadShelf(rootRouterOffset, targetOffset, out byte[]? cachedShelfBytes))
        {
            if (!visitedShelves.Add(targetOffset))
            {
                return 0;
            }

            Fixed32Scalar16ReadOnly cachedShelf = new(cachedShelfBytes.AsSpan(0, profile.ShelfExtentSize), profile);
            return CountFixed32Scalar16ShelfKeyRangeNarrow(cachedShelf, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
        }

        if (TryGetPromotedDirectRouterKeyDepth(targetOffset, out _))
        {
            return CountFixed32Scalar16IdentityRangeFromRouter(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        kernel.Read(targetOffset, magicBytes);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        if (magic == RouterLayout.Magic)
        {
            return CountFixed32Scalar16IdentityRangeFromRouter(
                rootRouterOffset,
                targetOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                lowerEdge,
                upperEdge,
                visitedShelves,
                visitedRouters);
        }

        if (magic != Fixed32Scalar16Layout.Magic)
        {
            throw new InvalidDataException($"FS32-16 range-count target at offset {targetOffset} does not contain a recognized LibraDex structure.");
        }

        if (!visitedShelves.Add(targetOffset))
        {
            return 0;
        }

        byte[] shelfBytes = ReadFixed32Scalar16ShelfBytes(rootRouterOffset, targetOffset, profile);
        Fixed32Scalar16ReadOnly shelf = new(shelfBytes.AsSpan(0, profile.ShelfExtentSize), profile);
        return CountFixed32Scalar16ShelfKeyRangeNarrow(shelf, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }

    /// <summary>
    /// Counts matching `FS32-16` slots inside one boundary shelf without copying identities.<br/>
    /// The shelf is searched by lower-bound key and then walked only until the upper key is exceeded.<br/>
    /// </summary>
    /// <param name="shelf">The validated immutable shelf image to inspect.<br/></param>
    /// <param name="lower0">The inclusive lower encoded key lane 0.<br/></param>
    /// <param name="lower1">The inclusive lower encoded key lane 1.<br/></param>
    /// <param name="lower2">The inclusive lower encoded key lane 2.<br/></param>
    /// <param name="lower3">The inclusive lower encoded key lane 3.<br/></param>
    /// <param name="upper0">The inclusive upper encoded key lane 0.<br/></param>
    /// <param name="upper1">The inclusive upper encoded key lane 1.<br/></param>
    /// <param name="upper2">The inclusive upper encoded key lane 2.<br/></param>
    /// <param name="upper3">The inclusive upper encoded key lane 3.<br/></param>
    /// <returns>The number of shelf slots whose keys are inside the range.<br/></returns>
    private long CountFixed32Scalar16ShelfKeyRangeNarrow(
        Fixed32Scalar16ReadOnly shelf,
        ulong lower0,
        ulong lower1,
        ulong lower2,
        ulong lower3,
        ulong upper0,
        ulong upper1,
        ulong upper2,
        ulong upper3)
    {
        int slotIndex = shelf.LowerBoundKey(lower0, lower1, lower2, lower3);
        int count = 0;
        for (int i = slotIndex; i < shelf.ItemCount; i++)
        {
            ulong key0 = shelf.ReadKeyPart0At(i);
            ulong key1 = shelf.ReadKeyPart1At(i);
            ulong key2 = shelf.ReadKeyPart2At(i);
            ulong key3 = shelf.ReadKeyPart3At(i);
            if (CompareFixedReaderFixed32Key(key0, key1, key2, key3, upper0, upper1, upper2, upper3) > 0)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private byte[] ReadFixed32Scalar16ShelfBytes(long shelfOffset, Fixed32Scalar16Profile profile)
    {
        byte[] shelfBytes = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Fixed32Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            throw new InvalidDataException("The routed FS32-16 shelf bytes are invalid.");
        }

        return shelfBytes;
    }

    /// <summary>
    /// Reads and validates one `FS32-16` shelf through the session-owned immutable read cache.<br/>
    /// Fixed shelf projections are stack-only, so readers borrow retained bytes and recreate lightweight views over them as they advance.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>A borrowed immutable shelf byte array containing a valid `FS32-16` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadFixed32Scalar16ShelfBytes(long indexRootOffset, long shelfOffset, Fixed32Scalar16Profile profile)
    {
        if (durabilityBatchActive)
            return ReadFixed32Scalar16ShelfBytesForBatch(shelfOffset, profile).AsSpan(0, profile.ShelfExtentSize).ToArray();

        int cacheGeneration = fixed32Scalar16ReadCache.Generation;
        if (fixed32Scalar16ReadCache.TryGet(indexRootOffset, shelfOffset, out byte[]? cachedShelfBytes))
        {
            return cachedShelfBytes;
        }

        byte[] shelfBytes = new byte[profile.ShelfExtentSize];
        kernel.Read(shelfOffset, shelfBytes.AsSpan(0, profile.ShelfExtentSize));
        Fixed32Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed FS32-16 shelf bytes are invalid.");
        }

        return fixed32Scalar16ReadCache.Store(indexRootOffset, shelfOffset, shelfBytes, cacheGeneration);
    }

    /// <summary>
    /// Tries to borrow one already-retained `FS32-16` shelf without route-target classification or file I/O.<br/>
    /// The owning range reader treats the returned bytes as immutable and clones them before any cursor mutation.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root that owns the shelf.<br/></param>
    /// <param name="shelfOffset">The physical shelf offset to probe.<br/></param>
    /// <param name="shelfBytes">Receives the immutable retained shelf bytes on a cache hit.<br/></param>
    /// <returns><see langword="true"/> when the shelf is already retained for this index.<br/></returns>
    internal bool TryGetFixed32Scalar16ReadShelf(long indexRootOffset, long shelfOffset, out byte[] shelfBytes)
    {
        if (durabilityBatchActive)
        {
            shelfBytes = null!;
            return false;
        }

        return fixed32Scalar16ReadCache.TryGet(indexRootOffset, shelfOffset, out shelfBytes);
    }

    private static int CompareFixedReaderScalar16Key(ulong leftHigh, ulong leftLow, ulong rightHigh, ulong rightLow)
    {
        int high = leftHigh.CompareTo(rightHigh);
        return high != 0 ? high : leftLow.CompareTo(rightLow);
    }

    private static int CompareFixedReaderFixed32Key(ulong left0, ulong left1, ulong left2, ulong left3, ulong right0, ulong right1, ulong right2, ulong right3)
    {
        int c = left0.CompareTo(right0);
        if (c != 0) return c;
        c = left1.CompareTo(right1);
        if (c != 0) return c;
        c = left2.CompareTo(right2);
        return c != 0 ? c : left3.CompareTo(right3);
    }

    /// <summary>
    /// Creates the inclusive lower four-lane key bound for one root prefix in an encoded fixed32 key space.<br/>
    /// The prefix owns the high byte of lane zero; every remaining key byte is set to zero so boundary range readers start at the first key in that prefix.<br/>
    /// </summary>
    /// <param name="prefix">The root prefix byte.<br/></param>
    /// <param name="key0">Receives fixed32 key lane zero.<br/></param>
    /// <param name="key1">Receives fixed32 key lane one.<br/></param>
    /// <param name="key2">Receives fixed32 key lane two.<br/></param>
    /// <param name="key3">Receives fixed32 key lane three.<br/></param>
    private static void CreateFixed32PrefixLowerBound(byte prefix, out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        key0 = (ulong)prefix << 56;
        key1 = 0;
        key2 = 0;
        key3 = 0;
    }

    /// <summary>
    /// Creates the inclusive upper four-lane key bound for one root prefix in an encoded fixed32 key space.<br/>
    /// The prefix owns the high byte of lane zero; every remaining key byte is set to one so boundary range readers stop at the last key in that prefix.<br/>
    /// </summary>
    /// <param name="prefix">The root prefix byte.<br/></param>
    /// <param name="key0">Receives fixed32 key lane zero.<br/></param>
    /// <param name="key1">Receives fixed32 key lane one.<br/></param>
    /// <param name="key2">Receives fixed32 key lane two.<br/></param>
    /// <param name="key3">Receives fixed32 key lane three.<br/></param>
    private static void CreateFixed32PrefixUpperBound(byte prefix, out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        key0 = ((ulong)prefix << 56) | 0x00FFFFFFFFFFFFFFUL;
        key1 = ulong.MaxValue;
        key2 = ulong.MaxValue;
        key3 = ulong.MaxValue;
    }

    /// <summary>
    /// Pushes one pending `SS8-8` range-plan route target onto pooled traversal stacks.<br/>
    /// The method grows all stack arrays together so offsets, hop counts, and edge flags retain matching indexes without allocating per target.<br/>
    /// </summary>
    /// <param name="offsets">The pooled offset stack, replaced when capacity grows.</param>
    /// <param name="hops">The pooled remaining-hop stack, replaced when capacity grows.</param>
    /// <param name="flags">The pooled lower/upper edge flag stack, replaced when capacity grows.</param>
    /// <param name="count">The current stack count, incremented after the target is stored.</param>
    /// <param name="offset">The routed shelf or router file offset.</param>
    /// <param name="remainingHops">The remaining router hop budget for this target.</param>
    /// <param name="lowerEdge">Whether this target remains on the lower bound route edge.</param>
    /// <param name="upperEdge">Whether this target remains on the upper bound route edge.</param>
    private static void PushScalar8Scalar8RangePlanTarget(
        ref long[] offsets,
        ref int[] hops,
        ref byte[] flags,
        ref int count,
        long offset,
        int remainingHops,
        bool lowerEdge,
        bool upperEdge)
    {
        if (count == offsets.Length)
        {
            int newLength = checked(offsets.Length * 2);
            long[] newOffsets = ArrayPool<long>.Shared.Rent(newLength);
            int[] newHops = ArrayPool<int>.Shared.Rent(newLength);
            byte[] newFlags = ArrayPool<byte>.Shared.Rent(newLength);
            offsets.AsSpan(0, count).CopyTo(newOffsets);
            hops.AsSpan(0, count).CopyTo(newHops);
            flags.AsSpan(0, count).CopyTo(newFlags);
            ArrayPool<long>.Shared.Return(offsets, clearArray: false);
            ArrayPool<int>.Shared.Return(hops, clearArray: false);
            ArrayPool<byte>.Shared.Return(flags, clearArray: false);
            offsets = newOffsets;
            hops = newHops;
            flags = newFlags;
        }

        offsets[count] = offset;
        hops[count] = remainingHops;
        flags[count] = (byte)((lowerEdge ? 1 : 0) | (upperEdge ? 2 : 0));
        count++;
    }

    /// <summary>
    /// Pops one pending `SS8-8` range-plan route target from pooled traversal stacks.<br/>
    /// Targets are processed in last-in-first-out order so parent router scans can push reversed prefix order and preserve ascending shelf visitation.<br/>
    /// </summary>
    /// <param name="offsets">The pooled offset stack.</param>
    /// <param name="hops">The pooled remaining-hop stack.</param>
    /// <param name="flags">The pooled lower/upper edge flag stack.</param>
    /// <param name="count">The current stack count, decremented before the target is read.</param>
    /// <param name="offset">Receives the routed shelf or router file offset.</param>
    /// <param name="remainingHops">Receives the remaining router hop budget for this target.</param>
    /// <param name="lowerEdge">Receives whether this target remains on the lower bound route edge.</param>
    /// <param name="upperEdge">Receives whether this target remains on the upper bound route edge.</param>
    private static void PopScalar8Scalar8RangePlanTarget(
        long[] offsets,
        int[] hops,
        byte[] flags,
        ref int count,
        out long offset,
        out int remainingHops,
        out bool lowerEdge,
        out bool upperEdge)
    {
        count--;
        offset = offsets[count];
        remainingHops = hops[count];
        byte edgeFlags = flags[count];
        lowerEdge = (edgeFlags & 1) != 0;
        upperEdge = (edgeFlags & 2) != 0;
    }

    /// <summary>
    /// Extracts one persisted prefix byte from an encoded `SS8-8` scalar key for fixed-reader range planning.<br/>
    /// The encoded key is stored in sortable byte order, so depth zero maps to the high byte and depth seven maps to the low byte.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key.</param>
    /// <param name="keyDepth">The zero-based byte depth to extract.</param>
    /// <returns>The persisted key byte at the requested depth.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keyDepth"/> is outside the 8-byte scalar key.</exception>
    private static byte GetFixedReaderScalar8Prefix(ulong encodedKey, int keyDepth)
    {
        if ((uint)keyDepth >= sizeof(ulong))
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS8-8 scalar keys expose exactly eight routing bytes.");
        }

        return (byte)(encodedKey >> ((sizeof(ulong) - 1 - keyDepth) * 8));
    }

    /// <summary>
    /// Extracts one persisted prefix byte from an encoded scalar-16 key for fixed-reader range planning.<br/>
    /// Depths zero through seven read the high lane, and depths eight through fifteen read the low lane, matching the routed scalar-16 reader layout.<br/>
    /// </summary>
    /// <param name="high">The encoded high key lane.<br/></param>
    /// <param name="low">The encoded low key lane.<br/></param>
    /// <param name="keyDepth">The zero-based byte depth to extract.<br/></param>
    /// <returns>The persisted key byte at the requested depth.<br/></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keyDepth"/> is outside the 16-byte scalar key.<br/></exception>
    private static byte GetFixedReaderScalar16Prefix(ulong high, ulong low, int keyDepth)
    {
        if ((uint)keyDepth >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS16 scalar keys expose exactly sixteen routing bytes.");
        }

        return keyDepth < sizeof(ulong)
            ? (byte)(high >> ((sizeof(ulong) - 1 - keyDepth) * 8))
            : (byte)(low >> ((sizeof(ulong) - 1 - (keyDepth - sizeof(ulong))) * 8));
    }

    /// <summary>
    /// Extracts one persisted prefix byte from an encoded fixed32 key for fixed-reader range planning.<br/>
    /// Depths are mapped across the four 64-bit lanes in big-endian sortable order, matching the fixed32 route walker.<br/>
    /// </summary>
    /// <param name="key0">The first encoded fixed32 key lane.<br/></param>
    /// <param name="key1">The second encoded fixed32 key lane.<br/></param>
    /// <param name="key2">The third encoded fixed32 key lane.<br/></param>
    /// <param name="key3">The fourth encoded fixed32 key lane.<br/></param>
    /// <param name="keyDepth">The zero-based byte depth to extract.<br/></param>
    /// <returns>The persisted key byte at the requested depth.<br/></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keyDepth"/> is outside the 32-byte fixed key.<br/></exception>
    private static byte GetFixedReaderFixed32Prefix(ulong key0, ulong key1, ulong key2, ulong key3, int keyDepth)
    {
        if ((uint)keyDepth >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "Fixed32 keys expose exactly thirty-two routing bytes.");
        }

        ulong lane = keyDepth switch
        {
            < 8 => key0,
            < 16 => key1,
            < 24 => key2,
            _ => key3
        };
        int laneDepth = keyDepth & 7;
        return (byte)(lane >> ((sizeof(ulong) - 1 - laneDepth) * 8));
    }
}
