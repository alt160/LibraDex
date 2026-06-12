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
    private readonly LibraDexIndex<TKey, TIdentity> index;
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
    {
        this.condition = condition;
        this.resolveIndex = resolveIndex;
        this.index = index;
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

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, count, count, groupOrder, itemDirection, takeGroups);
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

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, groupOrder, itemDirection, takeGroups);
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

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, groupOrder, itemDirection, takeGroups);
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
        => new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, order, itemDirection, takeGroups);

    /// <summary>
    /// Returns a grouped query with explicit item ordering inside each group.<br/>
    /// </summary>
    /// <param name="direction">The identity ordering to apply inside each returned group.</param>
    /// <returns>A new grouped query with the requested item ordering.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> OrderItemsBy(QueryDirection direction)
        => new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, groupOrder, direction, takeGroups);

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

        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, groupOrder, itemDirection, count);
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
        IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> groups = MaterializeOrderedGroups(ordering, deduplication);
#pragma warning disable CS8714
        Dictionary<TKey, long> result = new(LibraDexKeyEquality<TKey>.Comparer);
#pragma warning restore CS8714
        foreach (KeyValuePair<TKey, IReadOnlyList<TIdentity>> group in groups)
        {
            result.Add(group.Key, group.Value.Count);
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
        IReadOnlyDictionary<TKey, IReadOnlyList<TIdentity>> groups = MaterializeOrderedGroups(ordering, deduplication);
#pragma warning disable CS8714
        Dictionary<TKey, TIdentity> result = new(LibraDexKeyEquality<TKey>.Comparer);
#pragma warning restore CS8714
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

#pragma warning disable CS8714
        Dictionary<TKey, IReadOnlyList<TIdentity>> result = new(LibraDexKeyEquality<TKey>.Comparer);
#pragma warning restore CS8714
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
#pragma warning disable CS8714
        HashSet<TIdentity> identities = new(condition.ToList<TIdentity>(resolveIndex, ordering, deduplication), LibraDexKeyEquality<TIdentity>.Comparer);
#pragma warning restore CS8714
#pragma warning disable CS8714
        Dictionary<TKey, List<TIdentity>> groups = new(LibraDexKeyEquality<TKey>.Comparer);
#pragma warning restore CS8714
        foreach (LibraDexTuple<TKey, TIdentity> tuple in index.IterateAllTuples())
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

#pragma warning disable CS8714
        Dictionary<TKey, IReadOnlyList<TIdentity>> result = new(LibraDexKeyEquality<TKey>.Comparer);
#pragma warning restore CS8714
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
}

/// <summary>
/// Starts or continues an adopted condition by selecting the next LibraDex index name.<br/>
/// This class is the LibraDex replacement for Abraxas' property-path clause: the selected string is an index name inside the active identity group.<br/>
/// </summary>
