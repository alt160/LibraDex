using System.Buffers.Binary;
using System.Diagnostics;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const long Scalar8Scalar8GrowthCanaryFloorBytes = 16L * 1024 * 1024;
    private const int Scalar8Scalar8GrowthCanaryTupleByteFactor = 32;

    /// <summary>
    /// Builds an empty `SS8-8` index directly from globally sorted encoded tuples and publishes the completed topology through one stable-root rewrite.<br/>
    /// Child shelves, terminal identity chains, and child routers are constructed while unreachable and committed before the existing root router is changed.<br/>
    /// The method accepts only an empty direct root and therefore cannot expose a partially built generation to readers.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable empty root-router offset owned by the target index.<br/></param>
    /// <param name="profile">Persisted shelf profile selected by the target index.<br/></param>
    /// <param name="tuples">Globally sorted encoded `(key, identity)` tuples.<br/></param>
    /// <param name="allowDuplicateKeys">Whether more than one identity may share an encoded key.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether one encoded identity may occur under only one key.<br/></param>
    /// <param name="cancellationToken">Cancellation observed during validation and unreachable-child construction; publication proceeds without a cancellation gap after child durability succeeds.<br/></param>
    /// <returns>Topology counts, phase timings, and the two durability-boundary diagnostics.<br/></returns>
    internal Scalar8Scalar8SortedBuildResult BuildScalar8Scalar8FromSorted(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        Scalar8Scalar8SortedTuple[] tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        CancellationToken cancellationToken)
        => BuildScalar8Scalar8FromSorted(
            rootRouterOffset,
            profile,
            new Scalar8Scalar8SortedArraySource(tuples),
            allowDuplicateKeys,
            singleKeyPerIdentity,
            identityMultiplicityAlreadyValidated: false,
            cancellationToken);

    /// <summary>
    /// Builds an empty `SS8-8` index from a seekable, globally sorted encoded tuple source without requiring the complete population in one managed array.<br/>
    /// The source may be backed by bounded memory, a merged spill file, or another stable ordinal store; it must return the same tuple for an ordinal throughout the call.<br/>
    /// The identity-validation bypass is reserved for authoritative owners that prove one tuple per identity while extracting source records; all order, exact-tuple, and key-uniqueness checks remain mandatory.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable empty root-router offset owned by the target index.<br/></param>
    /// <param name="profile">Persisted shelf profile selected by the target index.<br/></param>
    /// <param name="tuples">Stable seekable source in encoded key-then-identity order.<br/></param>
    /// <param name="allowDuplicateKeys">Whether more than one identity may share an encoded key.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether one encoded identity may occur under only one key.<br/></param>
    /// <param name="identityMultiplicityAlreadyValidated">Whether an authoritative source lifecycle has already proved the single-key-per-identity contract.<br/></param>
    /// <param name="cancellationToken">Cancellation observed during validation and unreachable-child construction; publication proceeds without a cancellation gap after child durability succeeds.<br/></param>
    /// <returns>Topology counts, phase timings, and the two durability-boundary diagnostics.<br/></returns>
    internal Scalar8Scalar8SortedBuildResult BuildScalar8Scalar8FromSorted(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        IScalar8Scalar8SortedTupleSource tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        bool identityMultiplicityAlreadyValidated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The SS8-8 sorted builder requires a positive root-router offset.");
        if (durabilityBatchActive)
            throw new InvalidOperationException("The SS8-8 sorted builder cannot run inside an active durability batch.");

        ReaderWriterLockSlim topologySync = GetScalar8Scalar8TopologyMutationSync(rootRouterOffset);
        topologySync.EnterWriteLock();
        try
        {
            scalar8Scalar8WriterOperationSync.EnterWriteLock();
            try
            {
                lock (writePublicationSync)
                {
                    return BuildScalar8Scalar8FromSortedCore(
                        rootRouterOffset,
                        profile,
                        tuples,
                        allowDuplicateKeys,
                        singleKeyPerIdentity,
                        identityMultiplicityAlreadyValidated,
                        requireEmptyRoot: true,
                        cancellationToken);
                }
            }
            finally
            {
                scalar8Scalar8WriterOperationSync.ExitWriteLock();
            }
        }
        finally
        {
            topologySync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Replaces one live `SS8-8` stable-root generation from a seekable sorted source without first deleting tuples through the old topology.<br/>
    /// Candidate validation and unreachable child construction complete before the stable root page is rewritten, so a malformed source or pre-publication failure leaves the prior generation authoritative.<br/>
    /// The stable root offset is retained for existing handles while every former child extent becomes unreachable and eligible for later file compaction.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable root-router offset owned by the target index.<br/></param>
    /// <param name="profile">Persisted shelf profile selected by the target index.<br/></param>
    /// <param name="tuples">Globally sorted encoded key-and-identity tuples.<br/></param>
    /// <param name="allowDuplicateKeys">Whether more than one identity may share an encoded key.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether one encoded identity may occur under only one key.<br/></param>
    /// <param name="identityMultiplicityAlreadyValidated">Whether the authoritative source already proved one key per identity.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before durable child publication begins.<br/></param>
    /// <returns>Topology counts, phase timings, and durability diagnostics for the replacement generation.<br/></returns>
    internal Scalar8Scalar8SortedBuildResult ReplaceScalar8Scalar8StableRootFromSorted(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        IScalar8Scalar8SortedTupleSource tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        bool identityMultiplicityAlreadyValidated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The SS8-8 stable-root replacement requires a positive root-router offset.");
        if (durabilityBatchActive)
            throw new InvalidOperationException("The SS8-8 stable-root replacement cannot run inside an active durability batch.");

        ReaderWriterLockSlim topologySync = GetScalar8Scalar8TopologyMutationSync(rootRouterOffset);
        topologySync.EnterWriteLock();
        try
        {
            scalar8Scalar8WriterOperationSync.EnterWriteLock();
            try
            {
                lock (writePublicationSync)
                {
                    return BuildScalar8Scalar8FromSortedCore(
                        rootRouterOffset,
                        profile,
                        tuples,
                        allowDuplicateKeys,
                        singleKeyPerIdentity,
                        identityMultiplicityAlreadyValidated,
                        requireEmptyRoot: false,
                        cancellationToken);
                }
            }
            finally
            {
                scalar8Scalar8WriterOperationSync.ExitWriteLock();
            }
        }
        finally
        {
            topologySync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Validates the empty publication anchor, packs one unreachable topology, commits it, and then rewrites the stable root page.<br/>
    /// All input-contract failures are detected before the first allocation so malformed sorted streams cannot enlarge the catalog.<br/>
    /// </summary>
    private Scalar8Scalar8SortedBuildResult BuildScalar8Scalar8FromSortedCore(
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        IScalar8Scalar8SortedTupleSource tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        bool identityMultiplicityAlreadyValidated,
        bool requireEmptyRoot,
        CancellationToken cancellationToken)
    {
        byte[] currentRootBytes = new byte[RouterLayout.Size];
        kernel.Read(rootRouterOffset, currentRootBytes);
        RouterReader currentRoot = new(currentRootBytes);
        if (!currentRoot.IsValid || !currentRoot.HasDirectIndex || currentRoot.KeyDepth != 0)
            throw new InvalidOperationException("The SS8-8 sorted builder requires a valid direct root router at key depth zero.");
        for (int prefix = 0; requireEmptyRoot && prefix < RouterLayout.MaxOneByteRouteCount; prefix++)
        {
            if (currentRoot.GetDirectTarget((byte)prefix) != 0)
                throw new InvalidOperationException("The SS8-8 sorted builder currently accepts only an empty root router.");
        }

        ValidateScalar8Scalar8SortedInput(
            tuples,
            allowDuplicateKeys,
            singleKeyPerIdentity && !identityMultiplicityAlreadyValidated,
            cancellationToken);
        if (tuples.Count == 0)
        {
            long canaryLimit = GetScalar8Scalar8GrowthCanaryLimit(0);
            return new Scalar8Scalar8SortedBuildResult(
                0,
                0,
                0,
                0,
                0,
                RouterLayout.Size,
                canaryLimit,
                TimeSpan.Zero,
                TimeSpan.Zero,
                default,
                default);
        }

        var state = new Scalar8Scalar8SortedBuildState(profile, currentRoot.AllocationClassId, cancellationToken);
        var storageTimer = Stopwatch.StartNew();
        DataKernelCommitTelemetry childCommit = default;
        try
        {
            long[] rootTargets = BuildScalar8Scalar8SortedRouterTargets(tuples, 0, tuples.Count, 0, state);
            cancellationToken.ThrowIfCancellationRequested();
            long reachableTopologyBytes = GetScalar8Scalar8ReachableTopologyBytes(state);
            long growthCanaryLimitBytes = GetScalar8Scalar8GrowthCanaryLimit(tuples.Count);
            ValidateScalar8Scalar8GrowthCanary(tuples.Count, reachableTopologyBytes, growthCanaryLimitBytes);
            childCommit = CommitAndInvalidateRouterReadCache();
            storageTimer.Stop();

            var publishTimer = Stopwatch.StartNew();
            RawDataReservation rootRewrite = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
            RouterWriter rootWriter = new(rootRewrite.Span);
            rootWriter.InitializeExpandedOneByte(0, currentRoot.AllocationClassId, rootTargets);
            DataKernelCommitTelemetry rootCommit = CommitAndInvalidateRouterReadCache();
            publishTimer.Stop();

            return new Scalar8Scalar8SortedBuildResult(
                tuples.Count,
                state.OrdinaryShelfCount,
                state.TerminalRootCount,
                state.TerminalShelfCount,
                state.RouterCount,
                reachableTopologyBytes,
                growthCanaryLimitBytes,
                storageTimer.Elapsed,
                publishTimer.Elapsed,
                childCommit,
                rootCommit);
        }
        catch
        {
            kernel.DiscardPending();
            ClearTerminalIdentityReadCaches();
            ClearRouterReadCaches();
            throw;
        }
    }

    /// <summary>
    /// Calculates exact live topology bytes from shape-specific allocation counters before publication.<br/>
    /// The stable or detached root is included alongside child routers, ordinary shelves, terminal roots, and terminal shelves.<br/>
    /// </summary>
    /// <param name="state">The completed unreachable-build allocation counters and shelf profile.<br/></param>
    /// <returns>The exact bytes that the candidate generation will make reachable.<br/></returns>
    private static long GetScalar8Scalar8ReachableTopologyBytes(Scalar8Scalar8SortedBuildState state)
    {
        long routerBytes = checked((long)(state.RouterCount + 1) * RouterLayout.Size);
        long terminalRootBytes = checked((long)state.TerminalRootCount * TerminalIdentityRootLayout.Size);
        long shelfCount = checked((long)state.OrdinaryShelfCount + state.TerminalShelfCount);
        long shelfBytes = checked(shelfCount * state.Profile.ShelfExtentSize);
        return checked(routerBytes + terminalRootBytes + shelfBytes);
    }

    /// <summary>
    /// Returns the conservative `SS8-8` candidate-generation growth ceiling used to detect gross topology amplification before publication.<br/>
    /// The fixed floor permits intentionally sparse small indexes, while the tuple-scaled ceiling allows up to thirty-two times the dense 16-byte tuple payload for larger builds.<br/>
    /// This is a corruption/amplification canary rather than a packing target; normal packed builds are expected to remain far below it.<br/>
    /// </summary>
    /// <param name="tupleCount">The completely validated source tuple count.<br/></param>
    /// <returns>The maximum candidate-generation reachable bytes accepted for publication.<br/></returns>
    private static long GetScalar8Scalar8GrowthCanaryLimit(int tupleCount)
    {
        long scaled = checked((long)tupleCount * Scalar8Scalar8Layout.ItemSize * Scalar8Scalar8GrowthCanaryTupleByteFactor);
        return Math.Max(Scalar8Scalar8GrowthCanaryFloorBytes, scaled);
    }

    /// <summary>
    /// Rejects a completed detached or empty-root candidate whose shape counters exceed the conservative growth ceiling.<br/>
    /// Callers invoke this before the child commit or directory/root publication boundary, so failure discards pending bytes and leaves the prior live generation authoritative.<br/>
    /// </summary>
    /// <param name="tupleCount">The validated candidate tuple count.<br/></param>
    /// <param name="reachableTopologyBytes">The exact candidate topology bytes derived from allocation counters.<br/></param>
    /// <param name="growthCanaryLimitBytes">The conservative maximum accepted bytes.<br/></param>
    internal static void ValidateScalar8Scalar8GrowthCanary(
        int tupleCount,
        long reachableTopologyBytes,
        long growthCanaryLimitBytes)
    {
        if (reachableTopologyBytes <= growthCanaryLimitBytes)
            return;

        throw new InvalidDataException(
            $"SS8-8 native build growth canary rejected {reachableTopologyBytes:N0} reachable bytes for {tupleCount:N0} tuples; the conservative limit is {growthCanaryLimitBytes:N0} bytes.");
    }

    /// <summary>
    /// Checks global tuple order, exact tuple uniqueness, key uniqueness, and optional single-key-per-identity semantics before storage allocation begins.<br/>
    /// Encoded values are compared directly so validation uses the same byte ordering as persisted shelves and routers.<br/>
    /// </summary>
    private static void ValidateScalar8Scalar8SortedInput(
        IScalar8Scalar8SortedTupleSource tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        CancellationToken cancellationToken)
    {
        HashSet<ulong>? identities = singleKeyPerIdentity ? new HashSet<ulong>() : null;
        for (int i = 0; i < tuples.Count; i++)
        {
            if ((i & 0x0FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            Scalar8Scalar8SortedTuple current = tuples.GetTuple(i);
            if (identities is not null && !identities.Add(current.Identity))
                throw new InvalidOperationException($"The SS8-8 sorted input contains identity 0x{current.Identity:X16} more than once for a single-key-per-identity index.");
            if (i == 0)
                continue;

            Scalar8Scalar8SortedTuple previous = tuples.GetTuple(i - 1);
            if (previous.Key > current.Key ||
                (previous.Key == current.Key && previous.Identity > current.Identity))
            {
                throw new ArgumentException(
                    $"The SS8-8 sorted input is inverted at ordinal {i}.",
                    nameof(tuples));
            }
            if (previous.Key == current.Key && previous.Identity == current.Identity)
                throw new ArgumentException($"The SS8-8 sorted input repeats one exact tuple at ordinal {i}.", nameof(tuples));
            if (!allowDuplicateKeys && previous.Key == current.Key)
                throw new InvalidOperationException($"The SS8-8 sorted input repeats key 0x{current.Key:X16} for a unique index.");
        }
    }

    /// <summary>
    /// Builds the route targets for one encoded-key byte depth while greedily sharing packed shelves across adjacent complete byte groups.<br/>
    /// A group larger than one ordinary shelf recurses to the next key byte; at the final byte, an over-capacity exact key becomes a terminal identity chain.<br/>
    /// </summary>
    private long[] BuildScalar8Scalar8SortedRouterTargets(
        IScalar8Scalar8SortedTupleSource tuples,
        int start,
        int end,
        int keyDepth,
        Scalar8Scalar8SortedBuildState state)
    {
        if ((uint)keyDepth >= Scalar8Scalar8Layout.KeySize)
            throw new InvalidDataException("The SS8-8 sorted builder exhausted the encoded key without reaching a terminal identity route.");

        long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
        int current = start;
        while (current < end)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            byte prefix = GetScalar8Scalar8SortedKeyByte(tuples.GetTuple(current).Key, keyDepth);
            int groupEnd = FindScalar8Scalar8SortedPrefixEnd(tuples, current, end, keyDepth, prefix);
            int groupCount = groupEnd - current;
            if (groupCount > state.Profile.MaxItemCount)
            {
                targets[prefix] = keyDepth == Scalar8Scalar8Layout.KeySize - 1
                    ? BuildScalar8Scalar8SortedTerminal(tuples, current, groupEnd, state)
                    : BuildScalar8Scalar8SortedRouter(tuples, current, groupEnd, keyDepth + 1, state);
                current = groupEnd;
                continue;
            }

            int packStart = current;
            int packEnd = groupEnd;
            int next = groupEnd;
            while (next < end)
            {
                byte nextPrefix = GetScalar8Scalar8SortedKeyByte(tuples.GetTuple(next).Key, keyDepth);
                int nextEnd = FindScalar8Scalar8SortedPrefixEnd(tuples, next, end, keyDepth, nextPrefix);
                int nextCount = nextEnd - next;
                if (nextCount > state.Profile.MaxItemCount || nextEnd - packStart > state.Profile.MaxItemCount)
                    break;
                packEnd = nextEnd;
                next = nextEnd;
            }

            long shelfOffset = BuildScalar8Scalar8SortedOrdinaryShelf(tuples, packStart, packEnd, state);
            for (int i = packStart; i < packEnd; i++)
                targets[GetScalar8Scalar8SortedKeyByte(tuples.GetTuple(i).Key, keyDepth)] = shelfOffset;
            current = packEnd;
        }

        return targets;
    }

    /// <summary>
    /// Builds one non-root direct router after every child target for its range has been allocated and initialized.<br/>
    /// This is bottom-up construction: no reachable parent can reference an incomplete child, and the stable root remains untouched until the complete child graph is durable.<br/>
    /// </summary>
    private long BuildScalar8Scalar8SortedRouter(
        IScalar8Scalar8SortedTupleSource tuples,
        int start,
        int end,
        int keyDepth,
        Scalar8Scalar8SortedBuildState state)
    {
        if (end - start <= state.Profile.MaxItemCount)
            return BuildScalar8Scalar8SortedOrdinaryShelf(tuples, start, end, state);

        long[] targets = BuildScalar8Scalar8SortedRouterTargets(tuples, start, end, keyDepth, state);
        RawDataReservation router = kernel.Reserve(RouterLayout.Size);
        RouterWriter writer = new(router.Span);
        writer.InitializeExpandedOneByte(checked((ushort)keyDepth), state.AllocationClassId, targets);
        state.RouterCount++;
        return router.Extent.Offset;
    }

    /// <summary>
    /// Packs a contiguous sorted tuple range into one ordinary `SS8-8` shelf without per-item search, mutation, or publication.<br/>
    /// Physical payload order and logical slot order are identical for this builder, producing dense deterministic shelf bytes.<br/>
    /// </summary>
    private long BuildScalar8Scalar8SortedOrdinaryShelf(
        IScalar8Scalar8SortedTupleSource tuples,
        int start,
        int end,
        Scalar8Scalar8SortedBuildState state)
    {
        int count = end - start;
        if (count <= 0 || count > state.Profile.MaxItemCount)
            throw new ArgumentOutOfRangeException(nameof(end), count, "The SS8-8 sorted shelf range must fit one ordinary shelf.");

        RawDataReservation reservation = kernel.Reserve(state.Profile.ShelfExtentSize);
        Scalar8Scalar8 shelf = new(reservation.Span, state.Profile);
        shelf.Initialize();
        for (int i = 0; i < count; i++)
        {
            Scalar8Scalar8SortedTuple tuple = tuples.GetTuple(start + i);
            int itemOffset = Scalar8Scalar8Layout.GetItemOffset(state.Profile, i);
            Scalar8Scalar8Layout.WriteItemKey(reservation.Span, itemOffset, tuple.Key);
            Scalar8Scalar8Layout.WriteItemIdentity(reservation.Span, itemOffset, tuple.Identity);
            Scalar8Scalar8Layout.WriteSlot(reservation.Span, state.Profile, i, checked((ushort)itemOffset));
        }
        Scalar8Scalar8Layout.WriteItemCount(reservation.Span, checked((ushort)count));
        state.OrdinaryShelfCount++;
        return reservation.Extent.Offset;
    }

    /// <summary>
    /// Builds one exact-key terminal root and a forward chain of densely packed sorted identity shelves.<br/>
    /// Shelves are allocated from the last identity chunk toward the first so every persisted next pointer is known when its shelf is initialized.<br/>
    /// </summary>
    private long BuildScalar8Scalar8SortedTerminal(
        IScalar8Scalar8SortedTupleSource tuples,
        int start,
        int end,
        Scalar8Scalar8SortedBuildState state)
    {
        ulong key = tuples.GetTuple(start).Key;
        int capacity = TerminalIdentity8ShelfLayout.GetCapacity(state.Profile.ShelfExtentSize);
        long nextOffset = 0;
        long tailOffset = 0;
        for (int chunkEnd = end; chunkEnd > start;)
        {
            int chunkStart = Math.Max(start, chunkEnd - capacity);
            int count = chunkEnd - chunkStart;
            RawDataReservation shelf = kernel.Reserve(state.Profile.ShelfExtentSize);
            shelf.Span.Clear();
            TerminalIdentity8ShelfLayout.WriteMagic(shelf.Span, TerminalIdentity8ShelfLayout.Magic);
            TerminalIdentity8ShelfLayout.WriteFormatVersion(shelf.Span, TerminalIdentity8ShelfLayout.FormatVersion);
            TerminalIdentity8ShelfLayout.WriteHeaderSize(shelf.Span, TerminalIdentity8ShelfLayout.HeaderSize);
            TerminalIdentity8ShelfLayout.WriteItemCount(shelf.Span, count);
            TerminalIdentity8ShelfLayout.WriteNextShelfOffset(shelf.Span, nextOffset);
            for (int i = 0; i < count; i++)
                TerminalIdentity8ShelfLayout.WriteIdentity(shelf.Span, i, tuples.GetTuple(chunkStart + i).Identity);
            if (tailOffset == 0)
                tailOffset = shelf.Extent.Offset;
            nextOffset = shelf.Extent.Offset;
            state.TerminalShelfCount++;
            chunkEnd = chunkStart;
        }

        Span<byte> keyBytes = stackalloc byte[Scalar8Scalar8Layout.KeySize];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, key);
        RawDataReservation root = kernel.Reserve(TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(
            root.Span,
            TerminalIdentityRootLayout.ShapeScalar8,
            keyBytes,
            state.Profile.ShelfExtentSize,
            nextOffset);
        TerminalIdentityRootLayout.WriteTailShelfOffset(root.Span, tailOffset);
        state.TerminalRootCount++;
        return root.Extent.Offset;
    }

    /// <summary>
    /// Finds the exclusive end of one equal prefix-byte group inside a globally sorted encoded-key range.<br/>
    /// </summary>
    private static int FindScalar8Scalar8SortedPrefixEnd(
        IScalar8Scalar8SortedTupleSource tuples,
        int start,
        int end,
        int keyDepth,
        byte prefix)
    {
        int current = start + 1;
        while (current < end && GetScalar8Scalar8SortedKeyByte(tuples.GetTuple(current).Key, keyDepth) == prefix)
            current++;
        return current;
    }

    /// <summary>
    /// Extracts one big-endian routing byte from an encoded `SS8-8` key lane.<br/>
    /// </summary>
    private static byte GetScalar8Scalar8SortedKeyByte(ulong encodedKey, int keyDepth)
        => (byte)(encodedKey >> ((Scalar8Scalar8Layout.KeySize - keyDepth - 1) * 8));

    /// <summary>
    /// Builds one or more replacement `SS8-8` generations at unreachable offsets, validates their structural counts, and redirects every selected directory slot through one directory publication.<br/>
    /// The existing slot roots remain authoritative throughout source validation and detached construction; cancellation or failure before the directory commit leaves every live generation unchanged.<br/>
    /// Existing handles retain their captured root offsets after publication, while subsequent catalog opens resolve the incremented slot generations and replacement roots.<br/>
    /// </summary>
    /// <param name="requests">Detached replacement requests owned by this session and ordered only for diagnostic attribution.<br/></param>
    /// <param name="cancellationToken">Cancellation observed during input validation, detached construction, and pre-publication validation; after the final check the complete directory image is committed without another cancellation gap.<br/></param>
    /// <returns>Per-index detached topology diagnostics plus the one directory-publication commit.<br/></returns>
    internal Scalar8Scalar8DetachedBuildGroupResult ReplaceScalar8Scalar8FromSorted(
        ReadOnlySpan<Scalar8Scalar8DetachedBuildRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Length == 0)
            throw new ArgumentException("At least one SS8-8 detached replacement request is required.", nameof(requests));
        if (durabilityBatchActive)
            throw new InvalidOperationException("Detached SS8-8 replacement cannot run inside an active durability batch.");

        var requestCopies = requests.ToArray();
        var lockRoots = new long[requestCopies.Length];
        var slotIndexes = new HashSet<int>();
        var rootOffsets = new HashSet<long>();
        for (int i = 0; i < requestCopies.Length; i++)
        {
            Scalar8Scalar8DetachedBuildRequest request = requestCopies[i];
            ArgumentNullException.ThrowIfNull(request.Tuples);
            if ((uint)request.SlotIndex >= IndexDirectoryLayout.SlotCount)
                throw new ArgumentOutOfRangeException(nameof(requests), request.SlotIndex, "A detached replacement slot is outside the fixed index directory.");
            if (request.ExpectedRootRouterOffset <= 0)
                throw new ArgumentOutOfRangeException(nameof(requests), request.ExpectedRootRouterOffset, "A detached replacement requires a positive current root-router offset.");
            if (!slotIndexes.Add(request.SlotIndex))
                throw new ArgumentException($"Detached replacement contains duplicate directory slot {request.SlotIndex}.", nameof(requests));
            if (!rootOffsets.Add(request.ExpectedRootRouterOffset))
                throw new ArgumentException($"Detached replacement contains duplicate current root {request.ExpectedRootRouterOffset}.", nameof(requests));
            lockRoots[i] = request.ExpectedRootRouterOffset;
        }

        Array.Sort(lockRoots);
        var topologyLocks = new ReaderWriterLockSlim[lockRoots.Length];
        int enteredTopologyLocks = 0;
        try
        {
            for (int i = 0; i < lockRoots.Length; i++)
            {
                ReaderWriterLockSlim topologySync = GetScalar8Scalar8TopologyMutationSync(lockRoots[i]);
                topologySync.EnterWriteLock();
                topologyLocks[i] = topologySync;
                enteredTopologyLocks++;
            }

            scalar8Scalar8WriterOperationSync.EnterWriteLock();
            try
            {
                lock (writePublicationSync)
                {
                    var currentSlots = new IndexDirectorySlotSnapshot?[IndexDirectoryLayout.SlotCount];
                    ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = IndexDirectory.ActiveSlots;
                    for (int i = 0; i < activeSlots.Length; i++)
                        currentSlots[activeSlots[i].SlotIndex] = activeSlots[i];

                    for (int i = 0; i < requestCopies.Length; i++)
                    {
                        Scalar8Scalar8DetachedBuildRequest request = requestCopies[i];
                        IndexDirectorySlotSnapshot slot = currentSlots[request.SlotIndex]
                            ?? throw new InvalidOperationException($"Detached replacement slot {request.SlotIndex} is no longer active.");
                        if (slot.RootRouterOffset != request.ExpectedRootRouterOffset)
                        {
                            throw new InvalidOperationException(
                                $"Detached replacement slot {request.SlotIndex} changed from expected root {request.ExpectedRootRouterOffset} to {slot.RootRouterOffset} before preparation began.");
                        }
                    }

                    var prepared = new Scalar8Scalar8DetachedBuildResult[requestCopies.Length];
                    try
                    {
                        for (int i = 0; i < requestCopies.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Scalar8Scalar8DetachedBuildRequest request = requestCopies[i];
                            prepared[i] = BuildDetachedScalar8Scalar8FromSortedCore(request, cancellationToken);
                            long count = CountScalar8Scalar8Identities(prepared[i].ReplacementRootRouterOffset, request.Profile);
                            if (count != request.Tuples.Count)
                            {
                                throw new InvalidDataException(
                                    $"Detached SS8-8 replacement for slot {request.SlotIndex} built {count:N0} readable identities from {request.Tuples.Count:N0} validated tuples.");
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var replacementBySlot = new Scalar8Scalar8DetachedBuildResult?[IndexDirectoryLayout.SlotCount];
                        for (int i = 0; i < prepared.Length; i++)
                            replacementBySlot[prepared[i].SlotIndex] = prepared[i];

                        var publishTimer = Stopwatch.StartNew();
                        RawDataReservation directory = kernel.ReserveAt(Superblock.IndexDirectoryOffset, Superblock.IndexDirectoryLength);
                        IndexDirectoryWriter writer = new(directory.Span);
                        writer.InitializeEmpty();
                        for (int i = 0; i < activeSlots.Length; i++)
                        {
                            IndexDirectorySlotSnapshot slot = activeSlots[i];
                            Scalar8Scalar8DetachedBuildResult? replacement = replacementBySlot[slot.SlotIndex];
                            writer.WriteSlot(replacement.HasValue
                                ? slot with
                                {
                                    RootRouterOffset = replacement.Value.ReplacementRootRouterOffset,
                                    ItemCount = replacement.Value.Build.TupleCount,
                                    Generation = checked(slot.Generation + 1)
                                }
                                : slot);
                        }

                        IndexDirectorySnapshot updated = IndexDirectorySnapshot.FromBytes(directory.Span);
                        DataKernelCommitTelemetry publicationCommit = CommitAndInvalidateRouterReadCache();
                        IndexDirectory = updated;
                        publishTimer.Stop();
                        return new Scalar8Scalar8DetachedBuildGroupResult(prepared, publishTimer.Elapsed, publicationCommit);
                    }
                    catch
                    {
                        kernel.DiscardPending();
                        ClearTerminalIdentityReadCaches();
                        ClearRouterReadCaches();
                        throw;
                    }
                }
            }
            finally
            {
                scalar8Scalar8WriterOperationSync.ExitWriteLock();
            }
        }
        finally
        {
            for (int i = enteredTopologyLocks - 1; i >= 0; i--)
                topologyLocks[i].ExitWriteLock();
        }
    }

    /// <summary>
    /// Packs and commits one complete root generation without changing the fixed directory or the caller's live root page.<br/>
    /// The complete root and all descendants become durable but remain unreachable until the group-level directory rewrite succeeds.<br/>
    /// </summary>
    /// <param name="request">Validated directory ownership, profile, source, and index-contract request.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before the detached storage commit.<br/></param>
    /// <returns>The new unreachable root offset and topology diagnostics.<br/></returns>
    private Scalar8Scalar8DetachedBuildResult BuildDetachedScalar8Scalar8FromSortedCore(
        Scalar8Scalar8DetachedBuildRequest request,
        CancellationToken cancellationToken)
    {
        byte[] currentRootBytes = new byte[RouterLayout.Size];
        kernel.Read(request.ExpectedRootRouterOffset, currentRootBytes);
        RouterReader currentRoot = new(currentRootBytes);
        if (!currentRoot.IsValid || !currentRoot.HasDirectIndex || currentRoot.KeyDepth != 0)
            throw new InvalidOperationException($"Detached SS8-8 replacement slot {request.SlotIndex} does not own a valid direct root at key depth zero.");

        ValidateScalar8Scalar8SortedInput(
            request.Tuples,
            request.AllowDuplicateKeys,
            request.SingleKeyPerIdentity && !request.IdentityMultiplicityAlreadyValidated,
            cancellationToken);

        var state = new Scalar8Scalar8SortedBuildState(request.Profile, currentRoot.AllocationClassId, cancellationToken);
        var storageTimer = Stopwatch.StartNew();
        long[] rootTargets = request.Tuples.Count == 0
            ? new long[RouterLayout.MaxOneByteRouteCount]
            : BuildScalar8Scalar8SortedRouterTargets(request.Tuples, 0, request.Tuples.Count, 0, state);
        cancellationToken.ThrowIfCancellationRequested();
        RawDataReservation root = kernel.Reserve(RouterLayout.Size);
        new RouterWriter(root.Span).InitializeExpandedOneByte(0, currentRoot.AllocationClassId, rootTargets);
        long reachableTopologyBytes = GetScalar8Scalar8ReachableTopologyBytes(state);
        long growthCanaryLimitBytes = GetScalar8Scalar8GrowthCanaryLimit(request.Tuples.Count);
        ValidateScalar8Scalar8GrowthCanary(request.Tuples.Count, reachableTopologyBytes, growthCanaryLimitBytes);
        DataKernelCommitTelemetry storageCommit = CommitAndInvalidateRouterReadCache();
        storageTimer.Stop();

        return new Scalar8Scalar8DetachedBuildResult(
            request.SlotIndex,
            request.ExpectedRootRouterOffset,
            root.Extent.Offset,
            new Scalar8Scalar8SortedBuildResult(
                request.Tuples.Count,
                state.OrdinaryShelfCount,
                state.TerminalRootCount,
                state.TerminalShelfCount,
                state.RouterCount,
                reachableTopologyBytes,
                growthCanaryLimitBytes,
                storageTimer.Elapsed,
                TimeSpan.Zero,
                storageCommit,
                default));
    }
}

/// <summary>
/// Stores one already encoded tuple consumed by the native `SS8-8` sorted builder.<br/>
/// </summary>
internal readonly record struct Scalar8Scalar8SortedTuple(ulong Key, ulong Identity);

/// <summary>
/// Exposes a stable ordinal view of globally sorted encoded `SS8-8` tuples to the native topology builder.<br/>
/// Implementations may use a managed array, a bounded page cache over a spill file, or another seekable source whose contents do not change during one build.<br/>
/// </summary>
internal interface IScalar8Scalar8SortedTupleSource
{
    /// <summary>Gets the number of encoded tuples available by ordinal.<br/></summary>
    int Count { get; }

    /// <summary>
    /// Returns the encoded tuple stored at one zero-based ordinal.<br/>
    /// </summary>
    /// <param name="index">Zero-based tuple ordinal.<br/></param>
    /// <returns>The stable encoded tuple at <paramref name="index"/>.<br/></returns>
    Scalar8Scalar8SortedTuple GetTuple(int index);
}

/// <summary>
/// Adapts the established managed-array public build path to the shared seekable-source topology primitive.<br/>
/// </summary>
internal sealed class Scalar8Scalar8SortedArraySource : IScalar8Scalar8SortedTupleSource
{
    private readonly Scalar8Scalar8SortedTuple[] tuples;

    /// <summary>
    /// Initializes a stable ordinal adapter over one encoded tuple array.<br/>
    /// </summary>
    /// <param name="tuples">Owned encoded tuples retained for the duration of the build.<br/></param>
    internal Scalar8Scalar8SortedArraySource(Scalar8Scalar8SortedTuple[] tuples)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        this.tuples = tuples;
    }

    /// <inheritdoc/>
    public int Count => tuples.Length;

    /// <inheritdoc/>
    public Scalar8Scalar8SortedTuple GetTuple(int index) => tuples[index];
}

/// <summary>
/// Accumulates shape and cancellation state while one unreachable `SS8-8` topology is packed.<br/>
/// </summary>
internal sealed class Scalar8Scalar8SortedBuildState
{
    internal Scalar8Scalar8SortedBuildState(
        Scalar8Scalar8Profile profile,
        ushort allocationClassId,
        CancellationToken cancellationToken)
    {
        Profile = profile;
        AllocationClassId = allocationClassId;
        CancellationToken = cancellationToken;
    }

    internal Scalar8Scalar8Profile Profile { get; }

    internal ushort AllocationClassId { get; }

    internal CancellationToken CancellationToken { get; }

    internal int OrdinaryShelfCount { get; set; }

    internal int TerminalRootCount { get; set; }

    internal int TerminalShelfCount { get; set; }

    internal int RouterCount { get; set; }
}

/// <summary>
/// Returns internal topology and durability evidence from one native `SS8-8` sorted build.<br/>
/// </summary>
internal readonly record struct Scalar8Scalar8SortedBuildResult(
    int TupleCount,
    int OrdinaryShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int RouterCount,
    long ReachableTopologyBytes,
    long GrowthCanaryLimitBytes,
    TimeSpan StorageBuildTime,
    TimeSpan RootPublicationTime,
    DataKernelCommitTelemetry ChildStorageCommit,
    DataKernelCommitTelemetry RootPublicationCommit);

/// <summary>
/// Describes one existing `SS8-8` slot whose replacement tuples should be packed into a detached root generation.<br/>
/// </summary>
/// <param name="SlotIndex">Fixed directory slot whose root must still match <paramref name="ExpectedRootRouterOffset"/> at publication time.<br/></param>
/// <param name="ExpectedRootRouterOffset">Current live root retained until grouped directory publication succeeds.<br/></param>
/// <param name="Profile">Persisted `SS8-8` shelf profile.<br/></param>
/// <param name="Tuples">Stable seekable encoded tuple source in key-then-identity order.<br/></param>
/// <param name="AllowDuplicateKeys">Whether the physical index accepts more than one identity under one key.<br/></param>
/// <param name="SingleKeyPerIdentity">Whether one identity may occur under only one key.<br/></param>
/// <param name="IdentityMultiplicityAlreadyValidated">Whether authoritative extraction already proved the single-key-per-identity contract.<br/></param>
internal readonly record struct Scalar8Scalar8DetachedBuildRequest(
    int SlotIndex,
    long ExpectedRootRouterOffset,
    Scalar8Scalar8Profile Profile,
    IScalar8Scalar8SortedTupleSource Tuples,
    bool AllowDuplicateKeys,
    bool SingleKeyPerIdentity,
    bool IdentityMultiplicityAlreadyValidated);

/// <summary>
/// Identifies one completely built detached root and the live generation it is prepared to replace.<br/>
/// </summary>
internal readonly record struct Scalar8Scalar8DetachedBuildResult(
    int SlotIndex,
    long PriorRootRouterOffset,
    long ReplacementRootRouterOffset,
    Scalar8Scalar8SortedBuildResult Build);

/// <summary>
/// Returns every detached root plus the single fixed-directory publication boundary that made the group visible.<br/>
/// </summary>
internal readonly record struct Scalar8Scalar8DetachedBuildGroupResult(
    IReadOnlyList<Scalar8Scalar8DetachedBuildResult> Builds,
    TimeSpan DirectoryPublicationTime,
    DataKernelCommitTelemetry DirectoryPublicationCommit);
