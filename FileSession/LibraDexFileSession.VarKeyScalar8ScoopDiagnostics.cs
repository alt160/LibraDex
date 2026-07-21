using System.Buffers.Binary;
using System.Security.Cryptography;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Inspects one persisted `VS8` route graph as an ordered sequence of distinct logical shelf extents.<br/>
    /// The proof validates local alias contiguity, single-parent target ownership, monotonic non-overlapping shelf key intervals, and tuple-sequence equivalence with the ordinary full range reader.<br/>
    /// This method is read-only and intentionally performs full shelf decoding so the later count-only scoop can rely on evidence from the actual persisted topology before using narrow interior header counts.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset of the `VS8` index to inspect.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxRouterHops">The maximum router depth accepted before the topology is treated as invalid.<br/></param>
    /// <returns>The ordered extent snapshots and aggregate invariant results for the inspected storage version.<br/></returns>
    internal VarKeyScalar8ScoopTopologyDiagnostics InspectVarKeyScalar8ScoopTopology(
        long rootRouterOffset,
        int maxKeyLength,
        int maxRouterHops = DefaultVarKeyScalar8MaxRouterHops)
    {
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VS8 scoop inspector root router offset must be positive.");

        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VS8 scoop inspector maximum key length must be positive.");

        if (maxRouterHops <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRouterHops), maxRouterHops, "The VS8 scoop inspector maximum router hop count must be positive.");

        long startMutationVersion = kernel.MutationVersion;
        using VarKeyScalar8ScoopInspectionState state = new();
        InspectVarKeyScalar8ScoopRouter(rootRouterOffset, parentRouterOffset: 0, maxKeyLength, maxRouterHops, depth: 0, state);
        byte[] routeHash = state.RouteHash.GetHashAndReset();

        long readerTupleCount = 0;
        byte[] readerHash;
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (state.FirstKey.Length != 0)
            {
                using VarKeyScalar8RangeReader reader = OpenVarKeyScalar8RangeReader(
                    rootRouterOffset,
                    maxKeyLength,
                    state.FirstKey,
                    state.LastKey,
                    maxRouterHops,
                    decodeLogicalKeys: false);
                while (reader.MoveNext())
                {
                    AppendVarKeyScalar8ScoopTupleHash(hash, reader.CurrentKey, reader.CurrentEncodedIdentity);
                    readerTupleCount++;
                }
            }

            readerHash = hash.GetHashAndReset();
        }

        long endMutationVersion = kernel.MutationVersion;
        bool sequenceMatches = state.RouteTupleCount == readerTupleCount && routeHash.AsSpan().SequenceEqual(readerHash);
        bool valid = state.NonContiguousAliasCount == 0 &&
            state.MultipleParentTargetCount == 0 &&
            state.RouterCycleCount == 0 &&
            state.OverlappingExtentCount == 0 &&
            sequenceMatches &&
            startMutationVersion == endMutationVersion;

        return new VarKeyScalar8ScoopTopologyDiagnostics(
            state.Extents.ToArray(),
            state.RouterCount,
            state.RouteTargetRunCount,
            state.EmptyExtentCount,
            state.NonContiguousAliasCount,
            state.MultipleParentTargetCount,
            state.RouterCycleCount,
            state.OverlappingExtentCount,
            state.RouteTupleCount,
            readerTupleCount,
            Convert.ToHexString(routeHash),
            Convert.ToHexString(readerHash),
            startMutationVersion,
            endMutationVersion,
            sequenceMatches,
            valid);
    }

    /// <summary>
    /// Traverses one `VS8` router in logical key order while expanding each distinct child router once.<br/>
    /// Direct one-byte route slots are already byte ordered; compressed multi-byte slots are ordered by their persisted stem and final-byte range because physical insertion order is not a key-order contract.<br/>
    /// Repeated adjacent target slots are one local alias run; a target that reappears after another target or an unset gap is recorded as a noncontiguous alias violation.<br/>
    /// </summary>
    /// <param name="routerOffset">The router page offset to inspect.<br/></param>
    /// <param name="parentRouterOffset">The parent router offset, or zero for the root.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxRouterHops">The maximum permitted traversal depth.<br/></param>
    /// <param name="depth">The current zero-based router depth in the diagnostic traversal.<br/></param>
    /// <param name="state">The caller-owned inspection state accumulating topology and tuple evidence.<br/></param>
    private void InspectVarKeyScalar8ScoopRouter(
        long routerOffset,
        long parentRouterOffset,
        int maxKeyLength,
        int maxRouterHops,
        int depth,
        VarKeyScalar8ScoopInspectionState state)
    {
        if (depth >= maxRouterHops)
            throw new InvalidDataException("The VS8 scoop inspector exceeded the configured router hop count.");

        if (!state.ActiveRouters.Add(routerOffset))
        {
            state.RouterCycleCount++;
            return;
        }

        if (!state.ExpandedRouters.Add(routerOffset))
        {
            state.ActiveRouters.Remove(routerOffset);
            return;
        }

        state.RouterCount++;
        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException($"The VS8 scoop inspector found an invalid router at offset {routerOffset}.");

        int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
        Span<int> routeOrder = routeCount <= RouterLayout.MaxOneByteRouteCount
            ? stackalloc int[routeCount]
            : new int[routeCount];
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
            routeOrder[routeIndex] = routeIndex;

        if (router.PrefixByteCount > 1)
        {
            for (int current = 1; current < routeOrder.Length; current++)
            {
                int candidate = routeOrder[current];
                int insert = current;
                while (insert > 0)
                {
                    int preceding = routeOrder[insert - 1];
                    int order = router.GetMultiByteRouteStemAt(preceding).SequenceCompareTo(router.GetMultiByteRouteStemAt(candidate));
                    if (order == 0)
                        order = router.GetRoutePrefixStartAt(preceding).CompareTo(router.GetRoutePrefixStartAt(candidate));

                    if (order == 0)
                        order = router.GetRoutePrefixEndAt(preceding).CompareTo(router.GetRoutePrefixEndAt(candidate));

                    if (order <= 0)
                        break;

                    routeOrder[insert] = preceding;
                    insert--;
                }

                routeOrder[insert] = candidate;
            }
        }

        HashSet<long> closedTargets = [];
        long previousTarget = long.MinValue;
        for (int routePosition = 0; routePosition < routeOrder.Length; routePosition++)
        {
            int routeIndex = routeOrder[routePosition];
            long targetOffset = router.GetRouteTargetAt(routeIndex);
            if (targetOffset == previousTarget)
                continue;

            if (previousTarget > 0)
                closedTargets.Add(previousTarget);

            if (targetOffset > 0 && closedTargets.Contains(targetOffset))
                state.NonContiguousAliasCount++;

            previousTarget = targetOffset;
            if (targetOffset == 0)
                continue;

            state.RouteTargetRunCount++;
            InspectVarKeyScalar8ScoopTarget(targetOffset, routerOffset, maxKeyLength, maxRouterHops, depth + 1, state);
        }

        state.ActiveRouters.Remove(routerOffset);
    }

    /// <summary>
    /// Classifies one ordered route target and either descends into its router or emits its logical shelf extent once.<br/>
    /// A target referenced from different parent routers is recorded because the scoop proof requires one logical parent location even when one parent's adjacent route slots alias that location.<br/>
    /// </summary>
    /// <param name="targetOffset">The route target offset to inspect.<br/></param>
    /// <param name="parentRouterOffset">The router containing the current target run.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="maxRouterHops">The maximum permitted traversal depth.<br/></param>
    /// <param name="depth">The current traversal depth after following the target.<br/></param>
    /// <param name="state">The caller-owned inspection state accumulating topology and tuple evidence.<br/></param>
    private void InspectVarKeyScalar8ScoopTarget(
        long targetOffset,
        long parentRouterOffset,
        int maxKeyLength,
        int maxRouterHops,
        int depth,
        VarKeyScalar8ScoopInspectionState state)
    {
        if (state.TargetParents.TryGetValue(targetOffset, out long existingParent))
        {
            if (existingParent != parentRouterOffset)
                state.MultipleParentTargetCount++;
        }
        else
        {
            state.TargetParents.Add(targetOffset, parentRouterOffset);
        }

        VarKeyScalar8RouteTargetKind kind = ClassifyVarKeyScalar8RouteTarget(targetOffset);
        if (kind == VarKeyScalar8RouteTargetKind.Router)
        {
            InspectVarKeyScalar8ScoopRouter(targetOffset, parentRouterOffset, maxKeyLength, maxRouterHops, depth, state);
            return;
        }

        if (!state.EmittedTargets.Add(targetOffset))
            return;

        if (kind == VarKeyScalar8RouteTargetKind.Shelf)
        {
            InspectVarKeyScalar8ScoopShelfExtent(targetOffset, parentRouterOffset, maxKeyLength, state);
            return;
        }

        if (kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
        {
            InspectVarKeyScalar8ScoopTerminalExtent(targetOffset, parentRouterOffset, state);
            return;
        }

        throw new InvalidDataException($"The VS8 scoop inspector found an unsupported target at offset {targetOffset}.");
    }

    /// <summary>
    /// Reads one ordinary or duplicate-run `VS8` logical shelf extent, records its boundary tuples, and appends its complete tuple sequence to the route-order hash.<br/>
    /// Linked duplicate shelves are consolidated into the head extent because they represent one encoded key position rather than independent shelf intervals.<br/>
    /// </summary>
    /// <param name="headShelfOffset">The route-visible ordinary or duplicate-run shelf offset.<br/></param>
    /// <param name="parentRouterOffset">The router that owns the logical shelf position.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="state">The caller-owned inspection state accumulating topology and tuple evidence.<br/></param>
    private void InspectVarKeyScalar8ScoopShelfExtent(
        long headShelfOffset,
        long parentRouterOffset,
        int maxKeyLength,
        VarKeyScalar8ScoopInspectionState state)
    {
        long currentOffset = headShelfOffset;
        int physicalShelfCount = 0;
        long itemCount = 0;
        byte[] firstKey = [];
        byte[] lastKey = [];
        ulong firstIdentity = 0;
        ulong lastIdentity = 0;
        bool duplicateRun = false;
        HashSet<long> chainOffsets = [];
        while (currentOffset != 0)
        {
            if (!chainOffsets.Add(currentOffset))
                throw new InvalidDataException($"The VS8 scoop inspector found a duplicate-run cycle at shelf offset {currentOffset}.");

            VarKeyScalar8ReadOnly shelf = ReadVarKeyScalar8ReadOnlyShelf(currentOffset, maxKeyLength);
            if (physicalShelfCount == 0)
                duplicateRun = shelf.IsDuplicateRun;
            else if (!duplicateRun || !shelf.IsDuplicateRun)
                throw new InvalidDataException("The VS8 scoop inspector found an inconsistent duplicate-run shelf chain.");

            physicalShelfCount++;
            int shelfItemCount = shelf.ItemCount;
            for (int slotIndex = 0; slotIndex < shelfItemCount; slotIndex++)
            {
                ReadOnlySpan<byte> key = shelf.ReadKeyAt(slotIndex);
                ulong identity = shelf.ReadIdentityAt(slotIndex);
                if (itemCount == 0)
                {
                    firstKey = key.ToArray();
                    firstIdentity = identity;
                }

                lastKey = key.ToArray();
                lastIdentity = identity;
                AppendVarKeyScalar8ScoopTupleHash(state.RouteHash, key, identity);
                itemCount++;
            }

            currentOffset = shelf.IsDuplicateRun ? shelf.DuplicateRunNextOffset : 0;
        }

        AddVarKeyScalar8ScoopExtent(
            duplicateRun ? VarKeyScalar8ScoopExtentKind.DuplicateRun : VarKeyScalar8ScoopExtentKind.OrdinaryShelf,
            headShelfOffset,
            parentRouterOffset,
            physicalShelfCount,
            itemCount,
            firstKey,
            firstIdentity,
            lastKey,
            lastIdentity,
            state);
    }

    /// <summary>
    /// Reads one terminal `VS8` identity root and its identity-only shelf chain as one logical encoded-key extent.<br/>
    /// The terminal key is hashed once per identity so its tuple sequence can be compared directly with the ordinary routed range reader.<br/>
    /// </summary>
    /// <param name="rootOffset">The terminal identity root offset.<br/></param>
    /// <param name="parentRouterOffset">The router that owns the terminal key position.<br/></param>
    /// <param name="state">The caller-owned inspection state accumulating topology and tuple evidence.<br/></param>
    private void InspectVarKeyScalar8ScoopTerminalExtent(
        long rootOffset,
        long parentRouterOffset,
        VarKeyScalar8ScoopInspectionState state)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey)
            throw new InvalidDataException("The VS8 scoop inspector found a terminal root with the wrong shape.");

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        byte[] key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength).ToArray();
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long currentOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        int physicalShelfCount = 0;
        long itemCount = 0;
        ulong firstIdentity = 0;
        ulong lastIdentity = 0;
        HashSet<long> chainOffsets = [];
        while (currentOffset != 0)
        {
            if (!chainOffsets.Add(currentOffset))
                throw new InvalidDataException($"The VS8 scoop inspector found a terminal identity shelf cycle at offset {currentOffset}.");

            byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(currentOffset, shelfExtentSize);
            ValidateTerminalIdentity8Shelf(shelfBytes, shelfExtentSize);
            int shelfItemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
            for (int slotIndex = 0; slotIndex < shelfItemCount; slotIndex++)
            {
                ulong identity = TerminalIdentity8ShelfLayout.ReadIdentity(shelfBytes, slotIndex);
                if (itemCount == 0)
                    firstIdentity = identity;

                lastIdentity = identity;
                AppendVarKeyScalar8ScoopTupleHash(state.RouteHash, key, identity);
                itemCount++;
            }

            physicalShelfCount++;
            currentOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
        }

        AddVarKeyScalar8ScoopExtent(
            VarKeyScalar8ScoopExtentKind.TerminalIdentityRoot,
            rootOffset,
            parentRouterOffset,
            physicalShelfCount,
            itemCount,
            itemCount == 0 ? [] : key,
            firstIdentity,
            itemCount == 0 ? [] : key.ToArray(),
            lastIdentity,
            state);
    }

    /// <summary>
    /// Adds one logical `VS8` extent and validates that its non-empty key interval follows the preceding non-empty extent without overlap.<br/>
    /// Empty routed shelves remain visible in the diagnostic result but do not establish tuple boundaries.<br/>
    /// </summary>
    /// <param name="kind">The logical extent kind.<br/></param>
    /// <param name="headOffset">The route-visible head shelf or terminal root offset.<br/></param>
    /// <param name="parentRouterOffset">The router owning the logical extent position.<br/></param>
    /// <param name="physicalShelfCount">The number of physical shelves consolidated into the logical extent.<br/></param>
    /// <param name="itemCount">The number of identities in the logical extent.<br/></param>
    /// <param name="firstKey">The first encoded key, or an empty array for an empty extent.<br/></param>
    /// <param name="firstIdentity">The first encoded identity when the extent is non-empty.<br/></param>
    /// <param name="lastKey">The last encoded key, or an empty array for an empty extent.<br/></param>
    /// <param name="lastIdentity">The last encoded identity when the extent is non-empty.<br/></param>
    /// <param name="state">The caller-owned inspection state accumulating topology and tuple evidence.<br/></param>
    private static void AddVarKeyScalar8ScoopExtent(
        VarKeyScalar8ScoopExtentKind kind,
        long headOffset,
        long parentRouterOffset,
        int physicalShelfCount,
        long itemCount,
        byte[] firstKey,
        ulong firstIdentity,
        byte[] lastKey,
        ulong lastIdentity,
        VarKeyScalar8ScoopInspectionState state)
    {
        if (itemCount == 0)
        {
            state.EmptyExtentCount++;
        }
        else
        {
            if (state.LastKey.Length != 0 && state.LastKey.AsSpan().SequenceCompareTo(firstKey) >= 0)
                state.OverlappingExtentCount++;

            if (state.FirstKey.Length == 0)
                state.FirstKey = firstKey.ToArray();

            state.LastKey = lastKey.ToArray();
        }

        state.RouteTupleCount += itemCount;
        state.Extents.Add(new VarKeyScalar8ScoopExtentDiagnostics(
            state.Extents.Count,
            kind,
            headOffset,
            parentRouterOffset,
            physicalShelfCount,
            itemCount,
            firstKey,
            firstIdentity,
            lastKey,
            lastIdentity));
    }

    /// <summary>
    /// Appends one unambiguous encoded `VS8` tuple frame to an incremental SHA-256 sequence hash.<br/>
    /// The frame includes key length, encoded identity, and key bytes so variable-length concatenations cannot alias one another.<br/>
    /// </summary>
    /// <param name="hash">The incremental hash receiving the tuple frame.<br/></param>
    /// <param name="key">The encoded variable-length key.<br/></param>
    /// <param name="encodedIdentity">The encoded eight-byte identity.<br/></param>
    private static void AppendVarKeyScalar8ScoopTupleHash(IncrementalHash hash, ReadOnlySpan<byte> key, ulong encodedIdentity)
    {
        Span<byte> frame = stackalloc byte[sizeof(int) + sizeof(ulong)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, key.Length);
        BinaryPrimitives.WriteUInt64BigEndian(frame.Slice(sizeof(int)), encodedIdentity);
        hash.AppendData(frame);
        hash.AppendData(key);
    }
}

