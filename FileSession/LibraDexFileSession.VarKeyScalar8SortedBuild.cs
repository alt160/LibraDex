using System.Diagnostics;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Builds an empty `VS8` index directly from encoded tuples supplied in arbitrary order and publishes the completed topology through one stable-root rewrite.<br/>
    /// Input validation and sorting complete before storage allocation; each root-prefix subtree is then built while unreachable through the established optimizer replacement planner.<br/>
    /// Child storage is committed before the existing root changes, so cancellation or failure before publication leaves the empty root authoritative and any already-durable candidate extents unreachable.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable empty root-router offset owned by the target index.<br/></param>
    /// <param name="maxKeyLength">Maximum encoded key length accepted by the target `VS8` profile.<br/></param>
    /// <param name="requestedRouteCount">Preferred compressed-router fanout cap.<br/></param>
    /// <param name="tuples">Encoded key and scalar-8 identity tuples in arbitrary order.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple distinct identities may share one encoded key.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether an encoded identity may occur under only one key.<br/></param>
    /// <param name="cancellationToken">Cancellation observed through validation, sorting, and unreachable child construction; publication proceeds without a cancellation gap after the final check.<br/></param>
    /// <returns>Tuple and topology counts plus child-build and stable-root publication timings.<br/></returns>
    internal VarKeyScalar8SortedBuildResult BuildVarKeyScalar8FromUnordered(
        long rootRouterOffset,
        int maxKeyLength,
        int requestedRouteCount,
        ReadOnlySpan<VarKeyScalar8SortedTuple> tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        CancellationToken cancellationToken,
        bool descending = false)
    {
        if (rootRouterOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VS8 sorted builder requires a positive root-router offset.");
        }

        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VS8 sorted builder requires a maximum encoded key length from 1 through 1024.");
        }

        if (requestedRouteCount <= 0 || requestedRouteCount > RouterLayout.MaxOneByteRouteCount)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedRouteCount), requestedRouteCount, "The VS8 sorted builder route count must be from 1 through 256.");
        }

        if (durabilityBatchActive)
        {
            throw new InvalidOperationException("The VS8 sorted builder cannot run inside an active durability batch.");
        }

        VarKeyScalar8SortedTuple[] sorted = tuples.ToArray();
        ValidateVarKeyScalar8SortedBuildInput(
            sorted,
            maxKeyLength,
            allowDuplicateKeys,
            singleKeyPerIdentity,
            cancellationToken);
        Array.Sort(sorted, static (left, right) =>
        {
            int keyComparison = left.Key.AsSpan().SequenceCompareTo(right.Key);
            return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
        });
        ValidateVarKeyScalar8SortedBuildOrder(sorted, allowDuplicateKeys, cancellationToken);

        return BuildVarKeyScalar8FromSortedCore(
            rootRouterOffset,
            maxKeyLength,
            requestedRouteCount,
            new VarKeyScalar8SortedArraySource(sorted),
            cancellationToken,
            descending);
    }

    /// <summary>
    /// Builds an empty `VS8` index from a stable seekable source already ordered by encoded key then scalar identity.<br/>
    /// The source may be backed by bounded memory or a merged spill file; validation and topology construction address tuples by ordinal and do not require one managed tuple array.<br/>
    /// An authoritative caller may suppress the identity-multiplicity set after proving one emitted tuple per record identity during extraction, while key bounds, tuple order, exact duplicates, and key uniqueness remain validated here.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable empty root-router offset owned by the target index.<br/></param>
    /// <param name="maxKeyLength">Maximum encoded key length accepted by the target `VS8` profile.<br/></param>
    /// <param name="requestedRouteCount">Preferred compressed-router fanout cap.<br/></param>
    /// <param name="tuples">Stable seekable tuples in encoded key-then-identity order.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple distinct identities may share one encoded key.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether an encoded identity may occur under only one key.<br/></param>
    /// <param name="identityMultiplicityAlreadyValidated">Whether the authoritative extraction lifecycle already proved the single-key-per-identity contract.<br/></param>
    /// <param name="cancellationToken">Cancellation observed throughout validation and unreachable topology construction.<br/></param>
    /// <returns>Tuple and topology counts plus child-build and stable-root publication timings.<br/></returns>
    internal VarKeyScalar8SortedBuildResult BuildVarKeyScalar8FromSorted(
        long rootRouterOffset,
        int maxKeyLength,
        int requestedRouteCount,
        IVarKeyScalar8SortedTupleSource tuples,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        bool identityMultiplicityAlreadyValidated,
        CancellationToken cancellationToken,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (rootRouterOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootRouterOffset), rootRouterOffset, "The VS8 sorted builder requires a positive root-router offset.");
        if (maxKeyLength <= 0 || maxKeyLength > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The VS8 sorted builder requires a maximum encoded key length from 1 through 1024.");
        if (requestedRouteCount <= 0 || requestedRouteCount > RouterLayout.MaxOneByteRouteCount)
            throw new ArgumentOutOfRangeException(nameof(requestedRouteCount), requestedRouteCount, "The VS8 sorted builder route count must be from 1 through 256.");
        if (durabilityBatchActive)
            throw new InvalidOperationException("The VS8 sorted builder cannot run inside an active durability batch.");

        ValidateVarKeyScalar8SortedSource(
            tuples,
            maxKeyLength,
            allowDuplicateKeys,
            singleKeyPerIdentity && !identityMultiplicityAlreadyValidated,
            cancellationToken);
        return BuildVarKeyScalar8FromSortedCore(
            rootRouterOffset,
            maxKeyLength,
            requestedRouteCount,
            tuples,
            cancellationToken,
            descending);
    }

    /// <summary>
    /// Constructs and publishes one validated seekable `VS8` population while retaining the stable empty root until all child storage is durable.<br/>
    /// </summary>
    /// <param name="rootRouterOffset">Stable empty root-router offset owned by the target index.<br/></param>
    /// <param name="maxKeyLength">Maximum encoded key length accepted by the target profile.<br/></param>
    /// <param name="requestedRouteCount">Preferred compressed-router fanout cap.<br/></param>
    /// <param name="tuples">Validated stable tuples in canonical order.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before the stable-root publication boundary.<br/></param>
    /// <returns>Tuple and topology counts plus publication telemetry.<br/></returns>
    private VarKeyScalar8SortedBuildResult BuildVarKeyScalar8FromSortedCore(
        long rootRouterOffset,
        int maxKeyLength,
        int requestedRouteCount,
        IVarKeyScalar8SortedTupleSource tuples,
        CancellationToken cancellationToken,
        bool descending)
    {
        lock (writePublicationSync)
        {
            byte[] currentRootBytes = new byte[RouterLayout.Size];
            kernel.Read(rootRouterOffset, currentRootBytes);
            RouterReader currentRoot = new(currentRootBytes);
            if (!currentRoot.IsValid || !currentRoot.HasDirectIndex || currentRoot.KeyDepth != 0)
            {
                throw new InvalidOperationException("The VS8 sorted builder requires a valid direct root router at key depth zero.");
            }

            for (int prefix = 0; prefix < RouterLayout.MaxOneByteRouteCount; prefix++)
            {
                if (currentRoot.GetDirectTarget((byte)prefix) != 0)
                {
                    throw new InvalidOperationException("The VS8 sorted builder currently accepts only an empty root router.");
                }
            }

            if (tuples.Count == 0)
            {
                return new VarKeyScalar8SortedBuildResult(
                    0,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    default,
                    default);
            }

            var buildTimer = Stopwatch.StartNew();
            var rootTargets = new long[RouterLayout.MaxOneByteRouteCount];
            int rootPrefixCount = 0;
            int routeCount = 0;
            int routerCount = 0;
            int shelfCount = 0;
            try
            {
                int start = 0;
                while (start < tuples.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte rootPrefix = tuples.GetKey(start)[0];
                    int end = start + 1;
                    while (end < tuples.Count && tuples.GetKey(end)[0] == rootPrefix)
                    {
                        end++;
                    }

                    VarLenOptimizerReplacementSubtree replacement = CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
                        tuples,
                        start,
                        end,
                        maxKeyLength,
                        requestedRouteCount,
                        descending);
                    if (replacement.RootPrefix != rootPrefix || rootTargets[rootPrefix] != 0)
                    {
                        throw new InvalidDataException("The VS8 sorted builder produced an invalid or duplicate root-prefix replacement.");
                    }

                    rootTargets[rootPrefix] = replacement.TargetOffset;
                    rootPrefixCount++;
                    routeCount = checked(routeCount + replacement.Build.RouteCount);
                    routerCount = checked(routerCount + replacement.Build.RouterCount);
                    shelfCount = checked(shelfCount + replacement.Build.ShelfCount);
                    start = end;
                }

                cancellationToken.ThrowIfCancellationRequested();
                DataKernelCommitTelemetry childCommit = CommitAndInvalidateRouterReadCache();
                buildTimer.Stop();

                var publishTimer = Stopwatch.StartNew();
                RawDataReservation rootRewrite = kernel.ReserveAt(rootRouterOffset, RouterLayout.Size);
                new RouterWriter(rootRewrite.Span).InitializeExpandedOneByte(0, currentRoot.AllocationClassId, rootTargets);
                DataKernelCommitTelemetry rootCommit = CommitAndInvalidateRouterReadCache();
                publishTimer.Stop();
                return new VarKeyScalar8SortedBuildResult(
                    tuples.Count,
                    rootPrefixCount,
                    routeCount,
                    routerCount,
                    shelfCount,
                    buildTimer.Elapsed,
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
    }

    /// <summary>
    /// Validates one seekable `VS8` source in canonical key-then-identity order without materializing a second population.<br/>
    /// </summary>
    /// <param name="tuples">Stable seekable tuples to validate.<br/></param>
    /// <param name="maxKeyLength">Maximum accepted encoded key length.<br/></param>
    /// <param name="allowDuplicateKeys">Whether adjacent equal keys with distinct identities are accepted.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether to prove identity multiplicity with a temporary set.<br/></param>
    /// <param name="cancellationToken">Cancellation checked at bounded intervals.<br/></param>
    private static void ValidateVarKeyScalar8SortedSource(
        IVarKeyScalar8SortedTupleSource tuples,
        int maxKeyLength,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        CancellationToken cancellationToken)
    {
        HashSet<ulong>? identities = singleKeyPerIdentity ? new HashSet<ulong>() : null;
        for (int i = 0; i < tuples.Count; i++)
        {
            if ((i & 0x0FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> key = tuples.GetKey(i);
            if (key.Length == 0 || key.Length > maxKeyLength)
                throw new ArgumentException($"VS8 sorted tuple {i:N0} has encoded key length {key.Length:N0}; expected 1 through {maxKeyLength:N0}.", nameof(tuples));
            ulong identity = tuples.GetIdentity(i);
            if (identities is not null && !identities.Add(identity))
                throw new InvalidOperationException($"The VS8 sorted input contains identity 0x{identity:X16} more than once for a single-key-per-identity index.");
            if (i != 0)
            {
                int keyComparison = tuples.CompareKeys(i - 1, i);
                ulong previousIdentity = tuples.GetIdentity(i - 1);
                if (keyComparison > 0 || keyComparison == 0 && previousIdentity >= identity)
                    throw new ArgumentException($"The VS8 sorted input is not strictly ordered at ordinal {i:N0}.", nameof(tuples));
                if (!allowDuplicateKeys && keyComparison == 0)
                    throw new InvalidOperationException($"The VS8 sorted input contains a duplicate encoded key at ordinal {i:N0}.");
            }
        }
    }

    /// <summary>
    /// Validates tuple-local contracts that do not depend on final key order before the native `VS8` build allocates storage.<br/>
    /// Encoded keys must be non-empty, bounded, and non-null; optional single-key-per-identity enforcement uses one bounded hash set over scalar identities.<br/>
    /// </summary>
    /// <param name="tuples">Unordered encoded tuples to validate.<br/></param>
    /// <param name="maxKeyLength">Maximum accepted encoded key length.<br/></param>
    /// <param name="allowDuplicateKeys">Whether multiple identities may share one key; retained for diagnostic contract symmetry.<br/></param>
    /// <param name="singleKeyPerIdentity">Whether each identity may occur only once.<br/></param>
    /// <param name="cancellationToken">Cancellation checked at bounded intervals.<br/></param>
    private static void ValidateVarKeyScalar8SortedBuildInput(
        VarKeyScalar8SortedTuple[] tuples,
        int maxKeyLength,
        bool allowDuplicateKeys,
        bool singleKeyPerIdentity,
        CancellationToken cancellationToken)
    {
        _ = allowDuplicateKeys;
        HashSet<ulong>? identities = singleKeyPerIdentity ? new HashSet<ulong>() : null;
        for (int i = 0; i < tuples.Length; i++)
        {
            if ((i & 0x0FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte[] key = tuples[i].Key ?? throw new ArgumentException($"VS8 sorted tuple {i:N0} has a null encoded key.", nameof(tuples));
            if (key.Length == 0 || key.Length > maxKeyLength)
            {
                throw new ArgumentException($"VS8 sorted tuple {i:N0} has encoded key length {key.Length:N0}; expected 1 through {maxKeyLength:N0}.", nameof(tuples));
            }

            if (identities is not null && !identities.Add(tuples[i].Identity))
            {
                throw new InvalidOperationException($"The VS8 sorted input contains identity 0x{tuples[i].Identity:X16} more than once for a single-key-per-identity index.");
            }
        }
    }

    /// <summary>
    /// Validates exact-tuple and key-uniqueness contracts after encoded tuples have been sorted by key then identity.<br/>
    /// Adjacent comparison is sufficient and allocation-free because equal keys and exact tuples are contiguous in canonical physical order.<br/>
    /// </summary>
    /// <param name="tuples">Canonical sorted encoded tuples.<br/></param>
    /// <param name="allowDuplicateKeys">Whether adjacent equal keys with different identities are accepted.<br/></param>
    /// <param name="cancellationToken">Cancellation checked at bounded intervals.<br/></param>
    private static void ValidateVarKeyScalar8SortedBuildOrder(
        VarKeyScalar8SortedTuple[] tuples,
        bool allowDuplicateKeys,
        CancellationToken cancellationToken)
    {
        for (int i = 1; i < tuples.Length; i++)
        {
            if ((i & 0x0FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            VarKeyScalar8SortedTuple previous = tuples[i - 1];
            VarKeyScalar8SortedTuple current = tuples[i];
            if (!previous.Key.AsSpan().SequenceEqual(current.Key))
            {
                continue;
            }

            if (!allowDuplicateKeys)
            {
                throw new InvalidOperationException($"The VS8 sorted input contains a duplicate encoded key at sorted ordinal {i:N0}.");
            }

            if (previous.Identity == current.Identity)
            {
                throw new InvalidOperationException($"The VS8 sorted input contains duplicate tuple identity 0x{current.Identity:X16} at sorted ordinal {i:N0}.");
            }
        }
    }
}

/// <summary>
/// Stores one encoded variable-key/scalar-8 tuple consumed by the native `VS8` sorted builder.<br/>
/// </summary>
/// <param name="Key">Encoded variable key including its LibraDex key-state marker.<br/></param>
/// <param name="Identity">Encoded sortable scalar-8 identity.<br/></param>
internal readonly record struct VarKeyScalar8SortedTuple(byte[] Key, ulong Identity);

/// <summary>
/// Returns topology and durability evidence from one native empty-root `VS8` build.<br/>
/// </summary>
/// <param name="TupleCount">Validated tuple count published to the root.<br/></param>
/// <param name="RootPrefixCount">Populated direct-root prefix count.<br/></param>
/// <param name="RouteCount">Routes written by optimizer-planned descendant routers.<br/></param>
/// <param name="RouterCount">Descendant routers written below the stable root.<br/></param>
/// <param name="ShelfCount">Ordinary and terminal identity shelves written by the planner.<br/></param>
/// <param name="ChildBuildTime">Elapsed unreachable topology construction and child durability time.<br/></param>
/// <param name="RootPublicationTime">Elapsed stable-root rewrite and publication time.<br/></param>
/// <param name="ChildStorageCommit">Child topology durability telemetry.<br/></param>
/// <param name="RootPublicationCommit">Stable-root durability telemetry.<br/></param>
internal readonly record struct VarKeyScalar8SortedBuildResult(
    int TupleCount,
    int RootPrefixCount,
    int RouteCount,
    int RouterCount,
    int ShelfCount,
    TimeSpan ChildBuildTime,
    TimeSpan RootPublicationTime,
    DataKernelCommitTelemetry ChildStorageCommit,
    DataKernelCommitTelemetry RootPublicationCommit);

/// <summary>
/// Exposes a stable ordinal view of globally sorted encoded `VS8` tuples to the native topology builder.<br/>
/// Implementations may retain tuples in a managed array, page them from a merged spill file, or use another seekable source whose contents remain stable for one build call.<br/>
/// </summary>
internal interface IVarKeyScalar8SortedTupleSource
{
    /// <summary>Gets the number of encoded tuples available by ordinal.<br/></summary>
    int Count { get; }

    /// <summary>
    /// Returns the encoded key stored at one zero-based ordinal without requiring a per-key managed array.<br/>
    /// </summary>
    /// <param name="index">Zero-based tuple ordinal.<br/></param>
    /// <returns>A span that remains valid until the next source access.<br/></returns>
    ReadOnlySpan<byte> GetKey(int index);

    /// <summary>Returns the scalar identity stored at one zero-based ordinal.<br/></summary>
    /// <param name="index">Zero-based tuple ordinal.<br/></param>
    /// <returns>The encoded sortable identity.<br/></returns>
    ulong GetIdentity(int index);

    /// <summary>
    /// Compares two encoded keys without requiring both returned spans to remain alive across source paging.<br/>
    /// </summary>
    /// <param name="leftIndex">Left tuple ordinal.<br/></param>
    /// <param name="rightIndex">Right tuple ordinal.<br/></param>
    /// <returns>A negative, zero, or positive canonical byte-order comparison.<br/></returns>
    int CompareKeys(int leftIndex, int rightIndex);
}

/// <summary>
/// Adapts an owned managed tuple array to the shared seekable `VS8` builder contract.<br/>
/// </summary>
internal sealed class VarKeyScalar8SortedArraySource : IVarKeyScalar8SortedTupleSource
{
    private readonly VarKeyScalar8SortedTuple[] tuples;

    /// <summary>
    /// Initializes a stable ordinal adapter over one encoded tuple array.<br/>
    /// </summary>
    /// <param name="tuples">Owned encoded tuples retained for the duration of the build.<br/></param>
    internal VarKeyScalar8SortedArraySource(VarKeyScalar8SortedTuple[] tuples)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        this.tuples = tuples;
    }

    /// <inheritdoc/>
    public int Count => tuples.Length;

    /// <inheritdoc/>
    public ReadOnlySpan<byte> GetKey(int index) => tuples[index].Key;

    /// <inheritdoc/>
    public ulong GetIdentity(int index) => tuples[index].Identity;

    /// <inheritdoc/>
    public int CompareKeys(int leftIndex, int rightIndex)
        => tuples[leftIndex].Key.AsSpan().SequenceCompareTo(tuples[rightIndex].Key);
}
