using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

public sealed class LibraDexConditionGroups
{
    private readonly LibraDexConditionEndCondition condition;
    private readonly Func<string, IIndex> resolveIndex;

    internal LibraDexConditionGroups(LibraDexConditionEndCondition condition, Func<string, IIndex> resolveIndex)
    {
        this.condition = condition;
        this.resolveIndex = resolveIndex;
    }

    /// <summary>
    /// Groups this adopted condition's matching identities by the keys of a grouping index.<br/>
    /// The grouping index must belong to the same identity group as the condition so grouped results cannot silently mix identity universes.<br/>
    /// </summary>
    /// <typeparam name="TKey">The grouping index key type.</typeparam>
    /// <typeparam name="TIdentity">The grouping index identity type.</typeparam>
    /// <param name="index">The index whose keys define group boundaries.</param>
    /// <returns>A condition-scoped grouped query.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> By<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Condition grouping requires the grouping index to belong to the same identity group as the condition.");
        }

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index);
    }

    /// <summary>
    /// Groups this adopted condition's matching scalar identities by the exact keys of a logical string index facade.<br/>
    /// This overload intentionally groups by developer-facing exact string values; folded-text, sort-key, and reversed projection grouping should be exposed through explicit projection-aware APIs so bucket semantics stay visible to callers.<br/>
    /// </summary>
    /// <param name="index">The logical string index whose exact keys define group boundaries.<br/></param>
    /// <returns>A condition-scoped grouped query keyed by exact string values.<br/></returns>
    public LibraDexConditionGroupQuery<string?, ulong> By(LibraDexStringScalar8Index index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Condition grouping requires the grouping index to belong to the same identity group as the condition.");
        }

        return new LibraDexConditionGroupQuery<string?, ulong>(
            condition,
            resolveIndex,
            index,
            () => IterateStringTuples(index));
    }

    /// <summary>
    /// Groups this adopted condition's matching scalar identities by the folded-text projection of a logical string index facade.<br/>
    /// This overload is intentionally explicit because folded grouping normalizes case and culture-sensitive casing before bucket comparison, unlike exact string grouping.<br/>
    /// </summary>
    /// <param name="index">The logical string index whose folded-text projection defines group boundaries.<br/></param>
    /// <returns>A condition-scoped grouped query keyed by folded string values.<br/></returns>
    public LibraDexConditionGroupQuery<string?, ulong> ByFoldedText(LibraDexStringScalar8Index index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Condition grouping requires the grouping index to belong to the same identity group as the condition.");
        }

        return new LibraDexConditionGroupQuery<string?, ulong>(
            condition,
            resolveIndex,
            targetIndex: null,
            iterateAllTuples: () => IterateFoldedStringTuples(index));
    }

    /// <summary>
    /// Groups this adopted condition's matching scalar identities by the binary sort-key projection of a logical string index facade.<br/>
    /// Sort-key grouping exposes byte-array buckets because culture sort keys are not reversible display strings and should not be confused with exact or folded text values.<br/>
    /// </summary>
    /// <param name="index">The logical string index whose sort-key projection defines group boundaries.<br/></param>
    /// <returns>A condition-scoped grouped query keyed by binary sort-key values.<br/></returns>
    public LibraDexConditionGroupQuery<byte[], ulong> BySortKey(LibraDexStringScalar8Index index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Condition grouping requires the grouping index to belong to the same identity group as the condition.");
        }

        return new LibraDexConditionGroupQuery<byte[], ulong>(
            condition,
            resolveIndex,
            targetIndex: null,
            iterateAllTuples: () => IterateSortKeyTuples(index));
    }

    /// <summary>
    /// Groups this adopted condition's matching identities by the full key of a routed composite index.<br/>
    /// The caller supplies the identity type so dynamic composite handles can still fail early when the index belongs to a different identity universe.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The grouping index identity type.</typeparam>
    /// <param name="index">The composite index whose full keys define group boundaries.</param>
    /// <returns>A condition-scoped grouped query keyed by <see cref="LibraDexCompositeKey"/>.</returns>
    public LibraDexConditionGroupQuery<LibraDexCompositeKey, TIdentity> By<TIdentity>(LibraDexRoutedCompositeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(condition.Group, index.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Condition grouping requires the grouping index to belong to the same identity group as the condition.");
        }

        if (index.IdentityType != typeof(TIdentity))
        {
            throw new InvalidOperationException($"Condition grouping requested identity type {typeof(TIdentity).FullName}, but composite index '{index.Name}' stores {index.IdentityType.FullName}.");
        }

        return new LibraDexConditionGroupQuery<LibraDexCompositeKey, TIdentity>(
            condition,
            resolveIndex,
            index,
            () => IterateCompositeTuples<TIdentity>(index));
    }

    /// <summary>
    /// Streams all exact string key/scalar identity tuples from a logical string index as typed grouping tuples.<br/>
    /// The string facade owns projection indexes too, but this adapter deliberately uses its exact tuple stream so direct string grouping matches ordinary developer-facing key equality.<br/>
    /// </summary>
    /// <param name="index">The logical string index to scan.<br/></param>
    /// <returns>A forward-only stream of exact string key/scalar identity tuples.<br/></returns>
    private static IEnumerable<LibraDexTuple<string?, ulong>> IterateStringTuples(LibraDexStringScalar8Index index)
    {
        IIdentityPrimitiveTupleStreamer streamer = index;
        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(request))
        {
            yield return new LibraDexTuple<string?, ulong>(
                tuple.Key is null ? null : tuple.Key as string ?? throw new InvalidDataException("String grouping encountered a non-string key."),
                (ulong)tuple.Identity);
        }
    }

    /// <summary>
    /// Streams folded string projection/scalar identity tuples from a logical string index as typed grouping tuples.<br/>
    /// Null exact keys remain null folded keys, while ordinary strings use the maintained folded projection value.<br/>
    /// </summary>
    /// <param name="index">The logical string index to scan through its folded-text projection.<br/></param>
    /// <returns>A forward-only stream of folded string key/scalar identity tuples.<br/></returns>
    private static IEnumerable<LibraDexTuple<string?, ulong>> IterateFoldedStringTuples(LibraDexStringScalar8Index index)
    {
        IIdentityPrimitiveTupleStreamer exactStreamer = index;
        foreach (LibraDexObjectTuple tuple in exactStreamer.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Null })))
        {
            yield return new LibraDexTuple<string?, ulong>(null, (ulong)tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in exactStreamer.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Empty })))
        {
            yield return new LibraDexTuple<string?, ulong>(string.Empty, (ulong)tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in index.IterateFoldedTextTuplePrimitive())
        {
            yield return new LibraDexTuple<string?, ulong>(
                tuple.Key is null ? null : tuple.Key as string ?? throw new InvalidDataException("Folded string grouping encountered a non-string key."),
                (ulong)tuple.Identity);
        }
    }

    /// <summary>
    /// Streams binary sort-key projection/scalar identity tuples from a logical string index as typed grouping tuples.<br/>
    /// Sort-key bytes are copied before exposure so grouped keys remain stable even if a lower-level reader reuses buffers in the future.<br/>
    /// </summary>
    /// <param name="index">The logical string index to scan through its sort-key projection.<br/></param>
    /// <returns>A forward-only stream of binary sort-key/scalar identity tuples.<br/></returns>
    private static IEnumerable<LibraDexTuple<byte[], ulong>> IterateSortKeyTuples(LibraDexStringScalar8Index index)
    {
        foreach (LibraDexObjectTuple tuple in index.IterateSortKeyTuplePrimitive())
        {
            byte[] key = tuple.Key as byte[] ?? throw new InvalidDataException("Sort-key grouping encountered a non-byte-array key.");
            yield return new LibraDexTuple<byte[], ulong>(key.ToArray(), (ulong)tuple.Identity);
        }
    }

    /// <summary>
    /// Streams all composite key/identity tuples from a routed composite index as typed grouping tuples.<br/>
    /// This adapter keeps composite grouping on the same terminal decision tree as scalar grouping without making callers concatenate key parts into ad hoc strings.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The grouping index identity type.<br/></typeparam>
    /// <param name="index">The composite index to scan.<br/></param>
    /// <returns>A forward-only stream of composite key/identity tuples.<br/></returns>
    private static IEnumerable<LibraDexTuple<LibraDexCompositeKey, TIdentity>> IterateCompositeTuples<TIdentity>(LibraDexRoutedCompositeIndex index)
    {
        IIdentityPrimitiveTupleStreamer streamer = index;
        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(request))
        {
            yield return new LibraDexTuple<LibraDexCompositeKey, TIdentity>(
                tuple.Key as LibraDexCompositeKey ?? throw new InvalidDataException("Composite grouping encountered a non-composite key."),
                (TIdentity)tuple.Identity);
        }
    }
}