/// <summary>
/// Identifies the physical representation consolidated into one logical `VS8` scoop extent.<br/>
/// </summary>
internal enum VarKeyScalar8ScoopExtentKind : byte
{
    OrdinaryShelf = 1,
    DuplicateRun = 2,
    TerminalIdentityRoot = 3
}

/// <summary>
/// Captures one distinct logical `VS8` shelf interval in route order.<br/>
/// Duplicate-run and terminal chains are represented once because all of their physical shelves occupy one encoded-key position.<br/>
/// </summary>
/// <param name="Ordinal">The zero-based logical extent ordinal.<br/></param>
/// <param name="Kind">The physical representation of the logical extent.<br/></param>
/// <param name="HeadOffset">The route-visible shelf or terminal-root offset.<br/></param>
/// <param name="ParentRouterOffset">The router that owns this logical route position.<br/></param>
/// <param name="PhysicalShelfCount">The number of physical shelves consolidated into this extent.<br/></param>
/// <param name="ItemCount">The number of identities in this extent.<br/></param>
/// <param name="FirstKey">The first encoded key, or an empty array when the extent is empty.<br/></param>
/// <param name="FirstIdentity">The first encoded identity when the extent is non-empty.<br/></param>
/// <param name="LastKey">The last encoded key, or an empty array when the extent is empty.<br/></param>
/// <param name="LastIdentity">The last encoded identity when the extent is non-empty.<br/></param>
internal readonly record struct VarKeyScalar8ScoopExtentDiagnostics(
    int Ordinal,
    VarKeyScalar8ScoopExtentKind Kind,
    long HeadOffset,
    long ParentRouterOffset,
    int PhysicalShelfCount,
    long ItemCount,
    byte[] FirstKey,
    ulong FirstIdentity,
    byte[] LastKey,
    ulong LastIdentity);

