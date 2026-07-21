using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Reads `SV16` range results as a forward-only, span-based cursor over raw identity bytes.<br/>
/// The reader stores only matching shelf ranges, then advances slot-by-slot on demand so result rows are not materialized into arrays or per-row references.<br/>
/// This avoids `byte[][]` materialization while still giving callers ergonomic row-wise access, skip control, and explicit copy points for identities they choose to keep.<br/>
/// </summary>
internal sealed class Scalar16VarIdentityRangeReader : IDisposable
{
    private const int DefaultShelfCapacity = 8;

    private Scalar16VarIdentityReadOnly[] shelves;
    private int[] startSlots;
    private int[] endSlots;
    private PendingTarget[]? pendingTargets;
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
    private long indexRootOffset;
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
        indexRootOffset = rootRouterOffset;
        this.maxIdentityLength = maxIdentityLength;
        this.lowerEncodedKeyHigh = lowerEncodedKeyHigh;
        this.lowerEncodedKeyLow = lowerEncodedKeyLow;
        this.upperEncodedKeyHigh = upperEncodedKeyHigh;
        this.upperEncodedKeyLow = upperEncodedKeyLow;
        pendingTargets = ArrayPool<PendingTarget>.Shared.Rent(DefaultShelfCapacity);
        visitedShelves = RouteVisitedOffsetSet.Rent();
        visitedRouters = RouteVisitedOffsetSet.Rent();
        traversalComplete = false;

        if (lowerEncodedKeyHigh == upperEncodedKeyHigh && lowerEncodedKeyLow == upperEncodedKeyLow)
        {
            if (session.TryWalkScalar16VarIdentityExactShelf(
                rootRouterOffset,
                lowerEncodedKeyHigh,
                lowerEncodedKeyLow,
                LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops,
                out long exactShelfOffset))
            {
                PushTarget(
                    exactShelfOffset,
                    LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops,
                    lowerEncodedKeyHigh,
                    lowerEncodedKeyLow,
                    upperEncodedKeyHigh,
                    upperEncodedKeyLow);
            }

            return;
        }

