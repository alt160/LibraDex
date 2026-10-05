using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Describes the shape produced by a cold variable-length optimizer replacement planner.<br/>
/// The values are telemetry for validation and tuning; the durable output is the replacement target offset returned beside this result.<br/>
/// </summary>
/// <param name="FanoutDepth">The first byte depth where the planner introduced compressed fanout.</param>
/// <param name="RouteCount">The total route count written across planned compressed routers.</param>
/// <param name="RouterCount">The total router count written by the planner.</param>
/// <param name="ShelfCount">The total shelf count written by the planner.</param>
/// <param name="MaxPlannerDepth">The deepest byte depth consumed by the planner.</param>
internal readonly record struct VarLenOptimizerReplacementBuildResult(
    int FanoutDepth,
    int RouteCount,
    int RouterCount,
    int ShelfCount,
    int MaxPlannerDepth);

/// <summary>
/// Describes a staged replacement subtree produced for one variable-length optimizer candidate.<br/>
/// The target offset must be published by the session's version-checked optimizer publish boundary in the same durability batch.<br/>
/// </summary>
/// <param name="RootPrefix">The root-prefix byte owned by the staged replacement subtree.</param>
/// <param name="TargetOffset">The file offset of the staged replacement router or shelf.</param>
/// <param name="Build">The planner shape telemetry for the staged subtree.</param>
internal readonly record struct VarLenOptimizerReplacementSubtree(
    byte RootPrefix,
    long TargetOffset,
    VarLenOptimizerReplacementBuildResult Build);

internal sealed partial class LibraDexFileSession
{
    private struct VarKeyScalar8OptimizerItem
    {
        public byte[] Key;
        public ulong Identity;
    }

    private struct VarKeyScalar16OptimizerItem
    {
        public byte[] Key;
        public ulong IdentityHigh;
        public ulong IdentityLow;
    }

    private struct VarLenOptimizerReplacementBuildState
    {
        public int FirstFanoutDepth;
        public int RouteCount;
        public int RouterCount;
        public int ShelfCount;
        public int MaxPlannerDepth;

        public readonly VarLenOptimizerReplacementBuildResult ToResult()
        {
            return new VarLenOptimizerReplacementBuildResult(FirstFanoutDepth, RouteCount, RouterCount, ShelfCount, MaxPlannerDepth);
        }
    }

    /// <summary>
    /// Stages a cold `VS8` compressed-router replacement subtree for a queued varlen optimizer candidate.<br/>
    /// The method owns only replacement construction: callers must already be inside the durability batch that will later publish or abort the replacement target.<br/>
    /// Identity values intentionally match the current harness proof contract, using deterministic ordinal `VS8` identities aligned with the supplied key slice.<br/>
    /// </summary>
    /// <param name="keys">The key slice to include in the replacement subtree.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS8` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and planner telemetry.</returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar8OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (keys.Length == 0)
        {
            throw new ArgumentException("The VS8 optimizer replacement subtree requires at least one key.", nameof(keys));
        }

        VarKeyScalar8OptimizerItem[] items = new VarKeyScalar8OptimizerItem[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            items[i] = new VarKeyScalar8OptimizerItem
            {
                Key = keys[i],
                Identity = CreateVarKeyScalar8OptimizerIdentity(i)
            };
        }

