using System.Diagnostics;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Builds detached `SS8-8` and `VS8` root generations and redirects every selected catalog slot through one fixed-directory publication.<br/>
    /// Existing roots remain authoritative while inputs are validated and replacement topology is constructed; cancellation or failure before the directory commit leaves all live slots unchanged.<br/>
    /// This first mixed-family contract requires empty null/empty key-state routes because those routes are stored outside the ordinary root and must not survive a complete-generation replacement accidentally.<br/>
    /// </summary>
    /// <param name="scalarRequests">Existing scalar-8/scalar-8 slots and their stable sorted replacement sources.<br/></param>
    /// <param name="variableRequests">Existing variable-key/scalar-8 slots and their stable sorted replacement sources.<br/></param>
    /// <param name="cancellationToken">Cancellation observed through source validation, detached construction, and the final pre-publication boundary.<br/></param>
    /// <returns>Per-family replacement diagnostics plus the one directory publication commit.<br/></returns>
    internal NativeSortedDetachedBuildGroupResult ReplaceNativeSortedRootsAtomically(
        ReadOnlySpan<Scalar8Scalar8DetachedBuildRequest> scalarRequests,
        ReadOnlySpan<VarKeyScalar8DetachedBuildRequest> variableRequests,
        CancellationToken cancellationToken)
    {
        if (scalarRequests.Length == 0 && variableRequests.Length == 0)
            throw new ArgumentException("At least one native detached replacement request is required.");
        if (durabilityBatchActive)
            throw new InvalidOperationException("Native detached replacement cannot run inside an active durability batch.");

        Scalar8Scalar8DetachedBuildRequest[] scalarCopies = scalarRequests.ToArray();
        VarKeyScalar8DetachedBuildRequest[] variableCopies = variableRequests.ToArray();
        var slotIndexes = new HashSet<int>();
        var rootOffsets = new HashSet<long>();
        var topologyLocks = new List<(long RootOffset, ReaderWriterLockSlim Sync)>(scalarCopies.Length + variableCopies.Length);

        for (int i = 0; i < scalarCopies.Length; i++)
        {
            Scalar8Scalar8DetachedBuildRequest request = scalarCopies[i];
            ValidateNativeDetachedSlot(request.SlotIndex, request.ExpectedRootRouterOffset, request.Tuples, slotIndexes, rootOffsets);
            topologyLocks.Add((request.ExpectedRootRouterOffset, GetScalar8Scalar8TopologyMutationSync(request.ExpectedRootRouterOffset)));
        }
        for (int i = 0; i < variableCopies.Length; i++)
        {
            VarKeyScalar8DetachedBuildRequest request = variableCopies[i];
            ValidateNativeDetachedSlot(request.SlotIndex, request.ExpectedRootRouterOffset, request.Tuples, slotIndexes, rootOffsets);
            if (request.MaxKeyLength is < 1 or > 1024)
                throw new ArgumentOutOfRangeException(nameof(variableRequests), request.MaxKeyLength, "A detached VS8 replacement requires a maximum encoded key length from 1 through 1024.");
            if (request.RequestedRouteCount is < 1 or > RouterLayout.MaxOneByteRouteCount)
                throw new ArgumentOutOfRangeException(nameof(variableRequests), request.RequestedRouteCount, "A detached VS8 replacement route count must be from 1 through 256.");
            topologyLocks.Add((request.ExpectedRootRouterOffset, GetVarKeyScalar8TopologyMutationSync(request.ExpectedRootRouterOffset)));
        }

        topologyLocks.Sort(static (left, right) => left.RootOffset.CompareTo(right.RootOffset));
        int enteredTopologyLocks = 0;
        try
        {
            for (int i = 0; i < topologyLocks.Count; i++)
            {
                topologyLocks[i].Sync.EnterWriteLock();
                enteredTopologyLocks++;
            }

            scalar8Scalar8WriterOperationSync.EnterWriteLock();
            try
            {
                lock (writePublicationSync)
                {
                    ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = IndexDirectory.ActiveSlots;
                    var currentSlots = new IndexDirectorySlotSnapshot?[IndexDirectoryLayout.SlotCount];
                    for (int i = 0; i < activeSlots.Length; i++)
                        currentSlots[activeSlots[i].SlotIndex] = activeSlots[i];

                    ValidateNativeDetachedCurrentSlots(scalarCopies, variableCopies, currentSlots);
                    var scalarBuilds = new Scalar8Scalar8DetachedBuildResult[scalarCopies.Length];
                    var variableBuilds = new VarKeyScalar8DetachedBuildResult[variableCopies.Length];
                    try
                    {
                        for (int i = 0; i < scalarCopies.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Scalar8Scalar8DetachedBuildRequest request = scalarCopies[i];
                            EnsureNativeDetachedKeyStatesEmpty(request.SlotIndex, includeEmpty: false);
                            scalarBuilds[i] = BuildDetachedScalar8Scalar8FromSortedCore(request, cancellationToken);
                            long count = CountScalar8Scalar8Identities(scalarBuilds[i].ReplacementRootRouterOffset, request.Profile);
                            if (count != request.Tuples.Count)
                                throw new InvalidDataException($"Detached SS8-8 slot {request.SlotIndex} built {count:N0} readable identities from {request.Tuples.Count:N0} validated tuples.");
                        }
                        for (int i = 0; i < variableCopies.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            VarKeyScalar8DetachedBuildRequest request = variableCopies[i];
                            EnsureNativeDetachedKeyStatesEmpty(request.SlotIndex, includeEmpty: true);
                            variableBuilds[i] = BuildDetachedVarKeyScalar8FromSortedCore(request, cancellationToken);
                            long count = CountVarKeyScalar8Identities(variableBuilds[i].ReplacementRootRouterOffset, request.MaxKeyLength);
                            if (count != request.Tuples.Count)
                                throw new InvalidDataException($"Detached VS8 slot {request.SlotIndex} built {count:N0} readable identities from {request.Tuples.Count:N0} validated tuples.");
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var replacementRootBySlot = new long[IndexDirectoryLayout.SlotCount];
                        var replacementCountBySlot = new long[IndexDirectoryLayout.SlotCount];
                        for (int i = 0; i < scalarBuilds.Length; i++)
                        {
                            replacementRootBySlot[scalarBuilds[i].SlotIndex] = scalarBuilds[i].ReplacementRootRouterOffset;
                            replacementCountBySlot[scalarBuilds[i].SlotIndex] = scalarBuilds[i].Build.TupleCount;
                        }
                        for (int i = 0; i < variableBuilds.Length; i++)
                        {
                            replacementRootBySlot[variableBuilds[i].SlotIndex] = variableBuilds[i].ReplacementRootRouterOffset;
                            replacementCountBySlot[variableBuilds[i].SlotIndex] = variableBuilds[i].Build.TupleCount;
                        }

                        var publishTimer = Stopwatch.StartNew();
                        RawDataReservation directory = kernel.ReserveAt(Superblock.IndexDirectoryOffset, Superblock.IndexDirectoryLength);
                        IndexDirectoryWriter writer = new(directory.Span);
                        writer.InitializeEmpty();
                        for (int i = 0; i < activeSlots.Length; i++)
                        {
                            IndexDirectorySlotSnapshot slot = activeSlots[i];
                            long replacementRoot = replacementRootBySlot[slot.SlotIndex];
                            writer.WriteSlot(replacementRoot == 0
                                ? slot
                                : slot with
                                {
                                    RootRouterOffset = replacementRoot,
                                    ItemCount = replacementCountBySlot[slot.SlotIndex],
                                    Generation = checked(slot.Generation + 1)
                                });
                        }

                        IndexDirectorySnapshot updated = IndexDirectorySnapshot.FromBytes(directory.Span);
                        DataKernelCommitTelemetry publicationCommit = CommitAndInvalidateRouterReadCache();
                        IndexDirectory = updated;
                        publishTimer.Stop();
                        return new NativeSortedDetachedBuildGroupResult(scalarBuilds, variableBuilds, publishTimer.Elapsed, publicationCommit);
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
                topologyLocks[i].Sync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Validates one mixed detached request's stable slot/root identity and rejects duplicate ownership before locks or storage are acquired.<br/>
    /// </summary>
    private static void ValidateNativeDetachedSlot(
        int slotIndex,
        long rootRouterOffset,
        object tuples,
        HashSet<int> slotIndexes,
        HashSet<long> rootOffsets)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if ((uint)slotIndex >= IndexDirectoryLayout.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "A detached replacement slot is outside the fixed index directory.");
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "A detached replacement requires a positive current root-router offset.");
        if (!slotIndexes.Add(slotIndex))
            throw new ArgumentException($"Detached replacement contains duplicate directory slot {slotIndex}.");
        if (!rootOffsets.Add(rootRouterOffset))
            throw new ArgumentException($"Detached replacement contains duplicate current root {rootRouterOffset}.");
    }

    /// <summary>
    /// Revalidates every selected directory slot immediately before detached storage construction.<br/>
    /// </summary>
    private static void ValidateNativeDetachedCurrentSlots(
        IReadOnlyList<Scalar8Scalar8DetachedBuildRequest> scalarRequests,
        IReadOnlyList<VarKeyScalar8DetachedBuildRequest> variableRequests,
        IReadOnlyList<IndexDirectorySlotSnapshot?> currentSlots)
    {
        for (int i = 0; i < scalarRequests.Count; i++)
            ValidateNativeDetachedCurrentSlot(scalarRequests[i].SlotIndex, scalarRequests[i].ExpectedRootRouterOffset, currentSlots);
        for (int i = 0; i < variableRequests.Count; i++)
            ValidateNativeDetachedCurrentSlot(variableRequests[i].SlotIndex, variableRequests[i].ExpectedRootRouterOffset, currentSlots);
    }

    /// <summary>Validates one active slot's expected live root before candidate construction.<br/></summary>
    private static void ValidateNativeDetachedCurrentSlot(
        int slotIndex,
        long expectedRootRouterOffset,
        IReadOnlyList<IndexDirectorySlotSnapshot?> currentSlots)
    {
        IndexDirectorySlotSnapshot slot = currentSlots[slotIndex]
            ?? throw new InvalidOperationException($"Detached replacement slot {slotIndex} is no longer active.");
        if (slot.RootRouterOffset != expectedRootRouterOffset)
            throw new InvalidOperationException($"Detached replacement slot {slotIndex} changed from expected root {expectedRootRouterOffset} to {slot.RootRouterOffset} before preparation began.");
    }

    /// <summary>
    /// Rejects detached ordinary-root replacement when an out-of-root null or empty population exists.<br/>
    /// </summary>
    private void EnsureNativeDetachedKeyStatesEmpty(int slotIndex, bool includeEmpty)
    {
        if (ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Null).Length != 0)
            throw new NotSupportedException($"Detached native replacement for slot {slotIndex} requires an empty null-key route.");
        if (includeEmpty && ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Empty).Length != 0)
            throw new NotSupportedException($"Detached native replacement for slot {slotIndex} requires an empty empty-string route.");
    }

    /// <summary>
    /// Packs one complete `VS8` root generation at an unreachable offset and commits its storage without changing the live directory slot.<br/>
    /// </summary>
    private VarKeyScalar8DetachedBuildResult BuildDetachedVarKeyScalar8FromSortedCore(
        VarKeyScalar8DetachedBuildRequest request,
        CancellationToken cancellationToken)
    {
        byte[] currentRootBytes = new byte[RouterLayout.Size];
        kernel.Read(request.ExpectedRootRouterOffset, currentRootBytes);
        RouterReader currentRoot = new(currentRootBytes);
        if (!currentRoot.IsValid || !currentRoot.HasDirectIndex || currentRoot.KeyDepth != 0)
            throw new InvalidOperationException($"Detached VS8 replacement slot {request.SlotIndex} does not own a valid direct root at key depth zero.");

        ValidateVarKeyScalar8SortedSource(
            request.Tuples,
            request.MaxKeyLength,
            request.AllowDuplicateKeys,
            request.SingleKeyPerIdentity && !request.IdentityMultiplicityAlreadyValidated,
            cancellationToken);

        var buildTimer = Stopwatch.StartNew();
        var rootTargets = new long[RouterLayout.MaxOneByteRouteCount];
        int rootPrefixCount = 0;
        int routeCount = 0;
        int routerCount = 0;
        int shelfCount = 0;
        int start = 0;
        while (start < request.Tuples.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte rootPrefix = request.Tuples.GetKey(start)[0];
            int end = start + 1;
            while (end < request.Tuples.Count && request.Tuples.GetKey(end)[0] == rootPrefix)
                end++;
            VarLenOptimizerReplacementSubtree replacement = CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
                request.Tuples,
                start,
                end,
                request.MaxKeyLength,
                request.RequestedRouteCount,
                request.Descending);
            if (replacement.RootPrefix != rootPrefix || rootTargets[rootPrefix] != 0)
                throw new InvalidDataException("The detached VS8 builder produced an invalid or duplicate root-prefix replacement.");
            rootTargets[rootPrefix] = replacement.TargetOffset;
            rootPrefixCount++;
            routeCount = checked(routeCount + replacement.Build.RouteCount);
            routerCount = checked(routerCount + replacement.Build.RouterCount);
            shelfCount = checked(shelfCount + replacement.Build.ShelfCount);
            start = end;
        }

        cancellationToken.ThrowIfCancellationRequested();
        RawDataReservation root = kernel.Reserve(RouterLayout.Size);
        new RouterWriter(root.Span).InitializeExpandedOneByte(0, currentRoot.AllocationClassId, rootTargets);
        DataKernelCommitTelemetry storageCommit = CommitAndInvalidateRouterReadCache();
        buildTimer.Stop();
        return new VarKeyScalar8DetachedBuildResult(
            request.SlotIndex,
            request.ExpectedRootRouterOffset,
            root.Extent.Offset,
            new VarKeyScalar8SortedBuildResult(
                request.Tuples.Count,
                rootPrefixCount,
                routeCount,
                routerCount,
                shelfCount,
                buildTimer.Elapsed,
                TimeSpan.Zero,
                storageCommit,
                default));
    }
}