/// <summary>
/// Represents grouping of an adopted condition's matching identities by one index's keys.<br/>
/// This bridge mirrors the temporary identity-condition grouping shape while letting new call sites remain on `LibraDexCondition` terminals.<br/>
/// </summary>
/// <typeparam name="TKey">The grouping key type.</typeparam>
/// <typeparam name="TIdentity">The identity type.</typeparam>
public sealed class LibraDexConditionGroupQuery<TKey, TIdentity>
{
    private readonly LibraDexConditionEndCondition condition;
    private readonly Func<string, IIndex> resolveIndex;
    private readonly IIndex? targetIndex;
    private readonly Func<IEnumerable<LibraDexTuple<TKey, TIdentity>>> iterateAllTuples;
    private readonly long? minimumCount;
    private readonly long? maximumCount;
    private readonly LibraDexGroupOrder groupOrder;
    private readonly QueryDirection itemDirection;
    private readonly int? takeGroups;

    internal LibraDexConditionGroupQuery(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolveIndex,
        LibraDexIndex<TKey, TIdentity> index,
        long? minimumCount = null,
        long? maximumCount = null,
        LibraDexGroupOrder groupOrder = LibraDexGroupOrder.KeyAscending,
        QueryDirection itemDirection = QueryDirection.Ascending,
        int? takeGroups = null)
        : this(condition, resolveIndex, index, index.IterateAllTuples, minimumCount, maximumCount, groupOrder, itemDirection, takeGroups)
    {
    }

    internal LibraDexConditionGroupQuery(
        LibraDexConditionEndCondition condition,
        Func<string, IIndex> resolveIndex,
        IIndex? targetIndex,
        Func<IEnumerable<LibraDexTuple<TKey, TIdentity>>> iterateAllTuples,
        long? minimumCount = null,
        long? maximumCount = null,
        LibraDexGroupOrder groupOrder = LibraDexGroupOrder.KeyAscending,
        QueryDirection itemDirection = QueryDirection.Ascending,
        int? takeGroups = null)
    {
        this.condition = condition;
        this.resolveIndex = resolveIndex;
        this.targetIndex = targetIndex;
        this.iterateAllTuples = iterateAllTuples;
        this.minimumCount = minimumCount;
        this.maximumCount = maximumCount;
        this.groupOrder = groupOrder;
        this.itemDirection = itemDirection;
        this.takeGroups = takeGroups;
    }

