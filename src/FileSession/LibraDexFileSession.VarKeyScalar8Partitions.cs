using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    private const int FastFindTargetsPerWorker = 32;
    private const int FastFindMaximumFrontierTargets = 256;

    /// <summary>
    /// Attempts to divide one inclusive encoded `VS8` key range into an exact number of disjoint, topology-aligned physical ranges.<br/>
    /// Router targets are visited once in logical key order; ordinary shelves contribute header counts plus only their boundary keys, while duplicate and terminal chains contribute per-shelf header counts without decoding every identity.<br/>
    /// The returned plan retains the coherent read acquired on this thread so a caller can open every worker-owned reader before releasing the planning snapshot.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VS8` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower bound.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper bound.<br/></param>
    /// <param name="workerCount">The exact number of non-overlapping physical ranges required.<br/></param>
    /// <param name="plan">The topology-aligned plan when the current range contains enough independent extents; otherwise <see langword="null"/>.<br/></param>
    /// <returns><see langword="true"/> only when exactly <paramref name="workerCount"/> ranges were created.<br/></returns>
    internal bool TryCreateVarKeyScalar8PhysicalPartitions(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        int workerCount,
        out VarKeyScalar8PhysicalPartitionPlan? plan)
    {
        plan = null;
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset));
        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength));
        if (workerCount < 2)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "A physical partition plan requires at least two workers.");
        if (lowerKey.IsEmpty || upperKey.IsEmpty || lowerKey.SequenceCompareTo(upperKey) > 0)
            throw new ArgumentException("A VS8 physical partition plan requires a non-empty ascending encoded key range.");

        DataKernel.CoherentReadLease transitionRead = EnterCoherentRead();
        try
        {
            VarKeyScalar8PhysicalFrontierState frontier = new(lowerKey.ToArray(), upperKey.ToArray());
            frontier.Targets.Add(new VarKeyScalar8PhysicalTarget(
                rootRouterOffset,
                DefaultVarKeyScalar8MaxRouterHops + 1,
                LowerEdge: true,
                UpperEdge: true));
            frontier.TargetOffsets.Add(rootRouterOffset);
            frontier.TargetKinds.Add(rootRouterOffset, VarKeyScalar8RouteTargetKind.Router);

            int targetGoal = (int)Math.Min(
                FastFindMaximumFrontierTargets,
                Math.Max(workerCount, (long)workerCount * FastFindTargetsPerWorker));
            while (frontier.Targets.Count < targetGoal)
            {
                int expandableIndex = FindVarKeyScalar8ExpandableFrontierTarget(rootRouterOffset, frontier);
                if (expandableIndex < 0)
                {
                    if (frontier.Targets.Count < workerCount)
                    {
                        transitionRead.Dispose();
                        return false;
                    }

                    break;
                }

                ExpandVarKeyScalar8PhysicalFrontierRouter(expandableIndex, maxKeyLength, frontier);
            }

            VarKeyScalar8PhysicalPartition[] partitions = BuildVarKeyScalar8PhysicalFrontierPartitions(frontier, workerCount);
            plan = new VarKeyScalar8PhysicalPartitionPlan(
                partitions,
                transitionRead,
                frontier.RouterPagesRead,
                frontier.Targets.Count);
            return true;
        }
        catch
        {
            transitionRead.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to divide one exhausted-key `VS8` terminal identity chain into an exact number of disjoint identity-slot partitions.<br/>
    /// The planner walks only terminal shelf headers, retains no identity collection, and holds a coherent transition read until every worker has opened its own reader.<br/>
    /// Ordinary and duplicate-run shelves deliberately fail closed because their exact-key slot partitioning has a different physical contract.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The routed `VS8` root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="encodedKey">The exact nonempty encoded key whose terminal route is required.<br/></param>
    /// <param name="workerCount">The exact number of nonempty identity-slot partitions required.<br/></param>
    /// <param name="plan">The terminal-chain plan when the key resolves to enough identity slots; otherwise <see langword="null"/>.<br/></param>
    /// <returns><see langword="true"/> only when exactly <paramref name="workerCount"/> disjoint partitions were created.<br/></returns>
    internal bool TryCreateVarKeyScalar8TerminalIdentityPartitions(
        long rootRouterOffset,
        int maxKeyLength,
        ReadOnlySpan<byte> encodedKey,
        int workerCount,
        out VarKeyScalar8TerminalIdentityPartitionPlan? plan)
    {
        plan = null;
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset));
        if (maxKeyLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength));
        if (encodedKey.IsEmpty || encodedKey.Length > maxKeyLength)
            throw new ArgumentException("A terminal identity partition plan requires one valid nonempty encoded key.", nameof(encodedKey));
        if (workerCount < 2)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "A terminal identity partition plan requires at least two workers.");

        DataKernel.CoherentReadLease transitionRead = EnterCoherentRead();
        try
        {
            VarKeyScalar8RoutePathTarget pathTarget = WalkVarKeyScalar8RoutePathTarget(
                rootRouterOffset,
                encodedKey,
                DefaultVarKeyScalar8MaxRouterHops);
            if (pathTarget.Target.Kind != VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
            {
                transitionRead.Dispose();
                return false;
            }

            byte[] rootBytes = ReadTerminalIdentityRootBytes(pathTarget.Target.Offset);
            if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey ||
                !IsTerminalIdentityRootForKey(rootBytes, encodedKey, out long firstShelfOffset))
            {
                transitionRead.Dispose();
                return false;
            }

            int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
            List<VarKeyScalar8TerminalIdentityShelfExtent> shelves = [];
            HashSet<long> visited = [];
            long totalIdentityCount = 0;
            long shelfOffset = firstShelfOffset;
            while (shelfOffset != 0)
            {
                if (!visited.Add(shelfOffset))
                    throw new InvalidDataException("The VS8 terminal identity partition planner found a shelf cycle.");

                byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(shelfOffset, shelfExtentSize);
                ValidateTerminalIdentity8Shelf(shelfBytes, shelfExtentSize);
                int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
                if (itemCount > 0)
                {
                    shelves.Add(new VarKeyScalar8TerminalIdentityShelfExtent(shelfOffset, itemCount));
                    totalIdentityCount = checked(totalIdentityCount + itemCount);
                }

                shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
            }

            if (totalIdentityCount < workerCount)
            {
                transitionRead.Dispose();
                return false;
            }

            VarKeyScalar8TerminalIdentityPartition[] partitions =
                BalanceVarKeyScalar8TerminalIdentityPartitions(shelves, shelfExtentSize, totalIdentityCount, workerCount);
            plan = new VarKeyScalar8TerminalIdentityPartitionPlan(partitions, transitionRead);
            return true;
        }
        catch
        {
            transitionRead.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Streams one disjoint terminal identity-slot partition under a worker-owned coherent read.<br/>
    /// The iterator begins at the planned shelf and slot, follows the immutable chain snapshot forward, and stops after the exact assigned identity count.<br/>
    /// </summary>
    /// <param name="partition">The terminal shelf/slot slice created by the coherent planner.<br/></param>
    /// <returns>A lazy scalar-identity stream that acquires and releases its read lease on the enumerating worker thread.<br/></returns>
    internal IEnumerable<ulong> IterateVarKeyScalar8TerminalIdentityPartition(
        VarKeyScalar8TerminalIdentityPartition partition)
    {
        using DataKernel.CoherentReadLease coherentRead = EnterCoherentRead();
        long shelfOffset = partition.StartShelfOffset;
        int slotIndex = partition.StartSlotIndex;
        long remaining = partition.IdentityCount;
        while (remaining > 0)
        {
            if (shelfOffset == 0)
                throw new InvalidDataException("The VS8 terminal identity partition ended before its assigned identity count was read.");

            byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(shelfOffset, partition.ShelfExtentSize);
            ValidateTerminalIdentity8Shelf(shelfBytes, partition.ShelfExtentSize);
            int itemCount = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
            if ((uint)slotIndex > (uint)itemCount)
                throw new InvalidDataException("The VS8 terminal identity partition start slot exceeds its shelf item count.");

            int available = itemCount - slotIndex;
            int take = (int)Math.Min(remaining, available);
            for (int i = 0; i < take; i++)
                yield return TerminalIdentity8ShelfLayout.ReadIdentity(shelfBytes, slotIndex + i);

            remaining -= take;
            shelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
            slotIndex = 0;
        }
    }

    /// <summary>
    /// Balances one terminal identity chain by identity count and permits boundaries inside a shelf so every requested worker receives a nonempty slice.<br/>
    /// </summary>
    private static VarKeyScalar8TerminalIdentityPartition[] BalanceVarKeyScalar8TerminalIdentityPartitions(
        IReadOnlyList<VarKeyScalar8TerminalIdentityShelfExtent> shelves,
        int shelfExtentSize,
        long totalIdentityCount,
        int workerCount)
    {
        VarKeyScalar8TerminalIdentityPartition[] partitions = new VarKeyScalar8TerminalIdentityPartition[workerCount];
        int shelfIndex = 0;
        int slotIndex = 0;
        long remaining = totalIdentityCount;
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            int remainingWorkers = workerCount - workerIndex;
            long identityCount = remaining / remainingWorkers;
            if (remaining % remainingWorkers != 0)
                identityCount++;

            partitions[workerIndex] = new VarKeyScalar8TerminalIdentityPartition(
                shelves[shelfIndex].ShelfOffset,
                slotIndex,
                identityCount,
                shelfExtentSize);

            long advance = identityCount;
            while (advance > 0)
            {
                int available = shelves[shelfIndex].ItemCount - slotIndex;
                if (advance < available)
                {
                    slotIndex += (int)advance;
                    advance = 0;
                }
                else
                {
                    advance -= available;
                    shelfIndex++;
                    slotIndex = 0;
                }
            }

            remaining -= identityCount;
        }

        return partitions;
    }

    /// <summary>
    /// Walks one `VS8` router in logical key order and collects each distinct target exactly once.<br/>
    /// Direct target aliases must remain contiguous, and a routed target must have one logical parent; violations fail closed instead of risking overlapping worker ranges.<br/>
    /// </summary>
    private void CollectVarKeyScalar8PhysicalPartitionRouter(
        long routerOffset,
        long parentRouterOffset,
        int maxKeyLength,
        int maxRouterHops,
        int depth,
        VarKeyScalar8PhysicalPartitionState state)
    {
        if (depth >= maxRouterHops)
            throw new InvalidDataException("The VS8 physical partition planner exceeded the configured router hop count.");
        if (!state.ActiveRouters.Add(routerOffset))
            throw new InvalidDataException("The VS8 physical partition planner found a router cycle.");
        if (!state.ExpandedRouters.Add(routerOffset))
        {
            state.ActiveRouters.Remove(routerOffset);
            return;
        }

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(routerOffset, routerBytes);
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException($"The VS8 physical partition planner found an invalid router at offset {routerOffset}.");

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
                throw new InvalidDataException("The VS8 physical partition planner found a noncontiguous target alias.");

            previousTarget = targetOffset;
            if (targetOffset != 0)
            {
                CollectVarKeyScalar8PhysicalPartitionTarget(
                    targetOffset,
                    routerOffset,
                    maxKeyLength,
                    maxRouterHops,
                    depth + 1,
                    state);
            }
        }

        state.ActiveRouters.Remove(routerOffset);
    }

    /// <summary>
    /// Classifies one routed target and collects its child router or one logical shelf extent.<br/>
    /// </summary>
    private void CollectVarKeyScalar8PhysicalPartitionTarget(
        long targetOffset,
        long parentRouterOffset,
        int maxKeyLength,
        int maxRouterHops,
        int depth,
        VarKeyScalar8PhysicalPartitionState state)
    {
        if (state.TargetParents.TryGetValue(targetOffset, out long existingParent))
        {
            if (existingParent != parentRouterOffset)
                throw new InvalidDataException("The VS8 physical partition planner found one target referenced by multiple parent routers.");
        }
        else
        {
            state.TargetParents.Add(targetOffset, parentRouterOffset);
        }

        VarKeyScalar8RouteTargetKind kind = ClassifyVarKeyScalar8RouteTarget(targetOffset);
        if (kind == VarKeyScalar8RouteTargetKind.Router)
        {
            CollectVarKeyScalar8PhysicalPartitionRouter(targetOffset, parentRouterOffset, maxKeyLength, maxRouterHops, depth, state);
            return;
        }
        if (!state.EmittedTargets.Add(targetOffset))
            return;
        if (kind == VarKeyScalar8RouteTargetKind.Shelf)
        {
            CollectVarKeyScalar8PhysicalPartitionShelf(targetOffset, maxKeyLength, state);
            return;
        }
        if (kind == VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
        {
            CollectVarKeyScalar8PhysicalPartitionTerminal(targetOffset, state);
            return;
        }

        throw new InvalidDataException($"The VS8 physical partition planner found an unsupported target at offset {targetOffset}.");
    }

    /// <summary>
    /// Collects header counts and boundary keys for one ordinary or duplicate-run shelf extent.<br/>
    /// </summary>
    private void CollectVarKeyScalar8PhysicalPartitionShelf(
        long headShelfOffset,
        int maxKeyLength,
        VarKeyScalar8PhysicalPartitionState state)
    {
        long currentOffset = headShelfOffset;
        long itemCount = 0;
        byte[]? firstKey = null;
        byte[]? lastKey = null;
        bool duplicateRun = false;
        int physicalShelfCount = 0;
        HashSet<long> chainOffsets = [];
        while (currentOffset != 0)
        {
            if (!chainOffsets.Add(currentOffset))
                throw new InvalidDataException("The VS8 physical partition planner found a duplicate-run cycle.");

            VarKeyScalar8ReadOnly shelf = ReadVarKeyScalar8ReadOnlyShelf(currentOffset, maxKeyLength);
            if (physicalShelfCount == 0)
                duplicateRun = shelf.IsDuplicateRun;
            else if (!duplicateRun || !shelf.IsDuplicateRun)
                throw new InvalidDataException("The VS8 physical partition planner found an inconsistent duplicate-run chain.");

            int shelfItemCount = shelf.ItemCount;
            if (shelfItemCount > 0)
            {
                firstKey ??= shelf.ReadKeyAt(0).ToArray();
                lastKey = shelf.ReadKeyAt(shelfItemCount - 1).ToArray();
                itemCount = checked(itemCount + shelfItemCount);
            }

            physicalShelfCount++;
            currentOffset = shelf.IsDuplicateRun ? shelf.DuplicateRunNextOffset : 0;
        }

        if (firstKey is not null && lastKey is not null)
            AddVarKeyScalar8PhysicalPartitionExtent(firstKey, lastKey, itemCount, state);
    }

    /// <summary>
    /// Collects the encoded key and identity-shelf header counts for one terminal identity chain.<br/>
    /// </summary>
    private void CollectVarKeyScalar8PhysicalPartitionTerminal(
        long rootOffset,
        VarKeyScalar8PhysicalPartitionState state)
    {
        byte[] rootBytes = ReadTerminalIdentityRootBytes(rootOffset);
        if (TerminalIdentityRootLayout.ReadShape(rootBytes) != TerminalIdentityRootLayout.ShapeVarKey)
            throw new InvalidDataException("The VS8 physical partition planner found a terminal root with the wrong shape.");

        int keyLength = TerminalIdentityRootLayout.ReadKeyLength(rootBytes);
        byte[] key = rootBytes.AsSpan(TerminalIdentityRootLayout.KeyBytesOffset, keyLength).ToArray();
        int shelfExtentSize = TerminalIdentityRootLayout.ReadShelfExtentSize(rootBytes);
        long currentOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        long itemCount = 0;
        HashSet<long> chainOffsets = [];
        while (currentOffset != 0)
        {
            if (!chainOffsets.Add(currentOffset))
                throw new InvalidDataException("The VS8 physical partition planner found a terminal identity shelf cycle.");

            byte[] shelfBytes = ReadTerminalIdentity8ShelfBytesCached(currentOffset, shelfExtentSize);
            ValidateTerminalIdentity8Shelf(shelfBytes, shelfExtentSize);
            itemCount = checked(itemCount + TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes));
            currentOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
        }

        if (itemCount != 0)
            AddVarKeyScalar8PhysicalPartitionExtent(key, key, itemCount, state);
    }

    /// <summary>
    /// Validates one logical extent against its predecessor and retains it only when it intersects the requested query range.<br/>
    /// </summary>
    private static void AddVarKeyScalar8PhysicalPartitionExtent(
        byte[] firstKey,
        byte[] lastKey,
        long itemCount,
        VarKeyScalar8PhysicalPartitionState state)
    {
        if (state.LastTopologyKey is not null && state.LastTopologyKey.AsSpan().SequenceCompareTo(firstKey) >= 0)
            throw new InvalidDataException("The VS8 physical partition planner found overlapping or non-increasing logical extents.");

        state.LastTopologyKey = lastKey;
        if (lastKey.AsSpan().SequenceCompareTo(state.LowerKey) < 0 ||
            firstKey.AsSpan().SequenceCompareTo(state.UpperKey) > 0)
        {
            return;
        }

        state.Extents.Add(new VarKeyScalar8PhysicalExtent(firstKey, lastKey, itemCount));
    }

    /// <summary>
    /// Balances contiguous logical extents by header-reported tuple count while reserving at least one extent for every remaining worker.<br/>
    /// </summary>
    private static VarKeyScalar8PhysicalPartition[] BalanceVarKeyScalar8PhysicalPartitions(
        VarKeyScalar8PhysicalPartitionState state,
        int workerCount)
    {
        long remainingWeight = 0;
        for (int i = 0; i < state.Extents.Count; i++)
            remainingWeight = checked(remainingWeight + state.Extents[i].ItemCount);

        VarKeyScalar8PhysicalPartition[] partitions = new VarKeyScalar8PhysicalPartition[workerCount];
        int start = 0;
        for (int partitionIndex = 0; partitionIndex < workerCount; partitionIndex++)
        {
            int remainingPartitions = workerCount - partitionIndex;
            int maximumEnd = state.Extents.Count - remainingPartitions;
            long targetWeight = remainingPartitions == 0
                ? remainingWeight
                : (remainingWeight + remainingPartitions - 1) / remainingPartitions;
            int end = start;
            long partitionWeight = 0;
            do
            {
                partitionWeight = checked(partitionWeight + state.Extents[end].ItemCount);
                if (end >= maximumEnd || partitionWeight >= targetWeight)
                    break;
                end++;
            }
            while (true);

            VarKeyScalar8PhysicalExtent first = state.Extents[start];
            VarKeyScalar8PhysicalExtent last = state.Extents[end];
            byte[] lower = first.FirstKey.AsSpan().SequenceCompareTo(state.LowerKey) < 0
                ? state.LowerKey.ToArray()
                : first.FirstKey.ToArray();
            byte[] upper = last.LastKey.AsSpan().SequenceCompareTo(state.UpperKey) > 0
                ? state.UpperKey.ToArray()
                : last.LastKey.ToArray();
            partitions[partitionIndex] = new VarKeyScalar8PhysicalPartition(lower, upper, partitionWeight);
            remainingWeight -= partitionWeight;
            start = end + 1;
        }

        return partitions;
    }

    /// <summary>
    /// Finds one router target whose expansion can increase the current exact-worker frontier.<br/>
    /// The root is known to be a router and avoids a redundant target-classification read; subsequent targets are classified lazily only while the frontier still contains too few independent subtrees.<br/>
    /// Shelf and terminal targets remain final frontier units and are never opened merely to estimate their tuple population.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The known routed index root.<br/></param>
    /// <param name="frontier">The bounded planner state whose targets are candidates for expansion.<br/></param>
    /// <returns>The zero-based expandable target index, or `-1` when no additional router exists.<br/></returns>
    private int FindVarKeyScalar8ExpandableFrontierTarget(
        long rootRouterOffset,
        VarKeyScalar8PhysicalFrontierState frontier)
    {
        for (int index = 0; index < frontier.Targets.Count; index++)
        {
            long targetOffset = frontier.Targets[index].TargetOffset;
            if (!frontier.TargetKinds.TryGetValue(targetOffset, out VarKeyScalar8RouteTargetKind kind))
            {
                kind = ClassifyVarKeyScalar8RouteTarget(targetOffset);
                frontier.TargetKinds.Add(targetOffset, kind);
            }

            if (kind == VarKeyScalar8RouteTargetKind.Router)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Replaces one router frontier target with its unique range-intersecting child targets.<br/>
    /// The expansion mirrors the ordinary `VS8` range reader's edge propagation, but stops at child target offsets instead of opening shelves or descending the entire topology.<br/>
    /// Aliased route slots are coalesced into one physical child and conservatively drop an edge flag when more than one logical route context shares that child, preventing an aliased subtree from being pruned by only one of its owners.<br/>
    /// </summary>
    /// <param name="frontierIndex">The zero-based router target to replace.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="frontier">The bounded planner state updated in place.<br/></param>
    private void ExpandVarKeyScalar8PhysicalFrontierRouter(
        int frontierIndex,
        int maxKeyLength,
        VarKeyScalar8PhysicalFrontierState frontier)
    {
        VarKeyScalar8PhysicalTarget parent = frontier.Targets[frontierIndex];
        if (parent.RemainingRouterHops <= 0)
            throw new InvalidDataException("The VS8 physical frontier planner exceeded the configured router hop count.");
        if (!frontier.ExpandedRouterOffsets.Add(parent.TargetOffset))
            throw new InvalidDataException("The VS8 physical frontier planner found a router cycle or repeated router owner.");

        Span<byte> routerBytes = stackalloc byte[RouterLayout.Size];
        ReadRouterPageUsingArenaCache(parent.TargetOffset, routerBytes);
        frontier.RouterPagesRead++;
        RouterReader router = new(routerBytes);
        if (!router.IsValid)
            throw new InvalidDataException($"The VS8 physical frontier planner found an invalid router at offset {parent.TargetOffset}.");

        List<VarKeyScalar8PhysicalTarget> children = [];
        Dictionary<long, int> childIndexes = [];
        int childHops = parent.RemainingRouterHops - 1;
        if (router.PrefixByteCount == 1)
        {
            byte lowerPrefix = parent.LowerEdge
                ? GetVarKeyScalar8Prefix(frontier.LowerKey, router.KeyDepth)
                : byte.MinValue;
            byte upperPrefix = parent.UpperEdge
                ? GetVarKeyScalar8Prefix(frontier.UpperKey, router.KeyDepth)
                : byte.MaxValue;
            int prefix = lowerPrefix;
            while (prefix <= upperPrefix)
            {
                long targetOffset = router.FindTarget((byte)prefix);
                if (targetOffset == 0)
                {
                    prefix++;
                    continue;
                }

                int runStart = prefix;
                int runEnd = prefix;
                while (runEnd < upperPrefix && router.FindTarget((byte)(runEnd + 1)) == targetOffset)
                    runEnd++;

                bool singlePrefixRun = runStart == runEnd;
                AddVarKeyScalar8PhysicalFrontierChild(
                    children,
                    childIndexes,
                    new VarKeyScalar8PhysicalTarget(
                        targetOffset,
                        childHops,
                        singlePrefixRun && parent.LowerEdge && runStart == lowerPrefix,
                        singlePrefixRun && parent.UpperEdge && runEnd == upperPrefix));
                prefix = runEnd + 1;
            }
        }
        else
        {
            int lowerRouteIndex = -1;
            int upperRouteIndex = -1;
            if (parent.LowerEdge)
                _ = router.FindTarget(frontier.LowerKey, router.KeyDepth, out lowerRouteIndex);
            if (parent.UpperEdge)
                _ = router.FindTarget(frontier.UpperKey, router.KeyDepth, out upperRouteIndex);

            for (int routeIndex = 0; routeIndex < router.RouteCount; routeIndex++)
            {
                if (!router.TrySelectMultiByteRangeRoute(
                        routeIndex,
                        frontier.LowerKey,
                        frontier.UpperKey,
                        maxKeyLength,
                        parent.LowerEdge,
                        parent.UpperEdge,
                        lowerRouteIndex,
                        upperRouteIndex,
                        out long targetOffset,
                        out bool childLowerEdge,
                        out bool childUpperEdge))
                {
                    continue;
                }

                AddVarKeyScalar8PhysicalFrontierChild(
                    children,
                    childIndexes,
                    new VarKeyScalar8PhysicalTarget(targetOffset, childHops, childLowerEdge, childUpperEdge));
            }
        }

        frontier.Targets.RemoveAt(frontierIndex);
        frontier.TargetOffsets.Remove(parent.TargetOffset);
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            VarKeyScalar8PhysicalTarget child = children[childIndex];
            if (frontier.ExpandedRouterOffsets.Contains(child.TargetOffset))
                throw new InvalidDataException("The VS8 physical frontier planner found a child route targeting an already-expanded router.");
            if (!frontier.TargetOffsets.Add(child.TargetOffset))
                throw new InvalidDataException("The VS8 physical frontier planner found one physical target shared by separate frontier parents.");
            frontier.Targets.Add(child);
        }
    }

    /// <summary>
    /// Adds or merges one physical child discovered through the same parent router.<br/>
    /// Repeated route aliases retain one target offset; edge ownership survives only when every alias context carries that same edge, ensuring a worker can safely traverse the complete shared physical subtree once.<br/>
    /// </summary>
    /// <param name="children">Children accumulated for the current router expansion.<br/></param>
    /// <param name="childIndexes">Target-offset lookup into <paramref name="children"/>.<br/></param>
    /// <param name="candidate">The newly selected child route context.<br/></param>
    private static void AddVarKeyScalar8PhysicalFrontierChild(
        List<VarKeyScalar8PhysicalTarget> children,
        Dictionary<long, int> childIndexes,
        VarKeyScalar8PhysicalTarget candidate)
    {
        if (!childIndexes.TryGetValue(candidate.TargetOffset, out int existingIndex))
        {
            childIndexes.Add(candidate.TargetOffset, children.Count);
            children.Add(candidate);
            return;
        }

        VarKeyScalar8PhysicalTarget existing = children[existingIndex];
        children[existingIndex] = existing with
        {
            LowerEdge = existing.LowerEdge && candidate.LowerEdge,
            UpperEdge = existing.UpperEdge && candidate.UpperEdge
        };
    }

    /// <summary>
    /// Distributes the bounded disjoint frontier across exactly the requested worker partitions.<br/>
    /// Round-robin assignment spreads neighboring physical subtrees without estimating every descendant count; FastFind is unordered, so no cross-worker key-order merge is required.<br/>
    /// Every frontier target appears exactly once and every worker receives at least one target because callers invoke this method only after the frontier reaches the exact worker count.<br/>
    /// </summary>
    /// <param name="frontier">The completed bounded frontier.<br/></param>
    /// <param name="workerCount">The exact number of worker-owned target groups.<br/></param>
    /// <returns>Exactly <paramref name="workerCount"/> disjoint physical partitions.<br/></returns>
    private static VarKeyScalar8PhysicalPartition[] BuildVarKeyScalar8PhysicalFrontierPartitions(
        VarKeyScalar8PhysicalFrontierState frontier,
        int workerCount)
    {
        List<VarKeyScalar8PhysicalTarget>[] groups = new List<VarKeyScalar8PhysicalTarget>[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            groups[workerIndex] = [];
        for (int targetIndex = 0; targetIndex < frontier.Targets.Count; targetIndex++)
            groups[targetIndex % workerCount].Add(frontier.Targets[targetIndex]);

        VarKeyScalar8PhysicalPartition[] partitions = new VarKeyScalar8PhysicalPartition[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            partitions[workerIndex] = new VarKeyScalar8PhysicalPartition(
                frontier.LowerKey,
                frontier.UpperKey,
                EstimatedTupleCount: 0,
                groups[workerIndex].ToArray());
        }

        return partitions;
    }
}

/// <summary>
/// Describes one disjoint inclusive encoded `VS8` key range assigned to one physical worker.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8PhysicalPartition(
    byte[] LowerKey,
    byte[] UpperKey,
    long EstimatedTupleCount,
    VarKeyScalar8PhysicalTarget[]? SeedTargets = null);