        byte lowerPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(lowerEncodedKeyHigh, lowerEncodedKeyLow, 0);
        byte upperPrefix = LibraDexFileSession.GetScalar16VarIdentityPrefix(upperEncodedKeyHigh, upperEncodedKeyLow, 0);
        for (int prefix = upperPrefix; prefix >= lowerPrefix; prefix--)
        {
            long targetOffset = session.FindRouterTarget(rootRouterOffset, (byte)prefix);
            if (targetOffset != 0)
            {
                CreateScalar16RouteBounds(
                    lowerEncodedKeyHigh,
                    lowerEncodedKeyLow,
                    upperEncodedKeyHigh,
                    upperEncodedKeyLow,
                    keyDepth: 0,
                    (byte)prefix,
                    (byte)prefix,
                    out ulong targetLowerHigh,
                    out ulong targetLowerLow,
                    out ulong targetUpperHigh,
                    out ulong targetUpperLow);
                PushTarget(
                    targetOffset,
                    LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops,
                    targetLowerHigh,
                    targetLowerLow,
                    targetUpperHigh,
                    targetUpperLow);
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
        Scalar16VarIdentityReadOnly shelf,
        ulong lowerEncodedKeyHigh,
        ulong lowerEncodedKeyLow,
        ulong upperEncodedKeyHigh,
        ulong upperEncodedKeyLow)
    {
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
        if (pendingTargets is not null)
        {
            ArrayPool<PendingTarget>.Shared.Return(pendingTargets, clearArray: false);
        }

        visitedShelves?.Dispose();
        visitedRouters?.Dispose();
        pendingTargets = null;
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
            PopTarget(
                out long targetOffset,
                out int remainingHops,
                out ulong targetLowerHigh,
                out ulong targetLowerLow,
                out ulong targetUpperHigh,
                out ulong targetUpperLow);
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

                    Scalar16VarIdentityReadOnly shelf = localSession.ReadScalar16VarIdentityReadOnlyShelf(
                        indexRootOffset,
                        currentShelfOffset,
                        maxIdentityLength);
                    AddShelfRange(shelf, targetLowerHigh, targetLowerLow, targetUpperHigh, targetUpperLow);
                    currentShelfOffset = shelf.NextShelfOffset;
                    if (rowCount > previousRowCount)
                    {
                        if (currentShelfOffset != 0)
                        {
                            PushTarget(
                                currentShelfOffset,
                                remainingHops,
                                targetLowerHigh,
                                targetLowerLow,
                                targetUpperHigh,
                                targetUpperLow);
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

            bool sharedStem = KeysShareScalar16PrefixBeforeDepth(
                targetLowerHigh,
                targetLowerLow,
                targetUpperHigh,
                targetUpperLow,
                router.KeyDepth);
            byte lowerPrefix = sharedStem
                ? LibraDexFileSession.GetScalar16VarIdentityPrefix(targetLowerHigh, targetLowerLow, router.KeyDepth)
                : byte.MinValue;
            byte upperPrefix = sharedStem
                ? LibraDexFileSession.GetScalar16VarIdentityPrefix(targetUpperHigh, targetUpperLow, router.KeyDepth)
                : byte.MaxValue;
            int prefix = upperPrefix;
            while (prefix >= lowerPrefix)
            {
                long routeTarget = router.FindTarget((byte)prefix);
                if (routeTarget == 0)
                {
                    prefix--;
                    continue;
                }

                int runEnd = prefix;
                int runStart = prefix;
                while (runStart > lowerPrefix && router.FindTarget((byte)(runStart - 1)) == routeTarget)
                {
                    runStart--;
                }

                CreateScalar16RouteBounds(
                    targetLowerHigh,
                    targetLowerLow,
                    targetUpperHigh,
                    targetUpperLow,
                    router.KeyDepth,
                    (byte)runStart,
                    (byte)runEnd,
                    out ulong childLowerHigh,
                    out ulong childLowerLow,
                    out ulong childUpperHigh,
                    out ulong childUpperLow);
                PushTarget(
                    routeTarget,
                    remainingHops - 1,
                    childLowerHigh,
                    childLowerLow,
                    childUpperHigh,
                    childUpperLow);
                prefix = runStart - 1;
            }
        }

        traversalComplete = true;
        return false;
    }

    /// <summary>
    /// Determines whether two encoded SV16 keys share every router byte preceding <paramref name="keyDepth"/>.<br/>
    /// A router may restrict its child-prefix scan to the endpoint bytes only while both endpoints remain under the same preceding key stem.<br/>
    /// When an earlier byte differs, the current byte can roll over inside the range and the router must visit its complete child-prefix domain.<br/>
    /// </summary>
    /// <param name="lowerHigh">The high half of the inclusive lower encoded key.<br/></param>
    /// <param name="lowerLow">The low half of the inclusive lower encoded key.<br/></param>
    /// <param name="upperHigh">The high half of the inclusive upper encoded key.<br/></param>
    /// <param name="upperLow">The low half of the inclusive upper encoded key.<br/></param>
    /// <param name="keyDepth">The zero-based router byte depth whose preceding stem is compared.<br/></param>
    /// <returns><see langword="true"/> when bounded child-prefix traversal is safe at this depth; otherwise <see langword="false"/>.<br/></returns>
    internal static bool KeysShareScalar16PrefixBeforeDepth(
        ulong lowerHigh,
        ulong lowerLow,
        ulong upperHigh,
        ulong upperLow,
        int keyDepth)
    {
        if ((uint)keyDepth >= Scalar16VarIdentityLayout.KeySize)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "The SV16 router key depth is outside the encoded key.");
        }

        if (keyDepth == 0)
        {
            return true;
        }

        if (keyDepth <= sizeof(ulong))
        {
            int shift = (sizeof(ulong) - keyDepth) * 8;
            return (lowerHigh >> shift) == (upperHigh >> shift);
        }

        if (lowerHigh != upperHigh)
        {
            return false;
        }

        int lowShift = (Scalar16VarIdentityLayout.KeySize - keyDepth) * 8;
        return (lowerLow >> lowShift) == (upperLow >> lowShift);
    }

    /// <summary>
    /// Intersects one parent `SV16` key interval with a contiguous router-prefix run and returns the target-local inclusive bounds.<br/>
    /// Bytes before <paramref name="keyDepth"/> retain the parent route context, the selected prefix run constrains the current byte, and trailing bytes expand to their minimum or maximum values.<br/>
    /// The final max/min intersection preserves caller-supplied range edges while preventing physically overlapping shelves from returning tuples owned by neighboring routes.<br/>
    /// </summary>
    /// <param name="parentLowerHigh">The high half of the parent target's inclusive lower bound.<br/></param>
    /// <param name="parentLowerLow">The low half of the parent target's inclusive lower bound.<br/></param>
    /// <param name="parentUpperHigh">The high half of the parent target's inclusive upper bound.<br/></param>
    /// <param name="parentUpperLow">The low half of the parent target's inclusive upper bound.<br/></param>
    /// <param name="keyDepth">The router byte depth constrained by this route run.<br/></param>
    /// <param name="routeStart">The first byte owned by the contiguous route run.<br/></param>
    /// <param name="routeEnd">The last byte owned by the contiguous route run.<br/></param>
    /// <param name="lowerHigh">Receives the high half of the intersected inclusive lower bound.<br/></param>
    /// <param name="lowerLow">Receives the low half of the intersected inclusive lower bound.<br/></param>
    /// <param name="upperHigh">Receives the high half of the intersected inclusive upper bound.<br/></param>
    /// <param name="upperLow">Receives the low half of the intersected inclusive upper bound.<br/></param>
    internal static void CreateScalar16RouteBounds(
        ulong parentLowerHigh,
        ulong parentLowerLow,
        ulong parentUpperHigh,
        ulong parentUpperLow,
        int keyDepth,
        byte routeStart,
        byte routeEnd,
        out ulong lowerHigh,
        out ulong lowerLow,
        out ulong upperHigh,
        out ulong upperLow)
    {
        if ((uint)keyDepth >= Scalar16VarIdentityLayout.KeySize || routeStart > routeEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "The SV16 route-bound depth or prefix run is invalid.");
        }