    /// <summary>
    /// Returns a grouped query filtered to groups with exactly <paramref name="count"/> matching identities.<br/>
    /// </summary>
    /// <param name="count">The exact group count to keep.</param>
    /// <returns>A new grouped query with the exact count filter applied.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> WhereCount(long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Group count cannot be negative.");
        }

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, count, count, groupOrder, itemDirection, takeGroups);
    }

    /// <summary>
    /// Returns a grouped query filtered to groups with at least <paramref name="minimumCount"/> matching identities.<br/>
    /// </summary>
    /// <param name="minimumCount">The inclusive minimum number of matching identities required for a group.</param>
    /// <returns>A new grouped query with the count filter applied.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> WhereCountAtLeast(long minimumCount)
    {
        if (minimumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCount), minimumCount, "Minimum group count cannot be negative.");
        }

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, minimumCount, maximumCount, groupOrder, itemDirection, takeGroups);
    }

    /// <summary>
    /// Returns a grouped query filtered to groups with at most <paramref name="maximumCount"/> matching identities.<br/>
    /// </summary>
    /// <param name="maximumCount">The inclusive maximum number of matching identities allowed for a group.</param>
    /// <returns>A new grouped query with the count filter applied.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> WhereCountAtMost(long maximumCount)
    {
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), maximumCount, "Maximum group count cannot be negative.");
        }

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, minimumCount, maximumCount, groupOrder, itemDirection, takeGroups);
    }

    /// <summary>
    /// Returns a grouped query containing only duplicate groups.<br/>
    /// A duplicate group is any group with at least two matching identities under the same grouping key.<br/>
    /// </summary>
    /// <returns>A new grouped query filtered to duplicate groups.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> Duplicates()
        => WhereCountAtLeast(2);

    /// <summary>
    /// Returns a grouped query containing only singleton groups.<br/>
    /// </summary>
    /// <returns>A new grouped query filtered to groups with exactly one matching identity.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> Singletons()
        => WhereCount(1);

    /// <summary>
    /// Returns a grouped query with explicit group ordering.<br/>
    /// </summary>
    /// <param name="order">The group ordering to apply.</param>
    /// <returns>A new grouped query with the requested group ordering.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> OrderBy(LibraDexGroupOrder order)
        => new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, minimumCount, maximumCount, order, itemDirection, takeGroups);

    /// <summary>
    /// Returns a grouped query with explicit item ordering inside each group.<br/>
    /// </summary>
    /// <param name="direction">The identity ordering to apply inside each returned group.</param>
    /// <returns>A new grouped query with the requested item ordering.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> OrderItemsBy(QueryDirection direction)
        => new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, minimumCount, maximumCount, groupOrder, direction, takeGroups);

    /// <summary>
    /// Returns a grouped query limited to the first <paramref name="count"/> groups after filtering and ordering.<br/>
    /// </summary>
    /// <param name="count">The maximum number of groups to return.</param>
    /// <returns>A new grouped query with a group-level take limit.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> Take(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Group take count cannot be negative.");
        }

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, targetIndex, iterateAllTuples, minimumCount, maximumCount, groupOrder, itemDirection, count);
    }

    /// <summary>
    /// Returns duplicate groups ordered by count descending and limited to <paramref name="count"/> groups.<br/>
    /// </summary>
    /// <param name="count">The maximum duplicate-group count to return.</param>
    /// <returns>A new grouped query for the largest duplicate groups.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> TopDuplicates(int count)
        => Duplicates().OrderBy(LibraDexGroupOrder.CountDescending).Take(count);

    /// <summary>
    /// Materializes matching identities as ordered group objects keyed by the selected index's keys.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>Ordered group objects containing the grouping key and matching identities.</returns>
    public IReadOnlyList<LibraDexGroup<TKey, TIdentity>> ToList(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        List<LibraDexGroup<TKey, TIdentity>> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            result.Add(new LibraDexGroup<TKey, TIdentity>(group.Key, group.Value));
        }

        return result;
    }

    /// <summary>
    /// Materializes matching identities grouped by the selected index's keys.<br/>
    /// This is the adopted condition equivalent of the compatibility grouping dictionary bridge and keeps dictionary materialization explicit.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with matching identities as values.</returns>
    public IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> ToDictionary(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return MaterializeOrderedGroups(ordering, deduplication);
    }

    /// <summary>
    /// Materializes per-group counts without exposing every grouped identity as the primary result.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the number of matching identities in each group.</returns>
    public IReadOnlyDictionary<TKey, long> Counts(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        if (ChooseGroupingStrategy(LibraDexGroupingTerminal.Counts, ordering, deduplication) == LibraDexGroupingStrategy.StreamingCounts)
        {
            return StreamCounts(ordering, deduplication);
        }

        IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> groups = MaterializeOrderedGroups(ordering, deduplication);
        LibraDexGroupedDictionary<TKey, long> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in groups)
        {
            result.Add(group.Key, group.Value.Count);
        }

        return result;
    }

    /// <summary>
    /// Selects the lowest-materialization grouping strategy that can satisfy the requested terminal shape.<br/>
    /// The first decision tree deliberately optimizes count-only grouping because it can avoid per-group member-list allocation while preserving ordered-key semantics.<br/>
    /// </summary>
    /// <param name="terminal">The grouping terminal being executed.<br/></param>
    /// <param name="ordering">The identity ordering requested for the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy requested for the condition candidate set.<br/></param>
    /// <returns>The selected grouping execution strategy.<br/></returns>
    private LibraDexGroupingStrategy ChooseGroupingStrategy(
        LibraDexGroupingTerminal terminal,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        _ = ordering;
        _ = deduplication;
        if (terminal == LibraDexGroupingTerminal.Counts &&
            itemDirection == QueryDirection.Ascending &&
            (groupOrder == LibraDexGroupOrder.KeyAscending || groupOrder == LibraDexGroupOrder.CountAscending || groupOrder == LibraDexGroupOrder.CountDescending))
        {
            return LibraDexGroupingStrategy.StreamingCounts;
        }

        if (terminal == LibraDexGroupingTerminal.Metadata &&
            (itemDirection == QueryDirection.Ascending || itemDirection == QueryDirection.Descending) &&
            (groupOrder == LibraDexGroupOrder.KeyAscending || groupOrder == LibraDexGroupOrder.KeyDescending || groupOrder == LibraDexGroupOrder.CountAscending || groupOrder == LibraDexGroupOrder.CountDescending))
        {
            return LibraDexGroupingStrategy.StreamingMetadata;
        }

        if (terminal == LibraDexGroupingTerminal.Representatives &&
            (itemDirection == QueryDirection.Ascending || itemDirection == QueryDirection.Descending) &&
            (groupOrder == LibraDexGroupOrder.KeyAscending || groupOrder == LibraDexGroupOrder.KeyDescending || groupOrder == LibraDexGroupOrder.CountAscending || groupOrder == LibraDexGroupOrder.CountDescending))
        {
            return LibraDexGroupingStrategy.StreamingRepresentatives;
        }

        return LibraDexGroupingStrategy.MaterializedGroups;
    }

    /// <summary>
    /// Streams grouping-index tuples after applying this condition through the shared condition cursor executor when the target index is known.<br/>
    /// The fallback keeps delegate-backed grouping handles functional, while normal scalar and composite grouping can avoid the grouping layer's candidate `HashSet` materialization.<br/>
    /// </summary>
    /// <param name="ordering">The requested identity ordering contract for fallback candidate materialization.<br/></param>
    /// <param name="deduplication">The requested duplicate identity policy for fallback candidate materialization.<br/></param>
    /// <returns>Matching grouping-index key/identity tuples in grouping-index order.<br/></returns>
    private IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateMatchingTuples(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        if (CanUseFilteredTargetTupleStream())
        {
            IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(resolveIndex, static (_, _) => null);
            foreach (LibraDexObjectTuple tuple in LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, targetIndex!, skip: 0, take: null, QueryDirection.Ascending))
            {
                yield return new LibraDexTuple<TKey, TIdentity>((TKey)tuple.Key!, (TIdentity)tuple.Identity);
            }

            yield break;
        }

        HashSet<TIdentity> identities = CreateCandidateIdentitySet(ordering, deduplication);
        foreach (LibraDexTuple<TKey, TIdentity> tuple in iterateAllTuples())
        {
            if (identities.Contains(tuple.Identity))
            {
                yield return tuple;
            }
        }
    }

    /// <summary>
    /// Returns whether this condition contains the grouping index as an executable target-index leaf.<br/>
    /// Cursor-executor filtering is valuable for direct target-index plans, but broad unrelated conditions can degrade to list membership checks, so they stay on the local `HashSet` fallback.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the grouped terminal should use the shared condition cursor executor for target tuple filtering.<br/></returns>
    private bool CanUseFilteredTargetTupleStream()
    {
        if (targetIndex is null)
        {
            return false;
        }

        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = condition.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (string.Equals(leaves[i].IndexName, targetIndex.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the fallback candidate identity set directly from the condition projection iterator.<br/>
    /// This keeps unrelated-index grouping on hash membership while avoiding the extra typed list allocation produced by `ToList&lt;TIdentity&gt;()`.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when projecting candidate identities.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used when projecting candidate identities.<br/></param>
    /// <returns>A candidate identity set using LibraDex key equality for the grouped identity type.<br/></returns>
#pragma warning disable CS8714
    private HashSet<TIdentity> CreateCandidateIdentitySet(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
#pragma warning restore CS8714
    {
        return new HashSet<TIdentity>(
            condition.IDsWith(resolveIndex, ordering, deduplication).Iterate<TIdentity>(),
            LibraDexKeyEquality<TIdentity>.Comparer);
    }

    /// <summary>
    /// Counts condition-matching identities by scanning the ordered grouping index without building per-group member lists.<br/>
    /// Scalar and composite grouping handles use the condition cursor executor to avoid prebuilding the full candidate identity set when the executor can stream the target index.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with the number of matching identities in each group.<br/></returns>
    private IReadOnlyDictionary<TKey, long> StreamCounts(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        LibraDexGroupedDictionary<TKey, long> counts = new();
        bool hasCurrent = false;
        TKey currentKey = default!;
        long currentCount = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                currentCount = 1;
                continue;
            }

            if (LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                currentCount++;
                continue;
            }

            AddStreamedCount(counts, currentKey, currentCount);
            currentKey = tuple.Key;
            currentCount = 1;
        }

        if (hasCurrent)
        {
            AddStreamedCount(counts, currentKey, currentCount);
        }

        return OrderStreamedCounts(counts);
    }

    /// <summary>
    /// Adds a streamed count when it satisfies the query's group-count filters.<br/>
    /// This keeps duplicate/singleton/count-range filters inside the no-member-list path.<br/>
    /// </summary>
    /// <param name="counts">The output count dictionary.<br/></param>
    /// <param name="key">The completed group key.<br/></param>
    /// <param name="count">The completed group count.<br/></param>
    private void AddStreamedCount(LibraDexGroupedDictionary<TKey, long> counts, TKey key, long count)
    {
        if ((minimumCount is not null && count < minimumCount.Value) ||
            (maximumCount is not null && count > maximumCount.Value))
        {
            return;
        }

        counts.Add(key, count);
    }

    /// <summary>
    /// Applies group-level count ordering and take limits after the streaming count pass has avoided member-list materialization.<br/>
    /// Key-ascending order keeps the grouping index's natural order and therefore avoids a sort.<br/>
    /// </summary>
    /// <param name="counts">The streamed count dictionary before optional ordering and take projection.<br/></param>
    /// <returns>The final count dictionary in the requested group order.<br/></returns>
    private IReadOnlyDictionary<TKey, long> OrderStreamedCounts(LibraDexGroupedDictionary<TKey, long> counts)
    {
        IEnumerable<KeyValuePair<TKey, long>> ordered = groupOrder switch
        {
            LibraDexGroupOrder.KeyAscending => counts,
            LibraDexGroupOrder.CountAscending => counts.OrderBy(static count => count.Value),
            LibraDexGroupOrder.CountDescending => counts.OrderByDescending(static count => count.Value),
            _ => throw new NotSupportedException($"Group order {groupOrder} is not supported by streamed counts.")
        };

        if (takeGroups is not null)
        {
            ordered = ordered.Take(takeGroups.Value);
        }

        LibraDexGroupedDictionary<TKey, long> result = new();
        foreach (KeyValuePair<TKey, long> count in ordered)
        {
            result.Add(count.Key, count.Value);
        }

        return result;
    }

    /// <summary>
    /// Materializes one representative identity for each group.<br/>
    /// </summary>
    /// <param name="representative">The representative identity selection rule.</param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with one representative identity per group.</returns>
    public IReadOnlyDictionary<TKey, TIdentity> Representatives(
        LibraDexGroupRepresentative representative = LibraDexGroupRepresentative.First,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        if (ChooseGroupingStrategy(LibraDexGroupingTerminal.Representatives, ordering, deduplication) == LibraDexGroupingStrategy.StreamingRepresentatives)
        {
            return StreamRepresentatives(representative, ordering, deduplication);
        }

        IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> groups = MaterializeOrderedGroups(ordering, deduplication);
        LibraDexGroupedDictionary<TKey, TIdentity> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in groups)
        {
            if (group.Value.Count == 0)
            {
                continue;
            }

            TIdentity identity = representative switch
            {
                LibraDexGroupRepresentative.First => group.Value[0],
                LibraDexGroupRepresentative.Last => group.Value[group.Value.Count - 1],
                _ => throw new NotSupportedException($"Group representative {representative} is not supported.")
            };
            result.Add(group.Key, identity);
        }

        return result;
    }

    /// <summary>
    /// Materializes representative identities from compact streamed group state instead of full group member lists.<br/>
    /// First and last representatives use edge identities after item-order handling, so duplicate/singleton/top-N representative workflows avoid per-group lists.<br/>
    /// </summary>
    /// <param name="representative">The representative identity selection rule.<br/></param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one representative identity per group.<br/></returns>
    private IReadOnlyDictionary<TKey, TIdentity> StreamRepresentatives(
        LibraDexGroupRepresentative representative,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        List<LibraDexStreamedRepresentative> representatives = new();
        bool hasCurrent = false;
        TKey currentKey = default!;
        TIdentity firstIdentity = default!;
        TIdentity lastIdentity = default!;
        long currentCount = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                firstIdentity = tuple.Identity;
                lastIdentity = tuple.Identity;
                currentCount = 1;
                continue;
            }

            if (LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                lastIdentity = tuple.Identity;
                currentCount++;
                continue;
            }

            AddStreamedRepresentative(representatives, currentKey, currentCount, firstIdentity, lastIdentity);
            currentKey = tuple.Key;
            firstIdentity = tuple.Identity;
            lastIdentity = tuple.Identity;
            currentCount = 1;
        }

        if (hasCurrent)
        {
            AddStreamedRepresentative(representatives, currentKey, currentCount, firstIdentity, lastIdentity);
        }

        return OrderStreamedRepresentatives(representatives, representative);
    }

    /// <summary>
    /// Adds one compact streamed representative row when the completed group satisfies the query's count filters.<br/>
    /// Item-descending order swaps first and last edge identities so later representative selection observes the public item-order contract.<br/>
    /// </summary>
    /// <param name="representatives">The output compact representative rows.<br/></param>
    /// <param name="key">The completed group key.<br/></param>
    /// <param name="count">The completed group count.<br/></param>
    /// <param name="firstIdentity">The first identity seen in index order.<br/></param>
    /// <param name="lastIdentity">The last identity seen in index order.<br/></param>
    private void AddStreamedRepresentative(
        List<LibraDexStreamedRepresentative> representatives,
        TKey key,
        long count,
        TIdentity firstIdentity,
        TIdentity lastIdentity)
    {
        if ((minimumCount is not null && count < minimumCount.Value) ||
            (maximumCount is not null && count > maximumCount.Value))
        {
            return;
        }

        if (itemDirection == QueryDirection.Descending)
        {
            (firstIdentity, lastIdentity) = (lastIdentity, firstIdentity);
        }
        else if (itemDirection != QueryDirection.Ascending)
        {
            throw new NotSupportedException($"Group item direction {itemDirection} is not supported.");
        }

        representatives.Add(new LibraDexStreamedRepresentative(key, count, firstIdentity, lastIdentity));
    }

    /// <summary>
    /// Applies requested group ordering and take limits to streamed representatives.<br/>
    /// Count-order requests sort only compact count-plus-representative rows, avoiding full member-list materialization.<br/>
    /// </summary>
    /// <param name="representatives">The compact streamed representative rows before optional ordering and take projection.<br/></param>
    /// <param name="representative">The representative identity selection rule.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one representative identity per group.<br/></returns>
    private IReadOnlyDictionary<TKey, TIdentity> OrderStreamedRepresentatives(
        List<LibraDexStreamedRepresentative> representatives,
        LibraDexGroupRepresentative representative)
    {
        IEnumerable<LibraDexStreamedRepresentative> ordered = groupOrder switch
        {
            LibraDexGroupOrder.KeyAscending => representatives,
            LibraDexGroupOrder.KeyDescending => Enumerable.Reverse(representatives),
            LibraDexGroupOrder.CountAscending => representatives.OrderBy(static row => row.Count),
            LibraDexGroupOrder.CountDescending => representatives.OrderByDescending(static row => row.Count),
            _ => throw new NotSupportedException($"Group order {groupOrder} is not supported by streamed representatives.")
        };

        if (takeGroups is not null)
        {
            ordered = ordered.Take(takeGroups.Value);
        }

        LibraDexGroupedDictionary<TKey, TIdentity> result = new();
        foreach (LibraDexStreamedRepresentative row in ordered)
        {
            TIdentity identity = representative switch
            {
                LibraDexGroupRepresentative.First => row.FirstIdentity,
                LibraDexGroupRepresentative.Last => row.LastIdentity,
                _ => throw new NotSupportedException($"Group representative {representative} is not supported.")
            };
            result.Add(row.Key, identity);
        }

        return result;
    }

    /// <summary>
    /// Materializes the first identity in each group.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the first matching identity for each group.</returns>
    public IReadOnlyDictionary<TKey, TIdentity> FirstIdentities(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return Representatives(LibraDexGroupRepresentative.First, ordering, deduplication);
    }

    /// <summary>
    /// Materializes the last identity in each group.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the last matching identity for each group.</returns>
    public IReadOnlyDictionary<TKey, TIdentity> LastIdentities(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return Representatives(LibraDexGroupRepresentative.Last, ordering, deduplication);
    }

    /// <summary>
    /// Materializes the first group after filtering and ordering.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>The first group, or <see langword="null"/> when no group matches.</returns>
    public LibraDexGroup<TKey, TIdentity>? FirstGroup(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            return new LibraDexGroup<TKey, TIdentity>(group.Key, group.Value);
        }

        return null;
    }

    /// <summary>
    /// Materializes the last group after filtering and ordering.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>The last group, or <see langword="null"/> when no group matches.</returns>
    public LibraDexGroup<TKey, TIdentity>? LastGroup(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        LibraDexGroup<TKey, TIdentity>? last = null;
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            last = new LibraDexGroup<TKey, TIdentity>(group.Key, group.Value);
        }

        return last;
    }

    /// <summary>
    /// Materializes metadata for the first group after filtering and ordering.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>The first group's metadata, or <see langword="null"/> when no group matches.</returns>
    public LibraDexGroupMetadata<TKey, TIdentity>? FirstMetadata(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        LibraDexGroup<TKey, TIdentity>? group = FirstGroup(ordering, deduplication);
        return group is null ? null : CreateMetadata(group.Key, group.Items);
    }

    /// <summary>
    /// Materializes metadata for the last group after filtering and ordering.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>The last group's metadata, or <see langword="null"/> when no group matches.</returns>
    public LibraDexGroupMetadata<TKey, TIdentity>? LastMetadata(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        LibraDexGroup<TKey, TIdentity>? group = LastGroup(ordering, deduplication);
        return group is null ? null : CreateMetadata(group.Key, group.Items);
    }

    /// <summary>
    /// Materializes group metadata without exposing member lists as the primary result.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>Group metadata in requested group order.</returns>
    public IReadOnlyList<LibraDexGroupMetadata<TKey, TIdentity>> Metadata(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        if (ChooseGroupingStrategy(LibraDexGroupingTerminal.Metadata, ordering, deduplication) == LibraDexGroupingStrategy.StreamingMetadata)
        {
            return StreamMetadata(ordering, deduplication);
        }

        List<LibraDexGroupMetadata<TKey, TIdentity>> metadata = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            if (group.Value.Count == 0)
            {
                continue;
            }

            metadata.Add(new LibraDexGroupMetadata<TKey, TIdentity>(
                group.Key,
                group.Value.Count,
                group.Value[0],
                group.Value[group.Value.Count - 1]));
        }

        return metadata;
    }

    /// <summary>
    /// Materializes group metadata by scanning the ordered grouping index without building member lists.<br/>
    /// This keeps duplicate/singleton/top-N metadata on a count-and-edge-identity path instead of the full group materializer.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>Group metadata in requested group order.<br/></returns>
    private IReadOnlyList<LibraDexGroupMetadata<TKey, TIdentity>> StreamMetadata(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        List<LibraDexGroupMetadata<TKey, TIdentity>> groups = new();
        bool hasCurrent = false;
        TKey currentKey = default!;
        TIdentity firstIdentity = default!;
        TIdentity lastIdentity = default!;
        long currentCount = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                firstIdentity = tuple.Identity;
                lastIdentity = tuple.Identity;
                currentCount = 1;
                continue;
            }

            if (LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                lastIdentity = tuple.Identity;
                currentCount++;
                continue;
            }

            AddStreamedMetadata(groups, currentKey, currentCount, firstIdentity, lastIdentity);
            currentKey = tuple.Key;
            firstIdentity = tuple.Identity;
            lastIdentity = tuple.Identity;
            currentCount = 1;
        }

        if (hasCurrent)
        {
            AddStreamedMetadata(groups, currentKey, currentCount, firstIdentity, lastIdentity);
        }

        return OrderStreamedMetadata(groups);
    }

    /// <summary>
    /// Adds streamed metadata when the completed group satisfies the query's count filters.<br/>
    /// First and last identities are swapped for descending item order so metadata matches the full materialized terminal contract.<br/>
    /// </summary>
    /// <param name="groups">The output metadata state list.<br/></param>
    /// <param name="key">The completed group key.<br/></param>
    /// <param name="count">The completed group count.<br/></param>
    /// <param name="firstIdentity">The first identity seen in index order.<br/></param>
    /// <param name="lastIdentity">The last identity seen in index order.<br/></param>
    private void AddStreamedMetadata(
        List<LibraDexGroupMetadata<TKey, TIdentity>> groups,
        TKey key,
        long count,
        TIdentity firstIdentity,
        TIdentity lastIdentity)
    {
        if ((minimumCount is not null && count < minimumCount.Value) ||
            (maximumCount is not null && count > maximumCount.Value))
        {
            return;
        }

        if (itemDirection == QueryDirection.Descending)
        {
            (firstIdentity, lastIdentity) = (lastIdentity, firstIdentity);
        }
        else if (itemDirection != QueryDirection.Ascending)
        {
            throw new NotSupportedException($"Group item direction {itemDirection} is not supported.");
        }

        groups.Add(new LibraDexGroupMetadata<TKey, TIdentity>(key, count, firstIdentity, lastIdentity));
    }

    /// <summary>
    /// Applies requested group ordering and take limits to streamed metadata rows.<br/>
    /// Key-order requests reuse the grouping index order, while count-order requests sort only compact metadata rows.<br/>
    /// </summary>
    /// <param name="groups">The streamed metadata rows before optional ordering and take projection.<br/></param>
    /// <returns>Group metadata in requested group order.<br/></returns>
    private IReadOnlyList<LibraDexGroupMetadata<TKey, TIdentity>> OrderStreamedMetadata(List<LibraDexGroupMetadata<TKey, TIdentity>> groups)
    {
        if (takeGroups is null && groupOrder == LibraDexGroupOrder.KeyAscending)
        {
            return groups;
        }

        IEnumerable<LibraDexGroupMetadata<TKey, TIdentity>> ordered = groupOrder switch
        {
            LibraDexGroupOrder.KeyAscending => groups,
            LibraDexGroupOrder.KeyDescending => Enumerable.Reverse(groups),
            LibraDexGroupOrder.CountAscending => groups.OrderBy(static group => group.Count),
            LibraDexGroupOrder.CountDescending => groups.OrderByDescending(static group => group.Count),
            _ => throw new NotSupportedException($"Group order {groupOrder} is not supported by streamed metadata.")
        };

        if (takeGroups is not null)
        {
            ordered = ordered.Take(takeGroups.Value);
        }

        List<LibraDexGroupMetadata<TKey, TIdentity>> result = new();
        foreach (LibraDexGroupMetadata<TKey, TIdentity> group in ordered)
            result.Add(group);

        return result;
    }

    /// <summary>
    /// Aggregates matching identities inside each group without exposing every grouped identity as the primary result.<br/>
    /// The aggregate runs over LibraDex identities because source objects belong to the caller; callers that need source fields can project from identity inside <paramref name="accumulator"/>.<br/>
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate value type.</typeparam>
    /// <param name="seed">The initial aggregate value for each non-empty group.</param>
    /// <param name="accumulator">The per-identity aggregate update function.</param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with one aggregate value per matching group.</returns>
    public IReadOnlyDictionary<TKey, TAggregate> Aggregate<TAggregate>(
        TAggregate seed,
        Func<TAggregate, TIdentity, TAggregate> accumulator,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        if (itemDirection == QueryDirection.Ascending)
        {
            return StreamAggregate(seed, accumulator, ordering, deduplication);
        }

        return MaterializeAggregate(seed, accumulator, ordering, deduplication);
    }

    /// <summary>
    /// Sums one caller-projected numeric value per matching identity inside each group.<br/>
    /// This terminal is the common grouped aggregate case and uses the same no-member-list path as count and metadata when item order is ascending.<br/>
    /// </summary>
    /// <param name="valueSelector">Selects the value contributed by one matching identity.</param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the summed value for each matching group.</returns>
    public IReadOnlyDictionary<TKey, long> Sum(
        Func<TIdentity, long> valueSelector,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        ArgumentNullException.ThrowIfNull(valueSelector);
        return Aggregate(0L, (sum, identity) => sum + valueSelector(identity), ordering, deduplication);
    }

    /// <summary>
    /// Selects the minimum caller-projected value from matching identities inside each group.<br/>
    /// Empty groups are not returned, matching the other grouped terminals that only report groups with at least one matching identity.<br/>
    /// </summary>
    /// <typeparam name="TValue">The projected value type.</typeparam>
    /// <param name="valueSelector">Selects the value contributed by one matching identity.</param>
    /// <param name="comparer">Optional value comparer; when omitted, <see cref="Comparer{T}.Default"/> is used.</param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the minimum projected value for each matching group.</returns>
    public IReadOnlyDictionary<TKey, TValue> Min<TValue>(
        Func<TIdentity, TValue> valueSelector,
        IComparer<TValue>? comparer = null,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        ArgumentNullException.ThrowIfNull(valueSelector);
        return StreamOrMaterializeExtreme(valueSelector, comparer ?? Comparer<TValue>.Default, keepMinimum: true, ordering, deduplication);
    }

    /// <summary>
    /// Selects the maximum caller-projected value from matching identities inside each group.<br/>
    /// Empty groups are not returned, matching the other grouped terminals that only report groups with at least one matching identity.<br/>
    /// </summary>
    /// <typeparam name="TValue">The projected value type.</typeparam>
    /// <param name="valueSelector">Selects the value contributed by one matching identity.</param>
    /// <param name="comparer">Optional value comparer; when omitted, <see cref="Comparer{T}.Default"/> is used.</param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A dictionary keyed by grouping-index key with the maximum projected value for each matching group.</returns>
    public IReadOnlyDictionary<TKey, TValue> Max<TValue>(
        Func<TIdentity, TValue> valueSelector,
        IComparer<TValue>? comparer = null,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        ArgumentNullException.ThrowIfNull(valueSelector);
        return StreamOrMaterializeExtreme(valueSelector, comparer ?? Comparer<TValue>.Default, keepMinimum: false, ordering, deduplication);
    }

    /// <summary>
    /// Aggregates matching identities by scanning the ordered grouping index without building per-group member lists.<br/>
    /// The accumulator observes identities in grouping-index order, so this path is used only for ascending group item order.<br/>
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate value type.<br/></typeparam>
    /// <param name="seed">The initial aggregate value for each non-empty group.<br/></param>
    /// <param name="accumulator">The per-identity aggregate update function.<br/></param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one aggregate value per matching group.<br/></returns>
    private IReadOnlyDictionary<TKey, TAggregate> StreamAggregate<TAggregate>(
        TAggregate seed,
        Func<TAggregate, TIdentity, TAggregate> accumulator,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        List<LibraDexStreamedAggregate<TAggregate>> aggregates = new();
        bool hasCurrent = false;
        TKey currentKey = default!;
        TAggregate currentValue = seed;
        long currentCount = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                currentValue = accumulator(seed, tuple.Identity);
                currentCount = 1;
                continue;
            }

            if (LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                currentValue = accumulator(currentValue, tuple.Identity);
                currentCount++;
                continue;
            }

            AddStreamedAggregate(aggregates, currentKey, currentCount, currentValue);
            currentKey = tuple.Key;
            currentValue = accumulator(seed, tuple.Identity);
            currentCount = 1;
        }

        if (hasCurrent)
        {
            AddStreamedAggregate(aggregates, currentKey, currentCount, currentValue);
        }

        return OrderStreamedAggregates(aggregates);
    }

    /// <summary>
    /// Aggregates from the materialized group contract when the caller requests a non-streamable item order.<br/>
    /// This preserves public item-order semantics while keeping ascending-order aggregate calls on the lower-allocation path.<br/>
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate value type.<br/></typeparam>
    /// <param name="seed">The initial aggregate value for each non-empty group.<br/></param>
    /// <param name="accumulator">The per-identity aggregate update function.<br/></param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one aggregate value per matching group.<br/></returns>
    private IReadOnlyDictionary<TKey, TAggregate> MaterializeAggregate<TAggregate>(
        TAggregate seed,
        Func<TAggregate, TIdentity, TAggregate> accumulator,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        LibraDexGroupedDictionary<TKey, TAggregate> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            TAggregate value = seed;
            for (int i = 0; i < group.Value.Count; i++)
            {
                value = accumulator(value, group.Value[i]);
            }

            result.Add(group.Key, value);
        }

        return result;
    }

    /// <summary>
    /// Adds one compact aggregate row when the completed group satisfies the query's count filters.<br/>
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate value type.<br/></typeparam>
    /// <param name="aggregates">The output compact aggregate rows.<br/></param>
    /// <param name="key">The completed group key.<br/></param>
    /// <param name="count">The completed group count.<br/></param>
    /// <param name="value">The completed aggregate value.<br/></param>
    private void AddStreamedAggregate<TAggregate>(
        List<LibraDexStreamedAggregate<TAggregate>> aggregates,
        TKey key,
        long count,
        TAggregate value)
    {
        if ((minimumCount is not null && count < minimumCount.Value) ||
            (maximumCount is not null && count > maximumCount.Value))
        {
            return;
        }

        aggregates.Add(new LibraDexStreamedAggregate<TAggregate>(key, count, value));
    }

    /// <summary>
    /// Applies requested group ordering and take limits to compact aggregate rows.<br/>
    /// Count-order requests sort only key/count/value rows, avoiding full member-list materialization.<br/>
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate value type.<br/></typeparam>
    /// <param name="aggregates">The compact aggregate rows before optional ordering and take projection.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one aggregate value per matching group.<br/></returns>
    private IReadOnlyDictionary<TKey, TAggregate> OrderStreamedAggregates<TAggregate>(List<LibraDexStreamedAggregate<TAggregate>> aggregates)
    {
        IEnumerable<LibraDexStreamedAggregate<TAggregate>> ordered = groupOrder switch
        {
            LibraDexGroupOrder.KeyAscending => aggregates,
            LibraDexGroupOrder.KeyDescending => Enumerable.Reverse(aggregates),
            LibraDexGroupOrder.CountAscending => aggregates.OrderBy(static row => row.Count),
            LibraDexGroupOrder.CountDescending => aggregates.OrderByDescending(static row => row.Count),
            _ => throw new NotSupportedException($"Group order {groupOrder} is not supported by streamed aggregates.")
        };

        if (takeGroups is not null)
        {
            ordered = ordered.Take(takeGroups.Value);
        }

        LibraDexGroupedDictionary<TKey, TAggregate> result = new();
        foreach (LibraDexStreamedAggregate<TAggregate> row in ordered)
        {
            result.Add(row.Key, row.Value);
        }

        return result;
    }

    /// <summary>
    /// Selects grouped minimum or maximum values, streaming when item order allows it and falling back to materialized groups otherwise.<br/>
    /// This helper keeps min/max on the same compact aggregate path as custom aggregate calls while avoiding sentinel values for the first item in a group.<br/>
    /// </summary>
    /// <typeparam name="TValue">The projected value type.<br/></typeparam>
    /// <param name="valueSelector">Selects the value contributed by one matching identity.<br/></param>
    /// <param name="comparer">The comparer used to choose the winning value.<br/></param>
    /// <param name="keepMinimum"><see langword="true"/> for minimum, <see langword="false"/> for maximum.<br/></param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one extreme value per matching group.<br/></returns>
    private IReadOnlyDictionary<TKey, TValue> StreamOrMaterializeExtreme<TValue>(
        Func<TIdentity, TValue> valueSelector,
        IComparer<TValue> comparer,
        bool keepMinimum,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        if (itemDirection != QueryDirection.Ascending)
        {
            return MaterializeExtreme(valueSelector, comparer, keepMinimum, ordering, deduplication);
        }

        List<LibraDexStreamedAggregate<TValue>> aggregates = new();
        bool hasCurrent = false;
        TKey currentKey = default!;
        TValue currentValue = default!;
        long currentCount = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            TValue value = valueSelector(tuple.Identity);
            if (!hasCurrent)
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                currentValue = value;
                currentCount = 1;
                continue;
            }

            if (LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                if (ShouldReplaceExtreme(currentValue, value, comparer, keepMinimum))
                {
                    currentValue = value;
                }

                currentCount++;
                continue;
            }

            AddStreamedAggregate(aggregates, currentKey, currentCount, currentValue);
            currentKey = tuple.Key;
            currentValue = value;
            currentCount = 1;
        }

        if (hasCurrent)
        {
            AddStreamedAggregate(aggregates, currentKey, currentCount, currentValue);
        }

        return OrderStreamedAggregates(aggregates);
    }

    /// <summary>
    /// Selects grouped minimum or maximum values from materialized groups when item order is not streamable.<br/>
    /// </summary>
    /// <typeparam name="TValue">The projected value type.<br/></typeparam>
    /// <param name="valueSelector">Selects the value contributed by one matching identity.<br/></param>
    /// <param name="comparer">The comparer used to choose the winning value.<br/></param>
    /// <param name="keepMinimum"><see langword="true"/> for minimum, <see langword="false"/> for maximum.<br/></param>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A dictionary keyed by grouping-index key with one extreme value per matching group.<br/></returns>
    private IReadOnlyDictionary<TKey, TValue> MaterializeExtreme<TValue>(
        Func<TIdentity, TValue> valueSelector,
        IComparer<TValue> comparer,
        bool keepMinimum,
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        LibraDexGroupedDictionary<TKey, TValue> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in MaterializeOrderedGroups(ordering, deduplication))
        {
            if (group.Value.Count == 0)
            {
                continue;
            }

            TValue currentValue = valueSelector(group.Value[0]);
            for (int i = 1; i < group.Value.Count; i++)
            {
                TValue value = valueSelector(group.Value[i]);
                if (ShouldReplaceExtreme(currentValue, value, comparer, keepMinimum))
                {
                    currentValue = value;
                }
            }

            result.Add(group.Key, currentValue);
        }

        return result;
    }

    /// <summary>
    /// Decides whether a candidate value replaces the current grouped minimum or maximum.<br/>
    /// </summary>
    /// <typeparam name="TValue">The projected value type.<br/></typeparam>
    /// <param name="current">The current selected value.<br/></param>
    /// <param name="candidate">The candidate value.<br/></param>
    /// <param name="comparer">The comparer used to choose the winning value.<br/></param>
    /// <param name="keepMinimum"><see langword="true"/> for minimum, <see langword="false"/> for maximum.<br/></param>
    /// <returns><see langword="true"/> when the candidate should replace the current value.<br/></returns>
    private static bool ShouldReplaceExtreme<TValue>(
        TValue current,
        TValue candidate,
        IComparer<TValue> comparer,
        bool keepMinimum)
    {
        int comparison = comparer.Compare(candidate, current);
        return keepMinimum ? comparison < 0 : comparison > 0;
    }

    /// <summary>
    /// Opens a dictionary-backed grouped reader over this adopted condition's matching identities.<br/>
    /// Future physical group extents can replace the backing materializer while preserving this condition-terminal call shape.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A grouped reader over matching identities keyed by the grouping index.</returns>
    public LibraDexGroupReader<TKey, TIdentity> OpenReader(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return new LibraDexGroupReader<TKey, TIdentity>(MaterializeOrderedGroups(ordering, deduplication));
    }

    /// <summary>
    /// Opens a row-streaming grouped reader over this adopted condition's matching identities.<br/>
    /// This reader preserves grouped-result order while avoiding per-group member-list materialization for the streamable key-ascending/member-ascending shape.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.</param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.</param>
    /// <returns>A grouped row reader that reports group boundaries as rows are streamed.</returns>
    public LibraDexGroupRowReader<TKey, TIdentity> OpenRowReader(
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        EnsureCanStreamRows();
        return new LibraDexGroupRowReader<TKey, TIdentity>(StreamRows(ordering, deduplication));
    }

    /// <summary>
    /// Validates that the current grouped query shape can be streamed without pre-materializing group members.<br/>
    /// Count filters and count ordering require knowing full group counts before returning rows, while descending item order requires reversing group members.<br/>
    /// </summary>
    private void EnsureCanStreamRows()
    {
        if (minimumCount is not null || maximumCount is not null)
        {
            throw new NotSupportedException("Grouped row streaming does not support count filters because group counts are not known until each group is drained; use Metadata, Counts, or the materialized grouped reader for count-filtered groups.");
        }

        if (groupOrder != LibraDexGroupOrder.KeyAscending)
        {
            throw new NotSupportedException("Grouped row streaming currently supports key-ascending group order only; use Metadata, Counts, or the materialized grouped reader for other group orders.");
        }

        if (itemDirection != QueryDirection.Ascending)
        {
            throw new NotSupportedException("Grouped row streaming currently supports ascending item order only; use the materialized grouped reader for descending group members.");
        }
    }

    /// <summary>
    /// Streams matching grouped rows from ordered grouping-index tuples without allocating per-group member lists.<br/>
    /// Direct target-index conditions use the shared cursor executor, while unrelated-index conditions fall back to local candidate-set membership.<br/>
    /// </summary>
    /// <param name="ordering">The identity ordering used when materializing the condition candidate set.<br/></param>
    /// <param name="deduplication">The duplicate identity policy used for the condition candidate set.<br/></param>
    /// <returns>A forward-only grouped row sequence.<br/></returns>
    private IEnumerable<LibraDexGroupRow<TKey, TIdentity>> StreamRows(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        bool hasCurrent = false;
        TKey currentKey = default!;
        long groupOrdinal = -1;
        long itemOrdinal = 0;

        foreach (LibraDexTuple<TKey, TIdentity> tuple in IterateMatchingTuples(ordering, deduplication))
        {
            if (!hasCurrent || !LibraDexKeyEquality<TKey>.Comparer.Equals(tuple.Key, currentKey))
            {
                hasCurrent = true;
                currentKey = tuple.Key;
                groupOrdinal++;
                itemOrdinal = 0;
                if (takeGroups is not null && groupOrdinal >= takeGroups.Value)
                {
                    yield break;
                }

                yield return new LibraDexGroupRow<TKey, TIdentity>(tuple.Key, tuple.Identity, IsFirstInGroup: true, groupOrdinal, itemOrdinal);
                itemOrdinal++;
                continue;
            }

            yield return new LibraDexGroupRow<TKey, TIdentity>(tuple.Key, tuple.Identity, IsFirstInGroup: false, groupOrdinal, itemOrdinal);
            itemOrdinal++;
        }
    }

    private IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> MaterializeOrderedGroups(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> groups = BuildGroups(ordering, deduplication);
        IEnumerable<KeyValuePair<TKey, IReadOnlyList<TIdentity>>> ordered = groupOrder switch
        {
            LibraDexGroupOrder.KeyAscending => groups,
            LibraDexGroupOrder.KeyDescending => groups.Reverse(),
            LibraDexGroupOrder.CountAscending => groups.OrderBy(group => group.Value.Count),
            LibraDexGroupOrder.CountDescending => groups.OrderByDescending(group => group.Value.Count),
            _ => throw new NotSupportedException($"Group order {groupOrder} is not supported.")
        };

        if (takeGroups is not null)
        {
            ordered = ordered.Take(takeGroups.Value);
        }

        LibraDexGroupedDictionary<TKey, IReadOnlyList<TIdentity>> result = new();
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in ordered)
        {
            result.Add(group.Key, group.Value);
        }

        return result;
    }

    private static LibraDexGroupMetadata<TKey, TIdentity> CreateMetadata(TKey key, IReadOnlyList<TIdentity> identities)
    {
        return new LibraDexGroupMetadata<TKey, TIdentity>(
            key,
            identities.Count,
            identities[0],
            identities[identities.Count - 1]);
    }

    private IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> BuildGroups(
        IdentityResultOrdering ordering,
        IdentityDeduplication deduplication)
    {
        HashSet<TIdentity> identities = CreateCandidateIdentitySet(ordering, deduplication);
        LibraDexGroupedDictionary<TKey, List<TIdentity>> groups = new();
        foreach (LibraDexTuple<TKey, TIdentity> tuple in iterateAllTuples())
        {
            if (!identities.Contains(tuple.Identity))
            {
                continue;
            }

            if (!groups.TryGetValue(tuple.Key, out List<TIdentity>? group))
            {
                group = new List<TIdentity>();
                groups.Add(tuple.Key, group);
            }

            group.Add(tuple.Identity);
        }

        LibraDexGroupedDictionary<TKey, IReadOnlyList<TIdentity>> result = new();
        foreach (KeyValuePair<TKey, List<TIdentity>> group in groups)
        {
            if ((minimumCount is not null && group.Value.Count < minimumCount.Value) ||
                (maximumCount is not null && group.Value.Count > maximumCount.Value))
            {
                continue;
            }

            if (itemDirection == QueryDirection.Descending)
            {
                group.Value.Reverse();
            }
            else if (itemDirection != QueryDirection.Ascending)
            {
                throw new NotSupportedException($"Group item direction {itemDirection} is not supported.");
            }

            result.Add(group.Key, group.Value);
        }

        return result;
    }

    private enum LibraDexGroupingTerminal
    {
        Counts = 0,
        Metadata = 1,
        Representatives = 2
    }

    private enum LibraDexGroupingStrategy
    {
        MaterializedGroups = 0,
        StreamingCounts = 1,
        StreamingMetadata = 2,
        StreamingRepresentatives = 3
    }

    private readonly record struct LibraDexStreamedRepresentative(
        TKey Key,
        long Count,
        TIdentity FirstIdentity,
        TIdentity LastIdentity);

    private readonly record struct LibraDexStreamedAggregate<TAggregate>(
        TKey Key,
        long Count,
        TAggregate Value);

}

