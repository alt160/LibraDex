using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SV8` range results as a forward-only, span-based cursor over raw identity bytes.<br/>
/// The reader stores only matching shelf ranges, then advances slot-by-slot on demand so result rows are not materialized into arrays or per-row references.<br/>
/// This avoids `byte[][]` materialization while still giving callers ergonomic row-wise access, skip control, and explicit copy points for identities they choose to keep.<br/>
/// </summary>
internal sealed class Scalar8VarIdentityRangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private Scalar8VarIdentityReadOnly[] shelves;
    private byte[][] terminalShelves;
    private int[] startSlots;
    private int[] endSlots;
    private byte[] terminalFlags;
    private ulong[] terminalKeys;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedContextSet? visitedRouters;
    private LibraDexFileSession? session;
    private long indexRootOffset;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private long pendingTerminalShelfOffset;
    private int pendingTerminalShelfExtentSize;
    private ulong pendingTerminalKey;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private int maxIdentityLength;
    private ulong lowerEncodedKey;
    private ulong upperEncodedKey;
    private bool traversalComplete = true;
    private bool descendingTraversal;
    private bool physicalDescending;
    private bool reversePhysicalSlots;
    private bool disposed;

    internal static bool UseExhaustiveRouteTraversal { get; set; }

    internal static bool ScanDescendantRoutesForPrunedReads { get; set; }

    internal Scalar8VarIdentityRangeReader()
    {
        shelves = ArrayPool<Scalar8VarIdentityReadOnly>.Shared.Rent(DefaultShelfCapacity);
        terminalShelves = ArrayPool<byte[]>.Shared.Rent(DefaultShelfCapacity);
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        terminalFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        terminalKeys = ArrayPool<ulong>.Shared.Rent(DefaultShelfCapacity);
    }

    internal Scalar8VarIdentityRangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        bool physicalDescending = false)
        : this()
    {
        Reset(session, rootRouterOffset, maxIdentityLength, lowerEncodedKey, upperEncodedKey, direction, physicalDescending);
    }

    /// <summary>
    /// Reinitializes the reader for a new `SV8` key range while retaining rented traversal buffers.<br/>
    /// This is used by exact-key batch probes where the caller has a fixed key set and wants to process sorted keys without paying full reader setup/teardown for each key.<br/>
    /// </summary>
    /// <param name="session">The opened LibraDex session that owns the root router.</param>
    /// <param name="rootRouterOffset">The root router offset for the `SV8` index.</param>
    /// <param name="maxIdentityLength">The maximum raw identity length accepted by the index.</param>
    /// <param name="lowerEncodedKey">The inclusive lower encoded scalar key.</param>
    /// <param name="upperEncodedKey">The inclusive upper encoded scalar key.</param>
    /// <param name="physicalDescending">Whether the underlying physical shelf profile stores tuples in descending natural order.<br/></param>
    /// <param name="direction">The requested traversal direction for the matching keys and identities.<br/></param>
    internal void Reset(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxIdentityLength,
        ulong lowerEncodedKey,
        ulong upperEncodedKey,
        QueryDirection direction = QueryDirection.Ascending,
        bool physicalDescending = false)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        }

        if (lowerEncodedKey > upperEncodedKey)
        {
            throw new ArgumentException("The upper SV8 key must be greater than or equal to the lower key.", nameof(upperEncodedKey));
        }
        if (direction is not QueryDirection.Ascending and not QueryDirection.Descending)
            throw new ArgumentOutOfRangeException(nameof(direction));

        ClearLoadedRanges();
        this.session = session;
        indexRootOffset = rootRouterOffset;
        this.maxIdentityLength = maxIdentityLength;
        this.lowerEncodedKey = lowerEncodedKey;
        this.upperEncodedKey = upperEncodedKey;
        descendingTraversal = direction == QueryDirection.Descending;
        this.physicalDescending = physicalDescending;
        reversePhysicalSlots = descendingTraversal != physicalDescending;
        pendingOffsets ??= ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops ??= ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags ??= ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves ??= RouteVisitedOffsetSet.Rent();
        visitedRouters ??= RouteVisitedContextSet.Rent();
        visitedShelves.Clear();
        visitedRouters.Clear();
        pendingCount = 0;
        pendingTerminalShelfOffset = 0;
        pendingTerminalShelfExtentSize = 0;
        pendingTerminalKey = 0;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
        ordinal = -1;
        traversalComplete = false;

        byte lowerPrefix = UseExhaustiveRouteTraversal ? byte.MinValue : LibraDexFileSession.GetScalar8VarIdentityPrefix(lowerEncodedKey, 0);
        byte upperPrefix = UseExhaustiveRouteTraversal ? byte.MaxValue : LibraDexFileSession.GetScalar8VarIdentityPrefix(upperEncodedKey, 0);
        for (int prefix = descendingTraversal ? lowerPrefix : upperPrefix;
             descendingTraversal ? prefix <= upperPrefix : prefix >= lowerPrefix;
             prefix += descendingTraversal ? 1 : -1)
        {
            long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset != 0)
            {
                PushTarget(targetOffset, 32, prefix == lowerPrefix, prefix == upperPrefix);
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
    /// Gets the encoded scalar key for the current row.<br/>
    /// The caller must call <see cref="MoveNext"/> successfully before reading this property.<br/>
    /// </summary>
    public ulong CurrentEncodedKey => terminalFlags[currentShelfIndex] == 0 ? CurrentShelf.ReadKeyAt(currentSlotIndex) : terminalKeys[currentShelfIndex];

    /// <summary>
    /// Gets the raw identity bytes for the current row.<br/>
    /// The returned span is valid until <see cref="MoveNext"/> is called again or the reader is disposed.<br/>
    /// </summary>
    public ReadOnlySpan<byte> CurrentIdentity => terminalFlags[currentShelfIndex] == 0
        ? CurrentShelf.ReadIdentityAt(currentSlotIndex)
        : TerminalVarIdentityShelfLayout.ReadIdentityAt(terminalShelves[currentShelfIndex], currentSlotIndex);

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
            currentSlotIndex = reversePhysicalSlots ? endSlots[0] - 1 : startSlots[0];
        }
        else
        {
            currentSlotIndex += reversePhysicalSlots ? -1 : 1;
            while (currentShelfIndex < shelfCount &&
                   (reversePhysicalSlots ? currentSlotIndex < startSlots[currentShelfIndex] : currentSlotIndex >= endSlots[currentShelfIndex]))
            {
                currentShelfIndex++;
                if (currentShelfIndex < shelfCount)
                {
                    currentSlotIndex = reversePhysicalSlots ? endSlots[currentShelfIndex] - 1 : startSlots[currentShelfIndex];
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

    internal void AddShelfRange(byte[] shelfBytes, Scalar8VarIdentityProfile profile, ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        Scalar8VarIdentityReadOnly shelf = new(shelfBytes, profile, validateRecords: false);
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed SV8 range target shelf is invalid.");
        }

        AddShelfRange(shelf, lowerEncodedKey, upperEncodedKey);
    }

    private void AddShelfRange(Scalar8VarIdentityReadOnly shelf, ulong lowerEncodedKey, ulong upperEncodedKey)
    {
        int startSlot = shelf.LowerBoundKey(physicalDescending ? upperEncodedKey : lowerEncodedKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount &&
               (physicalDescending
                   ? shelf.ReadKeyAt(endSlot) >= lowerEncodedKey
                   : shelf.ReadKeyAt(endSlot) <= upperEncodedKey))
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
        ArrayPool<Scalar8VarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalFlags[i] != 0)
            {
                byte[]? terminalShelf = terminalShelves[i];
                if (terminalShelf is not null)
                {
                    ArrayPool<byte>.Shared.Return(terminalShelf, clearArray: false);
                }
            }
        }

        ArrayPool<byte[]>.Shared.Return(terminalShelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(terminalFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeys, clearArray: false);
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
        visitedShelves = null;
        visitedRouters = null;
        session = null;
        pendingFlags = null;
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private Scalar8VarIdentityReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The SV8 range reader is not positioned on a row.");
            }

            return shelves[currentShelfIndex];
        }
    }

    private void ClearLoadedRanges()
    {
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalFlags[i] != 0)
            {
                byte[]? terminalShelf = terminalShelves[i];
                if (terminalShelf is not null)
                {
                    ArrayPool<byte>.Shared.Return(terminalShelf, clearArray: false);
                }
            }

            shelves[i] = null!;
            terminalShelves[i] = null!;
            startSlots[i] = 0;
            endSlots[i] = 0;
            terminalFlags[i] = 0;
            terminalKeys[i] = 0;
        }

        shelfCount = 0;
        rowCount = 0;
    }


    private void AddShelfRange(Scalar8VarIdentityReadOnly shelf, int startSlot, int endSlot)
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
        terminalShelves[shelfCount] = null!;
        startSlots[shelfCount] = startSlot;
        endSlots[shelfCount] = endSlot;
        terminalFlags[shelfCount] = 0;
        terminalKeys[shelfCount] = 0;
        shelfCount++;
        rowCount = checked(rowCount + endSlot - startSlot);
    }

    private void AddTerminalVarIdentityShelfRange(byte[] shelfBytes, ulong encodedKey)
    {
        TerminalVarIdentityShelfLayout.Validate(shelfBytes, TerminalVarIdentityShelfLayout.ReadShelfExtentSize(shelfBytes));
        int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
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
        terminalShelves[shelfCount] = shelfBytes;
        startSlots[shelfCount] = 0;
        endSlots[shelfCount] = itemCount;
        terminalFlags[shelfCount] = 1;
        terminalKeys[shelfCount] = encodedKey;
        shelfCount++;
        rowCount = checked(rowCount + itemCount);
    }

    private bool LoadNextShelfRange()
    {
        if (traversalComplete)
        {
            return false;
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        RouteVisitedContextSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        int previousRowCount = rowCount;
        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        while (pendingTerminalShelfOffset != 0)
        {
            long terminalShelfOffset = pendingTerminalShelfOffset;
            if (!localVisitedShelves.Add(terminalShelfOffset))
            {
                pendingTerminalShelfOffset = 0;
                break;
            }

            byte[] terminalShelfBytes = localSession.ReadTerminalVarIdentityShelfBytes(terminalShelfOffset, pendingTerminalShelfExtentSize);
            pendingTerminalShelfOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(terminalShelfBytes);
            AddTerminalVarIdentityShelfRange(terminalShelfBytes, pendingTerminalKey);
            if (rowCount > previousRowCount)
                return true;
        }

        while (pendingCount > 0)
        {
            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed SV8 range read exceeded the configured router hop count.");
            }

            Scalar8VarIdentityRouteTargetKind kind = localSession.ClassifyScalar8VarIdentityRouteTarget(targetOffset);
            if (kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
            {
                byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(targetOffset);
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeScalar8VarIdentity ||
                    TerminalIdentityRootLayout.ReadKeyLength(rootBytes) != Scalar8VarIdentityLayout.KeySize)
                {
                    throw new InvalidDataException("The routed SV8 terminal var identity root is invalid.");
                }

                ulong encodedKey = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, Scalar8VarIdentityLayout.KeySize));
                if (encodedKey >= lowerEncodedKey && encodedKey <= upperEncodedKey)
                {
                    if (rowCount == 0)
                    {
                        physicalDescending = rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 0;
                        reversePhysicalSlots = descendingTraversal != physicalDescending;
                    }

                    int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                    long terminalShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
                    List<byte[]>? reverseShelves = reversePhysicalSlots ? new List<byte[]>() : null;
                    while (terminalShelfOffset != 0)
                    {
                        if (!localVisitedShelves.Add(terminalShelfOffset))
                        {
                            break;
                        }

                        byte[] terminalShelfBytes = localSession.ReadTerminalVarIdentityShelfBytes(terminalShelfOffset, shelfExtentSize);
                        long nextOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(terminalShelfBytes);
                        if (reverseShelves is null)
                        {
                            AddTerminalVarIdentityShelfRange(terminalShelfBytes, encodedKey);
                            if (rowCount > previousRowCount)
                            {
                                pendingTerminalShelfOffset = nextOffset;
                                pendingTerminalShelfExtentSize = shelfExtentSize;
                                pendingTerminalKey = encodedKey;
                                return true;
                            }
                        }
                        else
                            reverseShelves.Add(terminalShelfBytes);
                        terminalShelfOffset = nextOffset;
                    }

                    if (reverseShelves is not null)
                    {
                        for (int i = reverseShelves.Count - 1; i >= 0; i--)
                            AddTerminalVarIdentityShelfRange(reverseShelves[i], encodedKey);
                    }

                    if (rowCount > previousRowCount)
                    {
                        return true;
                    }
                }

                continue;
            }

            if (kind == Scalar8VarIdentityRouteTargetKind.Shelf)
            {
                long currentShelfOffset = targetOffset;
                while (currentShelfOffset != 0)
                {
                    if (!localVisitedShelves.Add(currentShelfOffset))
                    {
                        break;
                    }

                    Scalar8VarIdentityReadOnly shelf = localSession.ReadScalar8VarIdentityReadOnlyShelf(
                        indexRootOffset,
                        currentShelfOffset,
                        maxIdentityLength);
                    if (rowCount == 0)
                    {
                        physicalDescending = shelf.IsDescending;
                        reversePhysicalSlots = descendingTraversal != physicalDescending;
                    }

                    if (reversePhysicalSlots && shelf.NextShelfOffset != 0)
                    {
                        List<Scalar8VarIdentityReadOnly> chain = new() { shelf };
                        long nextOffset = shelf.NextShelfOffset;
                        while (nextOffset != 0 && localVisitedShelves.Add(nextOffset))
                        {
                            Scalar8VarIdentityReadOnly next = localSession.ReadScalar8VarIdentityReadOnlyShelf(indexRootOffset, nextOffset, maxIdentityLength);
                            chain.Add(next);
                            nextOffset = next.NextShelfOffset;
                        }
                        for (int i = chain.Count - 1; i >= 0; i--)
                            AddShelfRange(chain[i], lowerEncodedKey, upperEncodedKey);
                        if (rowCount > previousRowCount)
                            return true;
                        break;
                    }
                    AddShelfRange(shelf, lowerEncodedKey, upperEncodedKey);
                    currentShelfOffset = shelf.NextShelfOffset;
                    if (rowCount > previousRowCount)
                    {
                        if (currentShelfOffset != 0)
                        {
                            PushTarget(currentShelfOffset, remainingHops, lowerEdge, upperEdge);
                        }

                        return true;
                    }
                }

                continue;
            }

            if (kind != Scalar8VarIdentityRouteTargetKind.Router || !localVisitedRouters.Add(targetOffset, lowerEdge, upperEdge))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed SV8 range target router is invalid.");
            }

            bool exhaustiveForRouter = UseExhaustiveRouteTraversal ||
                (ScanDescendantRoutesForPrunedReads && (lowerEdge || upperEdge));
            byte edgeLowerPrefix = lowerEdge ? LibraDexFileSession.GetScalar8VarIdentityPrefix(lowerEncodedKey, router.KeyDepth) : byte.MinValue;
            byte edgeUpperPrefix = upperEdge ? LibraDexFileSession.GetScalar8VarIdentityPrefix(upperEncodedKey, router.KeyDepth) : byte.MaxValue;
            byte scanLowerPrefix = exhaustiveForRouter ? byte.MinValue : edgeLowerPrefix;
            byte scanUpperPrefix = exhaustiveForRouter ? byte.MaxValue : edgeUpperPrefix;
            long previousRouteTarget = 0;
            for (int prefix = descendingTraversal ? scanLowerPrefix : scanUpperPrefix;
                 descendingTraversal ? prefix <= scanUpperPrefix : prefix >= scanLowerPrefix;
                 prefix += descendingTraversal ? 1 : -1)
            {
                long routeTarget = router.FindTarget((byte)prefix);
                if (routeTarget != 0 && routeTarget != previousRouteTarget)
                {
                    PushTarget(routeTarget, remainingHops - 1, lowerEdge && prefix == edgeLowerPrefix, upperEdge && prefix == edgeUpperPrefix);
                }

                previousRouteTarget = routeTarget;
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
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
        Scalar8VarIdentityReadOnly[] newShelves = ArrayPool<Scalar8VarIdentityReadOnly>.Shared.Rent(newLength);
        byte[][] newTerminalShelves = ArrayPool<byte[]>.Shared.Rent(newLength);
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        byte[] newTerminalFlags = ArrayPool<byte>.Shared.Rent(newLength);
        ulong[] newTerminalKeys = ArrayPool<ulong>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        Array.Copy(terminalShelves, newTerminalShelves, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        terminalFlags.AsSpan(0, shelfCount).CopyTo(newTerminalFlags);
        terminalKeys.AsSpan(0, shelfCount).CopyTo(newTerminalKeys);
        ArrayPool<Scalar8VarIdentityReadOnly>.Shared.Return(shelves, clearArray: true);
        ArrayPool<byte[]>.Shared.Return(terminalShelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        ArrayPool<byte>.Shared.Return(terminalFlags, clearArray: false);
        ArrayPool<ulong>.Shared.Return(terminalKeys, clearArray: false);
        shelves = newShelves;
        terminalShelves = newTerminalShelves;
        startSlots = newStartSlots;
        endSlots = newEndSlots;
        terminalFlags = newTerminalFlags;
        terminalKeys = newTerminalKeys;
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
                currentSlotIndex = reversePhysicalSlots ? endSlots[i] - 1 - remaining : startSlots[i] + remaining;
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
            throw new ObjectDisposedException(nameof(Scalar8VarIdentityRangeReader));
        }
    }
}
