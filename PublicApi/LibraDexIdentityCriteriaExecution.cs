namespace LibraDex;

internal readonly record struct LibraDexIdentityPrimitiveRequest(
    LibraDexCriteriaKind CriteriaKind,
    IReadOnlyList<object?> Values,
    int? TakeLimit = null,
    QueryDirection Direction = QueryDirection.Ascending);

/// <summary>
/// Describes one inclusive key extent requested by condition-driven multi-range retrieval.<br/>
/// The bounds remain runtime objects at this layer because non-generic condition planning resolves index handles before generic physical readers are invoked.<br/>
/// </summary>
/// <param name="LowerKey">The inclusive lower key.</param>
/// <param name="UpperKey">The inclusive upper key.</param>
internal readonly record struct LibraDexIdentityKeyRange(object LowerKey, object UpperKey);

internal interface IIdentityPrimitiveExecutor
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

    IReadOnlyList<object> ExecuteAllIdentities();

    /// <summary>
    /// Streams the best available identity universe for the executor's identity group.<br/>
    /// Grouped catalog indexes should return the de-duplicated union of identities visible through indexes in the same group, while ungrouped handles may fall back to their own `All` primitive.<br/>
    /// This gives negated criteria a group-aware universe without exposing a public identity-source abstraction yet.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence representing the current identity universe.</returns>
    IEnumerable<object> IterateIdentityUniverse();
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
        if (string.IsNullOrWhiteSpace(index.Group))
        {
            throw new InvalidOperationException("Programmatic identity criteria require an index with persisted identity-group metadata.");
        }

        return new LibraDexIdentityCriterion(
            index.Group,
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
    {
        int ordinal = 0;
        foreach (object identityObject in Iterate())
        {
            if (identityObject is not TIdentity identity)
            {
                throw new InvalidCastException($"Identity at ordinal {ordinal} is {identityObject.GetType().FullName}, not {typeof(TIdentity).FullName}.");
            }

            ordinal++;
            yield return identity;
        }
    }

    public IReadOnlyList<TIdentity> ToList<TIdentity>()
    {
        IReadOnlyList<object> identities = ToList();
        TIdentity[] typed = new TIdentity[identities.Count];
        for (int i = 0; i < identities.Count; i++)
        {
            typed[i] = identities[i] is TIdentity identity
                ? identity
                : throw new InvalidCastException($"Identity at ordinal {i} is {identities[i].GetType().FullName}, not {typeof(TIdentity).FullName}.");
        }

        return typed;
    }

    public LibraDexIdentityExecutionResult Execute()
        => LibraDexIdentityExecutionPlanner.Execute(Criterion, Options);

}

internal static class LibraDexIdentityExecutionPlanner
{
    internal static LibraDexIdentityExecutionPlan Plan(IIdentityCriterion criterion, LibraDexIdentityQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        options.Validate();
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

    internal static bool Exists(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        _ = deduplication;
        return ExistsNode(criterion);
    }

    internal static long Count(IIdentityCriterion criterion, IdentityDeduplication deduplication)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (deduplication == IdentityDeduplication.Preserve && TryCountLeaf(criterion, out long leafCount))
        {
            return leafCount;
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
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition delete requires a target index that can capture and mutate exact physical tuples.");
        }

        List<object> matchedIdentities = ExecuteNode(criterion);
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
            if (!ContainsIdentity(matchedIdentities, tuple.Identity))
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
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor ||
            targetIndex is not IIdentityExactTupleMutator exactMutator)
        {
            throw new NotSupportedException("Targeted condition SetKey requires a target index that can capture and mutate exact physical tuples.");
        }

        List<object> matchedIdentities = ExecuteNode(criterion);
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
            if (!ContainsIdentity(matchedIdentities, tuple.Identity))
            {
                continue;
            }

            object? replacementKey = newKeyFactory is null
                ? hasNewKey ? newKey : throw new InvalidOperationException("Targeted SetKey mutation is missing a replacement key.")
                : newKeyFactory(tuple.Identity) ?? throw new InvalidOperationException("Targeted SetKey replacement-key factory returned null.");
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

        return primitiveExecutor.IterateIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
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

        return primitiveExecutor.ExecuteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values)).ToList();
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

        identities = primitiveExecutor.ExecuteIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, takeLimit));
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
            start = Math.Max(start, bookmark.Position);
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
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, TakeLimit: 1)).GetEnumerator();
        exists = enumerator.MoveNext();
        return true;
    }

    private static bool TryCountLeaf(IIdentityCriterion criterion, out long count)
    {
        count = 0;
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.CriteriaKind is null ||
            criterion.Index is not IIdentityPrimitiveExecutor primitiveExecutor)
        {
            return false;
        }

        count = primitiveExecutor.CountIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values));
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

    private static IEnumerable<object> ApplyStreamingPaging(IEnumerable<object> identities, LibraDexIdentityQueryOptions options)
    {
        long skip = options.SkipCount;
        if (options.Bookmark is { } bookmark)
        {
            skip = Math.Max(skip, bookmark.Position);
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
            skip = Math.Max(skip, bookmark.Position);
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
            start = Math.Max(start, checked((int)Math.Min(bookmark.Position, int.MaxValue)));
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
/// Carries opaque continuation information for a public query.<br/>
/// The first scaffold stores only public shape metadata; future implementations can add route, shelf, generation, and tie-breaker state without changing query method names.<br/>
/// </summary>