/// <summary>
/// Provides a null-key-capable grouped result dictionary while preserving the public <see cref="IReadOnlyDictionary{TKey, TValue}"/> contract.<br/>
/// The standard <see cref="Dictionary{TKey, TValue}"/> rejects null reference keys, but LibraDex string indexes have a real null-key state that grouped terminals must report distinctly from empty strings.<br/>
/// </summary>
/// <typeparam name="TKey">The grouped key type.<br/></typeparam>
/// <typeparam name="TValue">The grouped value type.<br/></typeparam>
internal sealed class LibraDexGroupedDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
{
    private readonly Dictionary<LibraDexGroupedKey<TKey>, TValue> inner = new();

    /// <summary>
    /// Gets or sets one grouped value by logical grouped key, including a null reference key when the key domain allows one.<br/>
    /// The setter is internal grouping infrastructure; public callers see this type through the read-only dictionary interface.<br/>
    /// </summary>
    /// <param name="key">The grouped key.<br/></param>
    /// <returns>The grouped value.</returns>
    public TValue this[TKey key]
    {
        get => inner[LibraDexGroupedKey<TKey>.Create(key)];
        set => inner[LibraDexGroupedKey<TKey>.Create(key)] = value;
    }

    /// <summary>
    /// Gets the logical grouped keys in insertion order, including a null key when one was added.<br/>
    /// </summary>
    public IEnumerable<TKey> Keys
    {
        get
        {
            foreach (LibraDexGroupedKey<TKey> key in inner.Keys)
            {
                yield return key.Value;
            }
        }
    }

