using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SS16-8` range results as a forward-only cursor over encoded 16-byte scalar keys and encoded scalar identities.<br/>
/// The reader keeps routed traversal state and shelf-local slot ranges so callers can stream keys, identities, or full tuples without per-row materialization.<br/>
/// Shelf bytes are retained for the reader lifetime because the fixed shelf projection is stack-only; terminal identity-only shelves retain their fixed key in reader state.<br/>
/// </summary>
internal sealed class Scalar16Scalar8RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private byte[][] shelves;
    private long[] shelfOffsets;
    private int[] startSlots;
    private int[] endSlots;
    private byte[] terminalShelfFlags;
    private ulong[] terminalKeyHighs;
    private ulong[] terminalKeyLows;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private byte[]? routerScratch;
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
    private long pendingTerminalRootOffset;
    private long pendingTerminalShelfOffset;
    private int pendingTerminalShelfExtent;
    private ulong pendingTerminalKeyHigh;
    private ulong pendingTerminalKeyLow;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private bool traversalComplete = true;
    private bool currentRowInvalidated;
    private bool descendingTraversal;
    private bool disposed;

    internal Scalar16Scalar8RangeReader()
    {
        shelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        shelfOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        terminalShelfFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        terminalKeyHighs = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
        terminalKeyLows = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Scalar16Scalar8RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar16Scalar8Profile profile,
        ulong lowerKeyHigh,
        ulong lowerKeyLow,
        ulong upperKeyHigh,
        ulong upperKeyLow,
        QueryDirection direction = QueryDirection.Ascending,
        int maxRouterHops = Scalar16Scalar8Layout.KeySize + 1)
        : this()
    {
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        }

        this.session = session;
        this.profile = profile;
        this.lowerKeyHigh = lowerKeyHigh;
        this.lowerKeyLow = lowerKeyLow;
        this.upperKeyHigh = upperKeyHigh;
        this.upperKeyLow = upperKeyLow;
        descendingTraversal = direction == QueryDirection.Descending;
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = GetPrefix(lowerKeyHigh, lowerKeyLow, keyDepth: 0);
        byte upperPrefix = GetPrefix(upperKeyHigh, upperKeyLow, keyDepth: 0);
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
    /// Gets the encoded high key lane for the current row.<br/>
    /// The value is the first persisted sortable scalar lane used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyHigh
    {
        get
        {
            Scalar16Scalar8ReadOnly shelf = CurrentShelf;
            return terminalShelfFlags[currentShelfIndex] != 0
                ? terminalKeyHighs[currentShelfIndex]
                : shelf.ReadKeyHighAt(currentSlotIndex);
        }
    }

    /// <summary>
    /// Gets the encoded low key lane for the current row.<br/>
    /// The value is the second persisted sortable scalar lane used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKeyLow
    {
        get
        {
            Scalar16Scalar8ReadOnly shelf = CurrentShelf;
            return terminalShelfFlags[currentShelfIndex] != 0
                ? terminalKeyLows[currentShelfIndex]
                : shelf.ReadKeyLowAt(currentSlotIndex);
        }
    }

    /// <summary>
    /// Gets the encoded scalar identity for the current row.<br/>
    /// The value is the persisted sortable scalar representation used by the `SS16-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentity
    {
        get
        {
            Scalar16Scalar8ReadOnly shelf = CurrentShelf;
            return terminalShelfFlags[currentShelfIndex] != 0
                ? TerminalIdentity8ShelfLayout.ReadIdentity(shelves[currentShelfIndex], currentSlotIndex)
                : shelf.ReadIdentityAt(currentSlotIndex);
        }
    }

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

        encodedIdentity = CurrentEncodedIdentity;
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
            currentRowInvalidated = false;
            return false;
        }

        if (ordinal < 0)
        {
            currentShelfIndex = 0;
            currentSlotIndex = profile.Descending ? endSlots[0] - 1 : startSlots[0];
        }
        else
        {
            currentSlotIndex += profile.Descending ? -1 : 1;
            while (currentShelfIndex < shelfCount && (profile.Descending
                ? currentSlotIndex < startSlots[currentShelfIndex]
                : currentSlotIndex >= endSlots[currentShelfIndex]))
            {
                currentShelfIndex++;
                if (currentShelfIndex < shelfCount)
                {
                    currentSlotIndex = profile.Descending
                        ? endSlots[currentShelfIndex] - 1
                        : startSlots[currentShelfIndex];
                }
            }
        }

        ordinal++;
        currentRowInvalidated = false;
        return true;
    }

    /// <summary>
    /// Moves the reader to the previous matching encoded key/identity row in key order.<br/>
    /// The method walks shelf-local slot ranges backward without materializing decoded tuples.<br/>
    /// A descending physical index loads terminal identity shelves lazily when the requested traversal matches its stored order.<br/>
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
                currentSlotIndex = profile.Descending ? startSlots[0] : endSlots[0] - 1;
            }
            else
            {
                currentSlotIndex += profile.Descending ? 1 : -1;
                while (currentShelfIndex < shelfCount && (profile.Descending
                    ? currentSlotIndex >= endSlots[currentShelfIndex]
                    : currentSlotIndex < startSlots[currentShelfIndex]))
                {
                    currentShelfIndex++;
                    if (currentShelfIndex < shelfCount)
                    {
                        currentSlotIndex = profile.Descending
                            ? startSlots[currentShelfIndex]
                            : endSlots[currentShelfIndex] - 1;
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
            currentSlotIndex = profile.Descending
                ? startSlots[currentShelfIndex]
                : endSlots[currentShelfIndex] - 1;
            ordinal = rowCount - 1;
            currentRowInvalidated = false;
            return true;
        }

        currentSlotIndex += profile.Descending ? 1 : -1;
        while (currentShelfIndex >= 0 && (profile.Descending
            ? currentSlotIndex >= endSlots[currentShelfIndex]
            : currentSlotIndex < startSlots[currentShelfIndex]))
        {
            currentShelfIndex--;
            if (currentShelfIndex >= 0)
            {
                currentSlotIndex = profile.Descending
                    ? startSlots[currentShelfIndex]
                    : endSlots[currentShelfIndex] - 1;
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
    /// Moves backward and reads the encoded 16-byte key plus encoded scalar identity tuple in one operation.<br/>
    /// This is the descending counterpart to the forward fused reader path and avoids separate current-property validation after movement.<br/>
    /// </summary>
    /// <param name="encodedKeyHigh">Receives the encoded high key lane when a row is available.</param>
    /// <param name="encodedKeyLow">Receives the encoded low key lane when a row is available.</param>
    /// <param name="encodedIdentity">Receives the encoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadPreviousEncodedTuple(out ulong encodedKeyHigh, out ulong encodedKeyLow, out ulong encodedIdentity)
    {
        if (!MovePrevious())
        {
            encodedKeyHigh = 0;
            encodedKeyLow = 0;
            encodedIdentity = 0;
            return false;
        }

        encodedKeyHigh = CurrentEncodedKeyHigh;
        encodedKeyLow = CurrentEncodedKeyLow;
        encodedIdentity = CurrentEncodedIdentity;
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
        ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeyHighs, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeyLows, clearArray: false);
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

        if (routerScratch is not null)
        {
            ArrayPool<byte>.Shared.Return(routerScratch, clearArray: false);
        }

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
        pendingFlags = null;
        routerScratch = null;
        visitedShelves = null;
        visitedRouters = null;
        session = null;
        shelfOffsets = Array.Empty<long>();
        terminalShelfFlags = Array.Empty<byte>();
        terminalKeyHighs = Array.Empty<ulong>();
        terminalKeyLows = Array.Empty<ulong>();
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
        currentRowInvalidated = false;
        pendingTerminalShelfOffset = 0;
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
            if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SS16-8 range reader is not positioned on a row.");
            }

            return new Scalar16Scalar8ReadOnly(shelves[currentShelfIndex], profile);
        }
    }

    /// <summary>
    /// Deletes the current cursor row and repositions the reader before the next surviving row.<br/>
    /// The method mutates only the retained shelf that owns the current slot, stages that shelf rewrite through the owning session, and adjusts the slot interval for the shelf's physical direction so a following <see cref="MoveNext"/> continues at the next surviving tuple.<br/>
    /// Current key and identity accessors are invalid until the next successful move.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the positioned row was deleted.</returns>
    internal bool DeleteCurrent()
    {
        ThrowIfDisposed();
        if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
        {
            throw new InvalidOperationException("The SS16-8 range reader is not positioned on a row.");
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        if (terminalShelfFlags[currentShelfIndex] != 0)
        {
            Span<byte> keyBytes = stackalloc byte[Scalar16Scalar8Layout.KeySize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBytes, terminalKeyHighs[currentShelfIndex]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), terminalKeyLows[currentShelfIndex]);
            ulong identity = TerminalIdentity8ShelfLayout.ReadIdentity(shelves[currentShelfIndex], currentSlotIndex);
            if (localSession.DeleteFixedScalar8TerminalIdentity(shelfOffsets[currentShelfIndex], keyBytes, profile.ShelfExtentSize, identity) != 1)
                return false;
            currentRowInvalidated = true;
            return true;
        }

        Scalar16Scalar8 shelf = new(shelves[currentShelfIndex], profile);
        int removed = shelf.RemoveSlotRange(currentSlotIndex, 1);
        if (removed != 1)
        {
            return false;
        }

        _ = localSession.StageScalar16Scalar8ShelfRewriteForBatch(shelfOffsets[currentShelfIndex], profile, shelves[currentShelfIndex]);
        endSlots[currentShelfIndex]--;
        rowCount--;
        ordinal--;
        if (!profile.Descending)
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
            throw new InvalidOperationException("The SS16-8 range reader is not positioned on a row.");
        }

        currentRowInvalidated = true;
    }

    internal long DeleteMatchedRanges()
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        EnsureAllRangesLoaded();
        long deleted = 0;
        Span<byte> terminalKeyBytes = stackalloc byte[Scalar16Scalar8Layout.KeySize];
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalShelfFlags[i] != 0)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(terminalKeyBytes, terminalKeyHighs[i]);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(terminalKeyBytes.Slice(sizeof(ulong)), terminalKeyLows[i]);
                for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
                    deleted += localSession.DeleteFixedScalar8TerminalIdentity(
                        shelfOffsets[i], terminalKeyBytes, profile.ShelfExtentSize,
                        TerminalIdentity8ShelfLayout.ReadIdentity(shelves[i], slot));
                continue;
            }

            byte[] shelfBytes = localSession.IsDurabilityBatchActive
                ? localSession.ReadScalar16Scalar8ShelfBytesForBatch(shelfOffsets[i], profile)
                : shelves[i];
            Scalar16Scalar8ReadOnly readOnly = new(shelfBytes, profile);
            int startSlot = localSession.IsDurabilityBatchActive
                ? readOnly.LowerBoundKey(profile.Descending ? upperKeyHigh : lowerKeyHigh, profile.Descending ? upperKeyLow : lowerKeyLow)
                : startSlots[i];
            int endSlot = localSession.IsDurabilityBatchActive ? startSlot : endSlots[i];
            if (localSession.IsDurabilityBatchActive)
            {
                while (endSlot < readOnly.ItemCount && (profile.Descending
                    ? CompareKey(readOnly.ReadKeyHighAt(endSlot), readOnly.ReadKeyLowAt(endSlot), lowerKeyHigh, lowerKeyLow) >= 0
                    : CompareKey(readOnly.ReadKeyHighAt(endSlot), readOnly.ReadKeyLowAt(endSlot), upperKeyHigh, upperKeyLow) <= 0))
                {
                    endSlot++;
                }
            }

            Scalar16Scalar8 shelf = new(shelfBytes, profile);
            int removed = localSession.IsDurabilityBatchActive
                ? shelf.MarkSlotRangeDeleted(startSlot, endSlot - startSlot)
                : shelf.RemoveSlotRange(startSlot, endSlot - startSlot);
            if (removed == 0)
            {
                continue;
            }

            _ = localSession.StageScalar16Scalar8ShelfRewriteForBatch(shelfOffsets[i], profile, shelfBytes);
            deleted += removed;
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the first row in this reader's current key range whose encoded identity matches the supplied value.<br/>
    /// Callers use this after opening an equality range over a known key, which keeps non-unique-key deletion scoped to one exact physical tuple.<br/>
    /// The reader should be disposed after this call because shelf slot positions may have shifted.<br/>
    /// </summary>
    /// <param name="encodedIdentity">The encoded identity to remove.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    internal bool DeleteFirstMatchingEncodedIdentity(ulong encodedIdentity)
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        EnsureAllRangesLoaded();
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalShelfFlags[i] != 0)
            {
                for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
                {
                    if (TerminalIdentity8ShelfLayout.ReadIdentity(shelves[i], slot) != encodedIdentity)
                        continue;
                    Span<byte> keyBytes = stackalloc byte[Scalar16Scalar8Layout.KeySize];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBytes, terminalKeyHighs[i]);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(keyBytes.Slice(sizeof(ulong)), terminalKeyLows[i]);
                    return localSession.DeleteFixedScalar8TerminalIdentity(shelfOffsets[i], keyBytes, profile.ShelfExtentSize, encodedIdentity) == 1;
                }

                continue;
            }

            Scalar16Scalar8ReadOnly readOnly = new(shelves[i], profile);
            for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
            {
                if (readOnly.ReadIdentityAt(slot) != encodedIdentity)
                {
                    continue;
                }

                Scalar16Scalar8 shelf = new(shelves[i], profile);
                _ = shelf.RemoveSlotRange(slot, 1);
                _ = localSession.StageScalar16Scalar8ShelfRewriteForBatch(shelfOffsets[i], profile, shelves[i]);
                return true;
            }
        }

        return false;
    }

    private void AddShelfRange(long shelfOffset, byte[] shelfBytes)
    {
        Scalar16Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || shelf.IsDescending != profile.Descending)
        {
            throw new InvalidDataException("The routed SS16-8 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(profile.Descending ? upperKeyHigh : lowerKeyHigh, profile.Descending ? upperKeyLow : lowerKeyLow);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && (profile.Descending
            ? CompareKey(shelf.ReadKeyHighAt(endSlot), shelf.ReadKeyLowAt(endSlot), lowerKeyHigh, lowerKeyLow) >= 0
            : CompareKey(shelf.ReadKeyHighAt(endSlot), shelf.ReadKeyLowAt(endSlot), upperKeyHigh, upperKeyLow) <= 0))
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
        terminalShelfFlags[shelfCount] = 0;
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
        while (pendingTerminalShelfOffset != 0)
        {
            long shelfOffset = pendingTerminalShelfOffset;
            pendingTerminalShelfOffset = 0;
            if (!localVisitedShelves.Add(shelfOffset))
                break;
            byte[] terminalBytes = localSession.ReadTerminalIdentity8ShelfBytes(shelfOffset, pendingTerminalShelfExtent);
            pendingTerminalShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(terminalBytes);
            AddTerminalIdentityChunk(pendingTerminalRootOffset, pendingTerminalKeyHigh, pendingTerminalKeyLow, terminalBytes);
            if (rowCount > previousRowCount)
                return true;
        }

        byte[] routerBytes = routerScratch ??= ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
        Span<byte> routerPage = routerBytes.AsSpan(0, RouterLayout.Size);
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

                AddShelfRange(targetOffset, localSession.ReadScalar16Scalar8ShelfBytes(targetOffset, profile));
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind == Scalar16Scalar8RouteTargetKind.TerminalIdentityRoot)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                AddTerminalIdentityRootRanges(targetOffset, localSession);
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

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerPage);
            RouterReader router = new(routerPage);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SS16-8 range router is invalid.");
            }

            byte startPrefix = lowerEdge ? GetPrefix(lowerKeyHigh, lowerKeyLow, router.KeyDepth) : (byte)0;
            byte endPrefix = upperEdge ? GetPrefix(upperKeyHigh, upperKeyLow, router.KeyDepth) : byte.MaxValue;
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

    /// <summary>
    /// Opens a persisted fixed-key scalar-eight terminal route without copying identities into ordinary `SS16-8` shelves.<br/>
    /// Matching physical traversal retains one identity-only shelf at a time; the opposite direction reverses the shelf chain before returning its first row.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root selected by routed traversal.<br/></param>
    /// <param name="localSession">The owning file session used to read the root and identity-only shelf chain.<br/></param>
    private void AddTerminalIdentityRootRanges(long rootOffset, LibraDexFileSession localSession)
    {
        byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Scalar16Scalar8Layout.KeySize ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != profile.ShelfExtentSize)
        {
            throw new InvalidDataException("The routed SS16-8 terminal identity root does not match its owning index shape.");
        }

        if ((rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0) != profile.Descending)
            throw new InvalidDataException("The routed SS16-8 terminal identity root physical order does not match its index profile.");

        ReadOnlySpan<byte> keyBytes = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar16Scalar8Layout.KeySize);
        ulong keyHigh = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes);
        ulong keyLow = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes.Slice(sizeof(ulong)));
        if (CompareKey(keyHigh, keyLow, lowerKeyHigh, lowerKeyLow) < 0 ||
            CompareKey(keyHigh, keyLow, upperKeyHigh, upperKeyLow) > 0)
        {
            return;
        }

        pendingTerminalRootOffset = rootOffset;
        pendingTerminalShelfExtent = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        pendingTerminalKeyHigh = keyHigh;
        pendingTerminalKeyLow = keyLow;
        pendingTerminalShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar16Scalar8RangeReader));
        if (profile.Descending != descendingTraversal)
        {
            List<byte[]> chain = [];
            while (pendingTerminalShelfOffset != 0)
            {
                long shelfOffset = pendingTerminalShelfOffset;
                pendingTerminalShelfOffset = 0;
                if (!localVisitedShelves.Add(shelfOffset))
                    break;
                byte[] shelfBytes = localSession.ReadTerminalIdentity8ShelfBytes(shelfOffset, pendingTerminalShelfExtent);
                pendingTerminalShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
                chain.Add(shelfBytes);
            }

            pendingTerminalShelfOffset = 0;
            for (int i = chain.Count - 1; i >= 0; i--)
                AddTerminalIdentityChunk(rootOffset, keyHigh, keyLow, chain[i]);
            return;
        }

        while (pendingTerminalShelfOffset != 0)
        {
            long shelfOffset = pendingTerminalShelfOffset;
            pendingTerminalShelfOffset = 0;
            if (!localVisitedShelves.Add(shelfOffset))
                break;
            byte[] shelfBytes = localSession.ReadTerminalIdentity8ShelfBytes(shelfOffset, pendingTerminalShelfExtent);
            pendingTerminalShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
            int previousCount = rowCount;
            AddTerminalIdentityChunk(rootOffset, keyHigh, keyLow, shelfBytes);
            if (rowCount > previousCount)
                break;
        }
    }

    /// <summary>
    /// Retains one identity-only terminal shelf as a direct cursor range.<br/>
    /// The fixed key is kept once in parallel reader state and no ordinary `SS16-8` projection is built.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset retained as the projection owner.<br/></param>
    /// <param name="keyHigh">The high encoded key lane.<br/></param>
    /// <param name="keyLow">The low encoded key lane.<br/></param>
    /// <param name="shelfBytes">The retained terminal identity-only shelf bytes.<br/></param>
    private void AddTerminalIdentityChunk(long rootOffset, ulong keyHigh, ulong keyLow, byte[] shelfBytes)
    {
        int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
        if (itemCount == 0)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        if (shelfCount == shelves.Length)
            GrowShelves();
        shelves[shelfCount] = shelfBytes;
        shelfOffsets[shelfCount] = rootOffset;
        startSlots[shelfCount] = 0;
        endSlots[shelfCount] = itemCount;
        terminalShelfFlags[shelfCount] = 1;
        terminalKeyHighs[shelfCount] = keyHigh;
        terminalKeyLows[shelfCount] = keyLow;
        shelfCount++;
        rowCount = checked(rowCount + itemCount);
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
        long[] newShelfOffsets = ArrayPool<long>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        byte[] newTerminalFlags = ArrayPool<byte>.Shared.Rent(newLength);
        ulong[] newTerminalKeyHighs = ArrayPool<ulong>.Shared.Rent(newLength);
        ulong[] newTerminalKeyLows = ArrayPool<ulong>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        shelfOffsets.AsSpan(0, shelfCount).CopyTo(newShelfOffsets);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        terminalShelfFlags.AsSpan(0, shelfCount).CopyTo(newTerminalFlags);
        terminalKeyHighs.AsSpan(0, shelfCount).CopyTo(newTerminalKeyHighs);
        terminalKeyLows.AsSpan(0, shelfCount).CopyTo(newTerminalKeyLows);
        ArrayPool<byte[]>.Shared.Return(shelves, clearArray: true);
        ArrayPool<long>.Shared.Return(shelfOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeyHighs, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeyLows, clearArray: false);
        shelves = newShelves;
        shelfOffsets = newShelfOffsets;
        startSlots = newStartSlots;
        endSlots = newEndSlots;
        terminalShelfFlags = newTerminalFlags;
        terminalKeyHighs = newTerminalKeyHighs;
        terminalKeyLows = newTerminalKeyLows;
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
                currentSlotIndex = profile.Descending
                    ? endSlots[i] - 1 - remaining
                    : startSlots[i] + remaining;
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
                currentSlotIndex = profile.Descending
                    ? startSlots[i] + remaining
                    : endSlots[i] - 1 - remaining;
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
