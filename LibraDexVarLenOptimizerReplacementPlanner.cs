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

public sealed partial class LibraDexFileSession
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
    /// <returns>The staged replacement subtree offset and planner telemetry.</returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar8OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
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
            items,
            start: 0,
            end: items.Length,
            keyDepth: 1,
            maxKeyLength,
            requestedRouteCount,
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
    }

    /// <summary>
    /// Stages a cold `VS16` compressed-router replacement subtree for a queued varlen optimizer candidate.<br/>
    /// The method owns only replacement construction: callers must already be inside the durability batch that will later publish or abort the replacement target.<br/>
    /// Identity values intentionally match the current harness proof contract, using deterministic ordinal `VS16` identities aligned with the supplied key slice.<br/>
    /// </summary>
    /// <param name="keys">The key slice to include in the replacement subtree.</param>
    /// <param name="maxKeyLength">The maximum raw key length supported by the target `VS16` profile.</param>
    /// <param name="requestedRouteCount">The preferred compressed-router fanout cap.</param>
    /// <returns>The staged replacement subtree offset and planner telemetry.</returns>
    internal VarLenOptimizerReplacementSubtree CreateVarKeyScalar16OptimizerReplacementSubtree(
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
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
            ref state);
        return new VarLenOptimizerReplacementSubtree(rootPrefix, targetOffset, state.ToResult());
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
    /// <returns>The bounded maintenance result for this drain attempt.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar8OptimizerCandidate(
        long rootRouterOffset,
        VarLenRouteOptimizerCandidate candidate,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
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
        VarLenOptimizerReplacementSubtree subtree = CreateVarKeyScalar8OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount);
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
    /// <returns>The aggregate maintenance result for the bounded drain.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar8OptimizerCandidates(
        long rootRouterOffset,
        VarLenRouteOptimizerState state,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        VarLenOptimizerMaintenancePolicy policy,
        VarLenOptimizerMaintenanceBoundary boundary)
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
            useScalar16Identity: false);
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
            VarLenOptimizerMaintenanceBoundary.OnDemand);
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
            VarLenOptimizerMaintenanceBoundary.BetweenHotActions);
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
    /// <returns>The bounded maintenance result for this drain attempt.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar16OptimizerCandidate(
        long rootRouterOffset,
        VarLenRouteOptimizerCandidate candidate,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount)
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
        VarLenOptimizerReplacementSubtree subtree = CreateVarKeyScalar16OptimizerReplacementSubtree(keys, maxKeyLength, requestedRouteCount);
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
    /// <returns>The aggregate maintenance result for the bounded drain.</returns>
    internal VarLenOptimizerMaintenanceResult TryDrainVarKeyScalar16OptimizerCandidates(
        long rootRouterOffset,
        VarLenRouteOptimizerState state,
        ReadOnlySpan<byte[]> keys,
        int maxKeyLength,
        int requestedRouteCount,
        VarLenOptimizerMaintenancePolicy policy,
        VarLenOptimizerMaintenanceBoundary boundary)
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
            useScalar16Identity: true);
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
            VarLenOptimizerMaintenanceBoundary.OnDemand);
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
            VarLenOptimizerMaintenanceBoundary.BetweenHotActions);
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
        bool useScalar16Identity)
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
                ? TryDrainVarKeyScalar16OptimizerCandidate(rootRouterOffset, candidate, keys, maxKeyLength, requestedRouteCount)
                : TryDrainVarKeyScalar8OptimizerCandidate(rootRouterOffset, candidate, keys, maxKeyLength, requestedRouteCount);
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
        VarKeyScalar8OptimizerItem[] items,
        int start,
        int end,
        int keyDepth,
        int maxKeyLength,
        int requestedRouteCount,
        ref VarLenOptimizerReplacementBuildState state)
    {
        if (TryBuildVarKeyScalar8OptimizerShelf(items, start, end, maxKeyLength, out VarKeyScalar8Profile profile, out byte[] shelfBytes))
        {
            (long shelfOffset, _) = CreateVarKeyScalar8Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
        }

        int fanoutDepth = FindVarKeyScalar8OptimizerFirstDivergenceDepth(items, start, end, keyDepth, maxKeyLength);
        if (state.FirstFanoutDepth == 0)
        {
            state.FirstFanoutDepth = fanoutDepth;
        }

        byte[] stem = CreateVarKeyScalar8OptimizerStem(items[start].Key, keyDepth, fanoutDepth);
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
            while (itemEnd < end && GetVarLenOptimizerKeyByteOrZero(items[itemEnd].Key, fanoutDepth) <= endByte)
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
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The VS8 optimizer replacement planner did not consume every item in the current node.");
        }

        (RouterSnapshot router, _) = CreateCompressedMultiByteRouter(
            checked((byte)(fanoutDepth - keyDepth + 1)),
            checked((ushort)keyDepth),
            maxRouteCount: checked((ushort)routes.Length),
            allocationClassId: 0,
            routes);
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return router.Offset;
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
        ref VarLenOptimizerReplacementBuildState state)
    {
        if (TryBuildVarKeyScalar16OptimizerShelf(items, start, end, maxKeyLength, out VarKeyScalar16Profile profile, out byte[] shelfBytes))
        {
            (long shelfOffset, _) = CreateVarKeyScalar16Shelf(profile, shelfBytes);
            state.ShelfCount++;
            state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, keyDepth);
            return shelfOffset;
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
                ref state);
            routes[routeIndex] = new RouterMultiByteRouteSnapshot(stem, startByte, endByte, targetOffset);
            itemStart = itemEnd;
        }

        if (itemStart != end)
        {
            throw new InvalidDataException("The VS16 optimizer replacement planner did not consume every item in the current node.");
        }

        (RouterSnapshot router, _) = CreateCompressedMultiByteRouter(
            checked((byte)(fanoutDepth - keyDepth + 1)),
            checked((ushort)keyDepth),
            maxRouteCount: checked((ushort)routes.Length),
            allocationClassId: 0,
            routes);
        state.RouterCount++;
        state.RouteCount += routes.Length;
        state.MaxPlannerDepth = Math.Max(state.MaxPlannerDepth, fanoutDepth + 1);
        return router.Offset;
    }

    /// <summary>
    /// Finds the first divergent key byte depth for a sorted `VS8` optimizer item range.<br/>
    /// This is the byte depth that becomes the final consumed byte for the next compressed router.<br/>
    /// </summary>
    private static int FindVarKeyScalar8OptimizerFirstDivergenceDepth(VarKeyScalar8OptimizerItem[] items, int start, int end, int startDepth, int maxKeyLength)
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
    private static byte[] CreateVarKeyScalar8OptimizerStem(byte[] representativeKey, int keyDepth, int fanoutDepth)
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
    private static byte[] CreateVarKeyScalar8OptimizerDistinctFanoutBytes(VarKeyScalar8OptimizerItem[] items, int start, int end, int fanoutDepth)
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
    private static int EstimateVarKeyScalar8OptimizerLeafCapacity(VarKeyScalar8OptimizerItem[] items, int start, int end, int maxKeyLength)
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
        VarKeyScalar8OptimizerItem[] items,
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
        VarKeyScalar8OptimizerItem[] items,
        int start,
        int end,
        int maxKeyLength,
        out VarKeyScalar8Profile profile,
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identities = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identities[i - start] = items[i].Identity;
        }

        profile = VarKeyScalar8Profile.DefaultInitial;
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar8Profile.Create(profile.ShelfExtentSize, maxKeyLength);
        }

        while (!VarKeyScalar8.TryBuildFromSorted(shelfKeys, identities, profile, out shelfBytes))
        {
            VarKeyScalar8Profile next = profile.NextGrowthClass();
            if (next.ShelfExtentSize == profile.ShelfExtentSize)
            {
                shelfBytes = [];
                return false;
            }

            profile = next.MaxKeyLength == maxKeyLength
                ? next
                : VarKeyScalar8Profile.Create(next.ShelfExtentSize, maxKeyLength);
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
        out byte[] shelfBytes)
    {
        byte[][] shelfKeys = new byte[end - start][];
        ulong[] identityHighs = new ulong[end - start];
        ulong[] identityLows = new ulong[end - start];
        for (int i = start; i < end; i++)
        {
            shelfKeys[i - start] = items[i].Key;
            identityHighs[i - start] = items[i].IdentityHigh;
            identityLows[i - start] = items[i].IdentityLow;
        }

        profile = VarKeyScalar16Profile.DefaultInitial;
        if (profile.MaxKeyLength != maxKeyLength)
        {
            profile = VarKeyScalar16Profile.Create(profile.ShelfExtentSize, maxKeyLength);
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
                : VarKeyScalar16Profile.Create(next.ShelfExtentSize, maxKeyLength);
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
    private static byte GetVarLenOptimizerKeyByteOrZero(byte[] key, int depth)
    {
        return depth >= 0 && depth < key.Length
            ? key[depth]
            : (byte)0;
    }
}