    /// <summary>
    /// Gets the grouped values in insertion order.<br/>
    /// </summary>
    public IEnumerable<TValue> Values => inner.Values;

    /// <summary>
    /// Gets the number of grouped entries.<br/>
    /// </summary>
    public int Count => inner.Count;

    /// <summary>
    /// Adds one grouped result entry.<br/>
    /// Duplicate keys are rejected with the same semantics as <see cref="Dictionary{TKey, TValue}.Add(TKey, TValue)"/>.<br/>
    /// </summary>
    /// <param name="key">The grouped key, including null when supported by the source key domain.<br/></param>
    /// <param name="value">The grouped value.<br/></param>
    public void Add(TKey key, TValue value)
        => inner.Add(LibraDexGroupedKey<TKey>.Create(key), value);

    /// <summary>
    /// Returns whether the dictionary contains the supplied logical grouped key.<br/>
    /// </summary>
    /// <param name="key">The grouped key to test.<br/></param>
    /// <returns><see langword="true"/> when the key exists; otherwise, <see langword="false"/>.</returns>
    public bool ContainsKey(TKey key)
        => inner.ContainsKey(LibraDexGroupedKey<TKey>.Create(key));

    /// <summary>
    /// Attempts to read a grouped value by logical grouped key.<br/>
    /// This method accepts null keys for nullable key domains instead of throwing before lookup.<br/>
    /// </summary>
    /// <param name="key">The grouped key to find.<br/></param>
    /// <param name="value">Receives the grouped value when found.<br/></param>
    /// <returns><see langword="true"/> when the key exists; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value)
        => inner.TryGetValue(LibraDexGroupedKey<TKey>.Create(key), out value);

