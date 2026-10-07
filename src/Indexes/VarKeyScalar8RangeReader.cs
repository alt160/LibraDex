using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `VS8` range results as a directional cursor over raw key byte spans and scalar identity values.<br/>
/// The reader owns routed traversal state and loads matching shelf ranges on demand through the owning session, leaving DK/session responsible for cache-backed reads.<br/>
/// This cursor is the comparison and public-read counterpart to the older identity-copy helper, allowing callers to iterate identities, keys, or full tuples without forced materialization.<br/>
/// </summary>
internal sealed class VarKeyScalar8RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private VarKeyScalar8ReadOnly[] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private byte[]? terminalShelfFlags;
    private byte[][]? terminalIdentityShelves;
    private byte[][]? terminalKeys;
    private byte[][]? rentedShelfBuffers;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private byte[]? routerScratch;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedContextSet? visitedRouters;
    private DataKernel.CoherentReadLease? coherentRead;
    private LibraDexFileSession? session;
    private byte[]? lowerKey;
    private byte[]? upperKey;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private int retiredStreamingRowCount;
    private int maxKeyLength;
    private long terminalContinuationOffset;
    private int terminalContinuationExtentSize;
    private byte[]? terminalContinuationKey;
    private bool decodeLogicalKeys;
    private bool captureDiagnostics;
    private bool descendingTraversal;
    private bool usePooledStreamingShelfReads;
    private bool traversalComplete = true;
    private bool resumeAtAppendedTarget;
    private bool disposed;
    private long targetsPopped;
    private long targetsQueued;
    private long routersVisited;
    private long oneByteRoutersVisited;
    private long multiByteRoutersVisited;
    private long routerRoutesExamined;
    private long routerBytesTouched;
    private long shelvesVisited;
    private long duplicateRunShelvesVisited;
    private long shelfSlotsDecoded;
    private long shelfBytesTouched;
    private long terminalRootsVisited;
    private long terminalIdentityShelvesVisited;
    private long terminalBytesTouched;
    private long matchingRows;

    internal VarKeyScalar8RangeReader()
    {
        shelves = ArrayPool<VarKeyScalar8ReadOnly>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        terminalShelfFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        terminalIdentityShelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        terminalKeys = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
    }

    internal VarKeyScalar8RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = 8,
        bool decodeLogicalKeys = false,
        bool captureDiagnostics = false,
        QueryDirection direction = QueryDirection.Ascending,
        DataKernel.CoherentReadLease? coherentRead = null)
        : this()
    {
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        }

        this.session = session;
        this.maxKeyLength = maxKeyLength;
        this.decodeLogicalKeys = decodeLogicalKeys;
        this.captureDiagnostics = captureDiagnostics;
        this.coherentRead = coherentRead;
        descendingTraversal = direction == QueryDirection.Descending;
        this.lowerKey = lowerKey.ToArray();
        this.upperKey = upperKey.ToArray();
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedContextSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(lowerKey, 0);
        byte upperPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(upperKey, 0);
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
    /// Opens a routed `VS8` range reader from an already-split set of disjoint continuation targets.<br/>
    /// The targets are produced under a coherent planning read and copied into this worker-owned reader's pending stack before that transition read is released.<br/>
    /// Global lower/upper bounds remain authoritative at every shelf, while each target's edge flags preserve the same boundary pruning context used by the ordinary root-starting cursor.<br/>
    /// </summary>
    /// <param name="session">The worker-visible LibraDex session.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive global lower encoded key bound.<br/></param>
    /// <param name="upperKey">The inclusive global upper encoded key bound.<br/></param>
    /// <param name="targets">Disjoint continuation targets assigned to this worker.<br/></param>
    /// <param name="decodeLogicalKeys">Whether current keys should expose decoded logical payload bytes.<br/></param>
    /// <param name="captureDiagnostics">Whether physical traversal counters should be retained.<br/></param>
    /// <param name="direction">The requested within-worker traversal direction.<br/></param>
    /// <param name="usePooledStreamingShelfReads">Whether cold shelf images should use reader-owned pooled buffers that are released immediately after forward consumption.<br/></param>
    /// <param name="coherentRead">The coherent read acquired on this worker thread.<br/></param>
    internal VarKeyScalar8RangeReader(
        LibraDexFileSession session,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        IReadOnlyList<VarKeyScalar8PhysicalTarget> targets,
        bool decodeLogicalKeys,
        bool captureDiagnostics,
        QueryDirection direction,
        bool usePooledStreamingShelfReads,
        DataKernel.CoherentReadLease coherentRead)
        : this()
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(coherentRead);
        if (targets.Count == 0)
            throw new ArgumentException("A physical VS8 worker reader requires at least one continuation target.", nameof(targets));
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        if (usePooledStreamingShelfReads && direction != QueryDirection.Ascending)
            throw new ArgumentException("Pooled VS8 streaming shelf reads require ascending forward traversal.", nameof(direction));

        this.session = session;
        this.maxKeyLength = maxKeyLength;
        this.decodeLogicalKeys = decodeLogicalKeys;
        this.captureDiagnostics = captureDiagnostics;
        this.usePooledStreamingShelfReads = usePooledStreamingShelfReads;
        this.coherentRead = coherentRead;
        descendingTraversal = direction == QueryDirection.Descending;
        this.lowerKey = lowerKey.ToArray();
        this.upperKey = upperKey.ToArray();
        pendingOffsets = ArrayPool<long>.Shared.Rent(Math.Max(DefaultShelfCapacity, targets.Count));
        pendingHops = ArrayPool<int>.Shared.Rent(Math.Max(DefaultShelfCapacity, targets.Count));
        pendingFlags = ArrayPool<byte>.Shared.Rent(Math.Max(DefaultShelfCapacity, targets.Count));
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedContextSet.Rent();
        if (usePooledStreamingShelfReads)
            rentedShelfBuffers = ArrayPool<byte[]>.Shared.Rent(shelves.Length);
        traversalComplete = false;

        if (descendingTraversal)
        {
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                PushPhysicalTarget(targets[targetIndex]);
        }
        else
        {
            for (int targetIndex = targets.Count - 1; targetIndex >= 0; targetIndex--)
                PushPhysicalTarget(targets[targetIndex]);
        }
    }

    /// <summary>
    /// Gets the number of matching rows available to this reader.<br/>
    /// The count is derived from shelf-local slot ranges; key bytes are not copied and per-row references are not allocated.<br/>
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            if (usePooledStreamingShelfReads)
            {
                throw new InvalidOperationException(
                    "A pooled streaming VS8 reader does not retain consumed shelves and therefore cannot calculate Count.");
            }

            EnsureAllRangesLoaded();
            return rowCount;
        }
    }

    /// <summary>
    /// Gets the zero-based row ordinal after a successful <see cref="MoveNext"/> call.<br/>
    /// The value is `-1` before the first row and equals <see cref="Count"/> after the reader passes the final row.<br/>
    /// </summary>
    public int Ordinal => usePooledStreamingShelfReads
        ? checked(retiredStreamingRowCount + ordinal)
        : ordinal;

    /// <summary>
    /// Gets the raw key bytes for the current row.<br/>
    /// The returned span is valid until <see cref="MoveNext"/> is called again or the reader is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> CurrentKey
    {
        get
        {
            byte[]? localFlags = terminalShelfFlags;
            if (localFlags is not null && localFlags[currentShelfIndex] != 0)
            {
                byte[][] localKeys = terminalKeys ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
                ReadOnlySpan<byte> terminalEncoded = localKeys[currentShelfIndex];
                return decodeLogicalKeys ? LibraDexVarLenKeyCodec.DecodePayloadSpan(terminalEncoded) : terminalEncoded;
            }

            ReadOnlySpan<byte> encoded = CurrentShelf.ReadKeyAt(currentSlotIndex);
            return decodeLogicalKeys ? LibraDexVarLenKeyCodec.DecodePayloadSpan(encoded) : encoded;
        }
    }

    /// <summary>
    /// Gets whether the current logical key is the null-key sentinel.<br/>
    /// This is false for raw encoded readers and for logical empty keys; callers can combine it with <see cref="CurrentKeyLength"/> to distinguish null from empty.<br/>
    /// </summary>
    public bool CurrentKeyIsNull
    {
        get
        {
            if (!decodeLogicalKeys)
            {
                return false;
            }

            byte[]? localFlags = terminalShelfFlags;
            if (localFlags is not null && localFlags[currentShelfIndex] != 0)
            {
                byte[][] localKeys = terminalKeys ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
                return LibraDexVarLenKeyCodec.IsNull(localKeys[currentShelfIndex]);
            }

            return LibraDexVarLenKeyCodec.IsNull(CurrentShelf.ReadKeyAt(currentSlotIndex));
        }
    }

    /// <summary>
    /// Gets the encoded scalar identity for the current row.<br/>
    /// The value uses the persisted sortable scalar representation used by the `VS8` shelf.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentity
    {
        get
        {
            byte[]? localFlags = terminalShelfFlags;
            if (localFlags is not null && localFlags[currentShelfIndex] != 0)
            {
                byte[][] localShelves = terminalIdentityShelves ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
                return TerminalIdentity8ShelfLayout.ReadIdentity(localShelves[currentShelfIndex], currentSlotIndex);
            }

            return CurrentShelf.ReadIdentityAt(currentSlotIndex);
        }
    }

    /// <summary>
    /// Gets the current key length without copying the key payload.<br/>
    /// </summary>
    public int CurrentKeyLength => CurrentKey.Length;

    /// <summary>
    /// Gets the current diagnostic snapshot for this routed `VS8` read.<br/>
    /// Counters remain zero unless the reader was opened through the internal proof path with diagnostic capture enabled.<br/>
    /// </summary>
    internal VarKeyScalar8RangeReadDiagnostics Diagnostics => new(
        targetsPopped,
        targetsQueued,
        routersVisited,
        oneByteRoutersVisited,
        multiByteRoutersVisited,
        routerRoutesExamined,
        routerBytesTouched,
        shelvesVisited,
        duplicateRunShelvesVisited,
        shelfSlotsDecoded,
        shelfBytesTouched,
        terminalRootsVisited,
        terminalIdentityShelvesVisited,
        terminalBytesTouched,
        matchingRows);

    /// <summary>
    /// Advances the reader to the next matching key/identity row.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid row.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        RetireConsumedStreamingWindow();
        while (ordinal + 1 >= rowCount && LoadNextShelfRange())
        {
        }

        if (ordinal + 1 >= rowCount)
        {
            ReleaseConsumedStreamingShelf(currentShelfIndex);
            ordinal = rowCount;
            currentShelfIndex = shelfCount;
            currentSlotIndex = -1;
            return false;
        }

        if (ordinal < 0)
        {
            currentShelfIndex = 0;
            bool physicalDescending = terminalShelfFlags![0] != 0
                ? (terminalShelfFlags[0] & 2) != 0
                : shelves[0].IsDescending;
            currentSlotIndex = physicalDescending ? endSlots[0] - 1 : startSlots[0];
        }
        else if (resumeAtAppendedTarget)
        {
            if ((uint)currentShelfIndex >= (uint)shelfCount)
                throw new InvalidDataException("The appended VS8 continuation target did not load a matching shelf range.");

            bool physicalDescending = terminalShelfFlags![currentShelfIndex] != 0
                ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                : shelves[currentShelfIndex].IsDescending;
            currentSlotIndex = physicalDescending ? endSlots[currentShelfIndex] - 1 : startSlots[currentShelfIndex];
            resumeAtAppendedTarget = false;
        }
        else
        {
            bool physicalDescending = terminalShelfFlags![currentShelfIndex] != 0
                ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                : shelves[currentShelfIndex].IsDescending;
            currentSlotIndex += physicalDescending ? -1 : 1;
            while (currentShelfIndex < shelfCount &&
                (physicalDescending ? currentSlotIndex < startSlots[currentShelfIndex] : currentSlotIndex >= endSlots[currentShelfIndex]))
            {
                ReleaseConsumedStreamingShelf(currentShelfIndex);
                currentShelfIndex++;
                if (currentShelfIndex < shelfCount)
                {
                    physicalDescending = terminalShelfFlags[currentShelfIndex] != 0
                        ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                        : shelves[currentShelfIndex].IsDescending;
                    currentSlotIndex = physicalDescending ? endSlots[currentShelfIndex] - 1 : startSlots[currentShelfIndex];
                }
            }
        }

        ordinal++;
        return true;
    }

    /// <summary>
    /// Advances a descending reader to the previous persisted tuple in key/identity order.<br/>
    /// Router targets are discovered from high key to low key, while each retained shelf range is consumed from its final matching slot to its first matching slot.<br/>
    /// Forward-only terminal and duplicate shelf chains are retained once and their shelf references are reversed without copying keys, identities, or shelf payloads.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the reader is positioned on a valid descending row; otherwise <see langword="false"/>.<br/></returns>
    public bool MovePrevious()
    {
        ThrowIfDisposed();
        if (usePooledStreamingShelfReads)
        {
            throw new InvalidOperationException(
                "A pooled streaming VS8 reader is forward-only because consumed shelf buffers are returned immediately.");
        }

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
            return false;
        }

        if (descendingTraversal)
        {
            if (ordinal < 0)
            {
                currentShelfIndex = 0;
                bool physicalDescending = terminalShelfFlags![0] != 0
                    ? (terminalShelfFlags[0] & 2) != 0
                    : shelves[0].IsDescending;
                currentSlotIndex = physicalDescending ? startSlots[0] : endSlots[0] - 1;
            }
            else
            {
                bool physicalDescending = terminalShelfFlags![currentShelfIndex] != 0
                    ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                    : shelves[currentShelfIndex].IsDescending;
                currentSlotIndex += physicalDescending ? 1 : -1;
                while (currentShelfIndex < shelfCount &&
                    (physicalDescending ? currentSlotIndex >= endSlots[currentShelfIndex] : currentSlotIndex < startSlots[currentShelfIndex]))
                {
                    currentShelfIndex++;
                    if (currentShelfIndex < shelfCount)
                    {
                        physicalDescending = terminalShelfFlags[currentShelfIndex] != 0
                            ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                            : shelves[currentShelfIndex].IsDescending;
                        currentSlotIndex = physicalDescending ? startSlots[currentShelfIndex] : endSlots[currentShelfIndex] - 1;
                    }
                }
            }

            if (currentShelfIndex >= shelfCount)
            {
                ordinal = rowCount;
                currentSlotIndex = -1;
                return false;
            }

            ordinal++;
            return true;
        }

        EnsureAllRangesLoaded();
        if (ordinal < 0)
        {
            currentShelfIndex = shelfCount - 1;
            bool physicalDescending = terminalShelfFlags![currentShelfIndex] != 0
                ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                : shelves[currentShelfIndex].IsDescending;
            currentSlotIndex = physicalDescending ? startSlots[currentShelfIndex] : endSlots[currentShelfIndex] - 1;
        }
        else
        {
            bool physicalDescending = terminalShelfFlags![currentShelfIndex] != 0
                ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                : shelves[currentShelfIndex].IsDescending;
            currentSlotIndex += physicalDescending ? 1 : -1;
            while (currentShelfIndex >= 0 &&
                (physicalDescending ? currentSlotIndex >= endSlots[currentShelfIndex] : currentSlotIndex < startSlots[currentShelfIndex]))
            {
                currentShelfIndex--;
                if (currentShelfIndex >= 0)
                {
                    physicalDescending = terminalShelfFlags[currentShelfIndex] != 0
                        ? (terminalShelfFlags[currentShelfIndex] & 2) != 0
                        : shelves[currentShelfIndex].IsDescending;
                    currentSlotIndex = physicalDescending ? startSlots[currentShelfIndex] : endSlots[currentShelfIndex] - 1;
                }
            }
        }

        if (currentShelfIndex < 0)
        {
            ordinal = rowCount;
            currentShelfIndex = shelfCount;
            currentSlotIndex = -1;
            return false;
        }

        ordinal++;
        return true;
    }

    /// <summary>
    /// Skips up to <paramref name="count"/> rows without copying or materializing their key bytes.<br/>
    /// The skip advances across shelf-local slot ranges instead of stepping one row at a time.<br/>
    /// </summary>
    /// <param name="count">The maximum number of rows to skip.</param>
    /// <returns>The number of rows actually skipped.</returns>
    public int Skip(int count)
    {
        ThrowIfDisposed();
        if (usePooledStreamingShelfReads)
        {
            throw new InvalidOperationException(
                "A pooled streaming VS8 reader does not support retained-range Skip semantics.");
        }

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

    internal void AddShelfRange(VarKeyScalar8ReadOnly shelf, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        _ = AddShelfRange(shelf, lowerKey, upperKey, rentedShelfBuffer: null);
    }

    /// <summary>
    /// Resolves and appends the matching slot interval from one shelf while transferring any pooled shelf-buffer ownership to the reader.<br/>
    /// A shelf with no matching slots returns its pooled buffer immediately and is not retained in the reader arrays.<br/>
    /// </summary>
    /// <param name="shelf">The validated shelf view.<br/></param>
    /// <param name="lowerKey">The inclusive lower encoded key.<br/></param>
    /// <param name="upperKey">The inclusive upper encoded key.<br/></param>
    /// <param name="rentedShelfBuffer">The optional ArrayPool-owned shelf image backing <paramref name="shelf"/>.<br/></param>
    /// <returns><see langword="true"/> when at least one matching row was retained.<br/></returns>
    private bool AddShelfRange(
        VarKeyScalar8ReadOnly shelf,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        byte[]? rentedShelfBuffer)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS8 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(shelf.IsDescending ? upperKey : lowerKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount &&
            (shelf.IsDescending ? shelf.ReadKeyAt(endSlot).SequenceCompareTo(lowerKey) >= 0 : shelf.ReadKeyAt(endSlot).SequenceCompareTo(upperKey) <= 0))
        {
            endSlot++;
        }

        bool retained = AddShelfRange(shelf, startSlot, endSlot, rentedShelfBuffer);
        if (!retained && rentedShelfBuffer is not null)
            ArrayPool<byte>.Shared.Return(rentedShelfBuffer, clearArray: false);
        return retained;
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
        ArrayPool<VarKeyScalar8ReadOnly>.Shared.Return(shelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        if (rentedShelfBuffers is not null)
        {
            for (int i = 0; i < shelfCount; i++)
            {
                if (rentedShelfBuffers[i] is byte[] rentedShelfBuffer)
                    ArrayPool<byte>.Shared.Return(rentedShelfBuffer, clearArray: false);
            }

            ArrayPool<byte[]>.Shared.Return(rentedShelfBuffers, clearArray: true);
        }
        if (terminalIdentityShelves is not null)
        {
            for (int i = 0; i < shelfCount; i++)
            {
                if (terminalShelfFlags is not null && terminalShelfFlags[i] != 0 && terminalIdentityShelves[i] is not null)
                {
                    ArrayPool<byte>.Shared.Return(terminalIdentityShelves[i], clearArray: false);
                }
            }

            ArrayPool<byte[]>.Shared.Return(terminalIdentityShelves, clearArray: true);
        }

        if (terminalShelfFlags is not null)
        {
            ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
        }

        if (terminalKeys is not null)
        {
            ArrayPool<byte[]>.Shared.Return(terminalKeys, clearArray: true);
        }

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
        terminalShelfFlags = null;
        terminalIdentityShelves = null;
        terminalKeys = null;
        terminalContinuationKey = null;
        terminalContinuationOffset = 0;
        rentedShelfBuffers = null;
        visitedShelves = null;
        visitedRouters = null;
        DataKernel.CoherentReadLease? completedCoherentRead = coherentRead;
        coherentRead = null;
        completedCoherentRead?.Dispose();
        session = null;
        lowerKey = null;
        upperKey = null;
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        retiredStreamingRowCount = 0;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private VarKeyScalar8ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The VS8 range reader is not positioned on a row.");
            }

            return shelves[currentShelfIndex];
        }
    }

    private bool AddShelfRange(
        VarKeyScalar8ReadOnly shelf,
        int startSlot,
        int endSlot,
        byte[]? rentedShelfBuffer = null)
    {
        if (endSlot <= startSlot)
        {
            return false;
        }

        if (shelfCount == shelves.Length)
        {
            GrowShelves();
        }

        shelves[shelfCount] = shelf;
        startSlots[shelfCount] = startSlot;
        endSlots[shelfCount] = endSlot;
        terminalShelfFlags![shelfCount] = 0;
        if (rentedShelfBuffers is not null)
            rentedShelfBuffers[shelfCount] = rentedShelfBuffer!;
        shelfCount++;
        int added = endSlot - startSlot;
        rowCount = checked(rowCount + added);
        if (captureDiagnostics)
        {
            matchingRows += added;
        }
        return true;
    }

    private void AddTerminalIdentityShelfRange(byte[] shelfBytes, byte[] keyBytes, bool physicalDescending)
    {
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

        shelves[shelfCount] = null!;
        startSlots[shelfCount] = 0;
        endSlots[shelfCount] = itemCount;
        terminalShelfFlags![shelfCount] = (byte)(physicalDescending ? 3 : 1);
        terminalIdentityShelves![shelfCount] = shelfBytes;
        terminalKeys![shelfCount] = keyBytes;
        shelfCount++;
        rowCount = checked(rowCount + itemCount);
        if (captureDiagnostics)
        {
            matchingRows += itemCount;
        }
    }

    private bool LoadNextShelfRange()
    {
        if (traversalComplete)
        {
            return false;
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        RouteVisitedContextSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        ReadOnlySpan<byte> localLowerKey = lowerKey ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        ReadOnlySpan<byte> localUpperKey = upperKey ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = routerScratch ??= ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
        Span<byte> routerPage = routerBytes.AsSpan(0, RouterLayout.Size);
        while (pendingCount > 0 || terminalContinuationOffset != 0)
        {
            if (terminalContinuationOffset != 0)
            {
                long continuationOffset = terminalContinuationOffset;
                if (!localVisitedShelves.Add(continuationOffset))
                {
                    terminalContinuationOffset = 0;
                    continue;
                }
                byte[] identityShelfBytes = localSession.ReadTerminalIdentity8ShelfBytes(continuationOffset, terminalContinuationExtentSize);
                if (captureDiagnostics)
                {
                    terminalIdentityShelvesVisited++;
                    terminalBytesTouched += identityShelfBytes.Length;
                }
                terminalContinuationOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(identityShelfBytes);
                AddTerminalIdentityShelfRange(identityShelfBytes,
                    terminalContinuationKey ?? throw new InvalidDataException("The VS8 terminal continuation key is missing."),
                    physicalDescending: true);
                if (rowCount > previousRowCount)
                    return true;
                continue;
            }
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (captureDiagnostics)
            {
                targetsPopped++;
            }
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed VS8 range read exceeded the configured router hop count.");
            }

            VarKeyScalar8RouteTargetKind kind = localSession.ClassifyVarKeyScalar8RouteTarget(targetOffset);
            if (kind == VarKeyScalar8RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                VarKeyScalar8ReadOnly shelf = ReadShelfForCurrentOwnership(
                    localSession,
                    targetOffset,
                    out byte[]? rentedShelfBuffer);
                if (captureDiagnostics)
                {
                    shelvesVisited++;
                    shelfSlotsDecoded += shelf.ItemCount;
                    shelfBytesTouched += shelf.ShelfExtentSize;
                    if (shelf.IsDuplicateRun)
                    {
                        duplicateRunShelvesVisited++;
                    }
                }
                if (descendingTraversal != shelf.IsDescending && shelf.IsDuplicateRun)
                {
                    int firstShelfIndex = shelfCount;
                    while (true)
                    {
                        _ = AddShelfRange(shelf, localLowerKey, localUpperKey, rentedShelfBuffer);
                        long nextOffset = shelf.DuplicateRunNextOffset;
                        if (nextOffset == 0 || !localVisitedShelves.Add(nextOffset))
                        {
                            break;
                        }

                        shelf = ReadShelfForCurrentOwnership(localSession, nextOffset, out rentedShelfBuffer);
                        if (!shelf.IsDuplicateRun)
                        {
                            throw new InvalidDataException("The routed VS8 duplicate-run continuation is not a duplicate-run shelf.");
                        }
                        if (captureDiagnostics)
                        {
                            shelvesVisited++;
                            duplicateRunShelvesVisited++;
                            shelfSlotsDecoded += shelf.ItemCount;
                            shelfBytesTouched += shelf.ShelfExtentSize;
                        }
                    }

                    ReverseShelfRanges(firstShelfIndex, shelfCount);
                }
                else
                {
                    long duplicateRunNextOffset = shelf.IsDuplicateRun
                        ? shelf.DuplicateRunNextOffset
                        : 0;
                    _ = AddShelfRange(shelf, localLowerKey, localUpperKey, rentedShelfBuffer);
                    if (duplicateRunNextOffset != 0)
                    {
                        PushTarget(duplicateRunNextOffset, remainingHops, lowerEdge, upperEdge);
                    }
                }

                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(targetOffset);
                if (captureDiagnostics)
                {
                    terminalRootsVisited++;
                    terminalBytesTouched += rootBytes.Length;
                }
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey)
                {
                    throw new InvalidDataException("The routed VS8 terminal identity root shape is invalid.");
                }

                int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
                byte[] keyBytes = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength).ToArray();
                if (keyBytes.AsSpan().SequenceCompareTo(localLowerKey) < 0 ||
                    keyBytes.AsSpan().SequenceCompareTo(localUpperKey) > 0)
                {
                    continue;
                }

                bool physicalDescending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
                int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                long identityShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
                int firstShelfIndex = shelfCount;
                while (identityShelfOffset != 0)
                {
                    if (!localVisitedShelves.Add(identityShelfOffset))
                    {
                        break;
                    }

                    byte[] identityShelfBytes = localSession.ReadTerminalIdentity8ShelfBytes(identityShelfOffset, shelfExtentSize);
                    if (captureDiagnostics)
                    {
                        terminalIdentityShelvesVisited++;
                        terminalBytesTouched += identityShelfBytes.Length;
                    }
                    long nextOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(identityShelfBytes);
                    AddTerminalIdentityShelfRange(identityShelfBytes, keyBytes, physicalDescending);
                    identityShelfOffset = nextOffset;
                    if (descendingTraversal && physicalDescending)
                    {
                        terminalContinuationOffset = nextOffset;
                        terminalContinuationExtentSize = shelfExtentSize;
                        terminalContinuationKey = keyBytes;
                        break;
                    }
                }
                if (descendingTraversal != physicalDescending && terminalContinuationOffset == 0)
                {
                    ReverseShelfRanges(firstShelfIndex, shelfCount);
                }

                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind != VarKeyScalar8RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed VS8 range target is not a shelf, terminal identity root, or router.");
            }

            if (!localVisitedRouters.Add(targetOffset, lowerEdge, upperEdge))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerPage);
            RouterReader router = new(routerPage);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed VS8 range router is invalid.");
            }

            if (captureDiagnostics)
            {
                routersVisited++;
                routerBytesTouched += RouterLayout.Size;
            }

            if (router.PrefixByteCount > 1)
            {
                if (captureDiagnostics)
                {
                    multiByteRoutersVisited++;
                    routerRoutesExamined += router.RouteCount;
                }

                if (lowerEdge && upperEdge && localLowerKey.SequenceEqual(localUpperKey))
                {
                    long exactTargetOffset = router.FindTarget(localLowerKey, router.KeyDepth, out _);
                    if (exactTargetOffset != 0)
                    {
                        PushTarget(exactTargetOffset, remainingHops - 1, lowerEdge: true, upperEdge: true);
                    }

                    continue;
                }

                int lowerRouteIndex = -1;
                int upperRouteIndex = -1;
                if (lowerEdge)
                {
                    _ = router.FindTarget(localLowerKey, router.KeyDepth, out lowerRouteIndex);
                }

                if (upperEdge)
                {
                    _ = router.FindTarget(localUpperKey, router.KeyDepth, out upperRouteIndex);
                }

                int routeIndex = descendingTraversal ? 0 : router.RouteCount - 1;
                int routeLimit = descendingTraversal ? router.RouteCount : -1;
                int routeStep = descendingTraversal ? 1 : -1;
                for (; routeIndex != routeLimit; routeIndex += routeStep)
                {
                    if (router.TrySelectMultiByteRangeRoute(
                        routeIndex,
                        localLowerKey,
                        localUpperKey,
                        maxKeyLength,
                        lowerEdge,
                        upperEdge,
                        lowerRouteIndex,
                        upperRouteIndex,
                        out long childTargetOffset,
                        out bool childLowerEdge,
                        out bool childUpperEdge))
                    {
                        PushTarget(childTargetOffset, remainingHops - 1, childLowerEdge, childUpperEdge);
                    }
                }

                continue;
            }

            byte lowerPrefix = lowerEdge ? LibraDexFileSession.GetVarKeyScalar8Prefix(localLowerKey, router.KeyDepth) : byte.MinValue;
            byte upperPrefix = upperEdge ? LibraDexFileSession.GetVarKeyScalar8Prefix(localUpperKey, router.KeyDepth) : byte.MaxValue;
            if (captureDiagnostics)
            {
                oneByteRoutersVisited++;
                routerRoutesExamined += upperPrefix - lowerPrefix + 1;
            }
            int prefix = descendingTraversal ? lowerPrefix : upperPrefix;
            while (descendingTraversal ? prefix <= upperPrefix : prefix >= lowerPrefix)
            {
                long childTargetOffset = router.FindTarget((byte)prefix);
                if (childTargetOffset == 0)
                {
                    prefix += descendingTraversal ? 1 : -1;
                    continue;
                }

                int runStart = prefix;
                int runEnd = prefix;
                if (descendingTraversal)
                {
                    while (runEnd < upperPrefix && router.FindTarget((byte)(runEnd + 1)) == childTargetOffset)
                    {
                        runEnd++;
                    }
                }
                else
                {
                    while (runStart > lowerPrefix && router.FindTarget((byte)(runStart - 1)) == childTargetOffset)
                    {
                        runStart--;
                    }
                }

                bool singlePrefixRun = runStart == runEnd;
                PushTarget(
                    childTargetOffset,
                    remainingHops - 1,
                    singlePrefixRun && lowerEdge && runStart == lowerPrefix,
                    singlePrefixRun && upperEdge && runEnd == upperPrefix);
                prefix = descendingTraversal ? runEnd + 1 : runStart - 1;
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
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
        if (captureDiagnostics)
        {
            targetsQueued++;
        }
    }

    /// <summary>
    /// Validates and appends one planner-produced continuation target to this reader's private pending stack.<br/>
    /// Keeping validation at the ownership transfer boundary prevents a malformed partition plan from weakening the ordinary cursor's offset and hop protections.<br/>
    /// </summary>
    /// <param name="target">The disjoint physical continuation target to enqueue.<br/></param>
    private void PushPhysicalTarget(VarKeyScalar8PhysicalTarget target)
    {
        if (target.TargetOffset <= 0)
            throw new InvalidDataException("A physical VS8 partition contained a non-positive target offset.");
        if (target.RemainingRouterHops <= 0)
            throw new InvalidDataException("A physical VS8 partition exhausted its router hop budget before worker traversal began.");

        PushTarget(target.TargetOffset, target.RemainingRouterHops, target.LowerEdge, target.UpperEdge);
    }

    /// <summary>
    /// Appends one disjoint planner-produced continuation target after this reader has drained its current target set.<br/>
    /// The reader retains its worker-owned coherent read and visited-route guards, allowing a FastFind worker to claim bounded topology work dynamically without reopening the index root or crossing a published generation.<br/>
    /// Existing shelf views remain valid until disposal; the next <see cref="MoveNext"/> resumes at the first matching slot loaded for <paramref name="target"/>.<br/>
    /// </summary>
    /// <param name="target">The next disjoint physical continuation target owned by this worker.<br/></param>
    internal void AppendPhysicalTarget(VarKeyScalar8PhysicalTarget target)
    {
        ThrowIfDisposed();
        if (!traversalComplete || pendingCount != 0 || ordinal != rowCount || currentShelfIndex != shelfCount)
        {
            throw new InvalidOperationException(
                "A VS8 continuation target can be appended only after the current target set has been drained completely.");
        }

        bool hadRows = rowCount != 0;
        PushPhysicalTarget(target);
        traversalComplete = false;
        ordinal = rowCount - 1;
        resumeAtAppendedTarget = hadRows;
        if (!hadRows)
            currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private void PopTarget(out long offset, out int remainingHops, out bool lowerEdge, out bool upperEdge)
    {
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
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
        VarKeyScalar8ReadOnly[] newShelves = ArrayPool<VarKeyScalar8ReadOnly>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<VarKeyScalar8ReadOnly>.Shared.Return(shelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        if (terminalShelfFlags is not null)
        {
            byte[] newTerminalFlags = ArrayPool<byte>.Shared.Rent(newLength);
            terminalShelfFlags.AsSpan(0, shelfCount).CopyTo(newTerminalFlags);
            ArrayPool<byte>.Shared.Return(terminalShelfFlags, clearArray: false);
            terminalShelfFlags = newTerminalFlags;
        }

        if (terminalIdentityShelves is not null)
        {
            byte[][] newTerminalShelves = ArrayPool<byte[]>.Shared.Rent(newLength);
            Array.Copy(terminalIdentityShelves, newTerminalShelves, shelfCount);
            ArrayPool<byte[]>.Shared.Return(terminalIdentityShelves, clearArray: true);
            terminalIdentityShelves = newTerminalShelves;
        }

        if (terminalKeys is not null)
        {
            byte[][] newTerminalKeys = ArrayPool<byte[]>.Shared.Rent(newLength);
            Array.Copy(terminalKeys, newTerminalKeys, shelfCount);
            ArrayPool<byte[]>.Shared.Return(terminalKeys, clearArray: true);
            terminalKeys = newTerminalKeys;
        }

        if (rentedShelfBuffers is not null)
        {
            byte[][] newRentedShelfBuffers = ArrayPool<byte[]>.Shared.Rent(newLength);
            Array.Copy(rentedShelfBuffers, newRentedShelfBuffers, shelfCount);
            ArrayPool<byte[]>.Shared.Return(rentedShelfBuffers, clearArray: true);
            rentedShelfBuffers = newRentedShelfBuffers;
        }

        shelves = newShelves;
        startSlots = newStartSlots;
        endSlots = newEndSlots;
    }

    /// <summary>
    /// Reverses a contiguous set of already-retained shelf ranges in place without copying any persisted shelf payload.<br/>
    /// Descending terminal and duplicate-key traversal uses this after following the format's forward-only continuation links once, making the last physical shelf the first range consumed by <see cref="MovePrevious"/>.<br/>
    /// All parallel shelf metadata arrays are exchanged together so key, identity, and slot bounds retain one ownership position.<br/>
    /// </summary>
    /// <param name="startIndex">Inclusive first retained shelf index to reverse.<br/></param>
    /// <param name="endIndexExclusive">Exclusive final retained shelf index to reverse.<br/></param>
    private void ReverseShelfRanges(int startIndex, int endIndexExclusive)
    {
        int left = startIndex;
        int right = endIndexExclusive - 1;
        while (left < right)
        {
            (shelves[left], shelves[right]) = (shelves[right], shelves[left]);
            (startSlots[left], startSlots[right]) = (startSlots[right], startSlots[left]);
            (endSlots[left], endSlots[right]) = (endSlots[right], endSlots[left]);
            (terminalShelfFlags![left], terminalShelfFlags[right]) = (terminalShelfFlags[right], terminalShelfFlags[left]);
            (terminalIdentityShelves![left], terminalIdentityShelves[right]) = (terminalIdentityShelves[right], terminalIdentityShelves[left]);
            (terminalKeys![left], terminalKeys[right]) = (terminalKeys[right], terminalKeys[left]);
            if (rentedShelfBuffers is not null)
                (rentedShelfBuffers[left], rentedShelfBuffers[right]) = (rentedShelfBuffers[right], rentedShelfBuffers[left]);
            left++;
            right--;
        }
    }

    /// <summary>
    /// Loads one ordinary shelf through either the existing session-owned cache contract or the FastFind-only pooled streaming contract.<br/>
    /// </summary>
    /// <param name="localSession">The owning LibraDex session.<br/></param>
    /// <param name="shelfOffset">The physical shelf offset.<br/></param>
    /// <param name="rentedShelfBuffer">The optional ArrayPool-owned backing buffer transferred to this reader.<br/></param>
    /// <returns>The validated shelf view.<br/></returns>
    private VarKeyScalar8ReadOnly ReadShelfForCurrentOwnership(
        LibraDexFileSession localSession,
        long shelfOffset,
        out byte[]? rentedShelfBuffer)
    {
        if (usePooledStreamingShelfReads)
        {
            return localSession.ReadVarKeyScalar8ReadOnlyShelfForStreaming(
                shelfOffset,
                maxKeyLength,
                out rentedShelfBuffer);
        }

        rentedShelfBuffer = null;
        return localSession.ReadVarKeyScalar8ReadOnlyShelf(shelfOffset, maxKeyLength);
    }

    /// <summary>
    /// Returns the pooled shelf image whose final row was consumed by a forward-only streaming cursor.<br/>
    /// The shelf reference is cleared at the same ownership boundary so no later reader operation can observe bytes after ArrayPool reuse.<br/>
    /// </summary>
    /// <param name="completedShelfIndex">The zero-based consumed shelf index.<br/></param>
    private void ReleaseConsumedStreamingShelf(int completedShelfIndex)
    {
        if (rentedShelfBuffers is null || (uint)completedShelfIndex >= (uint)shelfCount)
            return;

        byte[]? rentedShelfBuffer = rentedShelfBuffers[completedShelfIndex];
        if (rentedShelfBuffer is null)
            return;

        rentedShelfBuffers[completedShelfIndex] = null!;
        shelves[completedShelfIndex] = null!;
        ArrayPool<byte>.Shared.Return(rentedShelfBuffer, clearArray: false);
    }

    /// <summary>
    /// Retires a fully consumed forward-only streaming window before the next routed shelf is loaded.<br/>
    /// The ordinary bidirectional cursor retains every shelf range for counting, skipping, and reverse movement; the FastFind streaming cursor supports none of those operations and therefore reuses its metadata slots instead of growing parallel arrays across the entire scan.<br/>
    /// A cumulative row base preserves the externally observed ordinal while shelf views, slot bounds, terminal metadata, and pooled byte ownership become eligible for immediate reuse.<br/>
    /// </summary>
    private void RetireConsumedStreamingWindow()
    {
        if (!usePooledStreamingShelfReads ||
            rowCount == 0 ||
            ordinal + 1 < rowCount)
        {
            return;
        }

        for (int shelfIndex = 0; shelfIndex < shelfCount; shelfIndex++)
        {
            ReleaseConsumedStreamingShelf(shelfIndex);
            if (terminalShelfFlags![shelfIndex] != 0 &&
                terminalIdentityShelves![shelfIndex] is byte[] terminalShelfBytes)
            {
                ArrayPool<byte>.Shared.Return(terminalShelfBytes, clearArray: false);
            }
            shelves[shelfIndex] = null!;
            if (rentedShelfBuffers is not null)
                rentedShelfBuffers[shelfIndex] = null!;
            terminalShelfFlags[shelfIndex] = 0;
            terminalIdentityShelves![shelfIndex] = null!;
            terminalKeys![shelfIndex] = null!;
        }

        retiredStreamingRowCount = checked(retiredStreamingRowCount + rowCount);
        shelfCount = 0;
        rowCount = 0;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
        ordinal = -1;
        resumeAtAppendedTarget = false;
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
                bool physicalDescending = terminalShelfFlags![i] != 0
                    ? (terminalShelfFlags[i] & 2) != 0
                    : shelves[i].IsDescending;
                currentSlotIndex = physicalDescending == descendingTraversal
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

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VarKeyScalar8RangeReader));
        }
    }
}

