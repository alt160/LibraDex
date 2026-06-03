using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `VV` range results as a forward-only cursor over raw key and identity byte spans.<br/>
/// The reader owns routed traversal state and loads matching shelf ranges on demand through the owning session, leaving DK/session responsible for cache-backed reads.<br/>
/// This is the preferred public range-read surface for varlen key/varlen identity indexes because callers can inspect, skip, copy, or materialize each row intentionally.<br/>
/// </summary>
public sealed class VarKeyVarIdentityRangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private VarKeyVarIdentityReadOnly[] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private byte[]? lowerKey;
    private byte[]? upperKey;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private int maxKeyLength;
    private int maxIdentityLength;
    private bool decodeLogicalKeys;
    private bool traversalComplete = true;
    private bool disposed;

    internal VarKeyVarIdentityRangeReader()
    {
        shelves = ArrayPool<VarKeyVarIdentityReadOnly>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
    }

    internal VarKeyVarIdentityRangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxKeyLength,
        int maxIdentityLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = 8,
        bool decodeLogicalKeys = false)
        : this()
    {
        this.session = session;
        this.maxKeyLength = maxKeyLength;
        this.maxIdentityLength = maxIdentityLength;
        this.decodeLogicalKeys = decodeLogicalKeys;
        this.lowerKey = lowerKey.ToArray();
        this.upperKey = upperKey.ToArray();
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(lowerKey, 0);
        byte upperPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(upperKey, 0);
        for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
        {
            long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset != 0)
            {
                PushTarget(targetOffset, maxRouterHops);
            }
        }
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from shelf-local slot ranges; key and identity bytes are not copied and per-row references are not allocated.<br/>
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
    /// Gets the raw key bytes for the current row.<br/>
    /// The returned span is valid until <see cref="MoveNext"/> is called again or the reader is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> CurrentKey
    {
        get
        {
            ReadOnlySpan<byte> encoded = CurrentShelf.ReadKeyAt(currentSlotIndex);
            return decodeLogicalKeys ? LibraDexVarLenKeyCodec.DecodePayloadSpan(encoded) : encoded;
        }
    }

    /// <summary>
    /// Gets whether the current logical key is the null-key sentinel.<br/>
    /// This is false for raw encoded readers and for logical empty keys; callers can combine it with <see cref="CurrentKeyLength"/> to distinguish null from empty.<br/>
    /// </summary>
    public bool CurrentKeyIsNull => decodeLogicalKeys && LibraDexVarLenKeyCodec.IsNull(CurrentShelf.ReadKeyAt(currentSlotIndex));

    /// <summary>
    /// Gets the raw identity bytes for the current row.<br/>
    /// The returned span is valid until <see cref="MoveNext"/> is called again or the reader is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> CurrentIdentity => CurrentShelf.ReadIdentityAt(currentSlotIndex);

    /// <summary>
    /// Gets the current key length without copying the key payload.<br/>
    /// </summary>
    public int CurrentKeyLength => CurrentKey.Length;

    /// <summary>
    /// Gets the current identity length without copying the identity payload.<br/>
    /// </summary>
    public int CurrentIdentityLength => CurrentIdentity.Length;

    /// <summary>
    /// Advances the reader to the next matching key/identity row.<br/>
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
    /// Skips up to <paramref name="count"/> rows without copying or materializing their key or identity bytes.<br/>
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
    /// Copies the current key bytes into a caller-owned destination buffer.<br/>
    /// The destination must be at least <see cref="CurrentKeyLength"/> bytes long.<br/>
    /// </summary>
    /// <param name="destination">The caller-owned destination buffer.</param>
    /// <returns>The number of bytes copied.</returns>
    public int CopyCurrentKeyTo(Span<byte> destination)
    {
        ReadOnlySpan<byte> key = CurrentKey;
        key.CopyTo(destination);
        return key.Length;
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
    /// Returns the current key as an owned byte array for callers that explicitly need materialization.<br/>
    /// Prefer <see cref="CurrentKey"/> or <see cref="CopyCurrentKeyTo"/> in hot paths.<br/>
    /// </summary>
    /// <returns>An owned copy of the current key bytes.</returns>
    public byte[] MaterializeCurrentKey()
    {
        return CurrentKey.ToArray();
    }

    /// <summary>
    /// Returns the current logical key as an owned byte array, or null when the current logical key is the null-key sentinel.<br/>
    /// Prefer <see cref="CurrentKey"/> and <see cref="CurrentKeyIsNull"/> in hot paths to avoid allocation.<br/>
    /// </summary>
    /// <returns>An owned copy of the current logical key bytes, or null for a logical null key.</returns>
    public byte[]? MaterializeCurrentKeyOrNull()
    {
        if (CurrentKeyIsNull)
        {
            return null;
        }

        return CurrentKey.ToArray();
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

    internal void AddShelfRange(byte[] shelfBytes, VarKeyVarIdentityProfile profile, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        VarKeyVarIdentityReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VV range target shelf is invalid.");
        }

        AddShelfRange(shelf, lowerKey, upperKey);
    }

    internal void AddShelfRange(VarKeyVarIdentityReadOnly shelf, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VV range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(lowerKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && shelf.ReadKeyAt(endSlot).SequenceCompareTo(upperKey) <= 0)
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
        ArrayPool<VarKeyVarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
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
        lowerKey = null;
        upperKey = null;
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private VarKeyVarIdentityReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The VV range reader is not positioned on a row.");
            }

            return shelves[currentShelfIndex];
        }
    }

    private void AddShelfRange(VarKeyVarIdentityReadOnly shelf, int startSlot, int endSlot)
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

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        ReadOnlySpan<byte> localLowerKey = lowerKey ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        ReadOnlySpan<byte> localUpperKey = upperKey ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed VV range read exceeded the configured router hop count.");
            }

            VarKeyVarIdentityRouteTargetKind kind = localSession.ClassifyVarKeyVarIdentityRouteTarget(targetOffset);
            if (kind == VarKeyVarIdentityRouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                VarKeyVarIdentityReadOnly shelf = localSession.ReadVarKeyVarIdentityReadOnlyShelf(targetOffset, maxKeyLength, maxIdentityLength);
                AddShelfRange(shelf, localLowerKey, localUpperKey);
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind != VarKeyVarIdentityRouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed VV range target is not a shelf or router.");
            }

            if (!localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed VV range router is invalid.");
            }

            if (router.PrefixByteCount > 1)
            {
                for (int routeIndex = router.RouteCount - 1; routeIndex >= 0; routeIndex--)
                {
                    long childTargetOffset = router.GetRouteTargetAt(routeIndex);
                    if (childTargetOffset != 0)
                    {
                        PushTarget(childTargetOffset, remainingHops - 1);
                    }
                }

                continue;
            }

            byte lowerPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(localLowerKey, router.KeyDepth);
            byte upperPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(localUpperKey, router.KeyDepth);
            for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
            {
                long childTargetOffset = router.FindTarget((byte)prefix);
                if (childTargetOffset != 0)
                {
                    PushTarget(childTargetOffset, remainingHops - 1);
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        pendingCount--;
        offset = localOffsets[pendingCount];
        remainingHops = localHops[pendingCount];
    }

    private void GrowShelves()
    {
        int newLength = checked(shelves.Length * 2);
        VarKeyVarIdentityReadOnly[] newShelves = ArrayPool<VarKeyVarIdentityReadOnly>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<VarKeyVarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
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
            throw new ObjectDisposedException(nameof(VarKeyVarIdentityRangeReader));
        }
    }
}
