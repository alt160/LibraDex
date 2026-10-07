namespace LibraDex;

internal readonly record struct LibraDexIdentityPrimitiveRequest(
    LibraDexCriteriaKind CriteriaKind,
    IReadOnlyList<object?> Values,
    int? TakeLimit = null,
    QueryDirection Direction = QueryDirection.Ascending);

/// <summary>
/// Selects the aggregate operation requested over a condition-materialized identity primitive.<br/>
/// The aggregate layer reuses existing criteria semantics; this enum describes the operation to perform after the condition has already chosen an index and primitive request.<br/>
/// </summary>
internal enum LibraDexPrimitiveAggregateKind
{
    /// <summary>
    /// Counts matching physical identity tuples with the requested aggregate scope.<br/>
    /// </summary>
    Count = 0
}

/// <summary>
/// Describes how much physical work an aggregate executor used to produce a result.<br/>
/// Diagnostics can expose whether an aggregate was answered from free metadata, routed range counts, key-only scans, or identity iteration fallback.<br/>
/// </summary>
internal enum LibraDexPrimitiveAggregatePlanKind
{
    /// <summary>
    /// The executor did not classify the aggregate path.<br/>
    /// This is used by the compatibility adapter around existing primitive count implementations until each shape reports more detail.<br/>
    /// </summary>
    Unclassified = 0,

    /// <summary>
    /// The aggregate was answered from existing metadata that is maintained only with same-page writes.<br/>
    /// </summary>
    Metadata = 1,

    /// <summary>
    /// The aggregate was answered from routed shelf or slot range counts without inspecting every identity.<br/>
    /// </summary>
    RangeSlots = 2,

    /// <summary>
    /// The aggregate scanned candidate keys for residual predicates while avoiding identity materialization.<br/>
    /// </summary>
    KeyScan = 3,

    /// <summary>
    /// The aggregate fell back to identity or tuple iteration.<br/>
    /// </summary>
    IdentityScan = 4
}

/// <summary>
/// Carries one aggregate operation over an already-normalized primitive request.<br/>
/// This keeps aggregate execution aligned with the condition builder's existing materialization, projection, null-state, and composite semantics.<br/>
/// </summary>
/// <param name="Kind">The aggregate operation to execute.<br/></param>
/// <param name="PrimitiveRequest">The condition-materialized primitive request to aggregate over.<br/></param>
/// <param name="Scope">The tuple/key aggregate scope requested by the caller.<br/></param>
internal readonly record struct LibraDexPrimitiveAggregateRequest(
    LibraDexPrimitiveAggregateKind Kind,
    LibraDexIdentityPrimitiveRequest PrimitiveRequest,
    AggregateScope Scope = AggregateScope.Tuples);

/// <summary>
/// Represents the result of one primitive aggregate execution.<br/>
/// Count is the first connected aggregate value; future sum, average, min, and max fields can extend this result without changing condition materialization.<br/>
/// </summary>
/// <param name="Kind">The aggregate operation that produced the result.<br/></param>
/// <param name="Count">The count result for <see cref="LibraDexPrimitiveAggregateKind.Count"/>.<br/></param>
/// <param name="PlanKind">The physical aggregate plan classification used by the executor.<br/></param>
internal readonly record struct LibraDexPrimitiveAggregateResult(
    LibraDexPrimitiveAggregateKind Kind,
    long Count,
    LibraDexPrimitiveAggregatePlanKind PlanKind)
{
    /// <summary>
    /// Creates a count aggregate result.<br/>
    /// </summary>
    /// <param name="count">The matching tuple count.<br/></param>
    /// <param name="planKind">The physical aggregate plan classification used by the executor.<br/></param>
    /// <returns>A count aggregate result.</returns>
    internal static LibraDexPrimitiveAggregateResult ForCount(long count, LibraDexPrimitiveAggregatePlanKind planKind)
    {
        return new LibraDexPrimitiveAggregateResult(LibraDexPrimitiveAggregateKind.Count, count, planKind);
    }
}

/// <summary>
/// Describes one inclusive key extent requested by condition-driven multi-range retrieval.<br/>
/// The bounds remain runtime objects at this layer because non-generic condition planning resolves index handles before generic physical readers are invoked.<br/>
/// </summary>
/// <param name="LowerKey">The inclusive lower key.</param>
/// <param name="UpperKey">The inclusive upper key.</param>
internal readonly record struct LibraDexIdentityKeyRange(object LowerKey, object UpperKey);

internal interface IIdentityPrimitiveAggregateExecutor
{
    /// <summary>
    /// Executes one aggregate over an already-normalized identity primitive request.<br/>
    /// Implementations must preserve condition-builder semantics and should choose the lowest-work shape-native path available for the requested aggregate.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.</param>
    /// <returns>The aggregate result.</returns>
    LibraDexPrimitiveAggregateResult ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request);
}

internal interface IIdentityPrimitiveExecutor : IIdentityPrimitiveAggregateExecutor
{
    /// <summary>
    /// Streams identities for one normalized primitive request in the requested key traversal direction.<br/>
    /// The enumerable is intentionally internal so condition execution can use a cursor-style path without adding more public direct lookup verbs.<br/>
    /// Implementations should honor <see cref="LibraDexIdentityPrimitiveRequest.TakeLimit"/> while reading, not by materializing and trimming afterwards.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to execute.</param>
    /// <returns>A forward-only identity sequence over the requested key direction.</returns>
    IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);

    /// <summary>
    /// Executes count as the first aggregate primitive by adapting the existing primitive count implementation.<br/>
    /// Shape-specific executors can override <see cref="IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate"/> later to report richer plan kinds or support additional aggregate operations.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.</param>
    /// <returns>The aggregate result.</returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to identity primitive aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to identity primitive aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitive(request.PrimitiveRequest),
            LibraDexPrimitiveAggregatePlanKind.Unclassified);
    }

    IReadOnlyList<object> ExecuteAllIdentities();

    /// <summary>
    /// Streams the best available identity universe for the executor's identity group.<br/>
    /// Grouped catalog indexes should return the de-duplicated union of identities visible through indexes in the same group, while ungrouped handles may fall back to their own `All` primitive.<br/>
    /// This gives negated criteria a group-aware universe without exposing a public identity-source abstraction yet.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence representing the current identity universe.</returns>
    IEnumerable<object> IterateIdentityUniverse();
}

/// <summary>
/// Exposes the strongly typed counterpart of an internal identity primitive executor.<br/>
/// Built-in scalar indexes implement this contract so value-type identities remain unboxed from physical decoding through logical condition composition and final projection.<br/>
/// The non-generic executor remains the compatibility contract for runtime-shaped and public object APIs; callers should prefer this interface whenever their identity type is known.<br/>
/// </summary>
/// <typeparam name="TIdentity">The decoded identity type emitted by the physical index.<br/></typeparam>
internal interface IIdentityPrimitiveExecutor<TIdentity>
{
    /// <summary>
    /// Streams one normalized primitive as typed identities without routing values through <see cref="object"/>.<br/>
    /// Implementations must preserve the same traversal direction, take limit, and condition semantics as their non-generic executor counterpart.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to execute.<br/></param>
    /// <returns>A forward-only typed identity sequence.<br/></returns>
    IEnumerable<TIdentity> IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request);

    /// <summary>
    /// Streams the best available typed identity universe for this executor's identity group.<br/>
    /// Group-aware implementations must preserve the same de-duplicated universe semantics as <see cref="IIdentityPrimitiveExecutor.IterateIdentityUniverse"/>.<br/>
    /// </summary>
    /// <returns>A forward-only typed identity universe.<br/></returns>
    IEnumerable<TIdentity> IterateIdentityUniverseTyped();
}

internal interface IIdentityPrimitiveMutator
{
    /// <summary>
    /// Deletes tuples matched by one normalized primitive request from this index.<br/>
    /// The request is already produced by the adopted condition materializer, so implementers should preserve the same key, range, scan, projection, and composite routing semantics used by retrieval for that primitive.<br/>
    /// The returned changed count is tuple-oriented because index mutation removes key/identity entries, not source objects outside LibraDex.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to delete.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    LibraDexIdentityMutationResult DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request);
}

internal interface IIdentityPrimitiveTupleExecutor
{
    /// <summary>
    /// Materializes key/identity tuples matched by one normalized primitive request from this index.<br/>
    /// Criteria-scoped mutation uses this lower-level tuple capture when identity-only projection is not enough to safely replace or remove exact physical tuples.<br/>
    /// Implementers should preserve the same primitive routing semantics as identity retrieval while returning the original key side needed for exact mutation.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to execute.</param>
    /// <returns>The matching key/identity tuples as non-generic object values.</returns>
    IReadOnlyList<LibraDexObjectTuple> ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request);
}

internal interface IIdentityPrimitiveTupleStreamer
{
    /// <summary>
    /// Streams key/identity tuples matched by one normalized primitive request from this index.<br/>
    /// Condition-reader adapters use this path so public cursor-shaped APIs can sit on the existing physical readers instead of materializing tuple lists first.<br/>
    /// Mutation paths may still use <see cref="IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive"/> when they need a stable captured tuple set before applying changes.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request to stream.</param>
    /// <returns>A forward-only tuple sequence over the requested key direction.</returns>
    IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request);
}

internal interface IIdentityExactTupleMutator
{
    /// <summary>
    /// Tests whether one exact key/identity tuple is currently visible in this index.<br/>
    /// This is used by re-key operations to distinguish an already-satisfied target tuple from an insert conflict that did not create the requested tuple.<br/>
    /// </summary>
    /// <param name="key">The key value to test.</param>
    /// <param name="identity">The identity value to test.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    bool ContainsExactTuple(object? key, object identity);

    /// <summary>
    /// Deletes one exact key/identity tuple from this index.<br/>
    /// The operation must not delete neighboring identities that share the same key, because criteria-scoped re-key depends on tuple-level replacement semantics.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    /// <returns><see langword="true"/> when a tuple was removed.</returns>
    bool DeleteExactTuple(object? key, object identity);
}

internal sealed class LibraDexIdentityCriterion : IIdentityCriterion
{
    private readonly LibraDexIdentityCriterionProjection ids;
    private readonly LibraDexIdentityCriterionMutationBuilder mutations;

    private LibraDexIdentityCriterion(
        string group,
        LibraDexIdentityCriterionNodeKind nodeKind,
        IIndex? index,
        LibraDexCriteriaKind? criteriaKind,
        IReadOnlyList<object?> values,
        Func<LibraDexExternalIdentityContext, bool>? externalIdentityFilter,
        Func<IEnumerable<object>>? externalIdentitySource,
        IIdentityCriterion? left,
        IIdentityCriterion? right,
        LibraDexQueryDiagnostics diagnostics)
    {
        Group = group;
        NodeKind = nodeKind;
        Index = index;
        CriteriaKind = criteriaKind;
        Values = values;
        ExternalIdentityFilter = externalIdentityFilter;
        ExternalIdentitySource = externalIdentitySource;
        Left = left;
        Right = right;
        Diagnostics = diagnostics;
        ids = new LibraDexIdentityCriterionProjection(this, LibraDexIdentityQueryOptions.Default);
        mutations = new LibraDexIdentityCriterionMutationBuilder(this);
    }

    public string Group { get; }

    public LibraDexIdentityCriterionNodeKind NodeKind { get; }

    public IIndex? Index { get; }

    public LibraDexCriteriaKind? CriteriaKind { get; }

    public IReadOnlyList<object?> Values { get; }

    public Func<LibraDexExternalIdentityContext, bool>? ExternalIdentityFilter { get; }