        Array.Sort(items, static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
        });

        byte rootPrefix = items[0].Key[0];
        for (int i = 1; i < items.Length; i++)
        {
            if (items[i].Key[0] != rootPrefix)
            {
                throw new InvalidDataException("The VS8 optimizer replacement subtree expects one root prefix.");
            }
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar8OptimizerReplacementNode(
            new VarKeyScalar8OptimizerItemArraySource(items),
            start: 0,
            end: items.Length,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Stages a cold `VS8` replacement subtree from authoritative persisted key/identity tuples.<br/>
    /// Unlike the synthetic harness overload, this production maintenance path preserves the exact encoded identities supplied by the source topology.<br/>
    /// </summary>
    /// <param name="keys">The encoded keys belonging to one root prefix.<br/></param>
    /// <param name="identities">The encoded identities aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length supported by the physical profile.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and topology telemetry.<br/></returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar8OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        ReadOnlySpan<ulong> identities,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (keys.Length == 0 || keys.Length != identities.Length)
            throw new ArgumentException("The VS8 optimizer replacement requires equal non-empty key and identity collections.");

        VarKeyScalar8OptimizerItem[] items = new VarKeyScalar8OptimizerItem[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            items[i] = new VarKeyScalar8OptimizerItem { Key = keys[i], Identity = identities[i] };
        Array.Sort(items, static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
        });

        byte rootPrefix = items[0].Key[0];
        for (int i = 1; i < items.Length; i++)
        {
            if (items[i].Key[0] != rootPrefix)
                throw new InvalidDataException("The VS8 optimizer replacement subtree expects one root prefix.");
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar8OptimizerReplacementNode(
            new VarKeyScalar8OptimizerItemArraySource(items),
            start: 0,
            end: items.Length,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Stages one cold `VS8` replacement subtree from a canonical key-then-identity tuple range that has already been globally sorted and validated by the native builder.<br/>
    /// The method preserves tuple alignment while avoiding a second comparison sort for each root-prefix group; it still copies the compact planner items because recursive construction requires an owned stable array.<br/>
    /// </summary>
    /// <param name="tuples">One non-empty canonical tuple range sharing a single encoded root prefix.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length supported by the physical profile.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and topology telemetry.<br/></returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
        ReadOnlySpan<VarKeyScalar8SortedTuple> tuples,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        VarKeyScalar8SortedTuple[] owned = tuples.ToArray();
        return CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
            new VarKeyScalar8SortedArraySource(owned),
            0,
            owned.Length,
            maxKeyLength,
            requestedRouteCount,
            descending);
    }

    /// <summary>
    /// Stages one cold `VS8` replacement subtree directly from a stable ordinal range in canonical key-then-identity order.<br/>
    /// Tuple access remains seekable so bounded spill readers can supply arbitrarily large root-prefix groups without copying the group into a second managed array.<br/>
    /// </summary>
    /// <param name="tuples">Stable seekable tuple source shared by the complete native build.<br/></param>
    /// <param name="start">Inclusive tuple ordinal for this root-prefix group.<br/></param>
    /// <param name="end">Exclusive tuple ordinal for this root-prefix group.<br/></param>
    /// <param name="maxKeyLength">Maximum encoded key length supported by the physical profile.<br/></param>
    /// <param name="requestedRouteCount">Preferred compressed-router fanout cap.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and topology telemetry.<br/></returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
        IVarKeyScalar8SortedTupleSource tuples,
        int start,
        int end,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (start < 0 || end <= start || end > tuples.Count)
            throw new ArgumentOutOfRangeException(nameof(start), start, "The sorted VS8 optimizer replacement range must be non-empty and contained by the source.");

        byte rootPrefix = tuples.GetKey(start)[0];
        for (int i = start + 1; i < end; i++)
        {
            if (tuples.GetKey(i)[0] != rootPrefix)
                throw new InvalidDataException("The sorted VS8 optimizer replacement subtree expects one root prefix.");
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar8OptimizerReplacementNode(
            tuples,
            start,
            end,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Stages one bounded recursive `VS8` replacement target from canonical tuples already sorted by encoded key and scalar identity.<br/>
    /// Unlike the root-prefix optimizer entry point, this method begins at an arbitrary routed key depth so a single skewed full shelf can become a compact recursive subtree without rebuilding unrelated index ranges.<br/>
    /// The caller owns topology validation and final parent-route publication; every node created here remains unreachable until that parent is redirected.<br/>
    /// </summary>
    /// <param name="tuples">The non-empty canonical tuple range owned by one currently routed shelf.<br/></param>
    /// <param name="keyDepth">The first encoded key byte not fully discriminated by the owning parent route.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length supported by the physical profile.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement target offset and bounded topology-build telemetry.<br/></returns>
    internal (long TargetOffset, VarLenOptimizerReplacementBuildResult Build) CreateVarKeyScalar8OptimizerReplacementTargetFromSorted(
        ReadOnlySpan<VarKeyScalar8SortedTuple> tuples,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (tuples.Length == 0)
        {
            throw new ArgumentException("The routed VS8 replacement target requires at least one tuple.", nameof(tuples));
        }
        if (keyDepth < 0 || keyDepth >= maxKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(keyDepth), keyDepth, "The routed VS8 replacement key depth must address one encoded key byte.");
        }
        if (requestedRouteCount <= 0 || requestedRouteCount > RouterLayout.MaxOneByteRouteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedRouteCount), requestedRouteCount, "The routed VS8 replacement route count must be from 1 through 256.");
        }

        VarKeyScalar8OptimizerItem[] items = new VarKeyScalar8OptimizerItem[tuples.Length];
        for (int i = 0; i < tuples.Length; i++)
        {
            byte[] key = tuples[i].Key ?? throw new ArgumentException($"Routed VS8 replacement tuple {i:N0} has a null encoded key.", nameof(tuples));
            if (key.Length == 0 || key.Length > maxKeyLength)
            {
                throw new ArgumentException($"Routed VS8 replacement tuple {i:N0} has encoded key length {key.Length:N0}; expected 1 through {maxKeyLength:N0}.", nameof(tuples));
            }
            if (i != 0)
            {
                int keyComparison = tuples[i - 1].Key.AsSpan().SequenceCompareTo(key);
                if (keyComparison > 0 || keyComparison == 0 && tuples[i - 1].Identity >= tuples[i].Identity)
                {
                    throw new InvalidDataException($"The routed VS8 replacement tuple range is not strictly ordered at ordinal {i:N0}.");
                }
            }

            items[i] = new VarKeyScalar8OptimizerItem
            {
                Key = key,
                Identity = tuples[i].Identity
            };
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar8OptimizerReplacementNode(
            new VarKeyScalar8OptimizerItemArraySource(items),
            start: 0,
            end: items.Length,
            keyDepth,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return (targetOffset, state.ToResult());
    }

    /// <summary>
    /// Rebuilds every encoded root-prefix partition whose reachable subgraph contains one shared full shelf after that shelf reveals a first divergence earlier than its immediate parent's nominal child depth.<br/>
    /// The cold fallback streams each affected exact-prefix range independently, materializes raw keys and identities without Fractal or CLR projection work, adds the incoming tuple to its owning partition, sorts and removes exact duplicate emissions caused by converged routes, and publishes fresh monotonic subtrees for the complete root-owner set in one durability batch.<br/>
    /// This avoids placing a lower-depth router beneath a higher-depth parent and deliberately trades rare root-prefix rebuild work for unambiguous traversal correctness.<br/>
    /// The caller must already have left any outer durability batch and serialized topology mutation for the owning physical index.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The stable `VS8` root router offset.<br/></param>
    /// <param name="obsoleteShelfOffset">The shared full shelf whose complete reachable root-owner set must be replaced.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length supported by the physical index.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="incomingKey">The encoded key whose insertion required the fallback.<br/></param>
    /// <param name="incomingIdentity">The encoded scalar identity paired with <paramref name="incomingKey"/>.<br/></param>
    /// <param name="telemetry">Receives the final durability publication telemetry.<br/></param>
    /// <param name="replacementTargetOffset">Receives the newly published root-prefix subtree target.<br/></param>
    /// <param name="tupleCount">Receives the exact distinct tuple population published for the affected root prefix.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns><see langword="true"/> when the observed root route remained current and the replacement published; otherwise <see langword="false"/> so the caller can retry from a fresh route walk.<br/></returns>
    private bool TryPublishVarKeyScalar8RootPrefixRebuild(
        long rootRouterOffset,
        long obsoleteShelfOffset,
        int maxKeyLength,
        int requestedRouteCount,
        ReadOnlySpan<byte> incomingKey,
        ulong incomingIdentity,
        bool descending,
        out DataKernelCommitTelemetry telemetry,
        out long replacementTargetOffset,
        out int tupleCount)
    {
        telemetry = default;
        replacementTargetOffset = 0;
        tupleCount = 0;
        byte incomingRootPrefix = incomingKey[0];
        Dictionary<byte, long> observedRootTargets = new();
        Dictionary<byte, List<VarKeyScalar8SortedTuple>> partitions = new();
        for (int prefix = byte.MinValue; prefix <= byte.MaxValue; prefix++)
        {
            byte rootPrefix = checked((byte)prefix);
            long rootTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            if (rootTarget == 0 || !VarKeyScalar8TargetReachesShelf(rootTarget, obsoleteShelfOffset))
            {
                continue;
            }

            observedRootTargets.Add(rootPrefix, rootTarget);
            byte[] lower = [rootPrefix];
            byte[] upper = GC.AllocateUninitializedArray<byte>(maxKeyLength);
            upper[0] = rootPrefix;
            upper.AsSpan(1).Fill(byte.MaxValue);
            List<VarKeyScalar8SortedTuple> prefixTuples = new();
            using (VarKeyScalar8RangeReader reader = OpenVarKeyScalar8RangeReader(
                rootRouterOffset,
                maxKeyLength,
                lower,
                upper,
                decodeLogicalKeys: false))
            {
                while (reader.MoveNext())
                {
                    if (reader.CurrentKey.Length != 0 && reader.CurrentKey[0] == rootPrefix)
                    {
                        prefixTuples.Add(new VarKeyScalar8SortedTuple(reader.CurrentKey.ToArray(), reader.CurrentEncodedIdentity));
                    }
                }
            }

            partitions.Add(rootPrefix, prefixTuples);
        }
        if (!partitions.TryGetValue(incomingRootPrefix, out List<VarKeyScalar8SortedTuple>? incomingPartition))
        {
            return false;
        }
        incomingPartition.Add(new VarKeyScalar8SortedTuple(incomingKey.ToArray(), incomingIdentity));

        foreach ((byte rootPrefix, List<VarKeyScalar8SortedTuple> prefixTuples) in partitions)
        {
            prefixTuples.Sort(static (left, right) =>
            {
                int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
                return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
            });
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < prefixTuples.Count; readIndex++)
            {
                if (writeIndex != 0 &&
                    prefixTuples[writeIndex - 1].Identity == prefixTuples[readIndex].Identity &&
                    prefixTuples[writeIndex - 1].Key.AsSpan().SequenceEqual(prefixTuples[readIndex].Key))
                {
                    continue;
                }

                prefixTuples[writeIndex++] = prefixTuples[readIndex];
            }
            if (writeIndex != prefixTuples.Count)
            {
                prefixTuples.RemoveRange(writeIndex, prefixTuples.Count - writeIndex);
            }
            if (prefixTuples.Count == 0)
            {
                throw new InvalidDataException($"The VS8 root-owner-set rebuild produced an empty partition for prefix 0x{rootPrefix:X2}.");
            }
        }

        using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
        Dictionary<byte, VarLenOptimizerReplacementSubtree> replacements = new(partitions.Count);
        foreach ((byte rootPrefix, List<VarKeyScalar8SortedTuple> prefixTuples) in partitions)
        {
            VarLenOptimizerReplacementSubtree replacement = CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
                prefixTuples.ToArray(),
                maxKeyLength,
                requestedRouteCount,
                descending);
            if (replacement.RootPrefix != rootPrefix)
            {
                throw new InvalidDataException($"The VS8 root-owner-set rebuild produced prefix 0x{replacement.RootPrefix:X2} instead of 0x{rootPrefix:X2}.");
            }

            replacements.Add(rootPrefix, replacement);
        }
        foreach ((byte rootPrefix, long observedRootTarget) in observedRootTargets)
        {
            VarLenOptimizerReplacementSubtree replacement = replacements[rootPrefix];
            if (!TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                observedRootTarget,
                replacement.TargetOffset,
                out _))
            {
                _ = batch.Abort();
                return false;
            }
        }

        (telemetry, _, _) = batch.Commit();
        replacementTargetOffset = replacements[incomingRootPrefix].TargetOffset;
        tupleCount = incomingPartition.Count;
        return true;
    }

    /// <summary>
    /// Determines whether one reachable `VS8` target subgraph contains a selected shelf offset.<br/>
    /// The exceptional owner-set planner uses this raw topology walk to find every root prefix that must be detached from a shared shelf; routers are visited once per root candidate and cycles are rejected by the visited set.<br/>
    /// </summary>
    /// <param name="candidateTargetOffset">The root-prefix target whose reachable subgraph is inspected.<br/></param>
    /// <param name="shelfOffset">The shared shelf offset being replaced.<br/></param>
    /// <returns><see langword="true"/> when <paramref name="shelfOffset"/> is reachable from the candidate target; otherwise <see langword="false"/>.<br/></returns>
    private bool VarKeyScalar8TargetReachesShelf(long candidateTargetOffset, long shelfOffset)
    {
        HashSet<long> visited = new();
        Stack<long> pending = new();
        pending.Push(candidateTargetOffset);
        while (pending.Count != 0)
        {
            long targetOffset = pending.Pop();
            if (targetOffset == shelfOffset)
            {
                return true;
            }
            if (!visited.Add(targetOffset) || ClassifyVarKeyScalar8RouteTarget(targetOffset) != VarKeyScalar8RouteTargetKind.Router)
            {
                continue;
            }

            byte[] routerBytes = new byte[RouterLayout.Size];
            kernel.Read(targetOffset, routerBytes);
            RouterReader router = new(routerBytes);
            if (!router.IsValid)
            {
                throw new InvalidDataException($"The VS8 root-owner-set scan found an invalid router at offset {targetOffset:N0}.");
            }
            int routeCount = router.HasDirectIndex ? RouterLayout.MaxOneByteRouteCount : router.RouteCount;
            for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                long childTargetOffset = router.GetRouteTargetAt(routeIndex);
                if (childTargetOffset != 0)
                {
                    pending.Push(childTargetOffset);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds every populated `VS8` root-prefix subtree from authoritative encoded tuples and publishes each replacement through a version-checked durability batch.<br/>
    /// The operation changes reachable route/shelf topology but preserves the root router, logical tuple order, and catalog metadata; obsolete extents remain unreachable until closed-file compaction.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The physical index root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length for the index.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="maxWorkItems">The maximum authoritative tuples to consume, or null for the complete tuple stream.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The exact tuple work count, published subtree count, and any reason the tuple stream was not completed.<br/></returns>
    internal LibraDexMaintenanceWalkResult OptimizeVarKeyScalar8Topology(
        long rootRouterOffset,
        int maxKeyLength,
        int requestedRouteCount,
        int? maxWorkItems,
        bool descending = false)
    {
        if (maxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWorkItems), maxWorkItems, "The optimizer work limit must be positive when supplied.");

        byte[] lower = [LibraDexVarLenKeyCodec.ValueMarker];
        byte[] upper = GC.AllocateUninitializedArray<byte>(maxKeyLength);
        upper[0] = LibraDexVarLenKeyCodec.ValueMarker;
        upper.AsSpan(1).Fill(byte.MaxValue);
        using VarKeyScalar8RangeReader reader = OpenVarKeyScalar8RangeReader(
            rootRouterOffset,
            maxKeyLength,
            lower,
            upper,
            decodeLogicalKeys: false,
            allowWriteUpgrade: true);
        List<byte[]> keys = new();
        List<ulong> identities = new();
        int considered = 0;
        int changed = 0;
        byte activePrefix = 0;
        bool hasPrefix = false;
        while (true)
        {
            if (maxWorkItems is int limit && considered >= limit)
            {
                return new LibraDexMaintenanceWalkResult(
                    considered,
                    changed,
                    LibraDexMaintenanceIncompleteReason.WorkLimit);
            }
            if (!reader.MoveNext())
                break;

            byte[] key = reader.CurrentKey.ToArray();
            byte prefix = key[0];
            if (hasPrefix && prefix != activePrefix)
            {
                changed += Publish(activePrefix, keys, identities) ? 1 : 0;
                keys.Clear();
                identities.Clear();
            }

            considered++;
            activePrefix = prefix;
            hasPrefix = true;
            keys.Add(key);
            identities.Add(reader.CurrentEncodedIdentity);
        }

        if (hasPrefix)
            changed += Publish(activePrefix, keys, identities) ? 1 : 0;

        return new LibraDexMaintenanceWalkResult(
            considered,
            changed,
            LibraDexMaintenanceIncompleteReason.None);

        bool Publish(byte rootPrefix, List<byte[]> prefixKeys, List<ulong> prefixIdentities)
        {
            long currentTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
            VarLenOptimizerReplacementSubtree replacement = CreateVarKeyScalar8OptimizerReplacementSubtree(
                prefixKeys.ToArray(),
                prefixIdentities.ToArray(),
                maxKeyLength,
                requestedRouteCount,
                descending);
            bool published = TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                currentTarget,
                replacement.TargetOffset,
                out _);
            if (published)
                _ = batch.Commit();
            return published;
        }
    }

    /// <summary>
    /// Stages a cold `VS16` compressed-router replacement subtree for a queued varlen optimizer candidate.<br/>
    /// The method owns only replacement construction: callers must already be inside the durability batch that will later publish or abort the replacement target.<br/>
    /// Identity values intentionally match the current harness proof contract, using deterministic ordinal `VS16` identities aligned with the supplied key slice.<br/>
    /// </summary>
    /// <param name="keys">The key slice to include in the replacement subtree.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS16` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and planner telemetry.</returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar16OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (keys.Length == 0)
        {
            throw new ArgumentException("The VS16 optimizer replacement subtree requires at least one key.", nameof(keys));
        }

        VarKeyScalar16OptimizerItem[] items = new VarKeyScalar16OptimizerItem[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            CreateVarKeyScalar16OptimizerIdentity(i, out ulong identityHigh, out ulong identityLow);
            items[i] = new VarKeyScalar16OptimizerItem
            {
                Key = keys[i],
                IdentityHigh = identityHigh,
                IdentityLow = identityLow
            };
        }

        Array.Sort(items, static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            if (keyComparison != 0)
            {
                return keyComparison;
            }

            int highComparison = left.IdentityHigh.CompareTo(right.IdentityHigh);
            return highComparison != 0 ? highComparison : left.IdentityLow.CompareTo(right.IdentityLow);
        });

        byte rootPrefix = items[0].Key[0];
        for (int i = 1; i < items.Length; i++)
        {
            if (items[i].Key[0] != rootPrefix)
            {
                throw new InvalidDataException("The VS16 optimizer replacement subtree expects one root prefix.");
            }
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar16OptimizerReplacementNode(
            items,
            start: 0,
            end: items.Length,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Stages a cold `VS16` replacement subtree from authoritative persisted key/identity tuples.<br/>
    /// The high and low identity halves remain aligned with their encoded keys and retain the physical byte-ordinal identity ordering used by `VS16` shelves.<br/>
    /// </summary>
    /// <param name="keys">The encoded keys belonging to one root prefix.<br/></param>
    /// <param name="identityHighs">The encoded high identity halves aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="identityLows">The encoded low identity halves aligned with <paramref name="keys"/>.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length supported by the physical profile.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The staged replacement subtree offset and topology telemetry.<br/></returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar16OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        ReadOnlySpan<ulong> identityHighs,
        ReadOnlySpan<ulong> identityLows,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (keys.Length == 0 ||
            keys.Length != identityHighs.Length ||
            keys.Length != identityLows.Length)
        {
            throw new ArgumentException("The VS16 optimizer replacement requires equal non-empty key and identity collections.");
        }

        VarKeyScalar16OptimizerItem[] items = new VarKeyScalar16OptimizerItem[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            items[i] = new VarKeyScalar16OptimizerItem
            {
                Key = keys[i],
                IdentityHigh = identityHighs[i],
                IdentityLow = identityLows[i]
            };
        }

        Array.Sort(items, static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            if (keyComparison != 0)
                return keyComparison;
            int highComparison = left.IdentityHigh.CompareTo(right.IdentityHigh);
            return highComparison != 0 ? highComparison : left.IdentityLow.CompareTo(right.IdentityLow);
        });

        byte rootPrefix = items[0].Key[0];
        for (int i = 1; i < items.Length; i++)
        {
            if (items[i].Key[0] != rootPrefix)
                throw new InvalidDataException("The VS16 optimizer replacement subtree expects one root prefix.");
        }

        VarLenOptimizerReplacementBuildState state = default;
        long targetOffset = CreateVarKeyScalar16OptimizerReplacementNode(
            items,
            start: 0,
            end: items.Length,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            descending,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Rebuilds every populated `VS16` root-prefix subtree from authoritative encoded tuples and publishes each replacement through a version-checked durability batch.<br/>
    /// Finite work limits are checked before reader advancement so the optimizer never consumes an uncounted tuple merely to discover that its budget ended.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The physical index root router offset.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length for the index.<br/></param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.<br/></param>
    /// <param name="maxWorkItems">The maximum authoritative tuples to consume, or null for the complete tuple stream.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The exact tuple work count, published subtree count, and any reason the tuple stream was not completed.<br/></returns>
    internal LibraDexMaintenanceWalkResult OptimizeVarKeyScalar16Topology(
        long rootRouterOffset,
        int maxKeyLength,
        int requestedRouteCount,
        int? maxWorkItems,
        bool descending = false)
    {
        if (maxWorkItems is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWorkItems), maxWorkItems, "The optimizer work limit must be positive when supplied.");

        using DataKernel.CoherentReadLease maintenanceRead = EnterCoherentUpgradeableRead();
        byte[] lower = [LibraDexVarLenKeyCodec.ValueMarker];
        byte[] upper = GC.AllocateUninitializedArray<byte>(maxKeyLength);
        upper[0] = LibraDexVarLenKeyCodec.ValueMarker;
        upper.AsSpan(1).Fill(byte.MaxValue);
        using VarKeyScalar16RangeReader reader = OpenVarKeyScalar16RangeReader(
            rootRouterOffset,
            maxKeyLength,
            lower,
            upper,
            decodeLogicalKeys: false,
            descending: descending);
        maintenanceRead.Pause();
        List<byte[]> keys = new();
        List<ulong> identityHighs = new();
        List<ulong> identityLows = new();
        int considered = 0;
        int changed = 0;
        byte activePrefix = 0;
        bool hasPrefix = false;
        while (true)
        {
            if (maxWorkItems is int limit && considered >= limit)
            {
                return new LibraDexMaintenanceWalkResult(
                    considered,
                    changed,
                    LibraDexMaintenanceIncompleteReason.WorkLimit);
            }
            bool hasNext;
            byte[] key = [];
            ulong identityHigh = 0;
            ulong identityLow = 0;
            using (maintenanceRead.Use())
            {
                hasNext = reader.MoveNext();
                if (hasNext)
                {
                    key = reader.CurrentKey.ToArray();
                    reader.ReadCurrentIdentity(out identityHigh, out identityLow);
                }
            }
            if (!hasNext)
                break;

            byte prefix = key[0];
            if (hasPrefix && prefix != activePrefix)
            {
                changed += Publish(activePrefix, keys, identityHighs, identityLows) ? 1 : 0;
                keys.Clear();
                identityHighs.Clear();
                identityLows.Clear();
            }

            considered++;
            activePrefix = prefix;
            hasPrefix = true;
            keys.Add(key);
            identityHighs.Add(identityHigh);
            identityLows.Add(identityLow);
        }

        if (hasPrefix)
            changed += Publish(activePrefix, keys, identityHighs, identityLows) ? 1 : 0;

        return new LibraDexMaintenanceWalkResult(
            considered,
            changed,
            LibraDexMaintenanceIncompleteReason.None);

        bool Publish(
            byte rootPrefix,
            List<byte[]> prefixKeys,
            List<ulong> prefixIdentityHighs,
            List<ulong> prefixIdentityLows)
        {
            long currentTarget = FindRouterTarget(rootRouterOffset, rootPrefix);
            using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
            VarLenOptimizerReplacementSubtree replacement = CreateVarKeyScalar16OptimizerReplacementSubtree(
                prefixKeys.ToArray(),
                prefixIdentityHighs.ToArray(),
                prefixIdentityLows.ToArray(),
                maxKeyLength,
                requestedRouteCount,
                descending);
            bool published = TryUpdateRouterRouteTargetIfCurrent(
                rootRouterOffset,
                rootPrefix,
                currentTarget,
                replacement.TargetOffset,
                out _);
            if (published)
                _ = batch.Commit();
            return published;
        }
    }

    /// <summary>
    /// Drains one queued `VS8` optimizer candidate by building and publishing a replacement subtree in one durability batch.<br/>
    /// The candidate is treated as evidence only: the publish path refreshes the current route target and uses the version-checked route update before committing staged replacement bytes.<br/>
    /// If the publish check rejects the replacement, the durability batch is disposed without commit so the staged replacement is discarded.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset that owns the candidate route.</param>
    /// <param name="candidate">The queued optimizer candidate to drain.</param>
    /// <param name="keys">The key slice used to build the replacement subtree.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS8` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The bounded maintenance result for this drain attempt.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar8OptimizerCandidate(
        long rootRouterOffset,
        VarLenRouteOptimizerCandidate candidate,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (candidate.LastObservedAt == 0)
        {
            throw new InvalidDataException("The VS8 optimizer drain requires an enqueued candidate.");
        }

        if (keys.Length <= 0 || candidate.LastObservedAt > keys.Length)
        {
            throw new InvalidDataException("The VS8 optimizer drain requires keys through the candidate observation.");
        }

        byte rootPrefix = keys[candidate.LastObservedAt - 1][0];
        using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
        VarLenOptimizerReplacementSubtree subtree = CreateVarKeyScalar8OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount, descending);
        if (subtree.RootPrefix != rootPrefix)
        {
            throw new InvalidDataException("The VS8 optimizer drain built a subtree for an unexpected root prefix.");
        }

        VarLenOptimizerMaintenanceResult result = TryPublishVarLenOptimizerReplacement(candidate, rootRouterOffset, rootPrefix, subtree.TargetOffset, keys.Length);
        if (result.PublishedCount == 1)
        {
            _ = batch.Commit();
        }

        return result;
    }

    /// <summary>
    /// Drains a bounded ordered slice of queued `VS8` optimizer candidates at an approved maintenance boundary.<br/>
    /// The current replacement planner publishes a whole root-prefix subtree, so later queued candidates beneath the same root prefix are counted as covered instead of rebuilding the same subtree again.<br/>
    /// Automatic callers should normally use a one-candidate policy; explicit on-demand callers can use a larger policy cap to sweep queued work without entering the write hot path.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset that owns the candidate routes.</param>
    /// <param name="state">The optimizer state containing ordered queued candidates.</param>
    /// <param name="keys">The key slice available to build replacement subtrees.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS8` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="policy">The maintenance policy that authorizes this bounded drain.</param>
    /// <param name="boundary">The scheduler boundary where maintenance is being attempted.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The aggregate maintenance result for the bounded drain.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar8OptimizerCandidates(
        long rootRouterOffset,
        VarLenRouteOptimizerState state,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        VarLenOptimizerMaintenancePolicy policy,
        VarLenOptimizerMaintenanceBoundary boundary,
        bool descending = false)
    {
        bool[] coveredRootPrefixes = new bool[byte.MaxValue + 1];
        return TryDrainVarKeyScalarOptimizerCandidates(
            rootRouterOffset,
            state,
            keys,
            maxKeyLength,
            requestedRouteCount,
            policy,
            boundary,
            coveredRootPrefixes,
            useScalar16Identity: false,
            descending);
    }

    /// <summary>
    /// Runs explicit on-demand optimizer maintenance for a runtime `VS8` variable-key/scalar-identity handle.<br/>
    /// The method resolves the session-owned optimizer state from the handle, applies the handle-owned on-demand policy, and drains a bounded ordered candidate slice through the current root-prefix replacement planner.<br/>
    /// This is an internal runtime bridge only: it avoids harness-only orchestration without promoting a developer-facing varlen optimization API before the public surface is designed.<br/>
    /// </summary>
    /// <param name="handle">The runtime `VS8` handle that owns root offset, key limit, fanout, and optimizer policy.</param>
    /// <param name="keys">The key slice available to build replacement subtrees for queued candidates.</param>
    /// <returns>The aggregate optimizer maintenance result, or the default result when no policy-approved candidate is available.</returns>
    internal VarLenOptimizerMaintenanceResult OptimizeVarKeyScalar8OnDemand(
        VarKeyScalar8IndexHandle handle,
        ReadOnlySpan<byte[]> keys)
    {
        handle.Validate();
        VarLenRouteOptimizerState state = GetOrCreateVarLenRouteOptimizerState(handle);
        if (!ShouldRunVarLenOptimizerMaintenance(handle.OptimizerPolicy, VarLenOptimizerMaintenanceBoundary.OnDemand, candidateQueued: true, state, alreadyPublishedAtBoundary: 0))
        {
            return default;
        }

        return TryDrainVarKeyScalar8OptimizerCandidates(
            handle.RootRouterOffset,
            state,
            keys,
            handle.MaxKeyLength,
            handle.OptimizerRouteFanout,
            handle.OptimizerPolicy,
            VarLenOptimizerMaintenanceBoundary.OnDemand,
            handle.Descending);
    }

    /// <summary>
    /// Runs automatic between-hot-actions optimizer maintenance for a runtime `VS8` variable-key/scalar-identity handle.<br/>
    /// The caller supplies the cheap queued-candidate signal already produced by the write observation and the current boundary drain count; this method owns the policy check and handle-based drain wiring.<br/>
    /// Automatic policy is expected to remain tightly bounded, usually one candidate per boundary, so this bridge can be called after committing the active hot batch without exposing root/policy/fanout plumbing to the caller.<br/>
    /// </summary>
    /// <param name="handle">The runtime `VS8` handle that owns root offset, key limit, fanout, and optimizer policy.</param>
    /// <param name="candidateQueued">Whether the just-recorded observation queued a new optimizer candidate.</param>
    /// <param name="alreadyPublishedAtBoundary">The number of candidates already published at this between-action boundary.</param>
    /// <param name="keys">The key slice available to build replacement subtrees through the current insert.</param>
    /// <returns>The aggregate optimizer maintenance result, or the default result when automatic policy does not authorize work.</returns>
    internal VarLenOptimizerMaintenanceResult OptimizeVarKeyScalar8BetweenHotActions(
        VarKeyScalar8IndexHandle handle,
        bool candidateQueued,
        int alreadyPublishedAtBoundary,
        ReadOnlySpan<byte[]> keys)
    {
        handle.Validate();
        VarLenRouteOptimizerState state = GetOrCreateVarLenRouteOptimizerState(handle);
        if (!ShouldRunVarLenOptimizerMaintenance(handle.OptimizerPolicy, VarLenOptimizerMaintenanceBoundary.BetweenHotActions, candidateQueued, state, alreadyPublishedAtBoundary))
        {
            return default;
        }

        return TryDrainVarKeyScalar8OptimizerCandidates(
            handle.RootRouterOffset,
            state,
            keys,
            handle.MaxKeyLength,
            handle.OptimizerRouteFanout,
            handle.OptimizerPolicy,
            VarLenOptimizerMaintenanceBoundary.BetweenHotActions,
            handle.Descending);
    }

    /// <summary>
    /// Drains one queued `VS16` optimizer candidate by building and publishing a replacement subtree in one durability batch.<br/>
    /// The candidate is treated as evidence only: the publish path refreshes the current route target and uses the version-checked route update before committing staged replacement bytes.<br/>
    /// If the publish check rejects the replacement, the durability batch is disposed without commit so the staged replacement is discarded.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset that owns the candidate route.</param>
    /// <param name="candidate">The queued optimizer candidate to drain.</param>
    /// <param name="keys">The key slice used to build the replacement subtree.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS16` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The bounded maintenance result for this drain attempt.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar16OptimizerCandidate(
        long rootRouterOffset,
        VarLenRouteOptimizerCandidate candidate,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending = false)
    {
        if (candidate.LastObservedAt == 0)
        {
            throw new InvalidDataException("The VS16 optimizer drain requires an enqueued candidate.");
        }

        if (keys.Length <= 0 || candidate.LastObservedAt > keys.Length)
        {
            throw new InvalidDataException("The VS16 optimizer drain requires keys through the candidate observation.");
        }

        byte rootPrefix = keys[candidate.LastObservedAt - 1][0];
        using LibraDexFileSessionDurabilityBatch batch = BeginDurabilityBatch();
        VarLenOptimizerReplacementSubtree subtree = CreateVarKeyScalar16OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount, descending);
        if (subtree.RootPrefix != rootPrefix)
        {
            throw new InvalidDataException("The VS16 optimizer drain built a subtree for an unexpected root prefix.");
        }

        VarLenOptimizerMaintenanceResult result = TryPublishVarLenOptimizerReplacement(candidate, rootRouterOffset, rootPrefix, subtree.TargetOffset, keys.Length);
        if (result.PublishedCount == 1)
        {
            _ = batch.Commit();
        }

        return result;
    }

    /// <summary>
    /// Drains a bounded ordered slice of queued `VS16` optimizer candidates at an approved maintenance boundary.<br/>
    /// The current replacement planner publishes a whole root-prefix subtree, so later queued candidates beneath the same root prefix are counted as covered instead of rebuilding the same subtree again.<br/>
    /// This mirrors the `VS8` aggregate drain while preserving the wider identity replacement builder.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">The root router offset that owns the candidate routes.</param>
    /// <param name="state">The optimizer state containing ordered queued candidates.</param>
    /// <param name="keys">The key slice available to build replacement subtrees.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS16` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <param name="policy">The maintenance policy that authorizes this bounded drain.</param>
    /// <param name="boundary">The scheduler boundary where maintenance is being attempted.</param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The aggregate maintenance result for the bounded drain.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar16OptimizerCandidates(
        long rootRouterOffset,
        VarLenRouteOptimizerState state,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        VarLenOptimizerMaintenancePolicy policy,
        VarLenOptimizerMaintenanceBoundary boundary,
        bool descending = false)
    {
        bool[] coveredRootPrefixes = new bool[byte.MaxValue + 1];
        return TryDrainVarKeyScalarOptimizerCandidates(
            rootRouterOffset,
            state,
            keys,
            maxKeyLength,
            requestedRouteCount,
            policy,
            boundary,
            coveredRootPrefixes,
            useScalar16Identity: true,
            descending: descending);
    }

    /// <summary>
    /// Runs explicit on-demand optimizer maintenance for a runtime `VS16` variable-key/scalar-identity handle.<br/>
    /// The method resolves the session-owned optimizer state from the handle, applies the handle-owned on-demand policy, and drains a bounded ordered candidate slice through the current root-prefix replacement planner.<br/>
    /// This mirrors the `VS8` runtime bridge while preserving the shape-specific replacement builder and 16-byte identity encoding.<br/>
    /// </summary>
    /// <param name="handle">The runtime `VS16` handle that owns root offset, key limit, fanout, and optimizer policy.</param>
    /// <param name="keys">The key slice available to build replacement subtrees for queued candidates.</param>
    /// <returns>The aggregate optimizer maintenance result, or the default result when no policy-approved candidate is available.</returns>
    internal VarLenOptimizerMaintenanceResult OptimizeVarKeyScalar16OnDemand(
        VarKeyScalar16IndexHandle handle,
        ReadOnlySpan<byte[]> keys)
    {
        handle.Validate();
        VarLenRouteOptimizerState state = GetOrCreateVarLenRouteOptimizerState(handle);
        if (!ShouldRunVarLenOptimizerMaintenance(handle.OptimizerPolicy, VarLenOptimizerMaintenanceBoundary.OnDemand, candidateQueued: true, state, alreadyPublishedAtBoundary: 0))
        {
            return default;
        }

        return TryDrainVarKeyScalar16OptimizerCandidates(
            handle.RootRouterOffset,
            state,
            keys,
            handle.MaxKeyLength,
            handle.OptimizerRouteFanout,
            handle.OptimizerPolicy,
            VarLenOptimizerMaintenanceBoundary.OnDemand,
            handle.Descending);
    }

    /// <summary>
    /// Runs automatic between-hot-actions optimizer maintenance for a runtime `VS16` variable-key/scalar-identity handle.<br/>
    /// The caller supplies the cheap queued-candidate signal already produced by the write observation and the current boundary drain count; this method owns the policy check and handle-based drain wiring.<br/>
    /// This mirrors the `VS8` automatic bridge while preserving the shape-specific replacement builder and 16-byte identity encoding.<br/>
    /// </summary>
    /// <param name="handle">The runtime `VS16` handle that owns root offset, key limit, fanout, and optimizer policy.</param>
    /// <param name="candidateQueued">Whether the just-recorded observation queued a new optimizer candidate.</param>
    /// <param name="alreadyPublishedAtBoundary">The number of candidates already published at this between-action boundary.</param>
    /// <param name="keys">The key slice available to build replacement subtrees through the current insert.</param>
    /// <returns>The aggregate optimizer maintenance result, or the default result when automatic policy does not authorize work.</returns>
    internal VarLenOptimizerMaintenanceResult OptimizeVarKeyScalar16BetweenHotActions(
        VarKeyScalar16IndexHandle handle,
        bool candidateQueued,
        int alreadyPublishedAtBoundary,
        ReadOnlySpan<byte[]> keys)
    {
        handle.Validate();
        VarLenRouteOptimizerState state = GetOrCreateVarLenRouteOptimizerState(handle);
        if (!ShouldRunVarLenOptimizerMaintenance(handle.OptimizerPolicy, VarLenOptimizerMaintenanceBoundary.BetweenHotActions, candidateQueued, state, alreadyPublishedAtBoundary))
        {
            return default;
        }

        return TryDrainVarKeyScalar16OptimizerCandidates(
            handle.RootRouterOffset,
            state,
            keys,
            handle.MaxKeyLength,
            handle.OptimizerRouteFanout,
            handle.OptimizerPolicy,
            VarLenOptimizerMaintenanceBoundary.BetweenHotActions,
            handle.Descending);
    }

    /// <summary>
    /// Drains ordered varlen optimizer candidates through the supplied shape-specific single-candidate drain.<br/>
    /// The helper is deliberately cold-path code: it may allocate a tiny root-prefix coverage bitmap and use a delegate so the shape-specific publish rules stay centralized.<br/>
    /// It uses candidate order rather than publish count to advance through the queue, because same-root candidates can be covered without a second publish.<br/>
    /// </summary>
    private VarLenOptimizerMaintenanceResult TryDrainVarKeyScalarOptimizerCandidates(
        long rootRouterOffset,
        VarLenRouteOptimizerState state,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        VarLenOptimizerMaintenancePolicy policy,
        VarLenOptimizerMaintenanceBoundary boundary,
        bool[] coveredRootPrefixes,
        bool useScalar16Identity,
        bool descending = false)
    {
        int consideredCount = 0;
        int attemptedCount = 0;
        int publishedCount = 0;
        int coveredCandidateCount = 0;
        int staleCandidateTargetCount = 0;
        int rejectedCount = 0;
        int builtKeyCount = 0;
        long candidateTargetOffset = 0;
        long refreshedTargetOffset = 0;
        long replacementTargetOffset = 0;
        TimeSpan elapsed = default;

        for (int candidateIndex = 0;
             candidateIndex < state.CandidateCount &&
             policy.ShouldDrain(boundary, candidateQueued: true, alreadyPublishedAtBoundary: consideredCount);
             candidateIndex++)
        {
            if (!state.TryGetCandidate(candidateIndex, out VarLenRouteOptimizerCandidate candidate) ||
                candidate.LastObservedAt == 0)
            {
                break;
            }

            if (keys.Length <= 0 || candidate.LastObservedAt > keys.Length)
            {
                throw new InvalidDataException("The varlen optimizer aggregate drain requires keys through each candidate observation.");
            }

            consideredCount++;
            byte rootPrefix = keys[candidate.LastObservedAt - 1][0];
            if (coveredRootPrefixes[rootPrefix])
            {
                coveredCandidateCount++;
                continue;
            }

            VarLenOptimizerMaintenanceResult result = useScalar16Identity
                ? TryDrainVarKeyScalar16OptimizerCandidate(rootRouterOffset, candidate, keys, maxKeyLength, requestedRouteCount, descending)
                : TryDrainVarKeyScalar8OptimizerCandidate(rootRouterOffset, candidate, keys, maxKeyLength, requestedRouteCount, descending);
            attemptedCount += result.AttemptedCount;
            publishedCount += result.PublishedCount;
            coveredCandidateCount += result.CoveredCandidateCount;
            staleCandidateTargetCount += result.StaleCandidateTargetCount;
            rejectedCount += result.RejectedCount;
            builtKeyCount += result.BuiltKeyCount;
            elapsed += result.Elapsed;
            candidateTargetOffset = result.CandidateTargetOffset;
            refreshedTargetOffset = result.RefreshedTargetOffset;
            replacementTargetOffset = result.ReplacementTargetOffset;
            if (result.PublishedCount > 0)
            {
                coveredRootPrefixes[rootPrefix] = true;
            }
        }

        return new VarLenOptimizerMaintenanceResult(
            ConsideredCount: consideredCount,
            AttemptedCount: attemptedCount,
            PublishedCount: publishedCount,
            CoveredCandidateCount: coveredCandidateCount,
            StaleCandidateTargetCount: staleCandidateTargetCount,
            RejectedCount: rejectedCount,
            BuiltKeyCount: builtKeyCount,
            CandidateTargetOffset: candidateTargetOffset,
            RefreshedTargetOffset: refreshedTargetOffset,
            ReplacementTargetOffset: replacementTargetOffset,
            Elapsed: elapsed);
    }

    /// <summary>
    /// Creates one recursive `VS8` replacement node, using a shelf when the sorted range fits and a compressed router otherwise.<br/>
    /// The node builder is a cold optimizer path and may allocate temporary route arrays while it searches for a compact replacement structure.<br/>
    /// </summary>
    private long CreateVarKeyScalar8OptimizerReplacementNode(
        IVarKeyScalar8SortedTupleSource items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending,
        ref VarLenOptimizerReplacementBuildState state)
    {
        if (TryBuildVarKeyScalar8OptimizerShelf(items, start, end, maxKeyLength, out VarKeyScalar8Profile profile, out byte[] shelfBytes, descending))
        {
            (long shelfOffset, _) = CreateVarKeyScalar8Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
        }

        if (AreVarKeyScalar8OptimizerKeysEqual(items, start, end))
        {
            return CreateVarKeyScalar8OptimizerTerminalNode(
                items,
                start,
                end,
                keyDepth,
                maxKeyLength,
                descending,
                ref state);
        }

        int fanoutDepth = FindVarKeyScalar8OptimizerFirstDivergenceDepth(items, start, end, keyDepth, maxKeyLength);
        if (state.FirstFanoutDepth == 0)
        {
            state.FirstFanoutDepth = fanoutDepth;
        }

        byte[] stem = CreateVarKeyScalar8OptimizerStem(items.GetKey(start), keyDepth, fanoutDepth);
        byte[] distinctFanoutBytes = CreateVarKeyScalar8OptimizerDistinctFanoutBytes(items, start, end, fanoutDepth);
        int maxFittingRouteCount = CalculateVarLenOptimizerMaxRouteCount(checked((byte)(fanoutDepth - keyDepth + 1)));
        int estimatedLeafCapacity = EstimateVarKeyScalar8OptimizerLeafCapacity(items, start, end, maxKeyLength);
        int minimumUsefulRouteCount = Math.Max(2, (end - start + estimatedLeafCapacity - 1) / estimatedLeafCapacity);
        int routeCount = Math.Min(Math.Min(Math.Min(Math.Max(1, requestedRouteCount), minimumUsefulRouteCount), distinctFanoutBytes.Length), maxFittingRouteCount);
        if (routeCount < distinctFanoutBytes.Length &&
            !DoVarKeyScalar8OptimizerRouteGroupsFitLeaves(items, start, end, fanoutDepth, distinctFanoutBytes, routeCount, maxKeyLength))
        {
            routeCount = Math.Min(distinctFanoutBytes.Length, maxFittingRouteCount);
        }

        if (routeCount <= 0)
        {
            throw new InvalidDataException("The VS8 optimizer replacement planner could not fit a compressed multi-byte route.");
        }

        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[routeCount];
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte startByte = distinctFanoutBytes[firstFanoutIndex];
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarLenOptimizerKeyByteOrZero(items.GetKey(itemEnd), fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart)
            {
                throw new InvalidDataException("The VS8 optimizer replacement planner produced an empty route.");
            }

            long targetOffset = CreateVarKeyScalar8OptimizerReplacementNode(
                items,
                itemStart,
                itemEnd,
                checked(fanoutDepth + 1),
                maxKeyLength,
                requestedRouteCount,
                descending,
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The VS8 optimizer replacement planner did not consume every item in the current node.");
        }

        byte prefixByteCount = checked((byte)(fanoutDepth - keyDepth + 1));
        long routerOffset;
        if (prefixByteCount == 1)
        {
            long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
            foreach (RouterMultiByteRouteSnapshot route in routes)
            {
                for (int prefix = route.PrefixStart; prefix <= route.PrefixEnd; prefix++)
                    targets[prefix] = route.TargetOffset;
            }
            RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(reservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)keyDepth), allocationClassId: 0, targets);
            routerOffset = reservation.Extent.Offset;
        }
        else
        {
            (RouterSnapshot router, _) = CreateCompressedMultiByteRouter(
                prefixByteCount,
                checked((ushort)keyDepth),
                maxRouteCount: checked((ushort)routes.Length),
                allocationClassId: 0,
                routes);
            routerOffset = router.Offset;
        }
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return routerOffset;
    }

    /// <summary>
    /// Determines whether one sorted optimizer subrange contains exactly one encoded `VS8` key.<br/>
    /// Endpoint equality is sufficient because the input is already sorted by key then scalar-8 identity; matching endpoints therefore bound only the same key.<br/>
    /// </summary>
    /// <param name="items">The sorted optimizer items.<br/></param>
    /// <param name="start">The inclusive subrange start ordinal.<br/></param>
    /// <param name="end">The exclusive subrange end ordinal.<br/></param>
    /// <returns><see langword="true"/> when every item in the subrange has the same encoded key.<br/></returns>
    private static bool AreVarKeyScalar8OptimizerKeysEqual(
        IVarKeyScalar8SortedTupleSource items,
        int start,
        int end)
    {
        return end > start && items.CompareKeys(start, end - 1) == 0;
    }

    /// <summary>
    /// Builds one exact-key terminal scalar-8 identity node for an oversized all-equal-key optimizer subrange.<br/>
    /// The exhausted key is persisted once in the terminal root while sorted identities are packed directly into a backward-built shelf chain whose first and tail offsets are final before publication.<br/>
    /// This prevents duplicate-heavy inputs from entering a nonexistent divergence search and avoids repeated key bytes in terminal identity payloads.<br/>
    /// </summary>
    /// <param name="items">The sorted optimizer items.<br/></param>
    /// <param name="start">The inclusive same-key subrange start ordinal.<br/></param>
    /// <param name="end">The exclusive same-key subrange end ordinal.<br/></param>
    /// <param name="keyDepth">The first encoded key depth not already owned by the parent route.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="state">The replacement build telemetry state to update.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The top exact-key router or terminal-root offset for the replacement node.<br/></returns>
    private long CreateVarKeyScalar8OptimizerTerminalNode(
        IVarKeyScalar8SortedTupleSource items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        bool descending,
        ref VarLenOptimizerReplacementBuildState state)
    {
        const int TerminalShelfExtentSize = 4 * 1024;
        int capacity = TerminalIdentity8ShelfLayout.GetCapacity(TerminalShelfExtentSize);
        if (capacity <= 0)
        {
            throw new InvalidDataException("The VS8 optimizer terminal shelf extent cannot hold an identity payload.");
        }

        ulong[] identities = GC.AllocateUninitializedArray<ulong>(end - start);
        for (int i = start; i < end; i++)
        {
            identities[descending ? end - 1 - i : i - start] = items.GetIdentity(i);
        }

        int remaining = identities.Length;
        int terminalShelfCount = 0;
        long firstShelfOffset = 0;
        long tailShelfOffset = 0;
        long nextShelfOffset = 0;
        while (remaining > 0)
        {
            int take = Math.Min(capacity, remaining);
            int identityIndex = remaining - take;
            RawDataReservation shelfReservation = kernel.Reserve(TerminalShelfExtentSize);
            TerminalIdentity8ShelfLayout.Initialize(
                shelfReservation.Span,
                identities,
                identityIndex,
                take,
                nextShelfOffset);
            if (tailShelfOffset == 0)
            {
                tailShelfOffset = shelfReservation.Extent.Offset;
            }

            firstShelfOffset = shelfReservation.Extent.Offset;
            nextShelfOffset = shelfReservation.Extent.Offset;
            remaining = identityIndex;
            terminalShelfCount++;
        }

        ReadOnlySpan<byte> key = items.GetKey(start);
        RawDataReservation rootReservation = kernel.Reserve(TerminalIdentityRootLayout.Size);
        TerminalIdentityRootLayout.Initialize(
            rootReservation.Span,
            TerminalIdentityRootLayout.ShapeVarKey,
            key,
            TerminalShelfExtentSize,
            firstShelfOffset);
        TerminalIdentityRootLayout.WriteTailShelfOffset(rootReservation.Span, tailShelfOffset);
        rootReservation.Span[TerminalIdentityRootLayout.SortDirectionOffset] = descending ? (byte)1 : (byte)0;
        long targetOffset = CreateVarKeyVarIdentityTerminalRouterChain(
            checked((ushort)Math.Min(keyDepth, maxKeyLength)),
            allocationClassId: 0,
            key,
            maxKeyLength,
            emptyShelfOffset: 0,
            rootReservation.Extent.Offset);
        int terminalDepth = Math.Min(key.Length, maxKeyLength - 1);
        int routerCount = Math.Max(0, terminalDepth - keyDepth + 1);
        state.RouterCount = checked(state.RouterCount + routerCount);
        state.RouteCount = checked(state.RouteCount + routerCount);
        state.ShelfCount = checked(state.ShelfCount + terminalShelfCount);
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, terminalDepth + 1);
        return targetOffset;
    }

    /// <summary>
    /// Creates one recursive `VS16` replacement node, using a shelf when the sorted range fits and a compressed router otherwise.<br/>
    /// The node builder mirrors the `VS8` planner while preserving the wider two-lane identity ordering.<br/>
    /// </summary>
    private long CreateVarKeyScalar16OptimizerReplacementNode(
        VarKeyScalar16OptimizerItem[] items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        bool descending,
        ref VarLenOptimizerReplacementBuildState state)
    {
        if (TryBuildVarKeyScalar16OptimizerShelf(items, start, end, maxKeyLength, out VarKeyScalar16Profile profile, out byte[] shelfBytes, descending))
        {
            (long shelfOffset, _) = CreateVarKeyScalar16Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
        }

        if (AreVarKeyScalar16OptimizerKeysEqual(items, start, end))
        {
            return CreateVarKeyScalar16OptimizerTerminalNode(
                items,
                start,
                end,
                keyDepth,
                maxKeyLength,
                descending,
                ref state);
        }

        int fanoutDepth = FindVarKeyScalar16OptimizerFirstDivergenceDepth(items, start, end, keyDepth, maxKeyLength);
        if (state.FirstFanoutDepth == 0)
        {
            state.FirstFanoutDepth = fanoutDepth;
        }

        byte[] stem = CreateVarKeyScalar16OptimizerStem(items[start].Key, keyDepth, fanoutDepth);
        byte[] distinctFanoutBytes = CreateVarKeyScalar16OptimizerDistinctFanoutBytes(items, start, end, fanoutDepth);
        int maxFittingRouteCount = CalculateVarLenOptimizerMaxRouteCount(checked((byte)(fanoutDepth - keyDepth + 1)));
        int estimatedLeafCapacity = EstimateVarKeyScalar16OptimizerLeafCapacity(items, start, end, maxKeyLength);
        int minimumUsefulRouteCount = Math.Max(2, (end - start + estimatedLeafCapacity - 1) / estimatedLeafCapacity);
        int routeCount = Math.Min(Math.Min(Math.Min(Math.Max(1, requestedRouteCount), minimumUsefulRouteCount), distinctFanoutBytes.Length), maxFittingRouteCount);
        if (routeCount < distinctFanoutBytes.Length &&
            !DoVarKeyScalar16OptimizerRouteGroupsFitLeaves(items, start, end, fanoutDepth, distinctFanoutBytes, routeCount, maxKeyLength))
        {
            routeCount = Math.Min(distinctFanoutBytes.Length, maxFittingRouteCount);
        }

        if (routeCount <= 0)
        {
            throw new InvalidDataException("The VS16 optimizer replacement planner could not fit a compressed multi-byte route.");
        }

        RouterMultiByteRouteSnapshot[] routes = new RouterMultiByteRouteSnapshot[routeCount];
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int firstFanoutIndex = routeIndex * distinctFanoutBytes.Length / routeCount;
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte startByte = distinctFanoutBytes[firstFanoutIndex];
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarLenOptimizerKeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart)
            {
                throw new InvalidDataException("The VS16 optimizer replacement planner produced an empty route.");
            }

            long targetOffset = CreateVarKeyScalar16OptimizerReplacementNode(
                items,
                itemStart,
                itemEnd,
                checked(fanoutDepth + 1),
                maxKeyLength,
                requestedRouteCount,
                descending,
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The VS16 optimizer replacement planner did not consume every item in the current node.");
        }

        byte prefixByteCount = checked((byte)(fanoutDepth - keyDepth + 1));
        long routerOffset;
        if (prefixByteCount == 1)
        {
            long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
            foreach (RouterMultiByteRouteSnapshot route in routes)
            {
                for (int prefix = route.PrefixStart; prefix <= route.PrefixEnd; prefix++)
                    targets[prefix] = route.TargetOffset;
            }
            RawDataReservation reservation = kernel.Reserve(RouterLayout.Size);
            RouterWriter writer = new(reservation.Span);
            writer.InitializeExpandedOneByte(checked((ushort)keyDepth), allocationClassId: 0, targets);
            routerOffset = reservation.Extent.Offset;
        }
        else
        {
            (RouterSnapshot router, _) = CreateCompressedMultiByteRouter(
                prefixByteCount,
                checked((ushort)keyDepth),
                maxRouteCount: checked((ushort)routes.Length),
                allocationClassId: 0,
                routes);
            routerOffset = router.Offset;
        }
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return routerOffset;
    }

    /// <summary>
    /// Determines whether one sorted optimizer subrange contains exactly one encoded `VS16` key.<br/>
    /// Endpoint equality is sufficient because the input is already sorted by key then scalar-16 identity; matching endpoints therefore bound only the same key.<br/>
    /// </summary>
    /// <param name="items">The sorted optimizer items.<br/></param>
    /// <param name="start">The inclusive subrange start ordinal.<br/></param>
    /// <param name="end">The exclusive subrange end ordinal.<br/></param>
    /// <returns><see langword="true"/> when every item in the subrange has the same encoded key.<br/></returns>
    private static bool AreVarKeyScalar16OptimizerKeysEqual(
        VarKeyScalar16OptimizerItem[] items,
        int start,
        int end)
    {
        return end > start && items[start].Key.AsSpan().SequenceEqual(items[end - 1].Key);
    }

    /// <summary>
    /// Builds one exact-key terminal scalar-16 identity node for an oversized all-equal-key optimizer subrange.<br/>
    /// Identities are written once into the pooled canonical 16-byte workspace, then the shared terminal shelf builder and exact-key router chain publish the same physical representation used by mutation-time conversion.<br/>
    /// This keeps optimizer replacement proportional under duplicate pressure and prevents a nonexistent key-divergence search from rejecting a valid tuple set.<br/>
    /// </summary>
    /// <param name="items">The sorted optimizer items.<br/></param>
    /// <param name="start">The inclusive same-key subrange start ordinal.<br/></param>
    /// <param name="end">The exclusive same-key subrange end ordinal.<br/></param>
    /// <param name="keyDepth">The first encoded key depth not already owned by the parent route.<br/></param>
    /// <param name="maxKeyLength">The maximum encoded key length accepted by the index.<br/></param>
    /// <param name="state">The replacement build telemetry state to update.<br/></param>
    /// <param name="descending">Whether the physical key traversal and replacement ordering are descending.</param>
    /// <returns>The top exact-key router or terminal-root offset for the replacement node.<br/></returns>
    private long CreateVarKeyScalar16OptimizerTerminalNode(
        VarKeyScalar16OptimizerItem[] items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        bool descending,
        ref VarLenOptimizerReplacementBuildState state)
    {
        int count = end - start;
        using PooledTerminalVarIdentitySet identities = PooledTerminalVarIdentitySet.Rent(
            count,
            checked(count * VarKeyScalar16TerminalIdentitySize));
        Span<byte> identity = stackalloc byte[VarKeyScalar16TerminalIdentitySize];
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int i = descending ? end - 1 - ordinal : start + ordinal;
            WriteVarKeyScalar16TerminalIdentity(identity, items[i].IdentityHigh, items[i].IdentityLow);
            identities.Add(identity);
        }

        int terminalShelfExtentSize = SelectScalar8VarIdentityTerminalShelfExtentSize(
            VarKeyScalar16Profile.Default128KiB.ShelfExtentSize,
            identities);
        ReadOnlySpan<byte> key = items[start].Key;
        long terminalRootOffset = CreateScalar8VarIdentityTerminalRoute(
            TerminalIdentityRootLayout.ShapeVarKeyScalar16Identity,
            key,
            terminalShelfExtentSize,
            identities,
            descending);
        long targetOffset = CreateVarKeyVarIdentityTerminalRouterChain(
            checked((ushort)Math.Min(keyDepth, maxKeyLength)),
            allocationClassId: 0,
            key,
            maxKeyLength,
            emptyShelfOffset: 0,
            terminalRootOffset);
        int terminalDepth = Math.Min(key.Length, maxKeyLength - 1);
        int routerCount = Math.Max(0, terminalDepth - keyDepth + 1);
        state.RouterCount = checked(state.RouterCount + routerCount);
        state.RouteCount = checked(state.RouteCount + routerCount);
        state.ShelfCount++;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, terminalDepth + 1);
        return targetOffset;
    }

    /// <summary>
    /// Finds the first divergent key byte depth for a sorted `VS8` optimizer item range.<br/>
    /// This is the byte depth that becomes the final consumed byte for the next compressed router.<br/>
    /// </summary>
    private static int FindVarKeyScalar8OptimizerFirstDivergenceDepth(IVarKeyScalar8SortedTupleSource items, int start, int end, int startDepth, int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarLenOptimizerKeyByteOrZero(items.GetKey(start), depth);
            for (int i = start + 1; i < end; i++)
            {
                if (GetVarLenOptimizerKeyByteOrZero(items.GetKey(i), depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The VS8 optimizer replacement keys do not diverge within the maximum key length.");
    }

    /// <summary>
    /// Finds the first divergent key byte depth for a sorted `VS16` optimizer item range.<br/>
    /// This is the byte depth that becomes the final consumed byte for the next compressed router.<br/>
    /// </summary>
    private static int FindVarKeyScalar16OptimizerFirstDivergenceDepth(VarKeyScalar16OptimizerItem[] items, int start, int end, int startDepth, int maxKeyLength)
    {
        for (int depth = startDepth; depth < maxKeyLength; depth++)
        {
            byte first = GetVarLenOptimizerKeyByteOrZero(items[start].Key, depth);
            for (int i = start + 1; i < end; i++)
            {
                if (GetVarLenOptimizerKeyByteOrZero(items[i].Key, depth) != first)
                {
                    return depth;
                }
            }
        }

        throw new InvalidDataException("The VS16 optimizer replacement keys do not diverge within the maximum key length.");
    }

    /// <summary>
    /// Creates the compressed-router stem bytes between the already-consumed depth and the fanout byte depth.<br/>
    /// The stem is persisted in every route snapshot for the current compressed router.<br/>
    /// </summary>
    private static byte[] CreateVarKeyScalar8OptimizerStem(ReadOnlySpan<byte> representativeKey, int keyDepth, int fanoutDepth)
    {
        byte[] stem = new byte[fanoutDepth - keyDepth];
        for (int i = 0; i < stem.Length; i++)
        {
            stem[i] = GetVarLenOptimizerKeyByteOrZero(representativeKey, keyDepth + i);
        }

        return stem;
    }

    /// <summary>
    /// Creates the compressed-router stem bytes between the already-consumed depth and the fanout byte depth.<br/>
    /// This `VS16` wrapper mirrors the `VS8` method for clone-shaped planner readability.<br/>
    /// </summary>
    private static byte[] CreateVarKeyScalar16OptimizerStem(byte[] representativeKey, int keyDepth, int fanoutDepth)
    {
        return CreateVarKeyScalar8OptimizerStem(representativeKey, keyDepth, fanoutDepth);
    }

    /// <summary>
    /// Creates distinct sorted fanout bytes for a `VS8` optimizer item subrange.<br/>
    /// The result is used to divide one compressed router into non-overlapping final-byte ranges.<br/>
    /// </summary>
    private static byte[] CreateVarKeyScalar8OptimizerDistinctFanoutBytes(IVarKeyScalar8SortedTupleSource items, int start, int end, int fanoutDepth)
    {
        byte[] distinct = new byte[end - start];
        int count = 0;
        byte previous = 0;
        bool hasPrevious = false;
        for (int i = start; i < end; i++)
        {
            byte current = GetVarLenOptimizerKeyByteOrZero(items.GetKey(i), fanoutDepth);
            if (!hasPrevious || current != previous)
            {
                distinct[count++] = current;
                previous = current;
                hasPrevious = true;
            }
        }

        Array.Resize(ref distinct, count);
        return distinct;
    }

    /// <summary>
    /// Creates distinct sorted fanout bytes for a `VS16` optimizer item subrange.<br/>
    /// The result is used to divide one compressed router into non-overlapping final-byte ranges.<br/>
    /// </summary>
    private static byte[] CreateVarKeyScalar16OptimizerDistinctFanoutBytes(VarKeyScalar16OptimizerItem[] items, int start, int end, int fanoutDepth)
    {
        byte[] distinct = new byte[end - start];
        int count = 0;
        byte previous = 0;
        bool hasPrevious = false;
        for (int i = start; i < end; i++)
        {
            byte current = GetVarLenOptimizerKeyByteOrZero(items[i].Key, fanoutDepth);
            if (!hasPrevious || current != previous)
            {
                distinct[count++] = current;
                previous = current;
                hasPrevious = true;
            }
        }

        Array.Resize(ref distinct, count);
        return distinct;
    }

    /// <summary>
    /// Calculates how many compressed routes can fit in one router page for the supplied consumed byte count.<br/>
    /// This protects the optimizer from constructing a logical route table that cannot fit in the persisted router layout.<br/>
    /// </summary>
    private static int CalculateVarLenOptimizerMaxRouteCount(byte prefixByteCount)
    {
        int routeCount = 256;
        while (routeCount > 0 &&
               RouterLayout.RoutesOffset + RouterLayout.GetMultiByteRouteStorageLength(routeCount, prefixByteCount) > RouterLayout.Size)
        {
            routeCount--;
        }

        return routeCount;
    }

    /// <summary>
    /// Estimates the maximum `VS8` leaf item count by binary-searching with the real shelf builder.<br/>
    /// Measuring with the actual shelf format avoids guessing around varlen record overhead and slot reservation rules.<br/>
    /// </summary>
    private static int EstimateVarKeyScalar8OptimizerLeafCapacity(IVarKeyScalar8SortedTupleSource items, int start, int end, int maxKeyLength)
    {
        int low = 1;
        int high = end - start;
        int best = 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (TryBuildVarKeyScalar8OptimizerShelf(items, start, start + mid, maxKeyLength, out _, out _))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// Estimates the maximum `VS16` leaf item count by binary-searching with the real shelf builder.<br/>
    /// Measuring with the actual shelf format avoids guessing around varlen record overhead and slot reservation rules.<br/>
    /// </summary>
    private static int EstimateVarKeyScalar16OptimizerLeafCapacity(VarKeyScalar16OptimizerItem[] items, int start, int end, int maxKeyLength)
    {
        int low = 1;
        int high = end - start;
        int best = 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (TryBuildVarKeyScalar16OptimizerShelf(items, start, start + mid, maxKeyLength, out _, out _))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// Tests whether a proposed `VS8` compressed-router grouping can terminate every route as a shelf leaf.<br/>
    /// Grouped fanout bytes are accepted only when no child router would be needed below that grouped range.<br/>
    /// </summary>
    private static bool DoVarKeyScalar8OptimizerRouteGroupsFitLeaves(
        IVarKeyScalar8SortedTupleSource items,
        int start,
        int end,
        int fanoutDepth,
        byte[] distinctFanoutBytes,
        int routeCount,
        int maxKeyLength)
    {
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarLenOptimizerKeyByteOrZero(items.GetKey(itemEnd), fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart ||
                !TryBuildVarKeyScalar8OptimizerShelf(items, itemStart, itemEnd, maxKeyLength, out _, out _))
            {
                return false;
            }

            itemStart = itemEnd;
        }

        return itemStart == end;
    }

    /// <summary>
    /// Tests whether a proposed `VS16` compressed-router grouping can terminate every route as a shelf leaf.<br/>
    /// Grouped fanout bytes are accepted only when no child router would be needed below that grouped range.<br/>
    /// </summary>
    private static bool DoVarKeyScalar16OptimizerRouteGroupsFitLeaves(
        VarKeyScalar16OptimizerItem[] items,
        int start,
        int end,
        int fanoutDepth,
        byte[] distinctFanoutBytes,
        int routeCount,
        int maxKeyLength)
    {
        int itemStart = start;
        for (int routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            int nextFanoutIndex = (routeIndex + 1) * distinctFanoutBytes.Length / routeCount;
            byte endByte = routeIndex == routeCount - 1
                ? byte.MaxValue
                : checked((byte)(distinctFanoutBytes[nextFanoutIndex] - 1));
            int itemEnd = itemStart;
            while (itemEnd < end && GetVarLenOptimizerKeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
            {
                itemEnd++;
            }

            if (itemEnd == itemStart ||
                !TryBuildVarKeyScalar16OptimizerShelf(items, itemStart, itemEnd, maxKeyLength, out _, out _))
            {
                return false;
            }

            itemStart = itemEnd;
        }

        return itemStart == end;
    }

    /// <summary>
    /// Attempts to build one `VS8` optimizer shelf for a sorted item subrange.<br/>
    /// The method grows through configured shelf profiles until the subrange fits or the maximum profile is reached.<br/>
    /// </summary>
    private static bool TryBuildVarKeyScalar8OptimizerShelf(
        IVarKeyScalar8SortedTupleSource items,
        int start,
        int end,
        int maxKeyLength,
        out VarKeyScalar8Profile profile,
        out byte[] shelfBytes,
        bool descending = false)
    {
        profile = VarKeyScalar8Profile.DefaultInitial with { Descending = descending };
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar8Profile.Create(profile.ShelfExtentSize, maxKeyLength) with { Descending = descending };
        }

        while (!VarKeyScalar8.TryBuildFromSorted(items, start, end, profile, out shelfBytes))
        {
            VarKeyScalar8Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                shelfBytes = [];
                return false;
            }

            profile = next.MaxKeyLength == maxKeyLength
                ? next
                : VarKeyScalar8Profile.Create(next.ShelfExtentSize, maxKeyLength) with { Descending = descending };
        }

        return true;
    }

    /// <summary>
    /// Attempts to build one `VS16` optimizer shelf for a sorted item subrange.<br/>
    /// The method grows through configured shelf profiles until the subrange fits or the maximum profile is reached.<br/>
    /// </summary>
    private static bool TryBuildVarKeyScalar16OptimizerShelf(
        VarKeyScalar16OptimizerItem[] items,
        int start,
        int end,
        int maxKeyLength,
        out VarKeyScalar16Profile profile,
        out byte[] shelfBytes,
        bool descending = false)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identityHighs = new ulong[end - start];
        ulong[] identityLows = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            int target = descending ? end - 1 - i : i - start;
            shelfKeys[target] = items[i].Key;
            identityHighs[target] = items[i].IdentityHigh;
            identityLows[target] = items[i].IdentityLow;
        }

        profile = VarKeyScalar16Profile.DefaultInitial with { Descending = descending };
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar16Profile.Create(profile.ShelfExtentSize, maxKeyLength) with { Descending = descending };
        }

        while (!VarKeyScalar16.TryBuildFromSorted(shelfKeys, identityHighs, identityLows, profile, out shelfBytes))
        {
            VarKeyScalar16Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                shelfBytes = [];
                return false;
            }

            profile = next.MaxKeyLength == maxKeyLength
                ? next
                : VarKeyScalar16Profile.Create(next.ShelfExtentSize, maxKeyLength) with { Descending = descending };
        }

        return true;
    }

    /// <summary>
    /// Creates the deterministic `VS8` identity used by the current optimizer proof planner.<br/>
    /// This keeps planner promotion behavior identical to the harness rows while the eventual production API decides how caller identities are supplied.<br/>
    /// </summary>
    private static ulong CreateVarKeyScalar8OptimizerIdentity(int index)
    {
        return unchecked(0x8100_0000_0000_0000UL | (uint)index);
    }

    /// <summary>
    /// Creates the deterministic `VS16` identity used by the current optimizer proof planner.<br/>
    /// This keeps planner promotion behavior identical to the harness rows while the eventual production API decides how caller identities are supplied.<br/>
    /// </summary>
    private static void CreateVarKeyScalar16OptimizerIdentity(int index, out ulong identityHigh, out ulong identityLow)
    {
        identityHigh = unchecked(0xA100_0000_0000_0000UL | (uint)(index / 1024));
        identityLow = unchecked(0xB100_0000_0000_0000UL | (uint)index);
    }

    /// <summary>
    /// Reads a key byte at a depth, returning zero when the key is shorter than that depth.<br/>
    /// This matches the existing varlen route-planning behavior for shorter key suffixes.<br/>
    /// </summary>
    private static byte GetVarLenOptimizerKeyByteOrZero(ReadOnlySpan<byte> key, int depth)
    {
        return depth >= 0 && depth < key.Length
            ? key[depth]
            : (byte)0;
    }

    /// <summary>
    /// Adapts the optimizer's established owned item array to the seekable tuple contract shared by bounded native `VS8` construction.<br/>
    /// The adapter is allocation-constant and preserves the existing unordered maintenance planners while the recursive node builder consumes one common source abstraction.<br/>
    /// </summary>
    private sealed class VarKeyScalar8OptimizerItemArraySource : IVarKeyScalar8SortedTupleSource
    {
        private readonly VarKeyScalar8OptimizerItem[] items;

        /// <summary>
        /// Initializes the adapter over one stable optimizer item array.<br/>
        /// </summary>
        /// <param name="items">Owned sorted optimizer items retained for the complete recursive build.<br/></param>
        internal VarKeyScalar8OptimizerItemArraySource(VarKeyScalar8OptimizerItem[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            this.items = items;
        }

        /// <inheritdoc/>
        public int Count => items.Length;

        /// <inheritdoc/>
        public ReadOnlySpan<byte> GetKey(int index) => items[index].Key;

        /// <inheritdoc/>
        public ulong GetIdentity(int index) => items[index].Identity;

        /// <inheritdoc/>
        public int CompareKeys(int leftIndex, int rightIndex)
            => items[leftIndex].Key.AsSpan().SequenceCompareTo(items[rightIndex].Key);
    }
}