/// <summary>
/// Describes one range-reader continuation target owned by exactly one FastFind worker.<br/>
/// Edge flags preserve the ordinary range reader's lower/upper pruning context, while the remaining-hop budget prevents a partition handoff from weakening router-cycle protection.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8PhysicalTarget(
    long TargetOffset,
    int RemainingRouterHops,
    bool LowerEdge,
    bool UpperEdge);

/// <summary>
/// Owns exact topology-aligned `VS8` ranges and the thread-affine coherent read bridging planning to worker-reader acquisition.<br/>
/// </summary>
internal sealed class VarKeyScalar8PhysicalPartitionPlan : IDisposable
{
    private IDisposable? transitionRead;

    internal VarKeyScalar8PhysicalPartitionPlan(
        VarKeyScalar8PhysicalPartition[] partitions,
        IDisposable transitionRead,
        int routerPagesRead = 0,
        int frontierTargetCount = 0)
    {
        Partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        this.transitionRead = transitionRead ?? throw new ArgumentNullException(nameof(transitionRead));
        RouterPagesRead = Math.Max(0, routerPagesRead);
        FrontierTargetCount = Math.Max(0, frontierTargetCount);
    }

    internal IReadOnlyList<VarKeyScalar8PhysicalPartition> Partitions { get; }
    internal int RouterPagesRead { get; }
    internal int FrontierTargetCount { get; }