        if (keyDepth < sizeof(ulong))
        {
            int shift = (sizeof(ulong) - 1 - keyDepth) * 8;
            ulong precedingMask = keyDepth == 0 ? 0 : ulong.MaxValue << (shift + 8);
            ulong trailingMask = shift == 0 ? 0 : (1UL << shift) - 1;
            lowerHigh = (parentLowerHigh & precedingMask) | ((ulong)routeStart << shift);
            lowerLow = 0;
            upperHigh = (parentUpperHigh & precedingMask) | ((ulong)routeEnd << shift) | trailingMask;
            upperLow = ulong.MaxValue;
        }
        else
        {
            int lowDepth = keyDepth - sizeof(ulong);
            int shift = (sizeof(ulong) - 1 - lowDepth) * 8;
            ulong precedingMask = lowDepth == 0 ? 0 : ulong.MaxValue << (shift + 8);
            ulong trailingMask = shift == 0 ? 0 : (1UL << shift) - 1;
            lowerHigh = parentLowerHigh;
            lowerLow = (parentLowerLow & precedingMask) | ((ulong)routeStart << shift);
            upperHigh = parentUpperHigh;
            upperLow = (parentUpperLow & precedingMask) | ((ulong)routeEnd << shift) | trailingMask;
        }

        if (CompareKeys(lowerHigh, lowerLow, parentLowerHigh, parentLowerLow) < 0)
        {
            lowerHigh = parentLowerHigh;
            lowerLow = parentLowerLow;
        }

        if (CompareKeys(upperHigh, upperLow, parentUpperHigh, parentUpperLow) > 0)
        {
            upperHigh = parentUpperHigh;
            upperLow = parentUpperLow;
        }

        if (CompareKeys(lowerHigh, lowerLow, upperHigh, upperLow) > 0)
        {
            throw new InvalidDataException("The routed SV16 target-local key interval is empty.");
        }
    }

    private void EnsureAllRangesLoaded()
    {
        while (LoadNextShelfRange())
        {
        }
    }

    private void PushTarget(
        long offset,
        int remainingHops,
        ulong lowerHigh,
        ulong lowerLow,
        ulong upperHigh,
        ulong upperLow)
    {
        PendingTarget[] localTargets = pendingTargets ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        if (pendingCount == localTargets.Length)
        {
            int newLength = checked(localTargets.Length * 2);
            PendingTarget[] newTargets = ArrayPool<PendingTarget>.Shared.Rent(newLength);
            localTargets.AsSpan(0, pendingCount).CopyTo(newTargets);
            ArrayPool<PendingTarget>.Shared.Return(localTargets, clearArray: false);
            pendingTargets = newTargets;
            localTargets = newTargets;
        }

        localTargets[pendingCount] = new PendingTarget(offset, remainingHops, lowerHigh, lowerLow, upperHigh, upperLow);
        pendingCount++;
    }

    private void PopTarget(
        out long offset,
        out int remainingHops,
        out ulong lowerHigh,
        out ulong lowerLow,
        out ulong upperHigh,
        out ulong upperLow)
    {
        PendingTarget[] localTargets = pendingTargets ?? throw new ObjectDisposedException(nameof(Scalar16VarIdentityRangeReader));
        pendingCount--;
        PendingTarget target = localTargets[pendingCount];
        offset = target.Offset;
        remainingHops = target.RemainingHops;
        lowerHigh = target.LowerHigh;
        lowerLow = target.LowerLow;
        upperHigh = target.UpperHigh;
        upperLow = target.UpperLow;
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

    /// <summary>
    /// Stores one pending `SV16` route target and its route-owned inclusive scalar bounds in a single pooled frontier entry.<br/>
    /// Consolidating the target state avoids multiple pool rentals for every exact or range reader while keeping bound propagation allocation-free after the initial rent.<br/>
    /// </summary>
    /// <param name="Offset">The routed shelf or router offset.<br/></param>
    /// <param name="RemainingHops">The remaining router-hop safety budget.<br/></param>
    /// <param name="LowerHigh">The high half of the target-local inclusive lower bound.<br/></param>
    /// <param name="LowerLow">The low half of the target-local inclusive lower bound.<br/></param>
    /// <param name="UpperHigh">The high half of the target-local inclusive upper bound.<br/></param>
    /// <param name="UpperLow">The low half of the target-local inclusive upper bound.<br/></param>
    private readonly record struct PendingTarget(
        long Offset,
        int RemainingHops,
        ulong LowerHigh,
        ulong LowerLow,
        ulong UpperHigh,
        ulong UpperLow);
}

