using System.Buffers;
using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `VS16` range results as a forward-only cursor over raw key byte spans and 16-byte scalar identity halves.<br/>
/// The reader owns routed traversal state and loads matching shelf ranges on demand through the owning session, leaving DK/session responsible for cache-backed reads.<br/>
/// This cursor keeps the fixed-identity-width shape aligned with `VS8` and `VV` so comparisons can separately measure identities, keys, or full tuples.<br/>
/// </summary>
internal sealed class VarKeyScalar16RangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private VarKeyScalar16ReadOnly?[] shelves;
    private byte[]?[] terminalKeys;
    private byte[]?[] terminalShelfBytes;
    private int[] startSlots;
    private int[] endSlots;
    private long[]? pendingOffsets;
    private int[]? pendingHops;
    private byte[]? pendingFlags;
    private byte[]? routerScratch;
    private RouteVisitedOffsetSet? visitedShelves;
    private RouteVisitedOffsetSet? visitedRouters;
    private LibraDexFileSession? session;
    private byte[]? lowerKey;
    private byte[]? upperKey;
    private byte[]? terminalContinuationKey;
    private long terminalContinuationOffset;
    private int terminalContinuationExtentSize;
    private int shelfCount;
    private int rowCount;
    private int pendingCount;
    private int currentShelfIndex;
    private int currentSlotIndex = -1;
    private int ordinal = -1;
    private int maxKeyLength;
    private bool decodeLogicalKeys;
    private bool descendingTraversal;
    private bool traversalComplete = true;
    private bool disposed;

    internal VarKeyScalar16RangeReader()
    {
        shelves = ArrayPool<VarKeyScalar16ReadOnly?>.Shared.Rent(DefaultShelfCapacity);
        terminalKeys = ArrayPool<byte[]?>.Shared.Rent(DefaultShelfCapacity);
        terminalShelfBytes = ArrayPool<byte[]?>.Shared.Rent(DefaultShelfCapacity);
        terminalKeys.AsSpan().Clear();
        terminalShelfBytes.AsSpan().Clear();
        startSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        endSlots = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
    }

    internal VarKeyScalar16RangeReader(
        LibraDexFileSession session,
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int maxRouterHops = 8,
        bool decodeLogicalKeys = false,
        bool descending = false)
        : this()
    {
        this.session = session;
        this.maxKeyLength = maxKeyLength;
        this.decodeLogicalKeys = decodeLogicalKeys;
        descendingTraversal = descending;
        this.lowerKey = lowerKey.ToArray();
        this.upperKey = upperKey.ToArray();
        pendingOffsets = ArrayPool<long>.Shared.Rent(DefaultShelfCapacity);
        pendingHops = ArrayPool<int>.Shared.Rent(DefaultShelfCapacity);
        pendingFlags = ArrayPool<byte>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        byte lowerPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(lowerKey, 0);
        byte upperPrefix = LibraDexFileSession.GetVarKeyScalar8Prefix(upperKey, 0);
        for (int prefix = descending ? lowerPrefix : upperPrefix;
            descending ? prefix <= upperPrefix : prefix >= lowerPrefix;
            prefix += descending ? 1 : -1)
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
    /// The count is derived from shelf-local slot ranges; key bytes and identity bytes are not copied for counting.<br/>
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
            ReadOnlySpan<byte> encoded = ReadCurrentEncodedKey();
            return decodeLogicalKeys ? LibraDexVarLenKeyCodec.DecodePayloadSpan(encoded) : encoded;
        }
    }

    /// <summary>
    /// Gets whether the current logical key is the null-key sentinel.<br/>
    /// This is false for raw encoded readers and for logical empty keys; callers can combine it with <see cref="CurrentKeyLength"/> to distinguish null from empty.<br/>
    /// </summary>
    public bool CurrentKeyIsNull => decodeLogicalKeys && LibraDexVarLenKeyCodec.IsNull(ReadCurrentEncodedKey());

    /// <summary>
    /// Gets the high encoded 64 bits of the current 16-byte identity.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentityHigh
    {
        get
        {
            ReadCurrentEncodedIdentity(out ulong high, out _);
            return high;
        }
    }

    /// <summary>
    /// Gets the low encoded 64 bits of the current 16-byte identity.<br/>
    /// </summary>
    public ulong CurrentEncodedIdentityLow
    {
        get
        {
            ReadCurrentEncodedIdentity(out _, out ulong low);
            return low;
        }
    }

    /// <summary>
    /// Gets the current key length without copying the key payload.<br/>
    /// </summary>
    public int CurrentKeyLength => CurrentKey.Length;

    /// <summary>
    /// Reads both encoded halves of the current 16-byte identity in one shelf access.<br/>
    /// </summary>
    /// <param name="encodedIdentityHigh">The high encoded identity half.</param>
    /// <param name="encodedIdentityLow">The low encoded identity half.</param>
    public void ReadCurrentIdentity(out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        ReadCurrentEncodedIdentity(out encodedIdentityHigh, out encodedIdentityLow);
    }

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

    internal void AddShelfRange(VarKeyScalar16ReadOnly shelf, ReadOnlySpan<byte> lowerKey, ReadOnlySpan<byte> upperKey)
    {
        if (!shelf.IsValid)
        {
            throw new InvalidDataException("The routed VS16 range target shelf is invalid.");
        }

        int startSlot = shelf.LowerBoundKey(shelf.IsDescending ? upperKey : lowerKey);
        int endSlot = startSlot;
        while (endSlot < shelf.ItemCount &&
            (shelf.IsDescending ? shelf.ReadKeyAt(endSlot).SequenceCompareTo(lowerKey) >= 0 : shelf.ReadKeyAt(endSlot).SequenceCompareTo(upperKey) <= 0))
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
        ArrayPool<VarKeyScalar16ReadOnly?>.Shared.Return(shelves, clearArray: true);
        ArrayPool<byte[]?>.Shared.Return(terminalKeys, clearArray: true);
        for (int i = 0; i < shelfCount; i++)
        {
            if (terminalShelfBytes[i] is byte[] terminalBytes)
                ArrayPool<byte>.Shared.Return(terminalBytes, clearArray: false);
        }
        ArrayPool<byte[]?>.Shared.Return(terminalShelfBytes, clearArray: true);
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
        lowerKey = null;
        upperKey = null;
        terminalContinuationKey = null;
        terminalContinuationOffset = 0;
        terminalContinuationExtentSize = 0;
        shelfCount = 0;
        rowCount = 0;
        pendingCount = 0;
        ordinal = -1;
        currentShelfIndex = 0;
        currentSlotIndex = -1;
    }

    private VarKeyScalar16ReadOnly CurrentShelf
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
            {
                throw new InvalidOperationException("The VS16 range reader is not positioned on a row.");
            }

            return shelves[currentShelfIndex] ??
                throw new InvalidOperationException("The current VS16 cursor segment is a terminal identity shelf, not an ordinary shelf.");
        }
    }

    private void AddShelfRange(VarKeyScalar16ReadOnly shelf, int startSlot, int endSlot)
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
        terminalKeys[shelfCount] = null;
        terminalShelfBytes[shelfCount] = null;
        startSlots[shelfCount] = startSlot;
        endSlots[shelfCount] = endSlot;
        shelfCount++;
        rowCount = checked(rowCount + endSlot - startSlot);
    }

    /// <summary>
    /// Adds one terminal scalar-16 identity shelf as a native cursor segment for its root-owned exact key.<br/>
    /// The segment retains the terminal shelf bytes directly and therefore avoids rebuilding an ordinary `VS16` shelf with the same variable key repeated for every identity.<br/>
    /// </summary>
    /// <param name="key">The exact encoded key stored once by the terminal root.<br/></param>
    /// <param name="shelfBytes">The validated terminal variable-identity shelf bytes.<br/></param>
    private void AddTerminalShelfRange(byte[] key, byte[] shelfBytes)
    {
        int itemCount = TerminalVarIdentityShelfLayout.ReadItemCount(shelfBytes);
        if (itemCount <= 0)
        {
            ArrayPool<byte>.Shared.Return(shelfBytes, clearArray: false);
            return;
        }

        for (int i = 0; i < itemCount; i++)
        {
            if (TerminalVarIdentityShelfLayout.ReadIdentityAt(shelfBytes, i).Length != 16)
            {
                throw new InvalidDataException("The VS16 terminal cursor encountered a non-16-byte identity.");
            }
        }
        int newRowCount = checked(rowCount + itemCount);

        if (shelfCount == shelves.Length)
        {
            GrowShelves();
        }

        shelves[shelfCount] = default;
        terminalKeys[shelfCount] = key;
        terminalShelfBytes[shelfCount] = shelfBytes;
        startSlots[shelfCount] = 0;
        endSlots[shelfCount] = itemCount;
        shelfCount++;
        rowCount = newRowCount;
    }

    /// <summary>
    /// Reads the encoded key for the current ordinary or terminal cursor segment without allocating.<br/>
    /// Ordinary segments read the shelf record key; terminal segments return the key stored once by the terminal root.<br/>
    /// </summary>
    /// <returns>The current encoded key span.<br/></returns>
    private ReadOnlySpan<byte> ReadCurrentEncodedKey()
    {
        ThrowIfDisposed();
        if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
        {
            throw new InvalidOperationException("The VS16 range reader is not positioned on a row.");
        }

        byte[]? terminalKey = terminalKeys[currentShelfIndex];
        return terminalKey is null
            ? CurrentShelf.ReadKeyAt(currentSlotIndex)
            : terminalKey;
    }

    /// <summary>
    /// Reads the encoded scalar-16 identity for the current ordinary or terminal cursor segment.<br/>
    /// Terminal segments decode the canonical big-endian high/low halves directly from the retained terminal shelf bytes.<br/>
    /// </summary>
    /// <param name="encodedIdentityHigh">Receives the high encoded identity half.<br/></param>
    /// <param name="encodedIdentityLow">Receives the low encoded identity half.<br/></param>
    private void ReadCurrentEncodedIdentity(out ulong encodedIdentityHigh, out ulong encodedIdentityLow)
    {
        ThrowIfDisposed();
        if ((uint)ordinal >= (uint)rowCount || (uint)currentShelfIndex >= (uint)shelfCount)
        {
            throw new InvalidOperationException("The VS16 range reader is not positioned on a row.");
        }

        byte[]? terminalBytes = terminalShelfBytes[currentShelfIndex];
        if (terminalBytes is null)
        {
            CurrentShelf.ReadIdentityAt(currentSlotIndex, out encodedIdentityHigh, out encodedIdentityLow);
            return;
        }

        ReadOnlySpan<byte> identity = TerminalVarIdentityShelfLayout.ReadIdentityAt(terminalBytes, currentSlotIndex);
        if (identity.Length != 16)
        {
            throw new InvalidDataException("The VS16 terminal cursor encountered a non-16-byte identity.");
        }

        encodedIdentityHigh = BinaryPrimitives.ReadUInt64BigEndian(identity[..sizeof(ulong)]);
        encodedIdentityLow = BinaryPrimitives.ReadUInt64BigEndian(identity.Slice(sizeof(ulong), sizeof(ulong)));
    }

    private bool LoadNextShelfRange()
    {
        if (traversalComplete)
        {
            return false;
        }

        LibraDexFileSession localSession = session ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        RouteVisitedOffsetSet localVisitedShelves = visitedShelves ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        RouteVisitedOffsetSet localVisitedRouters = visitedRouters ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        ReadOnlySpan<byte> localLowerKey = lowerKey ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        ReadOnlySpan<byte> localUpperKey = upperKey ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        int previousRowCount = rowCount;
        byte[] routerBytes = routerScratch ??= ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
        Span<byte> routerPage = routerBytes.AsSpan(0, RouterLayout.Size);
        while (pendingCount > 0 || terminalContinuationOffset != 0)
        {
            if (terminalContinuationOffset != 0)
            {
                long terminalShelfOffset = terminalContinuationOffset;
                if (!localVisitedShelves.Add(terminalShelfOffset))
                    throw new InvalidDataException("The VS16 terminal identity chain contains a repeated shelf offset.");
                byte[] terminalBytes = localSession.ReadTerminalVarIdentityShelfBytes(
                    terminalShelfOffset, terminalContinuationExtentSize);
                try
                {
                    TerminalVarIdentityShelfLayout.Validate(terminalBytes, terminalContinuationExtentSize);
                    terminalContinuationOffset = TerminalVarIdentityShelfLayout.ReadNextShelfOffset(terminalBytes);
                    AddTerminalShelfRange(terminalContinuationKey ?? throw new InvalidDataException("The VS16 terminal continuation has no key."), terminalBytes);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(terminalBytes, clearArray: false);
                    throw;
                }
                if (rowCount > previousRowCount)
                    return true;
                continue;
            }

            PopTarget(out long targetOffset, out int remainingHops, out bool lowerEdge, out bool upperEdge);
            if (remainingHops <= 0)
            {
                throw new InvalidDataException("The routed VS16 range read exceeded the configured router hop count.");
            }

            VarKeyScalar16RouteTargetKind kind = localSession.ClassifyVarKeyScalar16RouteTarget(targetOffset);
            if (kind == VarKeyScalar16RouteTargetKind.Shelf)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                VarKeyScalar16ReadOnly shelf = localSession.ReadVarKeyScalar16ReadOnlyShelf(targetOffset, maxKeyLength);
                AddShelfRange(shelf, localLowerKey, localUpperKey);
                if (rowCount > previousRowCount)
                {
                    return true;
                }

                continue;
            }

            if (kind == VarKeyScalar16RouteTargetKind.TerminalIdentityRoot)
            {
                if (!localVisitedShelves.Add(targetOffset))
                {
                    continue;
                }

                byte[] rootBytes = localSession.ReadTerminalIdentityRootBytes(targetOffset);
                if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity)
                {
                    throw new InvalidDataException("The routed VS16 range terminal root has the wrong shape.");
                }

                int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
                byte[] terminalKey = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength).ToArray();
                if (terminalKey.AsSpan().SequenceCompareTo(localLowerKey) >= 0 &&
                    terminalKey.AsSpan().SequenceCompareTo(localUpperKey) <= 0)
                {
                    terminalContinuationKey = terminalKey;
                    terminalContinuationExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
                    terminalContinuationOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
                }

                continue;
            }

            if (kind != VarKeyScalar16RouteTargetKind.Router)
            {
                throw new InvalidDataException("The routed VS16 range target is not a shelf or router.");
            }

            if (!localVisitedRouters.Add(targetOffset))
            {
                continue;
            }

            localSession.ReadRouterPageUsingArenaCache(targetOffset, routerPage);
            RouterReader router = new(routerPage);
            if (!router.IsValid)
            {
                throw new InvalidDataException("The routed VS16 range router is invalid.");
            }

            if (router.PrefixByteCount > 1)
            {
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

                for (int routeIndex = descendingTraversal ? 0 : router.RouteCount - 1;
                    descendingTraversal ? routeIndex < router.RouteCount : routeIndex >= 0;
                    routeIndex += descendingTraversal ? 1 : -1)
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
            int prefix = descendingTraversal ? lowerPrefix : upperPrefix;
            while (descendingTraversal ? prefix <= upperPrefix : prefix >= lowerPrefix)
            {
                long childTargetOffset = router.FindTarget((byte)prefix);
                if (childTargetOffset == 0)
                {
                    prefix += descendingTraversal ? 1 : -1;
                    continue;
                }

                int runEnd = prefix;
                int runStart = prefix;
                while (descendingTraversal && runEnd < upperPrefix && router.FindTarget((byte)(runEnd + 1)) == childTargetOffset)
                {
                    runEnd++;
                }
                while (!descendingTraversal && runStart > lowerPrefix && router.FindTarget((byte)(runStart - 1)) == childTargetOffset)
                {
                    runStart--;
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
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
        long[] localOffsets = pendingOffsets ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        int[] localHops = pendingHops ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        byte[] localFlags = pendingFlags ?? throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
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
        VarKeyScalar16ReadOnly?[] newShelves = ArrayPool<VarKeyScalar16ReadOnly?>.Shared.Rent(newLength);
        byte[]?[] newTerminalKeys = ArrayPool<byte[]?>.Shared.Rent(newLength);
        byte[]?[] newTerminalShelfBytes = ArrayPool<byte[]?>.Shared.Rent(newLength);
        newTerminalKeys.AsSpan().Clear();
        newTerminalShelfBytes.AsSpan().Clear();
        int[] newStartSlots = ArrayPool<int>.Shared.Rent(newLength);
        int[] newEndSlots = ArrayPool<int>.Shared.Rent(newLength);
        Array.Copy(shelves, newShelves, shelfCount);
        Array.Copy(terminalKeys, newTerminalKeys, shelfCount);
        Array.Copy(terminalShelfBytes, newTerminalShelfBytes, shelfCount);
        startSlots.AsSpan(0, shelfCount).CopyTo(newStartSlots);
        endSlots.AsSpan(0, shelfCount).CopyTo(newEndSlots);
        ArrayPool<VarKeyScalar16ReadOnly?>.Shared.Return(shelves, clearArray: true);
        ArrayPool<byte[]?>.Shared.Return(terminalKeys, clearArray: true);
        ArrayPool<byte[]?>.Shared.Return(terminalShelfBytes, clearArray: true);
        ArrayPool<int>.Shared.Return(startSlots, clearArray: false);
        ArrayPool<int>.Shared.Return(endSlots, clearArray: false);
        shelves = newShelves;
        terminalKeys = newTerminalKeys;
        terminalShelfBytes = newTerminalShelfBytes;
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
            throw new ObjectDisposedException(nameof(VarKeyScalar16RangeReader));
        }
    }
}
