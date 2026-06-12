using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

public sealed partial class LibraDexFileSession
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

        return direction == QueryDirection.Ascending
            ? new Scalar8Scalar8RangeReader(BuildScalar8Scalar8RangePlan(rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey, maxRouterHops))
            : new Scalar8Scalar8RangeReader(this, rootRouterOffset, profile, lowerEncodedKey, upperEncodedKey, direction, maxRouterHops);
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
        using RouteVisitedOffsetSet visitedRouters = RouteVisitedOffsetSet.Rent();
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

                    plan.AddShelfRange(targetOffset, ReadScalar8Scalar8ShelfBytes(targetOffset, profile), lowerEncodedKey, upperEncodedKey);
                    continue;
                }

                if (kind != Scalar8Scalar8RouteTargetKind.Router)
                {
                    throw new InvalidDataException("The routed SS8-8 range plan target is not a shelf or router.");
                }

                if (!visitedRouters.Add(targetOffset))
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
                long previousTargetOffset = 0;
                for (int prefix = endPrefix; prefix >= startPrefix; prefix--)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0 || childTargetOffset == previousTargetOffset)
                    {
                        continue;
                    }

                    previousTargetOffset = childTargetOffset;
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
    /// Reads and validates one `FS32-8` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `FS32-8` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadFixed32Scalar8ShelfBytes(long shelfOffset, Fixed32Scalar8Profile profile)
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
    /// Reads and validates one `FS32-16` shelf into an owned byte array for a fixed-shape range reader.<br/>
    /// Fixed shelf projections are stack-only, so the reader retains the returned bytes and recreates lightweight views over them as it advances.<br/>
    /// </summary>
    /// <param name="shelfOffset">The file offset of the shelf to read.</param>
    /// <param name="profile">The fixed shelf profile for the index.</param>
    /// <returns>An owned shelf byte array containing a valid `FS32-16` shelf image.</returns>
    /// <exception cref="InvalidDataException">Thrown when the loaded shelf bytes fail validation.</exception>
    internal byte[] ReadFixed32Scalar16ShelfBytes(long shelfOffset, Fixed32Scalar16Profile profile)
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
}