/// <summary>
/// Describes one existing `VS8` directory slot and its detached sorted replacement source.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8DetachedBuildRequest(
    int SlotIndex,
    long ExpectedRootRouterOffset,
    int MaxKeyLength,
    int RequestedRouteCount,
    IVarKeyScalar8SortedTupleSource Tuples,
    bool AllowDuplicateKeys,
    bool SingleKeyPerIdentity,
    bool IdentityMultiplicityAlreadyValidated,
    bool Descending = false);

/// <summary>Identifies one built detached `VS8` root and the live generation it can replace.<br/></summary>
internal readonly record struct VarKeyScalar8DetachedBuildResult(
    int SlotIndex,
    long PriorRootRouterOffset,
    long ReplacementRootRouterOffset,
    VarKeyScalar8SortedBuildResult Build);

/// <summary>Returns every mixed native detached build plus its one fixed-directory publication boundary.<br/></summary>
internal readonly record struct NativeSortedDetachedBuildGroupResult(
    IReadOnlyList<Scalar8Scalar8DetachedBuildResult> ScalarBuilds,
    IReadOnlyList<VarKeyScalar8DetachedBuildResult> VariableBuilds,
    TimeSpan DirectoryPublicationTime,
    DataKernelCommitTelemetry DirectoryPublicationCommit);