    public Func<IEnumerable<object>>? ExternalIdentitySource { get; }

    public IIdentityCriterion? Left { get; }

    public IIdentityCriterion? Right { get; }

    public LibraDexQueryDiagnostics Diagnostics { get; }

    public IIdentityCriterionProjection IDs => ids;

    public IIdentityCriterionMutationBuilder Mutate => mutations;

    public IIdentityCriterionProjection IDsWith(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        LibraDexIdentityQueryOptions options = new(ordering, deduplication, skip, take, bookmark);
        options.Validate();
        return new LibraDexIdentityCriterionProjection(this, options);
    }

    internal static IIdentityCriterion Leaf(IIndex index, LibraDexCriteriaKind criteriaKind, LibraDexQueryDiagnostics diagnostics, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(index);
        string group = index.Group.Length == 0
            ? "__standalone:" + index.Name
            : index.Group;

        return new LibraDexIdentityCriterion(
            group,
            LibraDexIdentityCriterionNodeKind.Leaf,
            index,
            criteriaKind,
            Array.AsReadOnly(values),
            externalIdentityFilter: null,
            externalIdentitySource: null,
            left: null,
            right: null,
            diagnostics);
    }

    /// <summary>
    /// Creates an executable leaf criterion for a projection index while preserving the caller's logical identity group.<br/>
    /// Hidden projection indexes may intentionally have no public group metadata, but their identity values still belong to the owning logical group.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the owning condition.</param>
    /// <param name="index">The physical projection index that executes the primitive.</param>
    /// <param name="criteriaKind">The primitive criteria kind to execute.</param>
    /// <param name="diagnostics">The diagnostics descriptor for the primitive route.</param>
    /// <param name="values">The validated primitive operands.</param>
    /// <returns>An executable identity criterion leaf.</returns>
    internal static IIdentityCriterion Leaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, LibraDexQueryDiagnostics diagnostics, params object?[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Group.Length != 0 && !string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The projection index does not belong to the requested identity group.");
        }

