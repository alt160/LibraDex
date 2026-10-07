using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `FS32-8` range results in either query direction over encoded 32-byte fixed keys and encoded scalar identities.<br/>
/// The reader keeps routed traversal state and shelf-local slot ranges so callers can stream keys, identities, or full tuples without per-row materialization.<br/>
/// Shelf bytes are retained for the reader lifetime because the fixed shelf projection is stack-only; row access recreates that projection over the retained shelf buffer.<br/>
/// </summary>
internal sealed class Fixed32Scalar8RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private byte[][] shelves;
    private long[] shelfOffsets;
    private int[] startSlots;
    private int[] endSlots;
    private byte[] terminalShelfFlags;
    private ulong[] terminalKey0s;
    private ulong[] terminalKey1s;
    private ulong[] terminalKey2s;
    private ulong[] terminalKey3s;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private byte[]? routerBytes;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private Fixed32Scalar8Profile profile;
    private long indexRootOffset;
    private ulong lower0;
    private ulong lower1;
    private ulong lower2;
    private ulong lower3;
    private ulong upper0;
    private ulong upper1;
    private ulong upper2;
    private ulong upper3;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private long pendingTerminalRootOffset;
    private long pendingTerminalShelfOffset;
    private int pendingTerminalShelfExtent;
    private ulong pendingTerminalKey0;
    private ulong pendingTerminalKey1;
    private ulong pendingTerminalKey2;
    private ulong pendingTerminalKey3;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private bool traversalComplete = true;
    private bool currentRowInvalidated;
    private bool descendingTraversal;
    private bool disposed;

    internal Fixed32Scalar8RangeReader()
    {
        shelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        shelfOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        terminalShelfFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        terminalKey0s = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
        terminalKey1s = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
        terminalKey2s = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
        terminalKey3s = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Fixed32Scalar8RangeReader(
        LibraDexFileSession session,
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
        int maxRouterHops = 33)
        : this()
    {
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        }

        this.session = session;
        indexRootOffset = rootRouterOffset;
        this.profile = profile;
        this.lower0 = lower0;
        this.lower1 = lower1;
        this.lower2 = lower2;
        this.lower3 = lower3;
        this.upper0 = upper0;
        this.upper1 = upper1;
        this.upper2 = upper2;
        this.upper3 = upper3;
        descendingTraversal = direction == QueryDirection.Descending;
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        routerBytes = ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = GetPrefix(lower0, lower1, lower2, lower3, keyDepth: 0);
        byte upperPrefix = GetPrefix(upper0, upper1, upper2, upper3, keyDepth: 0);
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
    /// Copies the current encoded 32-byte key lanes to caller-provided output variables.<br/>
    /// This avoids tuple allocation and keeps the fixed key read explicit at call sites.<br/>
    /// </summary>
    /// <param name="key0">Receives encoded key lane 0.</param>
    /// <param name="key1">Receives encoded key lane 1.</param>
    /// <param name="key2">Receives encoded key lane 2.</param>
    /// <param name="key3">Receives encoded key lane 3.</param>
    public void ReadCurrentKey(out ulong key0, out ulong key1, out ulong key2, out ulong key3)
    {
        Fixed32Scalar8ReadOnly shelf = CurrentShelf;
        if (terminalShelfFlags[currentShelfIndex] != 0)
        {
            key0 = terminalKey0s[currentShelfIndex];
            key1 = terminalKey1s[currentShelfIndex];
            key2 = terminalKey2s[currentShelfIndex];
            key3 = terminalKey3s[currentShelfIndex];
            return;
        }

        key0 = shelf.ReadKeyPart0At(currentSlotIndex);
        key1 = shelf.ReadKeyPart1At(currentSlotIndex);
        key2 = shelf.ReadKeyPart2At(currentSlotIndex);
        key3 = shelf.ReadKeyPart3At(currentSlotIndex);
    }

    /// <summary>
    /// Gets the encoded scalar identity for the current row.<br/>
    /// The value is the persisted sortable scalar representation used by the `FS32-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentity
    {
        get
        {
            Fixed32Scalar8ReadOnly shelf = CurrentShelf;
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
    /// Moves backward and reads the encoded 32-byte fixed key plus encoded scalar identity tuple in one operation.<br/>
    /// This is the descending counterpart to the forward fused reader path and avoids separate current-property validation after movement.<br/>
    /// </summary>
    /// <param name="key0">Receives encoded key lane 0 when a row is available.</param>
    /// <param name="key1">Receives encoded key lane 1 when a row is available.</param>
    /// <param name="key2">Receives encoded key lane 2 when a row is available.</param>
    /// <param name="key3">Receives encoded key lane 3 when a row is available.</param>
    /// <param name="encodedIdentity">Receives the encoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadPreviousEncodedTuple(out ulong key0, out ulong key1, out ulong key2, out ulong key3, out ulong encodedIdentity)
    {
        if (!MovePrevious())
        {
            key0 = 0;
            key1 = 0;
            key2 = 0;
            key3 = 0;
            encodedIdentity = 0;
            return false;
        }

        ReadCurrentKey(out key0, out key1, out key2, out key3);
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
        ArrayPool<ulong>.Shared.Return(terminalKey0s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey1s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey2s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey3s, clearArray: false);
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

        if (routerBytes is not null)
        {
            ArrayPool<byte>.Shared.Return(routerBytes, clearArray: false);
        }

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
        pendingFlags = null;
        routerBytes = null;
        visitedShelves = null;
        visitedRouters = null;
        session = null;
        shelfOffsets = Array.Empty<long>();
        terminalShelfFlags = Array.Empty<byte>();
        terminalKey0s = Array.Empty<ulong>();
        terminalKey1s = Array.Empty<ulong>();
        terminalKey2s = Array.Empty<ulong>();
        terminalKey3s = Array.Empty<ulong>();
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
                if (terminalShelfFlags[i] != 0)
                    ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
                shelves[i] = null!;
            }
        }
    }

    private Fixed32Scalar8ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The FS32-8 range reader is not positioned on a row.");
            }

            return new Fixed32Scalar8ReadOnly(shelves[currentShelfIndex], profile);
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
            throw new InvalidOperationException("The FS32-8 range reader is not positioned on a row.");
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        if (terminalShelfFlags[currentShelfIndex] != 0)
        {
            Span<byte> keyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
            LibraDexFileSession.WriteFixed32Scalar8KeyBytes(keyBytes,
                terminalKey0s[currentShelfIndex], terminalKey1s[currentShelfIndex],
                terminalKey2s[currentShelfIndex], terminalKey3s[currentShelfIndex]);
            ulong identity = TerminalIdentity8ShelfLayout.ReadIdentity(shelves[currentShelfIndex], currentSlotIndex);
            if (localSession.DeleteFixedScalar8TerminalIdentity(shelfOffsets[currentShelfIndex], keyBytes, profile.ShelfExtentSize, identity) != 1)
                return false;
            currentRowInvalidated = true;
            return true;
        }

        byte[] shelfBytes = CloneShelfBytesForMutation(currentShelfIndex);
        Fixed32Scalar8 shelf = new(shelfBytes, profile);
        int removed = shelf.RemoveSlotRange(currentSlotIndex, 1);
        if (removed != 1)
        {
            return false;
        }

        _ = localSession.StageFixed32Scalar8ShelfRewriteForBatch(shelfOffsets[currentShelfIndex], profile, shelfBytes);
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
            throw new InvalidOperationException("The FS32-8 range reader is not positioned on a row.");
        }

        currentRowInvalidated = true;
    }

    internal long DeleteMatchedRanges()
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        EnsureAllRangesLoaded();
        long deleted = 0;
        Span<byte> terminalKeyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalShelfFlags[i] != 0)
            {
                LibraDexFileSession.WriteFixed32Scalar8KeyBytes(terminalKeyBytes,
                    terminalKey0s[i], terminalKey1s[i], terminalKey2s[i], terminalKey3s[i]);
                for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
                    deleted += localSession.DeleteFixedScalar8TerminalIdentity(
                        shelfOffsets[i], terminalKeyBytes, profile.ShelfExtentSize,
                        TerminalIdentity8ShelfLayout.ReadIdentity(shelves[i], slot));
                continue;
            }

            byte[] shelfBytes = localSession.IsDurabilityBatchActive
                ? localSession.ReadFixed32Scalar8ShelfBytesForBatch(shelfOffsets[i], profile)
                : CloneShelfBytesForMutation(i);
            Fixed32Scalar8ReadOnly readOnly = new(shelfBytes, profile);
            int startSlot = localSession.IsDurabilityBatchActive
                ? readOnly.LowerBoundKey(profile.Descending ? upper0 : lower0,
                    profile.Descending ? upper1 : lower1, profile.Descending ? upper2 : lower2,
                    profile.Descending ? upper3 : lower3)
                : startSlots[i];
            int endSlot = localSession.IsDurabilityBatchActive ? startSlot : endSlots[i];
            if (localSession.IsDurabilityBatchActive)
            {
                while (endSlot < readOnly.ItemCount && (profile.Descending
                    ? CompareKey(readOnly.ReadKeyPart0At(endSlot), readOnly.ReadKeyPart1At(endSlot), readOnly.ReadKeyPart2At(endSlot), readOnly.ReadKeyPart3At(endSlot), lower0, lower1, lower2, lower3) >= 0
                    : CompareKey(readOnly.ReadKeyPart0At(endSlot), readOnly.ReadKeyPart1At(endSlot), readOnly.ReadKeyPart2At(endSlot), readOnly.ReadKeyPart3At(endSlot), upper0, upper1, upper2, upper3) <= 0))
                {
                    endSlot++;
                }
            }

            Fixed32Scalar8 shelf = new(shelfBytes, profile);
            int removed = localSession.IsDurabilityBatchActive
                ? shelf.MarkSlotRangeDeleted(startSlot, endSlot - startSlot)
                : shelf.RemoveSlotRange(startSlot, endSlot - startSlot);
            if (removed == 0)
            {
                continue;
            }

            _ = localSession.StageFixed32Scalar8ShelfRewriteForBatch(shelfOffsets[i], profile, shelfBytes);
            deleted += removed;
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the first row in this reader's current key range whose encoded identity matches the supplied value.<br/>
    /// Callers use this after opening an equality range over a known fixed-width key, which keeps non-unique-key deletion scoped to one exact physical tuple.<br/>
    /// The reader should be disposed after this call because shelf slot positions may have shifted.<br/>
    /// </summary>
    /// <param name="encodedIdentity">The encoded identity to remove.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    internal bool DeleteFirstMatchingEncodedIdentity(ulong encodedIdentity)
    {
        ThrowIfDisposed();
        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        EnsureAllRangesLoaded();
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalShelfFlags[i] != 0)
            {
                for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
                {
                    if (TerminalIdentity8ShelfLayout.ReadIdentity(shelves[i], slot) != encodedIdentity)
                        continue;
                    Span<byte> keyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
                    LibraDexFileSession.WriteFixed32Scalar8KeyBytes(keyBytes,
                        terminalKey0s[i], terminalKey1s[i], terminalKey2s[i], terminalKey3s[i]);
                    return localSession.DeleteFixedScalar8TerminalIdentity(shelfOffsets[i], keyBytes, profile.ShelfExtentSize, encodedIdentity) == 1;
                }

                continue;
            }

            Fixed32Scalar8ReadOnly readOnly = new(shelves[i], profile);
            for (int slot = startSlots[i]; slot < endSlots[i]; slot++)
            {
                if (readOnly.ReadIdentityAt(slot) != encodedIdentity)
                {
                    continue;
                }

                if (localSession.ClassifyFixed32Scalar8RouteTarget(shelfOffsets[i]) == Fixed32Scalar8RouteTargetKind.TerminalIdentityRoot)
                {
                    Span<byte> keyBytes = stackalloc byte[Fixed32Scalar8Layout.KeySize];
                    LibraDexFileSession.WriteFixed32Scalar8KeyBytes(
                        keyBytes,
                        readOnly.ReadKeyPart0At(slot),
                        readOnly.ReadKeyPart1At(slot),
                        readOnly.ReadKeyPart2At(slot),
                        readOnly.ReadKeyPart3At(slot));
                    return localSession.DeleteFixedScalar8TerminalIdentity(shelfOffsets[i], keyBytes, profile.ShelfExtentSize, encodedIdentity) == 1;
                }

                byte[] shelfBytes = CloneShelfBytesForMutation(i);
                Fixed32Scalar8 shelf = new(shelfBytes, profile);
                _ = shelf.RemoveSlotRange(slot, 1);
                _ = localSession.StageFixed32Scalar8ShelfRewriteForBatch(shelfOffsets[i], profile, shelfBytes);
                return true;
            }
        }

        return false;
    }

    private void AddShelfRange(long shelfOffset, byte[] shelfBytes)
    {
        Fixed32Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid || shelf.IsDescending != profile.Descending)
        {
            throw new InvalidDataException("The routed FS32-8 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(profile.Descending ? upper0 : lower0,
            profile.Descending ? upper1 : lower1, profile.Descending ? upper2 : lower2,
            profile.Descending ? upper3 : lower3);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount && (profile.Descending
            ? CompareKey(shelf.ReadKeyPart0At(endSlot), shelf.ReadKeyPart1At(endSlot), shelf.ReadKeyPart2At(endSlot), shelf.ReadKeyPart3At(endSlot), lower0, lower1, lower2, lower3) >= 0
            : CompareKey(shelf.ReadKeyPart0At(endSlot), shelf.ReadKeyPart1At(endSlot), shelf.ReadKeyPart2At(endSlot), shelf.ReadKeyPart3At(endSlot), upper0, upper1, upper2, upper3) <= 0))
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

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        int previousRowCount = rowCount;
        while (pendingTerminalShelfOffset != 0)
        {
            long shelfOffset = pendingTerminalShelfOffset;
            pendingTerminalShelfOffset = 0;
            if (!localVisitedShelves.Add(shelfOffset))
                break;
            byte[] terminalBytes = localSession.ReadTerminalIdentity8ShelfBytes(shelfOffset, pendingTerminalShelfExtent);
            pendingTerminalShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(terminalBytes);
            AddTerminalIdentityChunk(pendingTerminalRootOffset, pendingTerminalKey0, pendingTerminalKey1,
                pendingTerminalKey2, pendingTerminalKey3, terminalBytes);
            if (rowCount > previousRowCount)
                return true;
        }

        byte[] localRouterBytes = routerBytes ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed FS32-8 range read exceeded the configured router hop count.");
            }

            if (localSession.TryGetFixed32Scalar8ReadShelf(indexRootOffset, targetOffset, out byte[]? cachedShelfBytes))
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                AddShelfRange(targetOffset, cachedShelfBytes);
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (localSession.TryGetPromotedDirectRouterKeyDepth(targetOffset, out ushort promotedRouterKeyDepth))
            {
                if (!localVisitedRouters.Add(targetOffset))
                {
                    continue;
                }

                PushDirectRouterTargets(localSession, targetOffset, promotedRouterKeyDepth, remainingHops, lowerEdge, upperEdge);
                continue;
            }

            Fixed32Scalar8RouteTargetKind kind = localSession.ClassifyFixed32Scalar8RouteTarget(targetOffset);
            if (kind == Fixed32Scalar8RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                AddShelfRange(targetOffset, localSession.ReadFixed32Scalar8ShelfBytes(indexRootOffset, targetOffset, profile));
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind == Fixed32Scalar8RouteTargetKind.TerminalIdentityRoot)
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

            if (kind != Fixed32Scalar8RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed FS32-8 range target is not a shelf or router.");
            }

            if (!localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            if (localSession.TryGetDirectRouterKeyDepth(targetOffset, out ushort directRouterKeyDepth))
            {
                PushDirectRouterTargets(localSession, targetOffset, directRouterKeyDepth, remainingHops, lowerEdge, upperEdge);
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, localRouterBytes.AsSpan(0, RouterLayout.Size));
            RouterReader router = new(localRouterBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed FS32-8 range router is invalid.");
            }

            byte startPrefix = lowerEdge ? GetPrefix(lower0, lower1, lower2, lower3, router.KeyDepth) : (byte)0;
            byte endPrefix = upperEdge ? GetPrefix(upper0, upper1, upper2, upper3, router.KeyDepth) : byte.MaxValue;
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
    /// Opens one persisted fixed-key scalar-eight terminal route without projecting its identities into ordinary `FS32-8` shelves.<br/>
    /// Matching physical traversal retains one identity-only shelf at a time; opposite traversal reverses the chain before its first row.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root selected by routed traversal.<br/></param>
    /// <param name="localSession">The owning file session used to read the root and identity-only shelf chain.<br/></param>
    private void AddTerminalIdentityRootRanges(long rootOffset, LibraDexFileSession localSession)
    {
        byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity ||
            TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Fixed32Scalar8Layout.KeySize ||
            TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes) != profile.ShelfExtentSize)
        {
            throw new InvalidDataException("The routed FS32-8 terminal identity root does not match its owning index shape.");
        }

        if ((rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0) != profile.Descending)
            throw new InvalidDataException("The routed FS32-8 terminal identity root physical order does not match its index profile.");

        ReadOnlySpan<byte> keyBytes = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Fixed32Scalar8Layout.KeySize);
        ulong key0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes);
        ulong key1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes.Slice(8));
        ulong key2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes.Slice(16));
        ulong key3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(keyBytes.Slice(24));
        if (CompareKey(key0, key1, key2, key3, lower0, lower1, lower2, lower3) < 0 ||
            CompareKey(key0, key1, key2, key3, upper0, upper1, upper2, upper3) > 0)
        {
            return;
        }

        pendingTerminalRootOffset = rootOffset;
        pendingTerminalShelfExtent = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        pendingTerminalKey0 = key0;
        pendingTerminalKey1 = key1;
        pendingTerminalKey2 = key2;
        pendingTerminalKey3 = key3;
        pendingTerminalShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
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

            for (int i = chain.Count - 1; i >= 0; i--)
                AddTerminalIdentityChunk(rootOffset, key0, key1, key2, key3, chain[i]);
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
            AddTerminalIdentityChunk(rootOffset, key0, key1, key2, key3, shelfBytes);
            if (rowCount > previousCount)
                break;
        }
    }

    /// <summary>
    /// Retains one identity-only terminal shelf as direct cursor state.<br/>
    /// The fixed key is kept once in parallel reader state, avoiding per-identity key projection and whole-chain materialization.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal root offset retained as the projection owner.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="shelfBytes">The retained pooled terminal identity shelf bytes.<br/></param>
    private void AddTerminalIdentityChunk(long rootOffset, ulong key0, ulong key1, ulong key2, ulong key3, byte[] shelfBytes)
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
        terminalKey0s[shelfCount] = key0;
        terminalKey1s[shelfCount] = key1;
        terminalKey2s[shelfCount] = key2;
        terminalKey3s[shelfCount] = key3;
        shelfCount++;
        rowCount = checked(rowCount + itemCount);
    }

    /// <summary>
    /// Expands one existing session-promoted direct router into the reader's pending traversal stack.<br/>
    /// Durable router target offsets remain authoritative; this path only avoids copying and reparsing the same 4 KiB router page after the session has already decoded it.<br/>
    /// Contiguous target runs are clipped to the requested lower and upper edge prefixes so traversal flags remain identical to the persisted-page path.<br/>
    /// </summary>
    /// <param name="localSession">The file session that owns the promoted router projection.<br/></param>
    /// <param name="routerOffset">The durable router file offset.<br/></param>
    /// <param name="keyDepth">The persisted key depth exposed by the promoted direct router.<br/></param>
    /// <param name="remainingHops">The remaining traversal-hop allowance before visiting a child.<br/></param>
    /// <param name="lowerEdge">Whether the current router lies on the query's lower edge.<br/></param>
    /// <param name="upperEdge">Whether the current router lies on the query's upper edge.<br/></param>
    private void PushDirectRouterTargets(
        LibraDexFileSession localSession,
        long routerOffset,
        ushort keyDepth,
        int remainingHops,
        bool lowerEdge,
        bool upperEdge)
    {
        byte startPrefix = lowerEdge ? GetPrefix(lower0, lower1, lower2, lower3, keyDepth) : (byte)0;
        byte endPrefix = upperEdge ? GetPrefix(upper0, upper1, upper2, upper3, keyDepth) : byte.MaxValue;
        if (descendingTraversal)
        {
            int prefix = startPrefix;
            while (prefix <= endPrefix)
            {
                long childTargetOffset = localSession.FindRouterTarget(routerOffset, (byte)prefix);
                if (childTargetOffset == 0)
                {
                    prefix++;
                    continue;
                }

                _ = localSession.TryGetDirectRouterTargetPrefixRange(routerOffset, (byte)prefix, childTargetOffset, out byte directRunStart, out byte directRunEnd);
                int runStart = Math.Max(prefix, directRunStart);
                int runEnd = Math.Min(endPrefix, directRunEnd);
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
                long childTargetOffset = localSession.FindRouterTarget(routerOffset, (byte)prefix);
                if (childTargetOffset == 0)
                {
                    prefix--;
                    continue;
                }

                _ = localSession.TryGetDirectRouterTargetPrefixRange(routerOffset, (byte)prefix, childTargetOffset, out byte directRunStart, out byte directRunEnd);
                int runEnd = Math.Min(prefix, directRunEnd);
                int runStart = Math.Max(startPrefix, directRunStart);
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

    private void EnsureAllRangesLoaded()
    {
        while (LoadNextShelfRange())
        {
        }
    }

    private void PushTarget(long offset, int remainingHops, bool lowerEdge, bool upperEdge)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
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
        byte[] newTerminalShelfFlags = ArrayPool<byte>.Shared.Rent(newLength);
        ulong[] newTerminalKey0s = ArrayPool<ulong>.Shared.Rent(newLength);
        ulong[] newTerminalKey1s = ArrayPool<ulong>.Shared.Rent(newLength);
        ulong[] newTerminalKey2s = ArrayPool<ulong>.Shared.Rent(newLength);
        ulong[] newTerminalKey3s = ArrayPool<ulong>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        shelfOffsets.AsSpan(0, shelfCount).CopyTo(newShelfOffsets);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        terminalShelfFlags.AsSpan(0, shelfCount).CopyTo(newTerminalShelfFlags);
        terminalKey0s.AsSpan(0, shelfCount).CopyTo(newTerminalKey0s);
        terminalKey1s.AsSpan(0, shelfCount).CopyTo(newTerminalKey1s);
        terminalKey2s.AsSpan(0, shelfCount).CopyTo(newTerminalKey2s);
        terminalKey3s.AsSpan(0, shelfCount).CopyTo(newTerminalKey3s);
        ArrayPool<byte[]>.Shared.Return(shelves, clearArray: true);
        ArrayPool<long>.Shared.Return(shelfOffsets, clearArray: false);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey0s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey1s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey2s, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKey3s, clearArray: false);
        shelves = newShelves;
        shelfOffsets = newShelfOffsets;
        startSlots = newStartSlots;
        endSlots = newEndSlots;
        terminalShelfFlags = newTerminalShelfFlags;
        terminalKey0s = newTerminalKey0s;
        terminalKey1s = newTerminalKey1s;
        terminalKey2s = newTerminalKey2s;
        terminalKey3s = newTerminalKey3s;
    }

    /// <summary>
    /// Clones one borrowed immutable `FS32-8` shelf before cursor-owned mutation.<br/>
    /// Replacing the reader slot keeps cached bytes immutable while allowing the existing delete path to stage an ordinary full-shelf rewrite.<br/>
    /// </summary>
    /// <param name="shelfIndex">The retained shelf index to clone.<br/></param>
    /// <returns>The mutable reader-owned shelf image.<br/></returns>
    private byte[] CloneShelfBytesForMutation(int shelfIndex)
    {
        byte[] mutableShelfBytes = shelves[shelfIndex].AsSpan(0, profile.ShelfExtentSize).ToArray();
        shelves[shelfIndex] = mutableShelfBytes;
        return mutableShelfBytes;
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
    /// Shelf ranges are already retained in high-to-low route order; slot direction follows the shelf's persisted physical order.<br/>
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

    private static int CompareKey(ulong left0, ulong left1, ulong left2, ulong left3, ulong right0, ulong right1, ulong right2, ulong right3)
    {
        int c = left0.CompareTo(right0);
        if (c != 0) return c;
        c = left1.CompareTo(right1);
        if (c != 0) return c;
        c = left2.CompareTo(right2);
        return c != 0 ? c : left3.CompareTo(right3);
    }

    private static byte GetPrefix(ulong key0, ulong key1, ulong key2, ulong key3, int keyDepth)
    {
        if ((uint)keyDepth >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "FS32-8 fixed keys expose exactly thirty-two routing bytes.");
        }

        ulong lane = keyDepth < 8 ? key0 : keyDepth < 16 ? key1 : keyDepth < 24 ? key2 : key3;
        int laneDepth = keyDepth & 7;
        return (byte)(lane >> ((sizeof(ulong) - 1 - laneDepth) * 8));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Fixed32Scalar8RangeReader));
        }
    }
}