/// <summary>
/// Captures the complete read-only proof for one persisted `VS8` route graph.<br/>
/// Sequence equivalence uses a framed SHA-256 hash over every route-order tuple and every ordinary range-reader tuple.<br/>
/// </summary>
/// <param name="Extents">The distinct logical extents in route order.<br/></param>
/// <param name="RouterCount">The number of distinct router pages expanded.<br/></param>
/// <param name="RouteTargetRunCount">The number of non-empty adjacent target runs encountered across routers.<br/></param>
/// <param name="EmptyExtentCount">The number of routed extents containing no identities.<br/></param>
/// <param name="NonContiguousAliasCount">The number of targets that reappeared after another target or gap in one router.<br/></param>
/// <param name="MultipleParentTargetCount">The number of target offsets referenced from different parent routers.<br/></param>
/// <param name="RouterCycleCount">The number of router cycles detected.<br/></param>
/// <param name="OverlappingExtentCount">The number of adjacent non-empty extent intervals that were non-increasing or overlapping.<br/></param>
/// <param name="RouteTupleCount">The tuples enumerated directly from distinct routed extents.<br/></param>
/// <param name="ReaderTupleCount">The tuples enumerated by the ordinary full range reader.<br/></param>
/// <param name="RouteTupleHash">The framed SHA-256 hash of direct route-order tuples.<br/></param>
/// <param name="ReaderTupleHash">The framed SHA-256 hash of ordinary range-reader tuples.<br/></param>
/// <param name="StartMutationVersion">The storage mutation version before route inspection.<br/></param>
/// <param name="EndMutationVersion">The storage mutation version after range-reader comparison.<br/></param>
/// <param name="SequenceMatchesReader">Whether tuple count and framed hash match the ordinary range reader.<br/></param>
/// <param name="AllInvariantsHold">Whether every inspected structural, sequence, and snapshot invariant passed.<br/></param>
internal readonly record struct VarKeyScalar8ScoopTopologyDiagnostics(
    VarKeyScalar8ScoopExtentDiagnostics[] Extents,
    int RouterCount,
    long RouteTargetRunCount,
    int EmptyExtentCount,
    int NonContiguousAliasCount,
    int MultipleParentTargetCount,
    int RouterCycleCount,
    int OverlappingExtentCount,
    long RouteTupleCount,
    long ReaderTupleCount,
    string RouteTupleHash,
    string ReaderTupleHash,
    long StartMutationVersion,
    long EndMutationVersion,
    bool SequenceMatchesReader,
    bool AllInvariantsHold);