        return new LibraDexIdentityCriterion(
            group,
            LibraDexIdentityCriterionNodeKind.Leaf,
            index,
            criteriaKind,
            Array.AsReadOnly(values),
            externalIdentityFilter: null,
            externalIdentitySource: null,
            left: null,
            right: null,
            diagnostics);
    }

    /// <summary>
    /// Creates an external identity-filter criterion for a logical identity group.<br/>
    /// The criterion is intentionally not index-backed and must be composed with an indexed sibling so execution has a candidate identity stream to pass through the caller predicate.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the owning condition.<br/></param>
    /// <param name="filter">The caller-supplied identity filter.<br/></param>
    /// <returns>An external identity-filter criterion.</returns>
    internal static IIdentityCriterion External(string group, Func<LibraDexExternalIdentityContext, bool> filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(filter);
        return new LibraDexIdentityCriterion(
            group,
            LibraDexIdentityCriterionNodeKind.External,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            externalIdentityFilter: filter,
            externalIdentitySource: null,
            left: null,
            right: null,
            diagnostics: new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }

    /// <summary>
    /// Creates an external identity-source criterion for a logical identity group.<br/>
    /// The source supplies identities directly, so it can execute without an indexed sibling and can compose with indexed criteria through normal identity set operations.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the owning condition.<br/></param>
    /// <param name="source">The caller-supplied identity source.<br/></param>
    /// <returns>An external identity-source criterion.</returns>
    internal static IIdentityCriterion ExternalSource(string group, Func<IEnumerable<object>> source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(source);
        return new LibraDexIdentityCriterion(
            group,
            LibraDexIdentityCriterionNodeKind.External,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            externalIdentityFilter: null,
            externalIdentitySource: source,
            left: null,
            right: null,
            diagnostics: new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }

    public IIdentityCriterion And(IIdentityCriterion other)
        => Compose(LibraDexIdentityCriterionNodeKind.And, this, other);

    public IIdentityCriterion Or(IIdentityCriterion other)
        => Compose(LibraDexIdentityCriterionNodeKind.Or, this, other);

    public IIdentityCriterion Except(IIdentityCriterion other)
        => Compose(LibraDexIdentityCriterionNodeKind.Except, this, other);

    public IIdentityCriterion Not()
    {
        return new LibraDexIdentityCriterion(
            Group,
            LibraDexIdentityCriterionNodeKind.Not,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            externalIdentityFilter: null,
            externalIdentitySource: null,
            left: this,
            right: null,
            diagnostics: new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }

    private static IIdentityCriterion Compose(
        LibraDexIdentityCriterionNodeKind nodeKind,
        IIdentityCriterion left,
        IIdentityCriterion right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (string.IsNullOrWhiteSpace(left.Group) ||
            string.IsNullOrWhiteSpace(right.Group) ||
            !string.Equals(left.Group, right.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Programmatic identity criteria can only be composed inside the same identity group.");
        }

        return new LibraDexIdentityCriterion(
            left.Group,
            nodeKind,
            index: null,
            criteriaKind: null,
            Array.Empty<object?>(),
            externalIdentityFilter: null,
            externalIdentitySource: null,
            left,
            right,
            diagnostics: new LibraDexQueryDiagnostics(LibraDexExecutionKind.Projection));
    }
}

internal sealed class LibraDexIdentityCriterionProjection : IIdentityCriterionProjection
{
    internal LibraDexIdentityCriterionProjection(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        Criterion = criterion;
        options.Validate();
        Options = options;
    }

    public IIdentityCriterion Criterion { get; }

    public LibraDexProjectionKind Projection => LibraDexProjectionKind.Identities;

    public LibraDexIdentityQueryOptions Options { get; }

    public LibraDexIdentityExecutionPlan Plan()
        => LibraDexIdentityExecutionPlanner.Plan(Criterion, Options);

    public IReadOnlyList<object> ToList()
        => Execute().Identities;

    public IEnumerable<object> Iterate()
        => LibraDexIdentityExecutionPlanner.Iterate(Criterion, Options);

    public IEnumerable<TIdentity> Iterate<TIdentity>()
        => LibraDexIdentityExecutionPlanner.IterateTyped<TIdentity>(Criterion, Options);

    public IReadOnlyList<TIdentity> ToList<TIdentity>()
        => LibraDexIdentityExecutionPlanner.ToListTyped<TIdentity>(Criterion, Options);

    public LibraDexIdentityExecutionResult Execute()
        => LibraDexIdentityExecutionPlanner.Execute(Criterion, Options);

}

internal static class LibraDexIdentityExecutionPlanner
{
    /// <summary>
    /// Resolves the physical duplicate policy for one materialized identity criterion.<br/>
    /// A single primitive over an index that explicitly guarantees one key per identity cannot emit duplicate identities, so satisfying a caller's distinct-result contract requires no runtime set.<br/>
    /// Composite, external, negated, union, and multiple-key-per-identity shapes retain the requested policy because their streams can repeat an identity.<br/>
    /// </summary>
    /// <param name="criterion">The materialized criterion whose physical multiplicity is known.<br/></param>
    /// <param name="options">The caller-requested execution options.<br/></param>
    /// <returns>Execution options with redundant physical deduplication removed only when the index contract proves it unnecessary.<br/></returns>
    private static LibraDexIdentityQueryOptions ResolvePhysicalDeduplication(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options)
    {
        if (options.Deduplication == IdentityDeduplication.Distinct &&
            criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf &&
            criterion.Index is { IdentityKeyMultiplicity: IdentityKeyMultiplicity.SingleKeyPerIdentity } &&
            criterion.CriteriaKind is { } criteriaKind &&
            PrimitiveVisitsEachTupleAtMostOnce(criteriaKind))
        {
            return options with { Deduplication = IdentityDeduplication.Preserve };
        }

        return options;
    }

    /// <summary>
    /// Identifies primitive executions that traverse one physical tuple stream without independently concatenating potentially overlapping operand streams.<br/>
    /// Membership and multi-range primitives remain excluded because repeated or overlapping operands can emit one otherwise single-valued identity more than once.<br/>
    /// </summary>
    /// <param name="criteriaKind">The normalized primitive execution kind.<br/></param>
    /// <returns><see langword="true"/> when one physical tuple can be visited at most once by the primitive.<br/></returns>
    private static bool PrimitiveVisitsEachTupleAtMostOnce(LibraDexCriteriaKind criteriaKind)
        => criteriaKind is
            LibraDexCriteriaKind.All or
            LibraDexCriteriaKind.Find or
            LibraDexCriteriaKind.Between or
            LibraDexCriteriaKind.Before or
            LibraDexCriteriaKind.AtOrBefore or
            LibraDexCriteriaKind.After or
            LibraDexCriteriaKind.AtOrAfter or
            LibraDexCriteriaKind.Prefix or
            LibraDexCriteriaKind.Suffix or
            LibraDexCriteriaKind.Contains or
            LibraDexCriteriaKind.Matches or
            LibraDexCriteriaKind.StructuredComponent or
            LibraDexCriteriaKind.GuidPattern or
            LibraDexCriteriaKind.BinaryPattern or
            LibraDexCriteriaKind.BinaryTypedSlice;

    internal static LibraDexIdentityExecutionPlan Plan(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        options = ResolvePhysicalDeduplication(criterion, options);
        PlanAccumulator accumulator = new();
        LibraDexIdentityExecutionPlan root = PlanNode(criterion, options, accumulator);
        bool requiresOrdering = options.Ordering != IdentityResultOrdering.PlanNatural;
        bool requiresPaging = options.SkipCount > 0 || options.TakeCount is not null || options.Bookmark is not null;
        bool requiresDistinct = options.Deduplication == IdentityDeduplication.Distinct || root.Kind == LibraDexIdentityPlanKind.Union;
        LibraDexIdentityPlanMaterialization materialization = MergeMaterialization(
            root.Materialization,
            requiresOrdering || requiresPaging || requiresDistinct
                ? LibraDexIdentityPlanMaterialization.IdentitySet
                : LibraDexIdentityPlanMaterialization.None);

        return new LibraDexIdentityExecutionPlan(
            criterion,
            options,
            root.Kind,
            materialization,
            requiresDistinct,
            requiresOrdering,
            requiresPaging,
            root.ContainsNegation,
            root.LeafCount,
            root.Depth,
            accumulator.Indexes,
            root.Children);
    }

    internal static LibraDexIdentityExecutionResult Execute(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        options = ResolvePhysicalDeduplication(criterion, options);
        LibraDexIdentityExecutionPlan plan = Plan(criterion, options);
        if (options.Ordering == IdentityResultOrdering.PlanNatural)
        {
            LibraDexIdentityNodeExecution streamed = ExecutePlanNaturalWithStats(criterion, options);
            return new LibraDexIdentityExecutionResult(
                streamed.Identities,
                plan,
                new LibraDexQueryDiagnostics(
                    plan.Materialization == LibraDexIdentityPlanMaterialization.IdentitySet
                        ? LibraDexExecutionKind.Projection
                        : LibraDexExecutionKind.FastPath,
                    RowsScanned: streamed.RowsScanned,
                    RowsReturned: streamed.Identities.Count));
        }

        LibraDexIdentityNodeExecution execution = ExecuteNodeWithStats(criterion);
        List<object> identities = execution.Identities;
        identities = ApplyDeduplication(identities, options.Deduplication);
        ApplyOrdering(identities, options.Ordering);
        IReadOnlyList<object> paged = ApplyPaging(identities, options);
        return new LibraDexIdentityExecutionResult(
            paged,
            plan,
            new LibraDexQueryDiagnostics(
                plan.Materialization == LibraDexIdentityPlanMaterialization.IdentitySet
                    ? LibraDexExecutionKind.Projection
                    : LibraDexExecutionKind.FastPath,
                RowsScanned: execution.RowsScanned,
                RowsReturned: paged.Count));
    }

    internal static IEnumerable<object> Iterate(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        options = ResolvePhysicalDeduplication(criterion, options);
        if (options.Ordering != IdentityResultOrdering.PlanNatural)
        {
            foreach (object identity in Execute(criterion, options).Identities)
            {
                yield return identity;
            }

            yield break;
        }

        IEnumerable<object> identities = IterateNode(criterion);
        if (options.Deduplication == IdentityDeduplication.Distinct)
        {
            identities = DistinctIterator(identities);
        }

        foreach (object identity in ApplyStreamingPaging(identities, options))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Streams a projection as the caller's identity type and retains genuine distinct-result state in that type.<br/>
    /// Plan-natural execution therefore lets value-type identities such as Abraxas record IDs use <see cref="HashSet{T}"/> without retaining one boxed object per distinct identity.<br/>
    /// Physical single-key proofs still suppress the set entirely, while ordered projections retain the established materialized ordering route.<br/>
    /// Duplicate removal occurs before paging so skip, take, and bookmark positions continue to count distinct logical identities.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The materialized criterion tree to execute.<br/></param>
    /// <param name="options">The requested ordering, duplicate, and paging contract.<br/></param>
    /// <returns>A lazy typed identity stream honoring the supplied projection options.<br/></returns>
    internal static IEnumerable<TIdentity> IterateTyped<TIdentity>(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
        options = ResolvePhysicalDeduplication(criterion, options);
        IEnumerable<TIdentity> identities = IterateNodeTyped<TIdentity>(criterion);
        if (options.Deduplication == IdentityDeduplication.Distinct)
        {
            identities = DistinctTypedIterator(identities);
        }

        if (options.Ordering != IdentityResultOrdering.PlanNatural)
        {
            List<TIdentity> ordered = identities.ToList();
            ordered.Sort(Comparer<TIdentity>.Default);
            if (options.Ordering == IdentityResultOrdering.IdentityDescending)
            {
                ordered.Reverse();
            }

            identities = ordered;
        }

        foreach (TIdentity identity in ApplyStreamingPagingTyped(identities, options))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Materializes a typed projection directly from the typed streaming route.<br/>
    /// This avoids first retaining an object result list and then allocating a second typed array for plan-natural Abraxas reads.<br/>
    /// Ordering, duplicate handling, and paging remain owned by <see cref="IterateTyped{TIdentity}(IIdentityCriterion, LibraDexIdentityQueryOptions)"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The materialized criterion tree to execute.<br/></param>
    /// <param name="options">The requested ordering, duplicate, and paging contract.<br/></param>
    /// <returns>A read-only typed list containing the projected page.<br/></returns>
    internal static IReadOnlyList<TIdentity> ToListTyped<TIdentity>(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options)
        => IterateTyped<TIdentity>(criterion, options).ToList();

    internal static bool Exists(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        _ = deduplication;
        return ExistsNode(criterion);
    }

    internal static long Count(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (deduplication == IdentityDeduplication.Preserve)
        {
            return CountPreserveNode(criterion);
        }

        LibraDexIdentityQueryOptions options = new(
            IdentityResultOrdering.PlanNatural,
            deduplication,
            SkipCount: 0,
            TakeCount: null,
            Bookmark: null);
        long count = 0;
        foreach (object _ in Iterate(criterion, options))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Counts a criteria tree using preserve-duplicate stream semantics without materializing the final identity result list.<br/>
    /// Leaf nodes route through primitive aggregate counts when available, so composed `Or` trees can sum physical metadata/range counts instead of expanding every matching identity.<br/>
    /// Operators that depend on identity membership still materialize only the side needed for membership tests, preserving the current condition-builder meaning exactly.<br/>
    /// </summary>
    /// <param name="criterion">The criteria node to count.</param>
    /// <returns>The number of identities that the preserve-duplicate plan-natural stream would produce.</returns>
    private static long CountPreserveNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => CountPreserveLeaf(criterion),
            LibraDexIdentityCriterionNodeKind.External => CountIterator(IterateExternal(criterion)),
            LibraDexIdentityCriterionNodeKind.And => CountPreserveIntersection(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => checked(CountPreserveNode(RequireLeft(criterion)) + CountPreserveNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Except => CountPreserveDifference(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Not => CountPreserveComplement(criterion, RequireLeft(criterion)),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity counting.")
        };
    }

    /// <summary>
    /// Counts one primitive leaf through the aggregate executor when the index exposes one, otherwise through the leaf iterator.<br/>
    /// This is the physical-count bridge used by composed preserve-duplicate counting, keeping `.Count(where)` aligned with the same primitive request used by retrieval.<br/>
    /// </summary>
    /// <param name="criterion">The leaf criterion to count.</param>
    /// <returns>The preserve-duplicate tuple count for the leaf.</returns>
    private static long CountPreserveLeaf(IIdentityCriterion criterion)
    {
        if (TryExecuteCountAggregateLeaf(criterion, out LibraDexPrimitiveAggregateResult aggregateResult))
        {
            return aggregateResult.Count;
        }

        return CountIterator(IterateLeaf(criterion));
    }

    /// <summary>
    /// Counts an identity intersection by materializing the right membership side and streaming the left side through it.<br/>
    /// Empty right-side membership short-circuits to zero so a large left-side primitive does not need to be read when the intersection cannot match.<br/>
    /// External identity predicates keep their filter-over-source execution model and are counted directly from that filtered stream.<br/>
    /// </summary>
    /// <param name="leftCriterion">The left intersection child.</param>
    /// <param name="rightCriterion">The right intersection child.</param>
    /// <returns>The number of left-side identities whose value is present in the right-side identity set.</returns>
    private static long CountPreserveIntersection(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        if (TryGetExternalFilterPair(leftCriterion, rightCriterion, out IIdentityCriterion? source, out Func<LibraDexExternalIdentityContext, bool>? filter))
        {
            return CountIterator(FilterExternalIdentityIterator(IterateNode(source), filter));
        }

        List<object> rightIdentities = ExecuteNode(rightCriterion);
        if (rightIdentities.Count == 0)
        {
            return 0;
        }

        HashSet<object> rightSet = new(rightIdentities, LibraDexObjectValueComparer.Instance);
        long count = 0;
        foreach (object identity in IterateNode(leftCriterion))
        {
            if (rightSet.Contains(identity))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Counts a preserve-duplicate difference by materializing the excluded identity set and streaming the source side through it.<br/>
    /// An empty excluded side delegates back to the source count path, allowing large source leaves or `Or` trees to keep using aggregate metadata counts.<br/>
    /// </summary>
    /// <param name="leftCriterion">The source criterion.</param>
    /// <param name="rightCriterion">The exclusion criterion.</param>
    /// <returns>The number of source identities not present in the exclusion set.</returns>
    private static long CountPreserveDifference(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        List<object> rightIdentities = ExecuteNode(rightCriterion);
        if (rightIdentities.Count == 0)
        {
            return CountPreserveNode(leftCriterion);
        }

        HashSet<object> rightSet = new(rightIdentities, LibraDexObjectValueComparer.Instance);
        long count = 0;
        foreach (object identity in IterateNode(leftCriterion))
        {
            if (!rightSet.Contains(identity))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Counts a preserve-duplicate complement by streaming the selected identity universe through the excluded identity set.<br/>
    /// When the excluded side is empty, the method counts the universe through the same single-index `All` aggregate that count-all uses when that scope is available.<br/>
    /// </summary>
    /// <param name="criterion">The negated criterion whose leaves define the universe scope.</param>
    /// <param name="childCriterion">The child criterion to exclude from the universe.</param>
    /// <returns>The number of universe identities not present in the child result set.</returns>
    private static long CountPreserveComplement(IIdentityCriterion criterion, IIdentityCriterion childCriterion)
    {
        List<object> excluded = ExecuteNode(childCriterion);
        if (excluded.Count == 0 && TryCountPreserveUniverse(criterion, out long universeCount))
        {
            return universeCount;
        }

        return CountIterator(ComplementIterator(criterion, excluded));
    }

    /// <summary>
    /// Counts the identity universe selected for a negated criterion when the universe can be represented as one primitive `All` aggregate.<br/>
    /// Multi-index universes intentionally fall back to iteration because their grouped identity-universe semantics are broader than one physical tuple count.<br/>
    /// </summary>
    /// <param name="criterion">The criterion whose universe scope should be counted.</param>
    /// <param name="count">Receives the universe count when a single aggregate executor can answer it.</param>
    /// <returns><see langword="true"/> when a single primitive aggregate produced the count.</returns>
    private static bool TryCountPreserveUniverse(IIdentityCriterion criterion, out long count)
    {
        count = 0;
        if (!TryResolveSingleExecutableUniverse(criterion, out IIdentityPrimitiveExecutor? singleIndexExecutor) ||
            singleIndexExecutor is not IIdentityPrimitiveAggregateExecutor aggregateExecutor)
        {
            return false;
        }

        LibraDexIdentityPrimitiveRequest primitiveRequest = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        LibraDexPrimitiveAggregateResult aggregateResult = aggregateExecutor.ExecuteIdentityPrimitiveAggregate(new LibraDexPrimitiveAggregateRequest(
            LibraDexPrimitiveAggregateKind.Count,
            primitiveRequest,
            AggregateScope.Tuples));
        count = aggregateResult.Count;
        return true;
    }

    /// <summary>
    /// Counts a forward-only identity stream without allocating a result list.<br/>
    /// This helper keeps count paths explicit at call sites that still need to consume identity streams for membership semantics.<br/>
    /// </summary>
    /// <param name="identities">The identity stream to consume.</param>
    /// <returns>The number of identities produced by the stream.</returns>
    private static long CountIterator(IEnumerable<object> identities)
    {
        long count = 0;
        foreach (object _ in identities)
        {
            count++;
        }

        return count;
    }

    internal static LibraDexIdentityMutationResult ExecuteMutation(IIdentityCriterionMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return mutation.Kind switch
        {
            LibraDexCriteriaMutationKind.Delete => ExecuteDeleteMutation(mutation.Criterion),
            LibraDexCriteriaMutationKind.SetKey => ExecuteSetKeyMutation(mutation),
            _ => throw new NotSupportedException($"Criteria mutation kind {mutation.Kind} is not supported.")
        };
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetDelete(IIdentityCriterion criterion, IIndex targetIndex)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        return ExecuteTargetDelete(Iterate(
            criterion,
            new LibraDexIdentityQueryOptions(Deduplication: IdentityDeduplication.Distinct)), targetIndex);
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetDelete(IEnumerable<object> identities, IIndex targetIndex)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition delete requires a target index that can capture and mutate exact physical tuples.");
        }

        HashSet<object> matchedIdentities = new(identities, LibraDexObjectValueComparer.Instance);
        if (matchedIdentities.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        IReadOnlyList<LibraDexObjectTuple> targetTuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        long matchedTuples = 0;
        long changed = 0;
        for (int i = 0; i < targetTuples.Count; i++)
        {
            LibraDexObjectTuple tuple = targetTuples[i];
            if (!matchedIdentities.Contains(tuple.Identity))
            {
                continue;
            }

            matchedTuples++;
            if (exactMutator.DeleteExactTuple(tuple.Key!, tuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            matchedTuples,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Scan, RowsScanned: targetTuples.Count, RowsReturned: changed));
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetSetKey(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        bool hasNewKey,
        object? newKey,
        Func<object, object?>? newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        return ExecuteTargetSetKey(
            Iterate(criterion, new LibraDexIdentityQueryOptions(Deduplication: IdentityDeduplication.Distinct)),
            targetIndex,
            hasNewKey,
            newKey,
            newKeyFactory,
            factoryUsesOldKey: false);
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetSetKey(
        IEnumerable<object> identities,
        IIndex targetIndex,
        bool hasNewKey,
        object? newKey,
        Func<object, object?>? newKeyFactory,
        bool factoryUsesOldKey)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition SetKey requires a target index that can capture and mutate exact physical tuples.");
        }

        HashSet<object> matchedIdentities = new(identities, LibraDexObjectValueComparer.Instance);
        if (matchedIdentities.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.SetKey,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        IReadOnlyList<LibraDexObjectTuple> targetTuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        List<LibraDexObjectTuple> oldTuples = new();
        List<LibraDexObjectTuple> replacementTuples = new();
        for (int i = 0; i < targetTuples.Count; i++)
        {
            LibraDexObjectTuple tuple = targetTuples[i];
            if (!matchedIdentities.Contains(tuple.Identity))
            {
                continue;
            }

            object? replacementKey = newKeyFactory is null
                ? hasNewKey ? newKey : throw new InvalidOperationException("Targeted SetKey mutation is missing a replacement key.")
                : newKeyFactory(factoryUsesOldKey ? tuple.Key! : tuple.Identity) ?? throw new InvalidOperationException("Targeted SetKey replacement-key factory returned null.");
            oldTuples.Add(tuple);
            replacementTuples.Add(new LibraDexObjectTuple(replacementKey, tuple.Identity));
        }

        for (int i = 0; i < replacementTuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = oldTuples[i];
            LibraDexObjectTuple replacement = replacementTuples[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (!exactMutator.ContainsExactTuple(replacement.Key, replacement.Identity))
            {
                LibraDexGenericInsertResult insert = targetIndex.Insert(replacement.Key, replacement.Identity);
                if (!insert.Inserted && !exactMutator.ContainsExactTuple(replacement.Key, replacement.Identity))
                {
                    throw new InvalidOperationException("Targeted SetKey could not create a replacement tuple; original tuples were left unchanged.");
                }
            }
        }

        long changed = 0;
        for (int i = 0; i < oldTuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = oldTuples[i];
            LibraDexObjectTuple replacement = replacementTuples[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (targetIndex.Delete(oldTuple.Key, oldTuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.SetKey,
            oldTuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.Scan, RowsScanned: targetTuples.Count, RowsReturned: changed));
    }

    internal static LibraDexIdentityMutationResult ExecuteTargetDeleteAll(IIndex targetIndex)
    {
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is IIdentityPrimitiveMutator primitiveMutator)
        {
            return primitiveMutator.DeleteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        }

        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("DeleteAll requires a target index that can capture and mutate exact physical tuples.");
        }

        IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        long changed = 0;
        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple tuple = tuples[i];
            if (exactMutator.DeleteExactTuple(tuple.Key!, tuple.Identity))
                changed++;
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            tuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
    }

    private static LibraDexIdentityMutationResult ExecuteDeleteMutation(IIdentityCriterion criterion)
    {
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveMutator primitiveMutator)
        {
            throw new NotSupportedException("Criteria-scoped delete is currently connected only for a single primitive leaf whose index implements physical tuple deletion.");
        }

        return primitiveMutator.DeleteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
    }

    private static LibraDexIdentityMutationResult ExecuteSetKeyMutation(IIdentityCriterionMutation mutation)
    {
        IIdentityCriterion criterion = mutation.Criterion;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            criterion.Index is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Criteria-scoped SetKey is currently connected only for a single primitive leaf whose index can capture and mutate exact physical tuples.");
        }

        IReadOnlyList<LibraDexObjectTuple> tuples = tupleExecutor.ExecuteTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
        long changed = 0;
        List<LibraDexObjectTuple> replacements = new(tuples.Count);
        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple tuple = tuples[i];
            object? newKey = mutation.NewKeyFactory is null
                ? mutation.HasNewKey ? mutation.NewKey : throw new InvalidOperationException("SetKey mutation is missing a replacement key.")
                : mutation.NewKeyFactory(tuple.Identity) ?? throw new InvalidOperationException("SetKey replacement-key factory returned null.");

            replacements.Add(new LibraDexObjectTuple(newKey, tuple.Identity));
            if (LibraDexObjectTuple.ValueEquals(tuple.Key, newKey))
            {
                continue;
            }

            if (!exactMutator.ContainsExactTuple(newKey, tuple.Identity))
            {
                LibraDexGenericInsertResult insert = criterion.Index.Insert(newKey, tuple.Identity);
                if (!insert.Inserted && !exactMutator.ContainsExactTuple(newKey, tuple.Identity))
                {
                    throw new InvalidOperationException("SetKey could not create the replacement tuple; the original tuple was left unchanged.");
                }
            }
        }

        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple oldTuple = tuples[i];
            LibraDexObjectTuple replacement = replacements[i];
            if (LibraDexObjectTuple.ValueEquals(oldTuple.Key, replacement.Key))
            {
                continue;
            }

            if (criterion.Index.Delete(oldTuple.Key, oldTuple.Identity))
            {
                changed++;
            }
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.SetKey,
            tuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
    }

    private static List<object> ExecuteNode(IIdentityCriterion criterion)
        => ExecuteNodeWithStats(criterion).Identities;

    private static bool ContainsIdentity(IReadOnlyList<object> identities, object candidate)
    {
        for (int i = 0; i < identities.Count; i++)
        {
            if (LibraDexObjectTuple.ValueEquals(identities[i], candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static LibraDexIdentityNodeExecution ExecuteNodeWithStats(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => ExecuteLeafWithStats(criterion),
            LibraDexIdentityCriterionNodeKind.External => ExecuteExternalWithStats(criterion),
            LibraDexIdentityCriterionNodeKind.And => ExecuteIntersectionWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => ExecuteUnionWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Except => ExecuteDifferenceWithStats(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Not => ExecuteComplementWithStats(criterion, RequireLeft(criterion)),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity execution.")
        };
    }

    private static IEnumerable<object> IterateNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => IterateLeaf(criterion),
            LibraDexIdentityCriterionNodeKind.External => IterateExternal(criterion),
            LibraDexIdentityCriterionNodeKind.And => IterateIntersection(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => UnionIterator(IterateNode(RequireLeft(criterion)), IterateNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Except => ExceptIterator(IterateNode(RequireLeft(criterion)), ExecuteNode(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Not => ComplementIterator(criterion, ExecuteNode(RequireLeft(criterion))),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity iteration.")
        };
    }

    /// <summary>
    /// Streams one criterion tree while retaining the caller's identity type across leaves and logical composition.<br/>
    /// Built-in typed primitive executors therefore avoid per-row boxing for leaf, union, intersection, difference, complement, distinct, ordering, and paging work.<br/>
    /// Runtime-only external sources and legacy executors remain supported through the checked compatibility adapter.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The criterion node to execute.<br/></param>
    /// <returns>A plan-natural typed identity stream for the supplied node.<br/></returns>
    private static IEnumerable<TIdentity> IterateNodeTyped<TIdentity>(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => IterateLeafTyped<TIdentity>(criterion),
            LibraDexIdentityCriterionNodeKind.External => IterateExternalTyped<TIdentity>(criterion),
            LibraDexIdentityCriterionNodeKind.And => IterateIntersectionTyped<TIdentity>(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => UnionTypedIterator(IterateNodeTyped<TIdentity>(RequireLeft(criterion)), IterateNodeTyped<TIdentity>(RequireRight(criterion))),
            LibraDexIdentityCriterionNodeKind.Except => ExceptTypedIterator(IterateNodeTyped<TIdentity>(RequireLeft(criterion)), IterateNodeTyped<TIdentity>(RequireRight(criterion)).ToList()),
            LibraDexIdentityCriterionNodeKind.Not => ComplementTypedIterator<TIdentity>(criterion, IterateNodeTyped<TIdentity>(RequireLeft(criterion)).ToList()),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by typed identity iteration.")
        };
    }

    /// <summary>
    /// Opens one primitive leaf through its typed executor when the concrete index supports the requested identity type.<br/>
    /// Legacy or runtime-shaped indexes fall back to the checked object adapter so compatibility is preserved without weakening type validation.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The executable leaf criterion.<br/></param>
    /// <returns>A typed primitive identity stream.<br/></returns>
    private static IEnumerable<TIdentity> IterateLeafTyped<TIdentity>(IIdentityCriterion criterion)
    {
        if (criterion.CriteriaKind is null)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index.");
        }

        LibraDexIdentityPrimitiveRequest request = CreatePlanNaturalRequest(criterion);
        if (criterion.Index is IIdentityPrimitiveExecutor<TIdentity> typedExecutor)
        {
            return typedExecutor.IterateIdentityPrimitiveTyped(request);
        }

        if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex primitive executor.");
        }

        return CastIdentityIterator<TIdentity>(primitiveExecutor.IterateIdentityPrimitive(request));
    }

    /// <summary>
    /// Adapts one caller-supplied runtime identity source to the typed execution pipeline.<br/>
    /// External sources retain their object-shaped public contract, so this is an intentional compatibility boundary rather than a built-in physical-index boxing path.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="criterion">The external source criterion.<br/></param>
    /// <returns>A checked typed view over the external identities.<br/></returns>
    private static IEnumerable<TIdentity> IterateExternalTyped<TIdentity>(IIdentityCriterion criterion)
        => CastIdentityIterator<TIdentity>(IterateExternal(criterion));

    /// <summary>
    /// Streams a typed intersection and retains typed membership state for the materialized right side.<br/>
    /// External predicates remain supported by constructing their documented object context only at that explicit callback boundary.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type carried by both logical branches.<br/></typeparam>
    /// <param name="leftCriterion">The left intersection child.<br/></param>
    /// <param name="rightCriterion">The right intersection child.<br/></param>
    /// <returns>The typed identities present in both branches.<br/></returns>
    private static IEnumerable<TIdentity> IterateIntersectionTyped<TIdentity>(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        if (TryGetExternalFilterPair(leftCriterion, rightCriterion, out IIdentityCriterion? source, out Func<LibraDexExternalIdentityContext, bool>? filter))
        {
            return FilterExternalIdentityIteratorTyped(IterateNodeTyped<TIdentity>(source), filter);
        }

        return IntersectTypedIterator(IterateNodeTyped<TIdentity>(leftCriterion), IterateNodeTyped<TIdentity>(rightCriterion).ToList());
    }

    /// <summary>
    /// Applies an object-context external predicate to a typed source while preserving typed output.<br/>
    /// The predicate invocation may box a value-type identity because the external callback contract is runtime-shaped; no box is retained after the callback returns.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The typed identity carried by the indexed source.<br/></typeparam>
    /// <param name="source">The typed candidate identity stream.<br/></param>
    /// <param name="filter">The caller-supplied runtime identity predicate.<br/></param>
    /// <returns>The typed identities accepted by the predicate.<br/></returns>
    private static IEnumerable<TIdentity> FilterExternalIdentityIteratorTyped<TIdentity>(
        IEnumerable<TIdentity> source,
        Func<LibraDexExternalIdentityContext, bool> filter)
    {
        long ordinal = 0;
        foreach (TIdentity identity in source)
        {
            LibraDexExternalIdentityContext context = new(identity!, ordinal, ordinal == 0);
            ordinal++;
            if (filter(context))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Tests whether a criteria tree can produce at least one identity without routing through the general projection pipeline.<br/>
    /// Existence is independent of duplicate handling, so this recursive path can short-circuit `Or`, leaf, intersection, difference, and complement shapes directly.<br/>
    /// The method still preserves explicit primitive failures for unsupported leaves instead of treating unsupported execution as an empty result.<br/>
    /// </summary>
    /// <param name="criterion">The criteria tree to test.</param>
    /// <returns><see langword="true"/> when at least one identity can be produced.</returns>
    private static bool ExistsNode(IIdentityCriterion criterion)
    {
        return criterion.NodeKind switch
        {
            LibraDexIdentityCriterionNodeKind.Leaf => TryLeafExists(criterion, out bool leafExists)
                ? leafExists
                : IterateNode(criterion).Take(1).Any(),
            LibraDexIdentityCriterionNodeKind.External => IterateExternal(criterion).Take(1).Any(),
            LibraDexIdentityCriterionNodeKind.And => ExistsIntersection(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Or => ExistsNode(RequireLeft(criterion)) || ExistsNode(RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Except => ExistsDifference(RequireLeft(criterion), RequireRight(criterion)),
            LibraDexIdentityCriterionNodeKind.Not => ComplementIterator(criterion, ExecuteNode(RequireLeft(criterion))).Take(1).Any(),
            _ => throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by identity existence checks.")
        };
    }

    private static bool ExistsIntersection(IIdentityCriterion left, IIdentityCriterion right)
    {
        if (TryGetExternalFilterPair(left, right, out IIdentityCriterion? source, out Func<LibraDexExternalIdentityContext, bool>? filter))
        {
            return FilterExternalIdentityIterator(IterateNode(source), filter).Take(1).Any();
        }

        List<object> rightIdentities = ExecuteNode(right);
        if (rightIdentities.Count == 0)
        {
            return false;
        }

        HashSet<object> rightSet = new(rightIdentities, LibraDexObjectValueComparer.Instance);
        foreach (object identity in IterateNode(left))
        {
            if (rightSet.Contains(identity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExistsDifference(IIdentityCriterion left, IIdentityCriterion right)
    {
        HashSet<object> rightSet = new(ExecuteNode(right), LibraDexObjectValueComparer.Instance);
        foreach (object identity in IterateNode(left))
        {
            if (!rightSet.Contains(identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the internal primitive iterator for a leaf criterion without materializing the leaf result list first.<br/>
    /// This is the condition-builder bridge into LibraDex's cursor-like read mechanics: public criteria keep developer-facing names, while this layer executes one normalized primitive request.<br/>
    /// The caller remains responsible for higher-level composition, de-duplication, ordering fallback, and paging.<br/>
    /// </summary>
    /// <param name="criterion">The leaf criterion to stream.</param>
    /// <returns>A forward-only identity sequence for the leaf.</returns>
    private static IEnumerable<object> IterateLeaf(IIdentityCriterion criterion)
    {
        if (criterion.CriteriaKind is null)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index.");
        }

        if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex primitive executor.");
        }

        return primitiveExecutor.IterateIdentityPrimitive(CreatePlanNaturalRequest(criterion));
    }

    /// <summary>
    /// Creates one primitive request in the selected index's persisted natural traversal order.<br/>
    /// Descending metadata is implemented as native reverse traversal over the ascending physical tree, preserving one storage shape and query-time explicit direction semantics.<br/>
    /// </summary>
    /// <param name="criterion">The materialized primitive leaf whose index owns the natural-order contract.<br/></param>
    /// <param name="takeLimit">The optional physical row limit that may be pushed into the primitive executor.<br/></param>
    /// <returns>A primitive request whose direction matches the index's persisted sort order.<br/></returns>
    private static LibraDexIdentityPrimitiveRequest CreatePlanNaturalRequest(IIdentityCriterion criterion, int? takeLimit = null)
    {
        QueryDirection direction = criterion.Index?.SortOrder == LibraDexIndexSortOrder.Descending
            ? QueryDirection.Descending
            : QueryDirection.Ascending;
        return new LibraDexIdentityPrimitiveRequest(
            criterion.CriteriaKind ?? throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index."),
            criterion.Values,
            takeLimit,
            direction);
    }

    private static List<object> ExecuteLeaf(IIdentityCriterion criterion)
    {
        if (criterion.CriteriaKind is null)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex index.");
        }

        if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            throw new NotSupportedException("The identity criterion leaf is not backed by an executable LibraDex primitive executor.");
        }

        return primitiveExecutor.ExecuteIdentityPrimitive(CreatePlanNaturalRequest(criterion)).ToList();
    }

    private static LibraDexIdentityNodeExecution ExecuteLeafWithStats(IIdentityCriterion criterion)
    {
        List<object> identities = ExecuteLeaf(criterion);
        return new LibraDexIdentityNodeExecution(identities, identities.Count);
    }

    /// <summary>
    /// Materializes an external identity source while keeping standalone external filters explicitly unsupported.<br/>
    /// Source delegates define an identity stream; filter delegates only narrow a sibling stream and must be handled by intersection planning.<br/>
    /// </summary>
    /// <param name="criterion">The external criterion to execute.<br/></param>
    /// <returns>The materialized external-source identities and rows observed.</returns>
    private static LibraDexIdentityNodeExecution ExecuteExternalWithStats(IIdentityCriterion criterion)
    {
        List<object> identities = IterateExternal(criterion).ToList();
        return new LibraDexIdentityNodeExecution(identities, identities.Count);
    }

    /// <summary>
    /// Opens an external identity source supplied by caller code.<br/>
    /// A boolean external filter cannot enumerate by itself, so this method fails explicitly for filter-only external criteria.<br/>
    /// </summary>
    /// <param name="criterion">The external criterion to stream.<br/></param>
    /// <returns>The external-source identity stream.</returns>
    private static IEnumerable<object> IterateExternal(IIdentityCriterion criterion)
    {
        Func<IEnumerable<object>> source = criterion.ExternalIdentitySource
            ?? throw new NotSupportedException("External identity filters must be composed with an indexed sibling using And; use External(() => ids) for standalone or Or-shaped external identity sources.");
        return source();
    }

    /// <summary>
    /// Executes a plan-natural identity projection through streaming primitives and materializes only the requested page.<br/>
    /// Ordered projections still use the full materialization path because sorting requires the complete result set.<br/>
    /// </summary>
    /// <param name="criterion">The materialized identity criterion tree.</param>
    /// <param name="options">The projection options controlling de-duplication and paging.</param>
    /// <returns>The materialized page and observed scan count.</returns>
    private static LibraDexIdentityNodeExecution ExecutePlanNaturalWithStats(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        if (TryExecuteLeafPreserveWithTake(criterion, options, out IReadOnlyList<object>? leafIdentities))
        {
            IReadOnlyList<object> takenIdentities = leafIdentities ?? Array.Empty<object>();
            return new LibraDexIdentityNodeExecution(takenIdentities.ToList(), takenIdentities.Count);
        }

        IEnumerable<object> identities = IterateNode(criterion);
        if (options.Deduplication == IdentityDeduplication.Distinct)
        {
            identities = DistinctIterator(identities);
        }

        return MaterializeStreamingPage(identities, options);
    }

    private static LibraDexIdentityNodeExecution ExecuteIntersectionWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        if (TryGetExternalFilterPair(leftCriterion, rightCriterion, out IIdentityCriterion? source, out Func<LibraDexExternalIdentityContext, bool>? filter))
        {
            LibraDexIdentityNodeExecution sourceExecution = ExecuteNodeWithStats(source);
            List<object> filtered = FilterExternalIdentityIterator(sourceExecution.Identities, filter).ToList();
            return new LibraDexIdentityNodeExecution(filtered, sourceExecution.RowsScanned);
        }

        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Intersect(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    /// <summary>
    /// Streams an intersection, using external identity filtering when either side is an external predicate.<br/>
    /// External predicates are filters over the opposite indexed stream; they do not enumerate identities independently.<br/>
    /// </summary>
    /// <param name="leftCriterion">The left intersection child.<br/></param>
    /// <param name="rightCriterion">The right intersection child.<br/></param>
    /// <returns>A plan-natural identity stream.</returns>
    private static IEnumerable<object> IterateIntersection(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        if (TryGetExternalFilterPair(leftCriterion, rightCriterion, out IIdentityCriterion? source, out Func<LibraDexExternalIdentityContext, bool>? filter))
        {
            return FilterExternalIdentityIterator(IterateNode(source), filter);
        }

        return IntersectIterator(IterateNode(leftCriterion), ExecuteNode(rightCriterion));
    }

    /// <summary>
    /// Resolves whether one side of an intersection is an external identity filter and the other side is the source identity stream.<br/>
    /// This keeps external predicates as candidate filters instead of treating them as independent identity universes.<br/>
    /// </summary>
    /// <param name="leftCriterion">The left intersection child.<br/></param>
    /// <param name="rightCriterion">The right intersection child.<br/></param>
    /// <param name="source">Receives the non-external source criterion when the shape is supported.<br/></param>
    /// <param name="filter">Receives the external identity filter when the shape is supported.<br/></param>
    /// <returns><see langword="true"/> when the intersection can execute as source identities filtered by caller code.<br/></returns>
    private static bool TryGetExternalFilterPair(
        IIdentityCriterion leftCriterion,
        IIdentityCriterion rightCriterion,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IIdentityCriterion? source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Func<LibraDexExternalIdentityContext, bool>? filter)
    {
        if (leftCriterion.NodeKind == LibraDexIdentityCriterionNodeKind.External &&
            leftCriterion.ExternalIdentityFilter is not null)
        {
            source = rightCriterion;
            filter = RequireExternalFilter(leftCriterion);
            return true;
        }

        if (rightCriterion.NodeKind == LibraDexIdentityCriterionNodeKind.External &&
            rightCriterion.ExternalIdentityFilter is not null)
        {
            source = leftCriterion;
            filter = RequireExternalFilter(rightCriterion);
            return true;
        }

        source = null;
        filter = null;
        return false;
    }

    /// <summary>
    /// Applies one external identity filter to an indexed source stream while supplying stable per-candidate context.<br/>
    /// Ordinal is counted over the candidate stream before filtering, so callers can use it for first-call setup and deterministic cache decisions.<br/>
    /// </summary>
    /// <param name="source">The indexed candidate identity stream.<br/></param>
    /// <param name="filter">The caller-supplied identity filter.<br/></param>
    /// <returns>The identities accepted by the external filter.</returns>
    private static IEnumerable<object> FilterExternalIdentityIterator(IEnumerable<object> source, Func<LibraDexExternalIdentityContext, bool> filter)
    {
        long ordinal = 0;
        foreach (object identity in source)
        {
            LibraDexExternalIdentityContext context = new(identity, ordinal, ordinal == 0);
            ordinal++;
            if (filter(context))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Gets the external predicate stored on one external criterion node.<br/>
    /// A missing predicate indicates a malformed criterion tree and is treated as an execution-time invariant failure.<br/>
    /// </summary>
    /// <param name="criterion">The external criterion node.<br/></param>
    /// <returns>The external identity predicate.</returns>
    private static Func<LibraDexExternalIdentityContext, bool> RequireExternalFilter(IIdentityCriterion criterion)
        => criterion.ExternalIdentityFilter ?? throw new InvalidOperationException("External identity criterion is missing its filter delegate.");

    private static LibraDexIdentityNodeExecution ExecuteUnionWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Union(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    private static LibraDexIdentityNodeExecution ExecuteDifferenceWithStats(IIdentityCriterion leftCriterion, IIdentityCriterion rightCriterion)
    {
        LibraDexIdentityNodeExecution left = ExecuteNodeWithStats(leftCriterion);
        LibraDexIdentityNodeExecution right = ExecuteNodeWithStats(rightCriterion);
        return new LibraDexIdentityNodeExecution(
            Except(left.Identities, right.Identities),
            left.RowsScanned + right.RowsScanned);
    }

    private static LibraDexIdentityNodeExecution ExecuteComplementWithStats(IIdentityCriterion criterion, IIdentityCriterion childCriterion)
    {
        LibraDexIdentityNodeExecution child = ExecuteNodeWithStats(childCriterion);
        LibraDexIdentityUniverse universe = MaterializeUniverse(criterion);
        return new LibraDexIdentityNodeExecution(
            Except(universe.Identities, child.Identities),
            child.RowsScanned + universe.Identities.Count);
    }

    private static bool TryExecuteLeafWithTake(IIdentityCriterion criterion, int takeLimit, out IReadOnlyList<object>? identities)
    {
        identities = null;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        identities = primitiveExecutor.ExecuteIdentityPrimitive(CreatePlanNaturalRequest(criterion, takeLimit));
        return true;
    }

    /// <summary>
    /// Executes a single preserve-duplicates leaf with the primitive take limit when paging makes that safe.<br/>
    /// Distinct projections cannot use this shortcut because raw primitive rows may collapse to fewer identities after de-duplication.<br/>
    /// </summary>
    /// <param name="criterion">The candidate leaf criterion.</param>
    /// <param name="options">The projection options containing skip, take, and bookmark state.</param>
    /// <param name="identities">Receives the requested paged identities when the shortcut applies.</param>
    /// <returns><see langword="true"/> when the shortcut handled the request.</returns>
    private static bool TryExecuteLeafPreserveWithTake(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options,
        out IReadOnlyList<object>? identities)
    {
        identities = null;
        if (options.Deduplication != IdentityDeduplication.Preserve ||
            options.TakeCount is not { } takeCount)
        {
            return false;
        }

        long start = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            start = Math.Max(start, bookmark.ResultsConsumed);
        }

        long requested = start + takeCount;
        if (requested > int.MaxValue)
        {
            return false;
        }

        if (!TryExecuteLeafWithTake(criterion, checked((int)requested), out IReadOnlyList<object>? rawIdentities))
        {
            return false;
        }

        identities = ApplyPaging(rawIdentities!.ToList(), options);
        return true;
    }

    /// <summary>
    /// Tests a single executable leaf through the primitive iterator with a one-row take limit.<br/>
    /// This preserves the no-materialization intent of `Exists` for exact, range, and membership leaves while keeping unsupported primitive errors explicit.<br/>
    /// The method returns <see langword="false"/> only when the criterion is not a primitive leaf; it does not swallow execution failures from a valid leaf.<br/>
    /// </summary>
    /// <param name="criterion">The criterion to test.</param>
    /// <param name="exists">Receives whether the primitive leaf produced at least one identity.</param>
    /// <returns><see langword="true"/> when the criterion was an executable primitive leaf.</returns>
    private static bool TryLeafExists(IIdentityCriterion criterion, out bool exists)
    {
        exists = false;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        using IEnumerator<object> enumerator = primitiveExecutor.IterateIdentityPrimitive(
            CreatePlanNaturalRequest(criterion, takeLimit: 1)).GetEnumerator();
        exists = enumerator.MoveNext();
        return true;
    }

    /// <summary>
    /// Attempts to execute a single primitive leaf through the aggregate executor path.<br/>
    /// This keeps public `.Count(where)` on the same condition-materialized primitive semantics while allowing indexes to specialize count, distinct count, and later aggregate operations independently from identity iteration.<br/>
    /// </summary>
    /// <param name="criterion">The criterion to aggregate.</param>
    /// <param name="result">Receives the aggregate result when the criterion is an executable aggregate leaf.</param>
    /// <returns><see langword="true"/> when the leaf was aggregated directly.</returns>
    private static bool TryExecuteCountAggregateLeaf(IIdentityCriterion criterion, out LibraDexPrimitiveAggregateResult result)
    {
        result = default;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveAggregateExecutor aggregateExecutor)
        {
            return false;
        }

        LibraDexIdentityPrimitiveRequest primitiveRequest = new(criterion.CriteriaKind.Value, criterion.Values);
        result = aggregateExecutor.ExecuteIdentityPrimitiveAggregate(new LibraDexPrimitiveAggregateRequest(
            LibraDexPrimitiveAggregateKind.Count,
            primitiveRequest,
            AggregateScope.Tuples));
        return true;
    }

    private static List<object> Complement(IIdentityCriterion criterion, IReadOnlyList<object> excluded)
        => Except(MaterializeUniverse(criterion).Identities, excluded);

    private static LibraDexIdentityUniverse MaterializeUniverse(IIdentityCriterion criterion)
    {
        IIdentityCriterion universeRoot = criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Not && criterion.Left is not null
            ? criterion.Left
            : criterion;
        if (TryResolveSingleExecutableUniverse(universeRoot, out IIdentityPrimitiveExecutor? singleIndexExecutor))
        {
            return new LibraDexIdentityUniverse(
                singleIndexExecutor.IterateIdentityPrimitive(
                    new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())).ToList());
        }

        IIdentityCriterion leaf = FindFirstLeaf(criterion);
        if (leaf.Index is not IIdentityPrimitiveExecutor executor)
        {
            throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
        }

        return new LibraDexIdentityUniverse(executor.IterateIdentityUniverse().ToList());
    }

    /// <summary>
    /// Resolves whether a negated criterion subtree is scoped to one executable physical index.<br/>
    /// Single-index exclusions such as `NotInSet`, `YearNotIn`, and same-index disjunction complements must enumerate that index's own `All` primitive rather than the broader identity-group universe.<br/>
    /// Multi-index negations intentionally return <see langword="false"/> so the existing identity-group universe path remains available for criteria whose meaning crosses indexes.<br/>
    /// </summary>
    /// <param name="criterion">The criterion subtree that defines the complement scope.</param>
    /// <param name="executor">The single executable index when all leaves share one executor; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every executable leaf in the subtree uses the same index executor.</returns>
    private static bool TryResolveSingleExecutableUniverse(IIdentityCriterion criterion, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IIdentityPrimitiveExecutor? executor)
    {
        executor = null;
        return TryResolveSingleExecutableUniverseCore(criterion, ref executor) && executor is not null;
    }

    /// <summary>
    /// Walks a criterion tree and verifies that all executable leaves share one index executor.<br/>
    /// The method uses reference identity because condition materialization binds leaves to concrete opened index handles; projection-specific handles are treated as distinct execution scopes unless a later planner deliberately groups them.<br/>
    /// </summary>
    /// <param name="criterion">The current criterion node.</param>
    /// <param name="executor">The first executable leaf encountered, reused as the equality anchor.</param>
    /// <returns><see langword="true"/> when no conflicting executable leaf is found.</returns>
    private static bool TryResolveSingleExecutableUniverseCore(IIdentityCriterion criterion, ref IIdentityPrimitiveExecutor? executor)
    {
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf)
        {
            if (criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
            {
                return false;
            }

            if (executor is null)
            {
                executor = primitiveExecutor;
                return true;
            }

            return ReferenceEquals(executor, primitiveExecutor);
        }

        if (criterion.Left is not null && !TryResolveSingleExecutableUniverseCore(criterion.Left, ref executor))
        {
            return false;
        }

        return criterion.Right is null || TryResolveSingleExecutableUniverseCore(criterion.Right, ref executor);
    }

    /// <summary>
    /// Streams a complement by reading the best identity universe available from the first executable leaf's primitive executor and excluding the child result set.<br/>
    /// Grouped catalog indexes provide a de-duplicated union of identities across the identity group; ungrouped handles fall back to the first leaf's own `All` primitive.<br/>
    /// This avoids materializing the entire universe for plan-natural negated iteration while keeping the universe source behind the internal primitive executor contract.<br/>
    /// </summary>
    /// <param name="criterion">The negated criterion whose first leaf supplies the current universe approximation.</param>
    /// <param name="excluded">The materialized child identities to exclude.</param>
    /// <returns>A forward-only sequence of identities outside the child criterion.</returns>
    private static IEnumerable<object> ComplementIterator(IIdentityCriterion criterion, IReadOnlyList<object> excluded)
    {
        IEnumerable<object> universe;
        if (TryResolveSingleExecutableUniverse(criterion, out IIdentityPrimitiveExecutor? singleIndexExecutor))
        {
            universe = singleIndexExecutor.IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        }
        else
        {
            IIdentityCriterion leaf = FindFirstLeaf(criterion);
            if (leaf.Index is not IIdentityPrimitiveExecutor executor)
            {
                throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
            }

            universe = executor.IterateIdentityUniverse();
        }

        HashSet<object> excludedSet = new(excluded, LibraDexObjectValueComparer.Instance);
        foreach (object identity in universe)
        {
            if (!excludedSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IIdentityCriterion FindFirstLeaf(IIdentityCriterion criterion)
    {
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf)
        {
            return criterion;
        }

        if (criterion.Left is not null)
        {
            return FindFirstLeaf(criterion.Left);
        }

        if (criterion.Right is not null)
        {
            return FindFirstLeaf(criterion.Right);
        }

        throw new InvalidOperationException("Identity criterion tree does not contain an index-backed leaf.");
    }

    private static List<object> Union(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        List<object> result = new(left.Count + right.Count);
        result.AddRange(left);
        result.AddRange(right);
        return result;
    }

    private static List<object> Intersect(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right, LibraDexObjectValueComparer.Instance);
        List<object> result = new();
        for (int i = 0; i < left.Count; i++)
        {
            if (rightSet.Contains(left[i]))
            {
                result.Add(left[i]);
            }
        }

        return result;
    }

    private static List<object> Except(IReadOnlyList<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right, LibraDexObjectValueComparer.Instance);
        List<object> result = new();
        for (int i = 0; i < left.Count; i++)
        {
            if (!rightSet.Contains(left[i]))
            {
                result.Add(left[i]);
            }
        }

        return result;
    }

    private static IEnumerable<object> UnionIterator(IEnumerable<object> left, IEnumerable<object> right)
    {
        foreach (object identity in left)
        {
            yield return identity;
        }

        foreach (object identity in right)
        {
            yield return identity;
        }
    }

    private static IEnumerable<object> IntersectIterator(IEnumerable<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right, LibraDexObjectValueComparer.Instance);
        foreach (object identity in left)
        {
            if (rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> ExceptIterator(IEnumerable<object> left, IReadOnlyList<object> right)
    {
        HashSet<object> rightSet = new(right, LibraDexObjectValueComparer.Instance);
        foreach (object identity in left)
        {
            if (!rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Concatenates two typed logical branches in plan-natural order.<br/>
    /// Duplicate policy remains the responsibility of the projection stage so `Preserve` and `Distinct` retain their existing semantics.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type carried by both branches.<br/></typeparam>
    /// <param name="left">The left branch emitted first.<br/></param>
    /// <param name="right">The right branch emitted second.<br/></param>
    /// <returns>The concatenated typed identity stream.<br/></returns>
    private static IEnumerable<TIdentity> UnionTypedIterator<TIdentity>(
        IEnumerable<TIdentity> left,
        IEnumerable<TIdentity> right)
    {
        foreach (TIdentity identity in left)
        {
            yield return identity;
        }

        foreach (TIdentity identity in right)
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Streams typed left-side identities present in the materialized right-side membership set.<br/>
    /// The set uses LibraDex identity equality so binary and composite identity types retain structural semantics without forcing scalar identities through <see cref="object"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type carried by both branches.<br/></typeparam>
    /// <param name="left">The left branch whose order is retained.<br/></param>
    /// <param name="right">The materialized right branch used for membership checks.<br/></param>
    /// <returns>The typed intersection stream.<br/></returns>
    private static IEnumerable<TIdentity> IntersectTypedIterator<TIdentity>(
        IEnumerable<TIdentity> left,
        IReadOnlyList<TIdentity> right)
    {
        HashSet<TIdentity> rightSet = new(right, LibraDexKeyEquality<TIdentity>.Comparer);
        foreach (TIdentity identity in left)
        {
            if (rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Streams typed left-side identities absent from the materialized right-side membership set.<br/>
    /// First-appearance order and repeated left-side identities are preserved until the projection's selected dedupe policy is applied.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type carried by both branches.<br/></typeparam>
    /// <param name="left">The left branch whose order is retained.<br/></param>
    /// <param name="right">The materialized right branch used for exclusion checks.<br/></param>
    /// <returns>The typed difference stream.<br/></returns>
    private static IEnumerable<TIdentity> ExceptTypedIterator<TIdentity>(
        IEnumerable<TIdentity> left,
        IReadOnlyList<TIdentity> right)
    {
        HashSet<TIdentity> rightSet = new(right, LibraDexKeyEquality<TIdentity>.Comparer);
        foreach (TIdentity identity in left)
        {
            if (!rightSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Streams the typed identity universe for a negated criterion and excludes the typed child result set.<br/>
    /// Single-index negation uses that index's own `All` primitive, while cross-index negation asks the first executable index for its group-aware universe.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the projection.<br/></typeparam>
    /// <param name="criterion">The negated criterion that defines universe scope.<br/></param>
    /// <param name="excluded">The materialized typed child identities to exclude.<br/></param>
    /// <returns>A typed stream containing identities outside the child criterion.<br/></returns>
    private static IEnumerable<TIdentity> ComplementTypedIterator<TIdentity>(
        IIdentityCriterion criterion,
        IReadOnlyList<TIdentity> excluded)
    {
        IEnumerable<TIdentity> universe;
        if (TryResolveSingleExecutableUniverse(criterion, out IIdentityPrimitiveExecutor? singleIndexExecutor))
        {
            LibraDexIdentityPrimitiveRequest allRequest = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
            universe = singleIndexExecutor is IIdentityPrimitiveExecutor<TIdentity> typedSingleIndexExecutor
                ? typedSingleIndexExecutor.IterateIdentityPrimitiveTyped(allRequest)
                : CastIdentityIterator<TIdentity>(singleIndexExecutor.IterateIdentityPrimitive(allRequest));
        }
        else
        {
            IIdentityCriterion leaf = FindFirstLeaf(criterion);
            if (leaf.Index is IIdentityPrimitiveExecutor<TIdentity> typedExecutor)
            {
                universe = typedExecutor.IterateIdentityUniverseTyped();
            }
            else if (leaf.Index is IIdentityPrimitiveExecutor executor)
            {
                universe = CastIdentityIterator<TIdentity>(executor.IterateIdentityUniverse());
            }
            else
            {
                throw new NotSupportedException("Negated identity criteria require at least one executable index to provide the identity universe.");
            }
        }

        HashSet<TIdentity> excludedSet = new(excluded, LibraDexKeyEquality<TIdentity>.Comparer);
        foreach (TIdentity identity in universe)
        {
            if (!excludedSet.Contains(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> DistinctIterator(IEnumerable<object> identities)
    {
        HashSet<object> seen = new(LibraDexObjectValueComparer.Instance);
        foreach (object identity in identities)
        {
            if (seen.Add(identity))
            {
                yield return identity;
            }
        }
    }

    /// <summary>
    /// Casts an internal object identity stream to the caller's requested identity type without materializing an intermediate collection.<br/>
    /// The reported ordinal identifies the exact malformed source position when a custom or mixed identity source violates its declared projection type.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="identities">The internal identity stream to cast.<br/></param>
    /// <returns>A lazy typed view over the supplied identities.<br/></returns>
    /// <exception cref="InvalidCastException">Thrown when an identity is null or has a different runtime type.<br/></exception>
    private static IEnumerable<TIdentity> CastIdentityIterator<TIdentity>(IEnumerable<object> identities)
    {
        int ordinal = 0;
        foreach (object? identityObject in identities)
        {
            if (identityObject is not TIdentity identity)
            {
                string actualType = identityObject?.GetType().FullName ?? "null";
                throw new InvalidCastException($"Identity at ordinal {ordinal} is {actualType}, not {typeof(TIdentity).FullName}.");
            }

            ordinal++;
            yield return identity;
        }
    }

    /// <summary>
    /// Removes repeated identities while preserving the first-appearance order of the composed plan-natural stream.<br/>
    /// The retained set stores <typeparamref name="TIdentity"/> values directly, eliminating long-lived boxing for value-type Abraxas identities while preserving LibraDex key equality semantics.<br/>
    /// This helper is selected only when logical composition can genuinely repeat identities; proven single-key primitive streams bypass it before enumeration begins.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type expected by the caller.<br/></typeparam>
    /// <param name="identities">The internal composed identity stream.<br/></param>
    /// <returns>A lazy typed stream containing the first occurrence of each identity.<br/></returns>
    private static IEnumerable<TIdentity> DistinctTypedIterator<TIdentity>(IEnumerable<TIdentity> identities)
    {
        HashSet<TIdentity> seen = new(LibraDexKeyEquality<TIdentity>.Comparer);
        foreach (TIdentity identity in identities)
        {
            if (seen.Add(identity))
            {
                yield return identity;
            }
        }
    }

    private static IEnumerable<object> ApplyStreamingPaging(IEnumerable<object> identities, LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.ResultsConsumed);
        }

        long returned = 0;
        foreach (object identity in identities)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            if (options.TakeCount is not null && returned >= options.TakeCount.Value)
            {
                yield break;
            }

            returned++;
            yield return identity;
        }
    }

    /// <summary>
    /// Applies skip, bookmark, and take controls to a typed plan-natural identity stream.<br/>
    /// Callers place this stage after distinct processing so every paging position refers to a logical result rather than a duplicated physical occurrence.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity type carried by the stream.<br/></typeparam>
    /// <param name="identities">The typed identities to page.<br/></param>
    /// <param name="options">The validated projection options.<br/></param>
    /// <returns>A lazy typed stream containing only the requested page.<br/></returns>
    private static IEnumerable<TIdentity> ApplyStreamingPagingTyped<TIdentity>(
        IEnumerable<TIdentity> identities,
        LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.ResultsConsumed);
        }

        long returned = 0;
        foreach (TIdentity identity in identities)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            if (options.TakeCount is not null && returned >= options.TakeCount.Value)
            {
                yield break;
            }

            returned++;
            yield return identity;
        }
    }

    /// <summary>
    /// Materializes a streamed identity sequence only until the requested page has been satisfied.<br/>
    /// This keeps `take` and bookmark projections from forcing full result materialization when natural plan order is acceptable.<br/>
    /// </summary>
    /// <param name="identities">The streamed identity sequence.</param>
    /// <param name="options">The projection options controlling skip, take, and bookmark state.</param>
    /// <returns>The materialized page and number of streamed identities consumed.</returns>
    private static LibraDexIdentityNodeExecution MaterializeStreamingPage(IEnumerable<object> identities, LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.ResultsConsumed);
        }

        if (options.TakeCount == 0)
        {
            return new LibraDexIdentityNodeExecution(new List<object>(), RowsScanned: 0);
        }

        int capacity = options.TakeCount is { } takeCount ? takeCount : 0;
        List<object> result = capacity > 0 ? new List<object>(capacity) : new List<object>();
        long scanned = 0;
        foreach (object identity in identities)
        {
            scanned++;
            if (skip > 0)
            {
                skip--;
                continue;
            }

            result.Add(identity);
            if (options.TakeCount is { } limit && result.Count >= limit)
            {
                break;
            }
        }

        return new LibraDexIdentityNodeExecution(result, scanned);
    }

    private static List<object> ApplyDeduplication(List<object> identities, IdentityDeduplication deduplication)
    {
        if (deduplication == IdentityDeduplication.Preserve)
        {
            return identities;
        }

        HashSet<object> seen = new(LibraDexObjectValueComparer.Instance);
        List<object> result = new(identities.Count);
        for (int i = 0; i < identities.Count; i++)
        {
            if (seen.Add(identities[i]))
            {
                result.Add(identities[i]);
            }
        }

        return result;
    }

    private static void ApplyOrdering(List<object> identities, IdentityResultOrdering ordering)
    {
        if (ordering == IdentityResultOrdering.PlanNatural)
        {
            return;
        }

        identities.Sort(CompareIdentityObjects);
        if (ordering == IdentityResultOrdering.IdentityDescending)
        {
            identities.Reverse();
        }
    }

    private static int CompareIdentityObjects(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (left is IComparable comparable)
        {
            return comparable.CompareTo(right);
        }

        throw new NotSupportedException($"Identity type {left.GetType().FullName} does not support ordering.");
    }

    private static IReadOnlyList<object> ApplyPaging(List<object> identities, LibraDexIdentityQueryOptions options)
    {
        int start = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            start = Math.Max(start, checked((int)Math.Min(bookmark.ResultsConsumed, int.MaxValue)));
        }

        if (start >= identities.Count)
        {
            return Array.Empty<object>();
        }

        int count = options.TakeCount ?? identities.Count - start;
        count = Math.Min(count, identities.Count - start);
        return identities.GetRange(start, count);
    }

    private static LibraDexIdentityExecutionPlan PlanNode(
        IIdentityCriterion criterion,
        LibraDexIdentityQueryOptions options,
        PlanAccumulator accumulator)
    {
        switch (criterion.NodeKind)
        {
            case LibraDexIdentityCriterionNodeKind.Leaf:
                if (criterion.Index is not null)
                {
                    accumulator.Add(criterion.Index);
                }

                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    LibraDexIdentityPlanKind.SingleIndex,
                    LibraDexIdentityPlanMaterialization.None,
                    requiresDistinct: false,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: false,
                    leafCount: 1,
                    depth: 1,
                    criterion.Index is null ? Array.Empty<IIndex>() : new[] { criterion.Index },
                    Array.Empty<LibraDexIdentityExecutionPlan>());

            case LibraDexIdentityCriterionNodeKind.External:
                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    criterion.ExternalIdentitySource is not null
                        ? LibraDexIdentityPlanKind.ExternalSource
                        : LibraDexIdentityPlanKind.ExternalFilter,
                    LibraDexIdentityPlanMaterialization.IdentitySet,
                    requiresDistinct: false,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: false,
                    leafCount: 0,
                    depth: 1,
                    Array.Empty<IIndex>(),
                    Array.Empty<LibraDexIdentityExecutionPlan>());

            case LibraDexIdentityCriterionNodeKind.Not:
            {
                LibraDexIdentityExecutionPlan child = PlanNode(RequireLeft(criterion), options, accumulator);
                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    LibraDexIdentityPlanKind.Complement,
                    LibraDexIdentityPlanMaterialization.IdentitySet,
                    requiresDistinct: true,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: true,
                    child.LeafCount,
                    child.Depth + 1,
                    child.Indexes,
                    new[] { child });
            }

            case LibraDexIdentityCriterionNodeKind.And:
            case LibraDexIdentityCriterionNodeKind.Or:
            case LibraDexIdentityCriterionNodeKind.Except:
            {
                LibraDexIdentityExecutionPlan left = PlanNode(RequireLeft(criterion), options, accumulator);
                LibraDexIdentityExecutionPlan right = PlanNode(RequireRight(criterion), options, accumulator);
                LibraDexIdentityPlanKind kind = criterion.NodeKind switch
                {
                    LibraDexIdentityCriterionNodeKind.And => LibraDexIdentityPlanKind.Intersection,
                    LibraDexIdentityCriterionNodeKind.Or => LibraDexIdentityPlanKind.Union,
                    LibraDexIdentityCriterionNodeKind.Except => LibraDexIdentityPlanKind.Difference,
                    _ => LibraDexIdentityPlanKind.Composite
                };
                LibraDexIdentityPlanMaterialization materialization = SelectCompositeMaterialization(kind, left, right);
                return new LibraDexIdentityExecutionPlan(
                    criterion,
                    options,
                    kind,
                    materialization,
                    requiresDistinct: kind == LibraDexIdentityPlanKind.Union || left.RequiresDistinct || right.RequiresDistinct,
                    requiresOrdering: false,
                    requiresPaging: false,
                    containsNegation: left.ContainsNegation || right.ContainsNegation,
                    left.LeafCount + right.LeafCount,
                    Math.Max(left.Depth, right.Depth) + 1,
                    MergeIndexes(left.Indexes, right.Indexes),
                    new[] { left, right });
            }

            default:
                throw new NotSupportedException($"Identity criterion node {criterion.NodeKind} is not supported by the planner.");
        }
    }

    private static LibraDexIdentityPlanMaterialization SelectCompositeMaterialization(
        LibraDexIdentityPlanKind kind,
        LibraDexIdentityExecutionPlan left,
        LibraDexIdentityExecutionPlan right)
    {
        if (left.ContainsNegation || right.ContainsNegation)
        {
            return LibraDexIdentityPlanMaterialization.IdentitySet;
        }

        if (kind == LibraDexIdentityPlanKind.Union)
        {
            return LibraDexIdentityPlanMaterialization.IdentitySet;
        }

        return MergeMaterialization(
            LibraDexIdentityPlanMaterialization.StreamingMerge,
            MergeMaterialization(left.Materialization, right.Materialization));
    }

    private static LibraDexIdentityPlanMaterialization MergeMaterialization(
        LibraDexIdentityPlanMaterialization left,
        LibraDexIdentityPlanMaterialization right)
    {
        return (LibraDexIdentityPlanMaterialization)Math.Max((int)left, (int)right);
    }

    private static IIdentityCriterion RequireLeft(IIdentityCriterion criterion)
        => criterion.Left ?? throw new InvalidOperationException("Identity criterion is missing its left child.");

    private static IIdentityCriterion RequireRight(IIdentityCriterion criterion)
        => criterion.Right ?? throw new InvalidOperationException("Identity criterion is missing its right child.");

    private static IReadOnlyList<IIndex> MergeIndexes(IReadOnlyList<IIndex> left, IReadOnlyList<IIndex> right)
    {
        List<IIndex> indexes = new(left.Count + right.Count);
        AddDistinct(indexes, left);
        AddDistinct(indexes, right);
        return indexes;
    }

    private static void AddDistinct(List<IIndex> target, IReadOnlyList<IIndex> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (!target.Contains(source[i]))
            {
                target.Add(source[i]);
            }
        }
    }

    private sealed class PlanAccumulator
    {
        private readonly List<IIndex> indexes = new();

        internal IReadOnlyList<IIndex> Indexes => indexes;

        internal void Add(IIndex index)
        {
            if (!indexes.Contains(index))
            {
                indexes.Add(index);
            }
        }
    }

    private readonly record struct LibraDexIdentityNodeExecution(List<object> Identities, long RowsScanned);

    private readonly record struct LibraDexIdentityUniverse(List<object> Identities);
}

internal sealed class LibraDexIdentityCriterionMutationBuilder : IIdentityCriterionMutationBuilder
{
    private readonly IIdentityCriterion criterion;

    internal LibraDexIdentityCriterionMutationBuilder(IIdentityCriterion criterion)
    {
        this.criterion = criterion;
    }

    public IIdentityCriterionMutation Delete()
        => new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.Delete, hasNewKey: false, newKey: null, newKeyFactory: null);

    public IIdentityCriterionMutation SetKey(object? newKey)
        => new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.SetKey, hasNewKey: true, newKey, newKeyFactory: null);

    public IIdentityCriterionMutation SetKeyUsing(Func<object, object?> newKeyFactory)
    {
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        return new LibraDexIdentityCriterionMutation(criterion, LibraDexCriteriaMutationKind.SetKey, hasNewKey: false, newKey: null, newKeyFactory);
    }
}

internal sealed class LibraDexIdentityCriterionMutation : IIdentityCriterionMutation
{
    internal LibraDexIdentityCriterionMutation(
        IIdentityCriterion criterion,
        LibraDexCriteriaMutationKind kind,
        bool hasNewKey,
        object? newKey,
        Func<object, object?>? newKeyFactory)
    {
        Criterion = criterion ?? throw new ArgumentNullException(nameof(criterion));
        Kind = kind;
        HasNewKey = hasNewKey;
        NewKey = newKey;
        NewKeyFactory = newKeyFactory;
    }

    public IIdentityCriterion Criterion { get; }

    public LibraDexCriteriaMutationKind Kind { get; }

    public bool HasNewKey { get; }

    public object? NewKey { get; }

    public Func<object, object?>? NewKeyFactory { get; }

    public LibraDexIdentityMutationResult Execute()
        => LibraDexIdentityExecutionPlanner.ExecuteMutation(this);
}

/// <summary>
/// Opens one independent physical identity partition for an exact-worker consumer.<br/>
/// The returned enumerator must acquire and retain its own coherent read on the calling worker thread; callers must not create it on one thread and consume it on another.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type emitted by the partition.<br/></typeparam>
internal interface IIdentityPrimitivePartition<TIdentity>
{
    /// <summary>
    /// Opens the worker-owned enumerator for this disjoint physical partition.<br/>
    /// Opening is intentionally explicit so a coordinator can prove that every requested worker owns a live coherent reader before releasing the planning-to-execution transition read.<br/>
    /// </summary>
    /// <returns>An enumerator that must be consumed and disposed on the calling worker thread.<br/></returns>
    IEnumerator<TIdentity> OpenEnumerator();

    /// <summary>
    /// Captures bounded work counters owned by this one physical partition.<br/>
    /// The coordinator reads the snapshot only after the worker has disposed its enumerator, so hot traversal requires no atomic counter or shared collection.<br/>
    /// </summary>
    /// <returns>Work descriptors claimed, physical source rows examined, and matching identities emitted by this partition.<br/></returns>
    IdentityPrimitivePartitionWork CaptureWork();
}

/// <summary>
/// Describes physical work completed by one exact-worker identity partition without retaining keys, identities, or payloads.<br/>
/// </summary>
/// <param name="WorkItemsClaimed">Topology targets or fixed physical slices claimed by the worker.<br/></param>
/// <param name="SourceItemsExamined">Physical keys, identities, or records examined before residual selection.<br/></param>
/// <param name="MatchesEmitted">Matching identities emitted by the partition enumerator.<br/></param>
internal readonly record struct IdentityPrimitivePartitionWork(
    int WorkItemsClaimed,
    long SourceItemsExamined,
    long MatchesEmitted);

/// <summary>
/// Creates an exact number of independent physical partitions for one normalized primitive request.<br/>
/// Implementations return <see langword="false"/> when the requested primitive or current topology cannot provide that exact physical worker count without overlapping keys, eager result materialization, or a shared producer cursor.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type emitted by each partition.<br/></typeparam>
internal interface IIdentityPrimitivePartitioner<TIdentity>
{
    /// <summary>
    /// Attempts to create an exact-worker partition set for one primitive request.<br/>
    /// A successful set owns a transition read acquired on this calling thread; the same thread must release or dispose it after all worker readers have opened.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request whose complete semantics each partition must preserve.<br/></param>
    /// <param name="workerCount">The exact number of independent physical workers requested by the developer.<br/></param>
    /// <param name="partitions">The exact partition set when successful; otherwise <see langword="null"/>.<br/></param>
    /// <param name="unsupportedReason">Receives the reason the request or current topology cannot supply the requested partitions.<br/></param>
    /// <returns><see langword="true"/> only when exactly <paramref name="workerCount"/> disjoint partitions were created.<br/></returns>
    bool TryCreateIdentityPrimitivePartitions(
        LibraDexIdentityPrimitiveRequest request,
        int workerCount,
        out LibraDexIdentityPrimitivePartitionSet<TIdentity>? partitions,
        out string? unsupportedReason);
}

/// <summary>
/// Owns one exact collection of disjoint physical identity partitions plus the coherent read that bridges topology planning to worker-reader acquisition.<br/>
/// The transition read is released explicitly after every worker reports that its own reader is open; disposal remains the failure-path guarantee.<br/>
/// </summary>
/// <typeparam name="TIdentity">The scalar identity type emitted by the partitions.<br/></typeparam>
internal sealed class LibraDexIdentityPrimitivePartitionSet<TIdentity> : IDisposable
{
    private IDisposable? transitionRead;

    /// <summary>
    /// Initializes one exact partition set.<br/>
    /// </summary>
    /// <param name="partitions">The non-empty exact partition collection.<br/></param>
    /// <param name="transitionRead">The coherent read acquired by the planner on the current coordinator thread.<br/></param>
    /// <param name="topologyRoutersRead">Router pages read while deriving the bounded partition frontier, or zero for a non-router partition shape.<br/></param>
    /// <param name="topologyTargetsSelected">Disjoint physical continuation targets assigned across the workers, or zero when the shape does not expose target-level work.<br/></param>
    internal LibraDexIdentityPrimitivePartitionSet(
        IReadOnlyList<IIdentityPrimitivePartition<TIdentity>> partitions,
        IDisposable transitionRead,
        int topologyRoutersRead = 0,
        int topologyTargetsSelected = 0)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(transitionRead);
        if (partitions.Count == 0)
            throw new ArgumentException("An identity primitive partition set requires at least one physical partition.", nameof(partitions));

        Partitions = partitions;
        this.transitionRead = transitionRead;
        TopologyRoutersRead = Math.Max(0, topologyRoutersRead);
        TopologyTargetsSelected = Math.Max(0, topologyTargetsSelected);
    }

    /// <summary>Gets the exact disjoint physical partitions in logical key-range order.<br/></summary>
    internal IReadOnlyList<IIdentityPrimitivePartition<TIdentity>> Partitions { get; }

    /// <summary>Gets the bounded router-page reads used to derive this partition set.<br/></summary>
    internal int TopologyRoutersRead { get; }

    /// <summary>Gets the disjoint continuation-target count distributed across this partition set.<br/></summary>
    internal int TopologyTargetsSelected { get; }

    /// <summary>
    /// Releases the planning-to-worker transition read after every worker has opened its own coherent reader.<br/>
    /// The call must occur on the thread that created this set because DataKernel coherent-read ownership is thread-affine.<br/>
    /// </summary>
    internal void ReleaseTransitionRead()
    {
        IDisposable? owned = Interlocked.Exchange(ref transitionRead, null);
        owned?.Dispose();
    }

    /// <summary>
    /// Releases the transition read on failure or early disposal.<br/>
    /// Worker-owned enumerators are not owned by this object and remain the responsibility of their worker loops.<br/>
    /// </summary>
    public void Dispose()
    {
        ReleaseTransitionRead();
    }
}