    internal IDisposable TakeTransitionRead()
    {
        return Interlocked.Exchange(ref transitionRead, null)
            ?? throw new InvalidOperationException("The VS8 physical partition transition read was already transferred.");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref transitionRead, null)?.Dispose();
    }
}

/// <summary>
/// Retains one logical topology extent used only while balancing exact physical reader ranges.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8PhysicalExtent(byte[] FirstKey, byte[] LastKey, long ItemCount);

/// <summary>
/// Owns bounded collections used by one lightweight topology-aligned `VS8` partition plan.<br/>
/// </summary>
internal sealed class VarKeyScalar8PhysicalPartitionState
{
    internal VarKeyScalar8PhysicalPartitionState(byte[] lowerKey, byte[] upperKey)
    {
        LowerKey = lowerKey;
        UpperKey = upperKey;
    }

    internal byte[] LowerKey { get; }
    internal byte[] UpperKey { get; }
    internal HashSet<long> ActiveRouters { get; } = [];
    internal HashSet<long> ExpandedRouters { get; } = [];
    internal Dictionary<long, long> TargetParents { get; } = [];
    internal HashSet<long> EmittedTargets { get; } = [];
    internal List<VarKeyScalar8PhysicalExtent> Extents { get; } = [];
    internal byte[]? LastTopologyKey { get; set; }
}

