using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SS8-16` range results as a forward-only cursor over encoded scalar keys and encoded 16-byte scalar identities.<br/>
/// The reader keeps routed traversal state and shelf-local slot ranges so callers can stream keys, identities, or full tuples without per-row materialization.<br/>
/// Shelf bytes are retained for the reader lifetime because the fixed shelf projection is stack-only; row access recreates that projection over the retained shelf buffer.<br/>
/// </summary>
internal sealed class Scalar8Scalar16RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private byte[][] shelves;
    private long[] shelfOffsets;
    private int[] startSlots;
    private int[] endSlots;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private Scalar8Scalar16Profile profile;
    private ulong lowerEncodedKey;
    private ulong upperEncodedKey;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private bool traversalComplete = true;
    private bool currentRowInvalidated;
    private bool descendingTraversal;
    private bool disposed;

    internal Scalar8Scalar16RangeReader()
    {
        shelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        shelfOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Scalar8Scalar16RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar16Profile profile,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = 8)
        : this()
    {
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        }

        this.session = session;
        this.profile = profile;
        this.lowerEncodedKey = lowerEncodedKey;
        this.upperEncodedKey = upperEncodedKey;
        descendingTraversal = direction == QueryDirection.Descending;
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = GetPrefix(lowerEncodedKey, keyDepth: 0);
        byte upperPrefix = GetPrefix(upperEncodedKey, keyDepth: 0);
        if (descendingTraversal)
        {
            for (int prefix = lowerPrefix; prefix <= upperPrefix; prefix++)
            {
                long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
                if (targetOffset != 0)
                {
                    PushTarget(targetOffset, maxRouterHops, prefix == lowerPrefix, prefix == upperPrefix);
                }
            }
        }
        else
        {
            for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
            {
                long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
                if (targetOffset != 0)
                {
                    PushTarget(targetOffset, maxRouterHops, prefix == lowerPrefix, prefix == upperPrefix);
                }
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
    /// Gets the encoded scalar key for the current row.<br/>
    /// The value is the persisted sortable scalar representation used by the `SS8-16` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKey => CurrentShelf.ReadKeyAt(currentSlotIndex);

    /// <summary>
    /// Gets the encoded high identity lane for the current row.<br/>
    /// The value is the first persisted sortable identity lane used by the `SS8-16` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentityHigh => CurrentShelf.ReadIdentityHighAt(currentSlotIndex);

    /// <summary>
    /// Gets the encoded low identity lane for the current row.<br/>
    /// The value is the second persisted sortable identity lane used by the `SS8-16` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentityLow => CurrentShelf.ReadIdentityLowAt(currentSlotIndex);

    /// <summary>
    /// Advances the reader and returns the next encoded 16-byte identity lanes without a separate current-row property read.<br/>
    /// This is the low-overhead identity-only streaming path for callers that do not need keys or full tuple projection.<br/>
    /// When the method returns <see langword="false"/>, both identity lanes are set to zero and the reader is positioned after the final row.<br/>
    /// </summary>
    /// <param name="encodedIdentityHigh">Receives the encoded high identity lane when a row is available.</param>
    /// <param name="encodedIdentityLow">Receives the encoded low identity lane when a row is available.</param>
    /// <returns><see langword="true"/> when an identity was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadNextEncodedIdentity(out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        if (!MoveNext())
        {
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        Scalar8Scalar16ReadOnly shelf = new(shelves[currentShelfIndex], profile);
        encodedIdentityHigh = shelf.ReadIdentityHighAt(currentSlotIndex);
        encodedIdentityLow = shelf.ReadIdentityLowAt(currentSlotIndex);
        return true;
    }

    /// <summary>
    /// Copies the current encoded identity lanes to caller-provided output variables.<br/>
    /// This avoids tuple allocation and keeps the 16-byte identity read explicit at call sites.<br/>
    /// </summary>
    /// <param name="encodedIdentityHigh">Receives the encoded high identity lane.</param>
    /// <param name="encodedIdentityLow">Receives the encoded low identity lane.</param>
    public void ReadCurrentIdentity(out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        Scalar8Scalar16ReadOnly shelf = CurrentShelf;
        encodedIdentityHigh = shelf.ReadIdentityHighAt(currentSlotIndex);
        encodedIdentityLow = shelf.ReadIdentityLowAt(currentSlotIndex);
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
            currentRowInvalidated = false;
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
        currentRowInvalidated = false;
        return true;
    }

    /// <summary>
    /// Moves the reader to the previous matching encoded key/identity row in key order.<br/>
    /// The method loads the retained range plan, then walks shelf-local slot ranges backward without materializing decoded tuples.<br/>
    /// This is intended for descending public cursors where allocation-free reverse traversal is more important than lazy first-row latency.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row; otherwise <see langword="false"/>.</returns>
    public bool MovePrevious()
    {
        ThrowIfDisposed();
        if (!descendingTraversal)
        {
            EnsureAllRangesLoaded();
        }
        else
        {
            while (ordinal + 1 >= rowCount && LoadNextShelfRange())
            {
            }
        }

        if (rowCount == 0)
        {
            ordinal = rowCount;
            currentShelfIndex = shelfCount;
            currentSlotIndex = -1;
            currentRowInvalidated = false;
            return false;
        }

        if (descendingTraversal)
        {
            if (ordinal < 0)
            {
                currentShelfIndex = 0;
                currentSlotIndex = endSlots[0] - 1;
            }
            else
            {
                currentSlotIndex--;
                while (currentShelfIndex < shelfCount && currentSlotIndex < startSlots[currentShelfIndex])
                {
                    currentShelfIndex++;
                    if (currentShelfIndex < shelfCount)
                    {
                        currentSlotIndex = endSlots[currentShelfIndex] - 1;
                    }
                }
            }

            if (currentShelfIndex >= shelfCount)
            {
                ordinal = rowCount;
                currentSlotIndex = -1;
                currentRowInvalidated = false;
                return false;
            }

            ordinal++;
            currentRowInvalidated = false;
            return true;
        }

        if (ordinal < 0 || ordinal >= rowCount)
        {
            currentShelfIndex = shelfCount - 1;
            currentSlotIndex = endSlots[currentShelfIndex] - 1;
            ordinal = rowCount - 1;
            currentRowInvalidated = false;
            return true;
        }

        currentSlotIndex--;
        while (currentShelfIndex >= 0 && currentSlotIndex < startSlots[currentShelfIndex])
        {
            currentShelfIndex--;
            if (currentShelfIndex >= 0)
            {
                currentSlotIndex = endSlots[currentShelfIndex] - 1;
            }
        }

        if (currentShelfIndex < 0)
        {
            ordinal = -1;
            currentSlotIndex = -1;
            currentRowInvalidated = false;
            return false;
        }

        ordinal--;
        currentRowInvalidated = false;
        return true;
    }

    /// <summary>
    /// Moves backward and reads the encoded scalar key plus encoded 16-byte identity tuple in one operation.<br/>
    /// This is the descending counterpart to the forward fused reader path and avoids separate current-property validation after movement.<br/>
    /// </summary>
    /// <param name="encodedKey">Receives the encoded key when a row is available.</param>
    /// <param name="encodedIdentityHigh">Receives the encoded high identity lane when a row is available.</param>
    /// <param name="encodedIdentityLow">Receives the encoded low identity lane when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadPreviousEncodedTuple(out ulong encodedKey, out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        if (!MovePrevious())
        {
            encodedKey = 0;
            encodedIdentityHigh = 0;
            encodedIdentityLow = 0;
            return false;
        }

        Scalar8Scalar16ReadOnly shelf = new(shelves[currentShelfIndex], profile);
        encodedKey = shelf.ReadKeyAt(currentSlotIndex);
        encodedIdentityHigh = shelf.ReadIdentityHighAt(currentSlotIndex);
        encodedIdentityLow = shelf.ReadIdentityLowAt(currentSlotIndex);
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
    /// Skips up to <paramref name="count"/> rows while traversing backward through the retained range.<br/>
    /// The method loads the complete retained range, computes the target ordinal, and repositions directly instead of stepping row by row.<br/>
    /// Descending public cursors use this for efficient `skip` pagination over key-descending streams.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int SkipPrevious(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Skip count cannot be negative.");
        }

        if (descendingTraversal)
        {
            int descendingOrdinal = ordinal < 0 ? -1 : ordinal;
            while (!traversalComplete && rowCount - descendingOrdinal - 1 < count)
            {
                if (!LoadNextShelfRange())
                {
                    break;
                }
            }

            int skippedDescending = Math.Min(count, Math.Max(0, rowCount - descendingOrdinal - 1));
            if (skippedDescending == 0)
            {
                return 0;
            }

            PositionAtDescendingOrdinal(descendingOrdinal + skippedDescending);
            return skippedDescending;
        }

        EnsureAllRangesLoaded();
        if (count == 0 || rowCount == 0)
        {
            return 0;
        }

        int currentOrdinal = ordinal < 0 || ordinal >= rowCount ? rowCount : ordinal;
        int skipped = Math.Min(count, currentOrdinal);
        if (skipped == 0)
        {
            return 0;
        }

        int targetOrdinal = currentOrdinal - skipped;
        PositionAtOrdinal(targetOrdinal);
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
        ArrayPool<long>.Shared.Return(shelfOffsets, clearArray: false);
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
        shelfOffsets = Array.Empty<long>();
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
        currentRowInvalidated = false;
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

    private Scalar8Scalar16ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SS8-16 range reader is not positioned on a row.");
            }

            return new Scalar8Scalar16ReadOnly(shelves[currentShelfIndex], profile);
        }
    }

    /// <summary>
    /// Deletes the current cursor row and repositions the reader before the next surviving row.<br/>
    /// The method mutates only the retained shelf that owns the current slot, stages that shelf rewrite through the owning session, and updates the reader's slot interval so a following <see cref="MoveNext"/> continues without skipping the tuple that shifted into the deleted slot.<br/>
    /// Current key and identity accessors are invalid until the next successful move.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the positioned row was deleted.</returns>
    internal bool DeleteCurrent()
    {
        ThrowIfDisposed();
        if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
        {
            throw new InvalidOperationException("The SS8-16 range reader is not positioned on a row.");
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        Scalar8Scalar16 shelf = new(shelves[currentShelfIndex], profile);
        int removed = shelf.RemoveSlotRange(currentSlotIndex, 1);
        if (removed != 1)
        {
            return false;
        }

        _ = localSession.StageScalar8Scalar16ShelfRewriteForBatch(shelfOffsets[currentShelfIndex], profile, shelves[currentShelfIndex]);
        endSlots[currentShelfIndex]--;
        rowCount--;
        ordinal--;
        currentSlotIndex--;
        currentRowInvalidated = true;
        return true;
    }

    /// <summary>
    /// Invalidates the current row after the owning index performs an exact external mutation for that row.<br/>
    /// The retained range remains a traversal snapshot; leaving the slot and ordinal unchanged makes the next <see cref="MoveNext"/> skip the stale current row and continue with the next original row.<br/>
    /// Current key and identity accessors remain invalid until the next successful move.<br/>
    /// </summary>
    internal void InvalidateCurrentAfterExternalMutation()
    {
        ThrowIfDisposed();
        if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
        {
            throw new InvalidOperationException("The SS8-16 range reader is not positioned on a row.");
        }

        currentRowInvalidated = true;
    }

    internal long DeleteMatchedRanges()
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        EnsureAllRangesLoaded();
        long deleted = 0;
        for (int i = 0; i < shelfCount; i++)
        {
            byte[] shelfBytes = localSession.IsDurabilityBatchActive
                ? localSession.ReadScalar8Scalar16ShelfBytesForBatch(shelfOffsets[i], profile)
                : shelves[i];
            Scalar8Scalar16ReadOnly readOnly = new(shelfBytes, profile);
            int startSlot = localSession.IsDurabilityBatchActive
                ? readOnly.LowerBoundKey(lowerEncodedKey)
                : startSlots[i];
            int endSlot = localSession.IsDurabilityBatchActive ? startSlot : endSlots[i];
            if (localSession.IsDurabilityBatchActive)
            {
                while (endSlot < readOnly.ItemCount && readOnly.ReadKeyAt(endSlot) <= upperEncodedKey)
                {
                    endSlot++;
                }
            }

            Scalar8Scalar16 shelf = new(shelfBytes, profile);
            int removed = localSession.IsDurabilityBatchActive
                ? shelf.MarkSlotRangeDeleted(startSlot, endSlot - startSlot)
                : shelf.RemoveSlotRange(startSlot, endSlot - startSlot);
            if (removed == 0)
            {
                continue;
            }

            _ = localSession.StageScalar8Scalar16ShelfRewriteForBatch(shelfOffsets[i], profile, shelfBytes);
            deleted += removed;
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the first row in this reader's current key range whose encoded 16-byte identity matches the supplied lanes.<br/>
    /// Callers use this after opening an equality range over a known key, which keeps non-unique-key deletion scoped to one exact physical tuple.<br/>
    /// The reader should be disposed after this call because shelf slot positions may have shifted.<br/>
    /// </summary>
    /// <param name="encodedIdentityHigh">The encoded high identity lane.</param>
    /// <param name="encodedIdentityLow">The encoded low identity lane.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    internal bool DeleteFirstMatchingEncodedIdentity(ulong encodedIdentityHigh, ulong encodedIdentityLow)
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        EnsureAllRangesLoaded();
        for (int i = 0; i < shelfCount; i++)
        {
            Scalar8Scalar16ReadOnly readOnly = new(shelves[i], profile);
            for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
            {
                if (readOnly.ReadIdentityHighAt(slot) != encodedIdentityHigh ||
                    readOnly.ReadIdentityLowAt(slot) != encodedIdentityLow)
                {
                    continue;
                }

                Scalar8Scalar16 shelf = new(shelves[i], profile);
                _ = shelf.RemoveSlotRange(slot, 1);
                _ = localSession.StageScalar8Scalar16ShelfRewriteForBatch(shelfOffsets[i], profile, shelves[i]);
                return true;
            }
        }

        return false;
    }

    private void AddShelfRange(long shelfOffset, byte[] shelfBytes)
    {
        Scalar8Scalar16ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SS8-16 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(lowerEncodedKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && shelf.ReadKeyAt(endSlot) <= upperEncodedKey)
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
        shelfOffsets[shelfCount] = shelfOffset;
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

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed SS8-16 range read exceeded the configured router hop count.");
            }

            Scalar8Scalar16RouteTargetKind kind = localSession.ClassifyScalar8Scalar16RouteTarget(targetOffset);
            if (kind == Scalar8Scalar16RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                AddShelfRange(targetOffset, localSession.ReadScalar8Scalar16ShelfBytes(targetOffset, profile));
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind != Scalar8Scalar16RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed SS8-16 range target is not a shelf or router.");
            }

            if (!localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SS8-16 range router is invalid.");
            }

            byte startPrefix = lowerEdge ? GetPrefix(lowerEncodedKey, router.KeyDepth) : (byte)0;
            byte endPrefix = upperEdge ? GetPrefix(upperEncodedKey, router.KeyDepth) : byte.MaxValue;
            if (descendingTraversal)
            {
                int prefix = startPrefix;
                while (prefix <= endPrefix)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0)
                    {
                        prefix++;
                        continue;
                    }

                    int runStart = prefix;
                    int runEnd = prefix;
                    while (runEnd < endPrefix && router.FindTarget((byte)(runEnd + 1)) == childTargetOffset)
                    {
                        runEnd++;
                    }

                    bool singlePrefixRun = runStart == runEnd;
                    PushTarget(
                        childTargetOffset,
                        remainingHops - 1,
                        singlePrefixRun && lowerEdge && runStart == startPrefix,
                        singlePrefixRun && upperEdge && runEnd == endPrefix);
                    prefix = runEnd + 1;
                }
            }
            else
            {
                int prefix = endPrefix;
                while (prefix >= startPrefix)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0)
                    {
                        prefix--;
                        continue;
                    }

                    int runEnd = prefix;
                    int runStart = prefix;
                    while (runStart > startPrefix && router.FindTarget((byte)(runStart - 1)) == childTargetOffset)
                    {
                        runStart--;
                    }

                    bool singlePrefixRun = runStart == runEnd;
                    PushTarget(
                        childTargetOffset,
                        remainingHops - 1,
                        singlePrefixRun && lowerEdge && runStart == startPrefix,
                        singlePrefixRun && upperEdge && runEnd == endPrefix);
                    prefix = runStart - 1;
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

    private void PushTarget(long offset, int remainingHops, bool lowerEdge, bool upperEdge)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
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
        long[] newShelfOffsets = ArrayPool<long>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        shelfOffsets.AsSpan(0, shelfCount).CopyTo(newShelfOffsets);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<byte[]>.Shared.Return(shelves, clearArray: true);
        ArrayPool<long>.Shared.Return(shelfOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        shelves = newShelves;
        shelfOffsets = newShelfOffsets;
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

    /// <summary>
    /// Positions a descending traversal cursor at the supplied descending ordinal.<br/>
    /// Shelf ranges are already retained in high-to-low route order, so the ordinal maps to each shelf's slot range from `end - 1` back toward `start`.<br/>
    /// </summary>
    /// <param name="targetOrdinal">The zero-based ordinal in descending stream order.</param>
    private void PositionAtDescendingOrdinal(int targetOrdinal)
    {
        int remaining = targetOrdinal;
        for (int i = 0; i < shelfCount; i++)
        {
            int length = endSlots[i] - startSlots[i];
            if (remaining < length)
            {
                currentShelfIndex = i;
                currentSlotIndex = endSlots[i] - 1 - remaining;
                ordinal = targetOrdinal;
                return;
            }

            remaining -= length;
        }

        ordinal = rowCount;
        currentShelfIndex = shelfCount;
        currentSlotIndex = -1;
    }

    private static byte GetPrefix(ulong encodedKey, int keyDepth)
    {
        if ((uint)keyDepth >= sizeof(ulong))
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS8-16 scalar keys expose exactly eight routing bytes.");
        }

        return (byte)(encodedKey >> ((sizeof(ulong) - 1 - keyDepth) * 8));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar8Scalar16RangeReader));
        }
    }
}
