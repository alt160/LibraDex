using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `VS8` range results as a forward-only cursor over raw key byte spans and scalar identity values.<br/>
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
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedContextSet? visitedRouters;
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
    private bool decodeLogicalKeys;
    private bool captureDiagnostics;
    private bool traversalComplete = true;
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
        bool captureDiagnostics = false)
        : this()
    {
        this.session = session;
        this.maxKeyLength = maxKeyLength;
        this.decodeLogicalKeys = decodeLogicalKeys;
        this.captureDiagnostics = captureDiagnostics;
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
    /// The count is derived from shelf-local slot ranges; key bytes are not copied and per-row references are not allocated.<br/>
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
    /// Skips up to <paramref name="count"/> rows without copying or materializing their key bytes.<br/>
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
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS8 range target shelf is invalid.");
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
        ArrayPool<VarKeyScalar8ReadOnly>.Shared.Return(shelves, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
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

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingOffsets = null;
        pendingHops = null;
        pendingFlags = null;
        terminalShelfFlags = null;
        terminalIdentityShelves = null;
        terminalKeys = null;
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

    private void AddShelfRange(VarKeyScalar8ReadOnly shelf, int startSlot, int endSlot)
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
        terminalShelfFlags![shelfCount] = 0;
        shelfCount++;
        int added = endSlot - startSlot;
        rowCount = checked(rowCount + added);
        if (captureDiagnostics)
        {
            matchingRows += added;
        }
    }

    private void AddTerminalIdentityShelfRange(byte[] shelfBytes, byte[] keyBytes)
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
        terminalShelfFlags![shelfCount] = 1;
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
        byte[] routerBytes = new byte[RouterLayout.Size];
        while (pendingCount > 0)
        {
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

                VarKeyScalar8ReadOnly shelf = localSession.ReadVarKeyScalar8ReadOnlyShelf(targetOffset, maxKeyLength);
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
                AddShelfRange(shelf, localLowerKey, localUpperKey);
                if (shelf.IsValid && shelf.IsDuplicateRun && shelf.DuplicateRunNextOffset != 0)
                {
                    PushTarget(shelf.DuplicateRunNextOffset, remainingHops, lowerEdge, upperEdge);
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

                int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                long identityShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
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
                    AddTerminalIdentityShelfRange(identityShelfBytes, keyBytes);
                    identityShelfOffset = nextOffset;
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

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
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

                for (int routeIndex = router.RouteCount - 1; routeIndex >= 0; routeIndex--)
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
            for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
            {
                long childTargetOffset = router.FindTarget((byte)prefix);
                if (childTargetOffset != 0)
                {
                    PushTarget(childTargetOffset, remainingHops - 1, lowerEdge && prefix == lowerPrefix, upperEdge && prefix == upperPrefix);
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