/// <summary>
/// Owns mutable collections and the incremental route hash for one `VS8` scoop inspection.<br/>
/// The state is diagnostic-only and is disposed immediately after one topology proof completes.<br/>
/// </summary>
internal sealed class VarKeyScalar8ScoopInspectionState : IDisposable
{
    internal readonly HashSet<long> ActiveRouters = [];
    internal readonly HashSet<long> ExpandedRouters = [];
    internal readonly Dictionary<long, long> TargetParents = [];
    internal readonly HashSet<long> EmittedTargets = [];
    internal readonly List<VarKeyScalar8ScoopExtentDiagnostics> Extents = [];
    internal readonly IncrementalHash RouteHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal byte[] FirstKey = [];
    internal byte[] LastKey = [];
    internal int RouterCount;
    internal long RouteTargetRunCount;
    internal int EmptyExtentCount;
    internal int NonContiguousAliasCount;
    internal int MultipleParentTargetCount;
    internal int RouterCycleCount;
    internal int OverlappingExtentCount;
    internal long RouteTupleCount;

    /// <summary>
    /// Releases the incremental cryptographic hash used by the diagnostic tuple-sequence comparison.<br/>
    /// The ordinary managed collections require no explicit ownership release.<br/>
    /// </summary>
    public void Dispose()
    {
        RouteHash.Dispose();
    }
}