/// <summary>
/// Owns the bounded router frontier used to create exact FastFind partitions without walking descendant shelves or materializing identities.<br/>
/// Target offsets remain globally unique across the frontier so worker subtrees cannot overlap.<br/>
/// </summary>
internal sealed class VarKeyScalar8PhysicalFrontierState
{
    internal VarKeyScalar8PhysicalFrontierState(byte[] lowerKey, byte[] upperKey)
    {
        LowerKey = lowerKey;
        UpperKey = upperKey;
    }

    internal byte[] LowerKey { get; }
    internal byte[] UpperKey { get; }
    internal List<VarKeyScalar8PhysicalTarget> Targets { get; } = [];
    internal HashSet<long> TargetOffsets { get; } = [];
    internal Dictionary<long, VarKeyScalar8RouteTargetKind> TargetKinds { get; } = [];
    internal HashSet<long> ExpandedRouterOffsets { get; } = [];
    internal int RouterPagesRead { get; set; }
}

/// <summary>
/// Describes one nonempty physical shelf in an exhausted-key `VS8` terminal identity chain.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8TerminalIdentityShelfExtent(long ShelfOffset, int ItemCount);

/// <summary>
/// Describes one disjoint identity-slot slice beginning at a physical terminal shelf and continuing through its forward chain.<br/>
/// </summary>
internal readonly record struct VarKeyScalar8TerminalIdentityPartition(
    long StartShelfOffset,
    int StartSlotIndex,
    long IdentityCount,
    int ShelfExtentSize);

/// <summary>
/// Owns exact exhausted-key terminal identity partitions and the thread-affine coherent read bridging planning to worker-reader acquisition.<br/>
/// </summary>
internal sealed class VarKeyScalar8TerminalIdentityPartitionPlan : IDisposable
{
    private IDisposable? transitionRead;

    internal VarKeyScalar8TerminalIdentityPartitionPlan(
        VarKeyScalar8TerminalIdentityPartition[] partitions,
        IDisposable transitionRead)
    {
        Partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        this.transitionRead = transitionRead ?? throw new ArgumentNullException(nameof(transitionRead));
    }

    internal IReadOnlyList<VarKeyScalar8TerminalIdentityPartition> Partitions { get; }

    internal IDisposable TakeTransitionRead()
    {
        return Interlocked.Exchange(ref transitionRead, null)
            ?? throw new InvalidOperationException("The VS8 terminal identity transition read was already transferred.");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref transitionRead, null)?.Dispose();
    }
}
