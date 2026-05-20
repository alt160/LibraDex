using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SS16-8` range results as a forward-only cursor over encoded 16-byte scalar keys and encoded scalar identities.<br/>
/// The reader keeps routed traversal state and shelf-local slot ranges so callers can stream keys, identities, or full tuples without per-row materialization.<br/>
/// Shelf bytes are retained for the reader lifetime because the fixed shelf projection is stack-only; row access recreates that projection over the retained shelf buffer.<br/>
/// </summary>
public sealed class Scalar16Scalar8RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private byte[][] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private Scalar16Scalar8Profile profile;
    private ulong lowerKeyHigh;
    private ulong lowerKeyLow;
    private ulong upperKeyHigh;
    private ulong upperKeyLow;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private bool traversalComplete = true;
    private bool disposed;

    internal Scalar16Scalar8RangeReader()
    {
        shelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Scalar16Scalar8RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar16Scalar8Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        int maxRouterHops = 8)
        : this()
    {
        this.session = session;
        this.profile = profile;
        this.lowerKeyHigh = lowerKeyHigh;
        this.lowerKeyLow = lowerKeyLow;
        this.upperKeyHigh = upperKeyHigh;
        this.upperKeyLow = upperKeyLow;
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = GetPrefix(lowerKeyHigh, lowerKeyLow, keyDepth: 0);
        byte upperPrefix = GetPrefix(upperKeyHigh, upperKeyLow, keyDepth: 0);
        for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
        {
            long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset != 0)
            {
                PushTarget(targetOffset, maxRouterHops, prefix == lowerPrefix, prefix == upperPrefix);
            }
        }
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from shelf-local slot ranges; tuple bytes are not copied and per-row references are not allocated.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            EnsureAllRangesLoaded();
            return rowCount;
        }
    }

    /// <summary>
    /// Gets the zero-based row ordinal after a successful <see cref="MoveNext"/> call.<br/>
    /// The value is `-1` before the first row and equals <see cref="Count"/> after the reader passes the final row.<br/>
    /// </summary>
    public int Ordinal => ordinal;

    /// <summary>
    /// Gets the encoded high key lane for the current row.<br/>
    /// The value is the first persisted sortable scalar lane used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyHigh => CurrentShelf.ReadKeyHighAt(currentSlotIndex);

    /// <summary>
    /// Gets the encoded low key lane for the current row.<br/>
    /// The value is the second persisted sortable scalar lane used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyLow => CurrentShelf.ReadKeyLowAt(currentSlotIndex);

    /// <summary>
    /// Gets the encoded scalar identity for the current row.<br/>
    /// The value is the persisted sortable scalar representation used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentity => CurrentShelf.ReadIdentityAt(currentSlotIndex);

    /// <summary>
    /// Advances the reader and returns the next encoded identity without a separate current-row property read.<br/>
    /// This is the low-overhead identity-only streaming path for callers that do not need keys or full tuple projection.<br/>
    /// When the method returns <see langword="false"/>, <paramref name="encodedIdentity"/> is set to zero and the reader is positioned after the final row.<br/>
    /// </summary>
    /// <param name="encodedIdentity">Receives the encoded scalar identity when a row is available.</param>
    /// <returns><see langword="true"/> when an identity was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextEncodedIdentity(out ulong encodedIdentity)
    {
        if (!MoveNext())
        {
            encodedIdentity = 0;
            return false;
        }

        Scalar16Scalar8ReadOnly shelf = new(shelves[currentShelfIndex], profile);
        encodedIdentity = shelf.ReadIdentityAt(currentSlotIndex);
        return true;
    }

    /// <summary>
    /// Advances the reader to the next matching encoded key/identity row.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        while (ordinal + 1 >= rowCount && LoadNextShelfRange())
        {
        }

        if (ordinal + 1 >= rowCount)
        {
            ordinal = rowCount;
            currentShelfIndex = shelfCount;
            currentSlotIndex = -1;
            return false;
        }

        if (ordinal < 0)
        {
            currentShelfIndex = 0;
            currentSlotIndex = startSlots[0];
        }
        else
        {
            currentSlotIndex++;
            while (currentShelfIndex < shelfCount && currentSlotIndex >= endSlots[currentShelfIndex])
            {
                currentShelfIndex++;
                if (currentShelfIndex < shelfCount)
                {
                    currentSlotIndex = startSlots[currentShelfIndex];
                }
            }
        }

        ordinal++;
        return true;
    }

    /// <summary>
    /// Skips up to <paramref name="count"/> rows without reading current tuple fields or materializing output.<br/>
    /// The skip advances across shelf-local slot ranges instead of stepping one row at a time.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        while (!traversalComplete && rowCount - ordinal - 1 < count)
        {
            if (!LoadNextShelfRange())
            {
                break;
            }
        }

        if (count <= 0 || ordinal >= rowCount)
        {
            return 0;
        }

        int skipped = Math.Min(count, rowCount - ordinal - 1);
        if (skipped == 0)
        {
            return 0;
        }

        PositionAtOrdinal(ordinal + skipped);
        return skipped;
    }

    /// <summary>
    /// Releases pooled reader arrays and drops retained shelf buffers.<br/>
    /// The reader must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ReturnShelfBuffers();
        ArrayPool<byte[]>.Shared.Return(shelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        if (pendingOffsets is not null)
        {
            ArrayPool<long>.Shared.Return(pendingOffsets, clearArray: false);
        }

        if (pendingHops is not null)
        {
            ArrayPool<int>.Shared.Return(pendingHops, clearArray: false);
        }

        if (pendingFlags is not null)
        {
            ArrayPool<byte>.Shared.Return(pendingFlags, clearArray: false);
        }

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
        pendingFlags = null;
        visitedShelves = null;
        visitedRouters = null;
        session = null;
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private void ReturnShelfBuffers()
    {
        for (int i = 0; i < shelfCount; i++)
        {
            byte[]? shelfBytes = shelves[i];
            if (shelfBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
                shelves[i] = null!;
            }
        }
    }

    private Scalar16Scalar8ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SS16-8 range reader is not positioned on a row.");
            }

            return new Scalar16Scalar8ReadOnly(shelves[currentShelfIndex], profile);
        }
    }

    private void AddShelfRange(byte[] shelfBytes)
    {
        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SS16-8 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(lowerKeyHigh, lowerKeyLow);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && CompareKey(shelf.ReadKeyHighAt(endSlot), shelf.ReadKeyLowAt(endSlot), upperKeyHigh, upperKeyLow) <= 0)
        {
            endSlot++;
        }

        if (endSlot <= startSlot)
        {
            return;
        }

        if (shelfCount == shelves.Length)
        {
            GrowShelves();
        }

        shelves[shelfCount] = shelfBytes;
        startSlots[shelfCount] = startSlot;
        endSlots[shelfCount] = endSlot;
        shelfCount++;
        rowCount = checked(rowCount + endSlot - startSlot);
    }

    private bool LoadNextShelfRange()
    {
        if (traversalComplete)
        {
            return false;
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed SS16-8 range read exceeded the configured router hop count.");
            }

            Scalar16Scalar8RouteTargetKind kind = localSession.ClassifyScalar16Scalar8RouteTarget(targetOffset);
            if (kind == Scalar16Scalar8RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                AddShelfRange(localSession.ReadScalar16Scalar8ShelfBytes(targetOffset, profile));
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind != Scalar16Scalar8RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed SS16-8 range target is not a shelf or router.");
            }

            if (!localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SS16-8 range router is invalid.");
            }

            byte startPrefix = lowerEdge ? GetPrefix(lowerKeyHigh, lowerKeyLow, router.KeyDepth) : (byte)0;
            byte endPrefix = upperEdge ? GetPrefix(upperKeyHigh, upperKeyLow, router.KeyDepth) : byte.MaxValue;
            long previousTargetOffset = 0;
            for (int prefix = endPrefix; prefix >= startPrefix; prefix--)
            {
                long childTargetOffset = router.FindTarget((byte)prefix);
                if (childTargetOffset == 0 || childTargetOffset == previousTargetOffset)
                {
                    continue;
                }

                previousTargetOffset = childTargetOffset;
                PushTarget(childTargetOffset, remainingHops - 1, lowerEdge && prefix == startPrefix, upperEdge && prefix == endPrefix);
            }
        }

        traversalComplete = true;
        return false;
    }

    private void EnsureAllRangesLoaded()
    {
        while (LoadNextShelfRange())
        {
        }
    }

    private void PushTarget(long offset, int remainingHops, bool lowerEdge, bool upperEdge)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        if (pendingCount == localOffsets.Length)
        {
            int newLength = checked(localOffsets.Length * 2);
            long[] newOffsets = ArrayPool<long>.Shared.Rent(newLength);
            int[] newHops = ArrayPool<int>.Shared.Rent(newLength);
            byte[] newFlags = ArrayPool<byte>.Shared.Rent(newLength);
            localOffsets.AsSpan(0, pendingCount).CopyTo(newOffsets);
            localHops.AsSpan(0, pendingCount).CopyTo(newHops);
            localFlags.AsSpan(0, pendingCount).CopyTo(newFlags);
            ArrayPool<long>.Shared.Return(localOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(localHops, clearArray: false);
            ArrayPool<byte>.Shared.Return(localFlags, clearArray: false);
            pendingOffsets = newOffsets;
            pendingHops = newHops;
            pendingFlags = newFlags;
            localOffsets = newOffsets;
            localHops = newHops;
            localFlags = newFlags;
        }

        localOffsets[pendingCount] = offset;
        localHops[pendingCount] = remainingHops;
        localFlags[pendingCount] = (byte)((lowerEdge ? 1 : 0) | (upperEdge ? 2 : 0));
        pendingCount++;
    }

    private void PopTarget(out long offset, out int remainingHops, out bool lowerEdge, out bool upperEdge)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        pendingCount--;
        offset = localOffsets[pendingCount];
        remainingHops = localHops[pendingCount];
        byte flags = localFlags[pendingCount];
        lowerEdge = (flags & 1) != 0;
        upperEdge = (flags & 2) != 0;
    }

    private void GrowShelves()
    {
        int newLength = checked(shelves.Length * 2);
        byte[][] newShelves = ArrayPool<byte[]>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<byte[]>.Shared.Return(shelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        shelves = newShelves;
        startSlots = newStartSlots;
        endSlots = newEndSlots;
    }

    private void PositionAtOrdinal(int targetOrdinal)
    {
        int remaining = targetOrdinal;
        for (int i = 0; i < shelfCount; i++)
        {
            int length = endSlots[i] - startSlots[i];
            if (remaining < length)
            {
                currentShelfIndex = i;
                currentSlotIndex = startSlots[i] + remaining;
                ordinal = targetOrdinal;
                return;
            }

            remaining -= length;
        }

        ordinal = rowCount;
        currentShelfIndex = shelfCount;
        currentSlotIndex = -1;
    }

    private static int CompareKey(ulong leftHigh, ulong leftLow, ulong rightHigh, ulong rightLow)
    {
        int high = leftHigh.CompareTo(rightHigh);
        return high != 0 ? high : leftLow.CompareTo(rightLow);
    }

    private static byte GetPrefix(ulong high, ulong low, int keyDepth)
    {
        if ((uint)keyDepth >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS16-8 scalar keys expose exactly sixteen routing bytes.");
        }

        ulong lane = keyDepth < 8 ? high : low;
        int laneDepth = keyDepth < 8 ? keyDepth : keyDepth - 8;
        return (byte)(lane >> ((sizeof(ulong) - 1 - laneDepth) * 8));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        }
    }
}