    /// <summary>
    /// Enumerates grouped entries as ordinary key/value pairs.<br/>
    /// The yielded key is the logical grouped key, not the internal null-safe wrapper.<br/>
    /// </summary>
    /// <returns>A grouped result enumerator.</returns>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (KeyValuePair<LibraDexGroupedKey<TKey>, TValue> entry in inner)
        {
            yield return new KeyValuePair<TKey, TValue>(entry.Key.Value, entry.Value);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}

/// <summary>
/// Wraps grouped keys so null can participate in hash dictionaries without being collapsed into another value.<br/>
/// Equality delegates non-null keys to LibraDex's key comparer, preserving byte-array and composite key equality behavior used by existing grouped terminals.<br/>
/// </summary>
/// <typeparam name="TKey">The grouped key type.<br/></typeparam>
internal readonly struct LibraDexGroupedKey<TKey> : IEquatable<LibraDexGroupedKey<TKey>>
{
#pragma warning disable CS8714
    private static readonly IEqualityComparer<TKey> Comparer = LibraDexKeyEquality<TKey>.Comparer;
#pragma warning restore CS8714
    private readonly bool isNull;

    private LibraDexGroupedKey(TKey value)
    {
        Value = value;
        isNull = value is null;
    }

    /// <summary>
    /// Gets the logical grouped key value.<br/>
    /// This value can be null when the source key domain has a null-key state.<br/>
    /// </summary>
    public TKey Value { get; }

    /// <summary>
    /// Creates a null-safe grouped key wrapper.<br/>
    /// </summary>
    /// <param name="value">The logical grouped key value.<br/></param>
    /// <returns>A grouped key wrapper suitable for hash dictionary storage.<br/></returns>
    public static LibraDexGroupedKey<TKey> Create(TKey value)
        => new(value);

    /// <summary>
    /// Compares two grouped key wrappers using null identity first and LibraDex key equality for non-null values.<br/>
    /// </summary>
    /// <param name="other">The grouped key wrapper to compare.<br/></param>
    /// <returns><see langword="true"/> when the keys are equal; otherwise, <see langword="false"/>.</returns>
    public bool Equals(LibraDexGroupedKey<TKey> other)
    {
        if (isNull || other.isNull)
        {
            return isNull == other.isNull;
        }

        return Comparer.Equals(Value, other.Value);
    }

    /// <summary>
    /// Compares this grouped key wrapper with an arbitrary object.<br/>
    /// </summary>
    /// <param name="obj">The object to compare.<br/></param>
    /// <returns><see langword="true"/> when the object is an equal grouped key wrapper; otherwise, <see langword="false"/>.</returns>
    public override bool Equals(object? obj)
        => obj is LibraDexGroupedKey<TKey> other && Equals(other);

    /// <summary>
    /// Returns a hash code that keeps null distinct and delegates non-null hashing to LibraDex key equality.<br/>
    /// </summary>
    /// <returns>The grouped key hash code.</returns>
    public override int GetHashCode()
        => isNull ? 0 : Comparer.GetHashCode(Value!);
}

/// <summary>
/// Starts or continues an adopted condition by selecting the next LibraDex index name.<br/>
/// This class is the LibraDex replacement for Abraxas' property-path clause: the selected string is an index name inside the active identity group.<br/>
/// </summary>