/// <summary>
/// Captures logical traversal work performed by one diagnostic `VS8` range reader.<br/>
/// Byte counters describe validated structure bytes touched by the reader and remain separate from DataKernel backing-read telemetry.<br/>
/// </summary>
/// <param name="TargetsPopped">The number of pending route targets classified by the reader.<br/></param>
/// <param name="TargetsQueued">The number of nonzero route targets added to the pending stack.<br/></param>
/// <param name="RoutersVisited">The number of router pages visited.<br/></param>
/// <param name="OneByteRoutersVisited">The number of one-byte routers visited.<br/></param>
/// <param name="MultiByteRoutersVisited">The number of compressed multi-byte routers visited.<br/></param>
/// <param name="RouterRoutesExamined">The number of route prefixes or compressed route slots examined.<br/></param>
/// <param name="RouterBytesTouched">The logical router-page bytes touched.<br/></param>
/// <param name="ShelvesVisited">The number of ordinary or duplicate-run shelves decoded.<br/></param>
/// <param name="DuplicateRunShelvesVisited">The number of decoded duplicate-run shelves.<br/></param>
/// <param name="ShelfSlotsDecoded">The total shelf slots decoded into read-only views.<br/></param>
/// <param name="ShelfBytesTouched">The logical ordinary-shelf extent bytes touched.<br/></param>
/// <param name="TerminalRootsVisited">The number of terminal identity roots visited.<br/></param>
/// <param name="TerminalIdentityShelvesVisited">The number of linked terminal identity shelves visited.<br/></param>
/// <param name="TerminalBytesTouched">The logical terminal-root and terminal-shelf bytes touched.<br/></param>
/// <param name="MatchingRows">The number of exact or ranged rows admitted by shelf-local bounds.<br/></param>
internal readonly record struct VarKeyScalar8RangeReadDiagnostics(
    long TargetsPopped,
    long TargetsQueued,
    long RoutersVisited,
    long OneByteRoutersVisited,
    long MultiByteRoutersVisited,
    long RouterRoutesExamined,
    long RouterBytesTouched,
    long ShelvesVisited,
    long DuplicateRunShelvesVisited,
    long ShelfSlotsDecoded,
    long ShelfBytesTouched,
    long TerminalRootsVisited,
    long TerminalIdentityShelvesVisited,
    long TerminalBytesTouched,
    long MatchingRows);
