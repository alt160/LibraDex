using System.Buffers;
using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SS8-8` range results as a forward-only cursor over encoded scalar keys and encoded scalar identities.<br/>
/// The reader keeps routed traversal state and shelf-local slot ranges so callers can stream keys, identities, or full tuples without per-row materialization.<br/>
/// Shelf bytes are retained for the reader lifetime because the fixed shelf projection is stack-only; row access recreates that projection over the retained shelf buffer.<br/>
/// </summary>
internal sealed class Scalar8Scalar8RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private byte[][] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private byte[]? terminalShelfFlags;
    private ulong[]? terminalScalarKeys;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedContextSet? visitedRouters;
    private Scalar8Scalar8RangePlan? plan;
    private LibraDexFileSession? session;
    private Scalar8Scalar8Profile profile;
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

    internal Scalar8Scalar8RangeReader()
    {
        shelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        terminalShelfFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        terminalScalarKeys = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
    }

    /// <summary>
    /// Creates an `SS8-8` range reader over an already-built retained range plan.<br/>
    /// The reader borrows the plan-owned arrays for cursor movement and returns them by disposing the plan during reader disposal.<br/>
    /// </summary>
    /// <param name="plan">The retained range plan that owns shelf buffers and slot extents for this reader.</param>
    internal Scalar8Scalar8RangeReader(Scalar8Scalar8RangePlan plan)
    {
        this.plan = plan;
        session = plan.Session;
        profile = plan.Profile;
        shelves = plan.Shelves;
        startSlots = plan.StartSlots;
        endSlots = plan.EndSlots;
        terminalShelfFlags = plan.TerminalShelfFlags;
        terminalScalarKeys = plan.TerminalScalarKeys;
        shelfCount = plan.ShelfCount;
        rowCount = plan.RowCount;
        traversalComplete = true;
    }

    internal Scalar8Scalar8RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
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
        visitedRouters = RouteVisitedContextSet.Rent();
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
                    PushTarget(targetOffset, maxRouterHops, lowerEdge: prefix == lowerPrefix, upperEdge: prefix == upperPrefix);
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
                    PushTarget(targetOffset, maxRouterHops, lowerEdge: prefix == lowerPrefix, upperEdge: prefix == upperPrefix);
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
    /// The value is the persisted sortable scalar representation used by the `SS8-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedKey
    {
        get
        {
            byte[]? localFlags = terminalShelfFlags;
            if (localFlags is not null && localFlags[currentShelfIndex] != 0)
            {
                ulong[] localKeys = terminalScalarKeys ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
                return localKeys[currentShelfIndex];
            }

            return CurrentShelf.ReadKeyAt(currentSlotIndex);
        }
    }

    /// <summary>
    /// Gets the encoded scalar identity for the current row.<br/>
    /// The value is the persisted sortable scalar representation used by the `SS8-8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentity
    {
        get
        {
            byte[]? localFlags = terminalShelfFlags;
            if (localFlags is not null && localFlags[currentShelfIndex] != 0)
            {
                return TerminalIdentity8ShelfLayout.ReadIdentity(shelves[currentShelfIndex], currentSlotIndex);
            }

            return CurrentShelf.ReadIdentityAt(currentSlotIndex);
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
    /// Moves backward and reads the encoded scalar key/identity tuple in one operation.<br/>
    /// This is the descending counterpart to the forward fused reader path and avoids separate current-property validation after movement.<br/>
    /// </summary>
    /// <param name="encodedKey">Receives the encoded key when a row is available.</param>
    /// <param name="encodedIdentity">Receives the encoded identity when a row is available.</param>
    /// <returns><see langword="true"/> when a tuple was read; otherwise <see langword="false"/>.</returns>
    public bool TryReadPreviousEncodedTuple(out ulong encodedKey, out ulong encodedIdentity)
    {
        if (!MovePrevious())
        {
            encodedKey = 0;
            encodedIdentity = 0;
            return false;
        }

        encodedKey = CurrentEncodedKey;
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
        if (plan is not null)
        {
            plan.Dispose();
            plan = null;
            shelves = Array.Empty<byte[]>();
            startSlots = Array.Empty<int>();
            endSlots = Array.Empty<int>();
            terminalShelfFlags = null;
            terminalScalarKeys = null;
            shelfCount = 0;
            rowCount = 0;
            pendingCount = 0;
            ordinal = -1;
            currentShelfIndex = 0;
            currentSlotIndex = -1;
            currentRowInvalidated = false;
            return;
        }

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

        if (terminalShelfFlags is not null)
        {
            ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
        }

        if (terminalScalarKeys is not null)
        {
            ArrayPool<ulong>.Shared.Return(terminalScalarKeys, clearArray: false);
        }

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
        pendingFlags = null;
        terminalShelfFlags = null;
        terminalScalarKeys = null;
        visitedShelves = null;
        visitedRouters = null;
        session = null;
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

    private Scalar8Scalar8ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if (currentRowInvalidated || (uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SS8-8 range reader is not positioned on a row.");
            }

            return new Scalar8Scalar8ReadOnly(shelves[currentShelfIndex], profile);
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
            throw new InvalidOperationException("The SS8-8 range reader is not positioned on a row.");
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        Scalar8Scalar8 shelf = new(shelves[currentShelfIndex], profile);
        int removed = shelf.RemoveSlotRange(currentSlotIndex, 1);
        if (removed != 1)
        {
            return false;
        }

        long shelfOffset = plan is not null ? plan.ShelfOffsets[currentShelfIndex] : 0;
        if (shelfOffset <= 0)
        {
            throw new InvalidOperationException("The SS8-8 range reader does not have a durable shelf offset for cursor-local delete.");
        }

        _ = localSession.StageScalar8Scalar8ShelfRewriteForBatch(shelfOffset, profile, shelves[currentShelfIndex]);
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
            throw new InvalidOperationException("The SS8-8 range reader is not positioned on a row.");
        }

        currentRowInvalidated = true;
    }

    private void AddShelfRange(byte[] shelfBytes)
    {
        Scalar8Scalar8ReadOnly shelf = new(shelfBytes, profile);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SS8-8 range target shelf is invalid.");
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
        startSlots[shelfCount] = startSlot;
        endSlots[shelfCount] = endSlot;
        terminalShelfFlags![shelfCount] = 0;
        shelfCount++;
        rowCount = checked(rowCount + endSlot - startSlot);
    }

    private void AddTerminalIdentityShelfRange(byte[] shelfBytes, ulong encodedKey)
    {
        if (encodedKey < lowerEncodedKey || encodedKey > upperEncodedKey)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
        if (itemCount <= 0)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        if (shelfCount == shelves.Length)
        {
            GrowShelves();
        }

        shelves[shelfCount] = shelfBytes;
        startSlots[shelfCount] = 0;
        endSlots[shelfCount] = itemCount;
        terminalShelfFlags![shelfCount] = 1;
        terminalScalarKeys![shelfCount] = encodedKey;
        shelfCount++;
        rowCount = checked(rowCount + itemCount);
    }

    private bool LoadNextShelfRange()
    {
        if (traversalComplete)
        {
            return false;
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        RouteVisitedContextSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed SS8-8 range read exceeded the configured router hop count.");
            }

            Scalar8Scalar8RouteTargetKind kind = localSession.ClassifyScalar8Scalar8RouteTarget(targetOffset);
            if (kind == Scalar8Scalar8RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                byte[] shelfBytes = localSession.ReadScalar8Scalar8ShelfBytes(targetOffset, profile);
                Scalar8Scalar8ReadOnly linkedProbe = new(shelfBytes, profile);
                AddShelfRange(shelfBytes);
                if (linkedProbe.IsValid && linkedProbe.IsDuplicateRun && linkedProbe.DuplicateRunNextOffset != 0)
                {
                    PushTarget(linkedProbe.DuplicateRunNextOffset, remainingHops, lowerEdge, upperEdge);
                }

                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind == Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(targetOffset);
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8 ||
                    TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Scalar8Scalar8Layout.KeySize)
                {
                    throw new InvalidDataException("The routed SS8-8 terminal identity root shape is invalid.");
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
                    if (!localVisitedShelves.Add(identityShelfOffset))
                    {
                        break;
                    }

                    byte[] identityShelfBytes = localSession.ReadTerminalIdentity8ShelfBytes(identityShelfOffset, shelfExtentSize);
                    long nextOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(identityShelfBytes);
                    AddTerminalIdentityShelfRange(identityShelfBytes, encodedKey);
                    identityShelfOffset = nextOffset;
                }

                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind != Scalar8Scalar8RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed SS8-8 range target is not a shelf, terminal identity root, or router.");
            }

            if (!localVisitedRouters.Add(targetOffset, lowerEdge, upperEdge))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SS8-8 range router is invalid.");
            }

            byte startPrefix = lowerEdge ? GetPrefix(lowerEncodedKey, router.KeyDepth) : (byte)0;
            byte endPrefix = upperEdge ? GetPrefix(upperEncodedKey, router.KeyDepth) : byte.MaxValue;
            if (descendingTraversal)
            {
                for (int prefix = startPrefix; prefix <= endPrefix; prefix++)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0)
                    {
                        continue;
                    }

                    PushTarget(
                        childTargetOffset,
                        remainingHops - 1,
                        lowerEdge && prefix == startPrefix,
                        upperEdge && prefix == endPrefix);
                }
            }
            else
            {
                for (int prefix = endPrefix; prefix >= startPrefix; prefix--)
                {
                    long childTargetOffset = router.FindTarget((byte)prefix);
                    if (childTargetOffset == 0)
                    {
                        continue;
                    }

                    PushTarget(
                        childTargetOffset,
                        remainingHops - 1,
                        lowerEdge && prefix == startPrefix,
                        upperEdge && prefix == endPrefix);
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
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
        if (terminalShelfFlags is not null)
        {
            byte[] newTerminalFlags = ArrayPool<byte>.Shared.Rent(newLength);
            terminalShelfFlags.AsSpan(0, shelfCount).CopyTo(newTerminalFlags);
            ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
            terminalShelfFlags = newTerminalFlags;
        }

        if (terminalScalarKeys is not null)
        {
            ulong[] newTerminalKeys = ArrayPool<ulong>.Shared.Rent(newLength);
            terminalScalarKeys.AsSpan(0, shelfCount).CopyTo(newTerminalKeys);
            ArrayPool<ulong>.Shared.Return(terminalScalarKeys, clearArray: false);
            terminalScalarKeys = newTerminalKeys;
        }

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
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "SS8-8 scalar keys expose exactly eight routing bytes.");
        }

        return (byte)(encodedKey >> ((sizeof(ulong) - 1 - keyDepth) * 8));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar8Scalar8RangeReader));
        }
    }
}
