using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SV16` range results as a forward-only, span-based cursor over raw identity bytes.<br/>
/// The reader stores only matching shelf ranges, then advances slot-by-slot on demand so result rows are not materialized into arrays or per-row references.<br/>
/// This avoids `byte[][]` materialization while still giving callers ergonomic row-wise access, skip control, and explicit copy points for identities they choose to keep.<br/>
/// </summary>
public sealed class Scalar16VarIdentityRangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private Scalar16VarIdentityReadOnly[] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private int maxIdentityLength;
    private ulong lowerEncodedKeyHigh;
    private ulong lowerEncodedKeyLow;
    private ulong upperEncodedKeyHigh;
    private ulong upperEncodedKeyLow;
    private bool traversalComplete = true;
    private bool disposed;

    internal Scalar16VarIdentityRangeReader()
    {
        shelves = ArrayPool<Scalar16VarIdentityReadOnly>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Scalar16VarIdentityRangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
        : this()
    {
        this.session = session;
        this.maxIdentityLength = maxIdentityLength;
        this.lowerEncodedKeyHigh = lowerEncodedKeyHigh;
        this.lowerEncodedKeyLow = lowerEncodedKeyLow;
        this.upperEncodedKeyHigh = upperEncodedKeyHigh;
        this.upperEncodedKeyLow = upperEncodedKeyLow;
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(lowerEncodedKeyHigh, lowerEncodedKeyLow, 0);
        byte upperPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(upperEncodedKeyHigh, upperEncodedKeyLow, 0);
        for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
        {
            long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset != 0)
            {
                PushTarget(targetOffset, 8);
            }
        }
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from shelf-local slot ranges; identity bytes are not copied and per-row references are not allocated.<br/>
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
    /// The value is `-1` before the first row and equals <see cref="Count"/> after the reader has passed the final row.<br/>
    /// </summary>
    public int Ordinal => ordinal;

    /// <summary>
    /// Gets the high 8 bytes of the current encoded 16-byte scalar key.<br/>
    /// The caller must call <see cref="MoveNext"/> successfully before reading this property.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyHigh => CurrentShelf.ReadKeyHighAt(currentSlotIndex);

    /// <summary>
    /// Gets the low 8 bytes of the current encoded 16-byte scalar key.<br/>
    /// The caller must call <see cref="MoveNext"/> successfully before reading this property.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyLow => CurrentShelf.ReadKeyLowAt(currentSlotIndex);

    /// <summary>
    /// Gets the raw identity bytes for the current row.<br/>
    /// The returned span is valid until <see cref="MoveNext"/> is called again or the reader is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> CurrentIdentity => CurrentShelf.ReadIdentityAt(currentSlotIndex);

    /// <summary>
    /// Gets the current identity length without forcing the caller to copy identity bytes.<br/>
    /// This is useful for sizing a destination buffer before calling <see cref="CopyCurrentIdentityTo"/>.<br/>
    /// </summary>
    public int CurrentIdentityLength => CurrentIdentity.Length;

    /// <summary>
    /// Advances the reader to the next matching identity row.<br/>
    /// The method returns <see langword="false"/> when no more rows are available.<br/>
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
    /// Skips up to <paramref name="count"/> rows without exposing identity bytes for the skipped rows.<br/>
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

        int targetOrdinal = ordinal + skipped;
        PositionAtOrdinal(targetOrdinal);
        return skipped;
    }

    /// <summary>
    /// Copies the current identity bytes into a caller-owned destination buffer.<br/>
    /// The destination must be at least <see cref="CurrentIdentityLength"/> bytes long.<br/>
    /// </summary>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <returns>The number of bytes copied.</returns>
    public int CopyCurrentIdentityTo(Span<byte> destination)
    {
        ReadOnlySpan<byte> identity = CurrentIdentity;
        identity.CopyTo(destination);
        return identity.Length;
    }

    /// <summary>
    /// Returns the current identity as an owned byte array for callers that explicitly need materialization.<br/>
    /// Prefer <see cref="CurrentIdentity"/> or <see cref="CopyCurrentIdentityTo"/> in hot paths.<br/>
    /// </summary>
    /// <returns>An owned copy of the current identity bytes.</returns>
    public byte[] MaterializeCurrentIdentity()
    {
        return CurrentIdentity.ToArray();
    }

    internal void AddShelfRange(
        byte[] shelfBytes,
        Scalar16VarIdentityProfile profile,
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
        Scalar16VarIdentityReadOnly shelf = new(shelfBytes, profile, validateRecords: false);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SV16 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(lowerEncodedKeyHigh, lowerEncodedKeyLow);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && CompareKeys(shelf.ReadKeyHighAt(endSlot), shelf.ReadKeyLowAt(endSlot), upperEncodedKeyHigh, upperEncodedKeyLow) <= 0)
        {
            endSlot++;
        }

        AddShelfRange(shelf, startSlot, endSlot);
    }

    /// <summary>
    /// Releases pooled reader arrays.<br/>
    /// The reader must not be used after disposal.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<Scalar16VarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
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

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
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

    private Scalar16VarIdentityReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SV16 range reader is not positioned on a row.");
            }

            return shelves[currentShelfIndex];
        }
    }

    private void AddShelfRange(Scalar16VarIdentityReadOnly shelf, int startSlot, int endSlot)
    {
        if (endSlot <= startSlot)
        {
            return;
        }

        if (shelfCount == shelves.Length)
        {
            GrowShelves();
        }

        shelves[shelfCount] = shelf;
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

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        int previousRowCount = rowCount;
        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed SV16 range read exceeded the configured router hop count.");
            }

            Scalar16VarIdentityRouteTargetKind kind = localSession.ClassifyScalar16VarIdentityRouteTarget(targetOffset);
            if (kind == Scalar16VarIdentityRouteTargetKind.Shelf)
            {
                long currentShelfOffset = targetOffset;
                while (currentShelfOffset != 0)
                {
                    if (!localVisitedShelves.Add(currentShelfOffset))
                    {
                        break;
                    }

                    byte[] shelfBytes = localSession.ReadScalar16VarIdentityShelfBytes(currentShelfOffset, maxIdentityLength, out Scalar16VarIdentityProfile profile);
                    AddShelfRange(shelfBytes, profile, lowerEncodedKeyHigh, lowerEncodedKeyLow, upperEncodedKeyHigh, upperEncodedKeyLow);
                    currentShelfOffset = Scalar16VarIdentityLayout.ReadNextShelfOffset(shelfBytes);
                    if (rowCount > previousRowCount)
                    {
                        if (currentShelfOffset != 0)
                        {
                            PushTarget(currentShelfOffset, remainingHops);
                        }

                        return true;
                    }
                }

                continue;
            }

            if (kind != Scalar16VarIdentityRouteTargetKind.Router || !localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SV16 range target router is invalid.");
            }

            byte lowerPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(lowerEncodedKeyHigh, lowerEncodedKeyLow, router.KeyDepth);
            byte upperPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(upperEncodedKeyHigh, upperEncodedKeyLow, router.KeyDepth);
            for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
            {
                long routeTarget = router.FindTarget((byte)prefix);
                if (routeTarget != 0)
                {
                    PushTarget(routeTarget, remainingHops - 1);
                }
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

    private void PushTarget(long offset, int remainingHops)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        if (pendingCount == localOffsets.Length)
        {
            int newLength = checked(localOffsets.Length * 2);
            long[] newOffsets = ArrayPool<long>.Shared.Rent(newLength);
            int[] newHops = ArrayPool<int>.Shared.Rent(newLength);
            localOffsets.AsSpan(0, pendingCount).CopyTo(newOffsets);
            localHops.AsSpan(0, pendingCount).CopyTo(newHops);
            ArrayPool<long>.Shared.Return(localOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(localHops, clearArray: false);
            pendingOffsets = newOffsets;
            pendingHops = newHops;
            localOffsets = newOffsets;
            localHops = newHops;
        }

        localOffsets[pendingCount] = offset;
        localHops[pendingCount] = remainingHops;
        pendingCount++;
    }

    private void PopTarget(out long offset, out int remainingHops)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        pendingCount--;
        offset = localOffsets[pendingCount];
        remainingHops = localHops[pendingCount];
    }

    private void GrowShelves()
    {
        int newLength = checked(shelves.Length * 2);
        Scalar16VarIdentityReadOnly[] newShelves = ArrayPool<Scalar16VarIdentityReadOnly>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<Scalar16VarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
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

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        }
    }

    private static int CompareKeys(ulong leftHigh, ulong leftLow, ulong rightHigh, ulong rightLow)
    {
        if (leftHigh < rightHigh)
        {
            return -1;
        }

        if (leftHigh > rightHigh)
        {
            return 1;
        }

        if (leftLow < rightLow)
        {
            return -1;
        }

        return leftLow > rightLow ? 1 : 0;
    }
}

