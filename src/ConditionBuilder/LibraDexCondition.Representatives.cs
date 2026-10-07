namespace LibraDex;

/// <summary>
/// Represents the natural result of selecting the first or last indexed entry from each group.<br/>
/// The group key preserves the grouping projection, while the identity is the representative selected in grouping-index traversal order.<br/>
/// </summary>
/// <param name="GroupKey">The logical group key, or binary sort key when sort-key grouping was requested.<br/></param>
/// <param name="Identity">The selected first or last identity for the group.<br/></param>
public readonly record struct LibraDexRepresentativeRow(object? GroupKey, object Identity);

public sealed partial class LibraDexConditionGroupBy
{
    /// <summary>
    /// Selects the first distinct identity encountered in each group using the grouping index's natural traversal order.<br/>
    /// Filtering is applied before representative selection, so the winner is the first matching identity rather than the first unfiltered identity.<br/>
    /// Use the returned stage to select identities or indexed keys before the terminal <c>EndCondition</c>.<br/>
    /// </summary>
    public LibraDexConditionRepresentative First
        => new(new LibraDexRepresentativeSpec(
            group,
            filter,
            groupingIndexName,
            stringSubIndex,
            LibraDexGroupRepresentative.First));

    /// <summary>
    /// Selects the last distinct identity encountered in each group using the grouping index's natural traversal order.<br/>
    /// Filtering is applied before representative selection, so the winner is the last matching identity rather than the last unfiltered identity.<br/>
    /// Use the returned stage to select identities or indexed keys before the terminal <c>EndCondition</c>.<br/>
    /// </summary>
    public LibraDexConditionRepresentative Last
        => new(new LibraDexRepresentativeSpec(
            group,
            filter,
            groupingIndexName,
            stringSubIndex,
            LibraDexGroupRepresentative.Last));
}

/// <summary>
/// Selects the logical return shape of a first- or last-entry-per-group condition.<br/>
/// Identity-returning overloads place identity first; <c>ReturnKeys</c> excludes identity and returns only named indexed keys.<br/>
/// </summary>
public sealed class LibraDexConditionRepresentative
{
    private readonly LibraDexRepresentativeSpec spec;

    internal LibraDexConditionRepresentative(LibraDexRepresentativeSpec spec)
    {
        this.spec = spec;
    }

    /// <summary>
    /// Completes the condition with its natural representative result: group key plus selected identity.<br/>
    /// Use <see cref="Return()"/> or <see cref="Return{TIdentity}()"/> when only representative identities should be returned.<br/>
    /// </summary>
    public LibraDexCondition<LibraDexRepresentativeRow> EndCondition => Create(
        Array.Empty<string>(),
        static row => new LibraDexRepresentativeRow(row.GroupValue.Value, row.Identity.Value!),
        "RepresentativeRow");

    /// <summary>
    /// Orders natural representative rows by grouping key in ascending order.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexRepresentativeRow> OrderByAscending()
        => End(EndCondition).OrderByAscending();

    /// <summary>
    /// Orders natural representative rows by grouping key in descending order.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexRepresentativeRow> OrderByDescending()
        => End(EndCondition).OrderByDescending();

    /// <summary>
    /// Keeps the first non-zero number of representative rows in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of rows to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexRepresentativeRow> Top(int count)
        => End(EndCondition).Top(count);

    /// <summary>
    /// Keeps the last non-zero number of representative rows in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of rows to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexRepresentativeRow> Bottom(int count)
        => End(EndCondition).Bottom(count);

    /// <summary>
    /// Selects representative identities in their binary form.<br/>
    /// Returned arrays are caller-owned and remain stable after the getter, iterator, or reader advances.<br/>
    /// </summary>
    /// <returns>A terminal stage whose result type is a raw identity byte array.<br/></returns>
    public LibraDexConditionResultEnd<byte[]> Return()
        => End(Create(Array.Empty<string>(), static row => row.Identity.As<byte[]>(), "Identity.Bytes"));

    /// <summary>
    /// Selects representative identities converted to <typeparamref name="TIdentity"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <returns>A terminal stage returning one representative identity per group.<br/></returns>
    public LibraDexConditionResultEnd<TIdentity> Return<TIdentity>()
        => End(Create(Array.Empty<string>(), static row => row.Identity.As<TIdentity>(), "Identity"));

    /// <summary>
    /// Selects each representative identity followed by one named indexed key.<br/>
    /// Generic parameter order matches result order: identity first, then the key named by <paramref name="index1"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A terminal stage returning identity/key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1)> Return<TIdentity, TKey1>(string index1)
        => End(Create(Names(index1), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>()), "Identity+1Key"));

    /// <summary>
    /// Selects each representative identity followed by two named indexed keys.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2)> Return<TIdentity, TKey1, TKey2>(
        string index1,
        string index2)
        => End(Create(
            Names(index1, index2),
            static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()),
            "Identity+2Keys"));

    /// <summary>
    /// Selects each representative identity followed by three named indexed keys.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3)> Return<TIdentity, TKey1, TKey2, TKey3>(
        string index1,
        string index2,
        string index3)
        => End(Create(
            Names(index1, index2, index3),
            static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()),
            "Identity+3Keys"));

    /// <summary>
    /// Selects each representative identity followed by four named indexed keys.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> Return<TIdentity, TKey1, TKey2, TKey3, TKey4>(
        string index1,
        string index2,
        string index3,
        string index4)
        => End(Create(
            Names(index1, index2, index3, index4),
            static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()),
            "Identity+4Keys"));

    /// <summary>
    /// Selects one named indexed key from each representative entry and excludes identity from the result.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested key type.<br/></typeparam>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A terminal stage returning one key per group.<br/></returns>
    public LibraDexConditionResultEnd<TKey1> ReturnKeys<TKey1>(string index1)
        => End(Create(Names(index1), static row => row.Keys[0].As<TKey1>(), "1Key"));

    /// <summary>
    /// Selects two named indexed keys from each representative entry and excludes identity from the result.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(
        string index1,
        string index2)
        => End(Create(
            Names(index1, index2),
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()),
            "2Keys"));

    /// <summary>
    /// Selects three named indexed keys from each representative entry and excludes identity from the result.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(
        string index1,
        string index2,
        string index3)
        => End(Create(
            Names(index1, index2, index3),
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()),
            "3Keys"));

    /// <summary>
    /// Selects four named indexed keys from each representative entry and excludes identity from the result.<br/>
    /// Generic parameters and index names map positionally to the returned tuple.<br/>
    /// </summary>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(
        string index1,
        string index2,
        string index3,
        string index4)
        => End(Create(
            Names(index1, index2, index3, index4),
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()),
            "4Keys"));

    private LibraDexCondition<TResult> Create<TResult>(
        string[] returnedIndexNames,
        Func<LibraDexRepresentativeProjectionRow, TResult> projector,
        string returnShape)
    {
        var descriptor = new LibraDexRepresentativeResultDescriptor<TResult>(
            spec,
            returnedIndexNames,
            projector,
            returnShape);
        return new LibraDexCondition<TResult>(spec.Group, spec.Filter, descriptor);
    }

    private static LibraDexConditionResultEnd<TResult> End<TResult>(LibraDexCondition<TResult> condition)
        => new(condition);

    private static string[] Names(params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
            ArgumentException.ThrowIfNullOrWhiteSpace(names[i], $"index{i + 1}");
        return names;
    }
}

internal readonly record struct LibraDexRepresentativeSpec(
    string Group,
    LibraDexConditionEndCondition? Filter,
    string GroupingIndexName,
    SubIndexType? StringSubIndex,
    LibraDexGroupRepresentative Representative);

internal sealed class LibraDexRepresentativeResultDescriptor<TResult> : ILibraDexConditionResultDescriptor<TResult>
{
    private readonly LibraDexRepresentativeSpec spec;
    private readonly string[] returnedIndexNames;
    private readonly Func<LibraDexRepresentativeProjectionRow, TResult> projector;
    private readonly string returnShape;
    private readonly LibraDexConditionResultSequence sequence;

    internal LibraDexRepresentativeResultDescriptor(
        LibraDexRepresentativeSpec spec,
        string[] returnedIndexNames,
        Func<LibraDexRepresentativeProjectionRow, TResult> projector,
        string returnShape,
        LibraDexConditionResultSequence sequence = default)
    {
        this.spec = spec;
        this.returnedIndexNames = returnedIndexNames;
        this.projector = projector;
        this.returnShape = returnShape;
        this.sequence = sequence;
    }

    public LibraDexConditionResultPlan Plan => new(
        $"GroupBy.{spec.Representative}.{returnShape}{DescribeSequence(sequence)}",
        IsBlocking: true,
        "One distinct representative identity per raw-byte group and only explicitly returned key lookups.");

    public LibraDexConditionResultSequence Sequence => sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes
    {
        get
        {
            List<LibraDexBookmarkIndexReference> indexes = new(returnedIndexNames.Length + 2)
            {
                new(spec.GroupingIndexName, null, LibraDexBookmarkIndexRole.Group | LibraDexBookmarkIndexRole.NaturalOrder)
            };
            for (int i = 0; i < returnedIndexNames.Length; i++)
                indexes.Add(new LibraDexBookmarkIndexReference(returnedIndexNames[i], null, LibraDexBookmarkIndexRole.Return));
            if (sequence.OrderIndexName is not null)
                indexes.Add(new LibraDexBookmarkIndexReference(sequence.OrderIndexName, null, LibraDexBookmarkIndexRole.Order));
            return indexes;
        }
    }

    public IEnumerable<TResult> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexRepresentativeProjectionRow row in LibraDexRepresentativeExecutor.Execute(
            indexes,
            spec,
            returnedIndexNames,
            sequence))
        {
            yield return projector(row);
        }
    }

    public IEnumerable<LibraDexBookmarkResult<TResult>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        foreach (LibraDexRepresentativeProjectionRow row in LibraDexRepresentativeExecutor.Execute(
            indexes,
            spec,
            returnedIndexNames,
            sequence))
        {
            yield return new LibraDexBookmarkResult<TResult>(
                projector(row),
                new LibraDexBookmarkAnchor(row.OrderValue, row.Identity.Value));
        }
    }

    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexRepresentativeWinner winner in LibraDexRepresentativeExecutor.ExecuteWinners(indexes, spec, sequence))
            yield return winner.Identity;
    }

    public ILibraDexConditionResultDescriptor<TResult> WithSequence(LibraDexConditionResultSequence value)
        => new LibraDexRepresentativeResultDescriptor<TResult>(spec, returnedIndexNames, projector, returnShape, value);

    public ILibraDexConditionResultDescriptor<TResult> WithFilter(LibraDexConditionEndCondition? filter)
        => new LibraDexRepresentativeResultDescriptor<TResult>(
            spec with { Filter = filter },
            returnedIndexNames,
            projector,
            returnShape,
            sequence);

    private static string DescribeSequence(LibraDexConditionResultSequence value)
    {
        string order = value.Direction switch
        {
            QueryDirection.Ascending => ".OrderAscending",
            QueryDirection.Descending => ".OrderDescending",
            _ => ".NaturalOrder"
        };
        if (!value.Count.HasValue)
            return order;
        return $"{order}.{(value.IsBottom ? "Bottom" : "Top")}({value.Count.Value})";
    }
}

internal static class LibraDexRepresentativeExecutor
{
    internal static IEnumerable<LibraDexRepresentativeProjectionRow> Execute(
        CatalogIdentityGroupIndexes indexes,
        LibraDexRepresentativeSpec spec,
        string[] returnedIndexNames,
        LibraDexConditionResultSequence sequence)
    {
        Dictionary<string, LibraDexAggregateReturnLookup> lookups = new(StringComparer.Ordinal);
        for (int i = 0; i < returnedIndexNames.Length; i++)
        {
            string name = returnedIndexNames[i];
            if (string.Equals(name, spec.GroupingIndexName, StringComparison.Ordinal) || lookups.ContainsKey(name))
                continue;
            IIndex returnedIndex = indexes.Index(name);
            EnsureIndexGroup(spec, returnedIndex);
            lookups.Add(name, LibraDexAggregateReturnLookup.Create(returnedIndex));
        }

        LibraDexAggregateReturnLookup? orderLookup = null;
        if (sequence.OrderIndexName is not null &&
            !string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal))
        {
            if (!lookups.TryGetValue(sequence.OrderIndexName, out orderLookup))
            {
                IIndex orderIndex = indexes.Index(sequence.OrderIndexName);
                EnsureIndexGroup(spec, orderIndex);
                orderLookup = LibraDexAggregateReturnLookup.Create(orderIndex);
                lookups.Add(sequence.OrderIndexName, orderLookup);
            }
        }

        foreach (LibraDexRepresentativeWinner winner in ExecuteWinners(indexes, spec, sequence, orderLookup))
        {
            LibraDexResultValue[] keys = returnedIndexNames.Length == 0
                ? Array.Empty<LibraDexResultValue>()
                : new LibraDexResultValue[returnedIndexNames.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string name = returnedIndexNames[i];
                keys[i] = string.Equals(name, spec.GroupingIndexName, StringComparison.Ordinal)
                    ? new LibraDexResultValue(winner.GroupValue, winner.GroupingIndex, IsIdentity: false)
                    : lookups[name].Get(winner.Identity);
            }

            object? orderValue = ResolveOrderValue(indexes, spec, sequence, winner, lookups);
            yield return new LibraDexRepresentativeProjectionRow(
                new LibraDexResultValue(winner.GroupValue, winner.GroupingIndex, IsIdentity: false),
                new LibraDexResultValue(winner.Identity, winner.GroupingIndex, IsIdentity: true),
                keys,
                orderValue);
        }
    }

    internal static IEnumerable<LibraDexRepresentativeWinner> ExecuteWinners(
        CatalogIdentityGroupIndexes indexes,
        LibraDexRepresentativeSpec spec,
        LibraDexConditionResultSequence sequence,
        LibraDexAggregateReturnLookup? preparedOrderLookup = null)
    {
        IIndex groupingIndex = indexes.Index(spec.GroupingIndexName);
        EnsureIndexGroup(spec, groupingIndex);
        HashSet<LibraDexRuntimeValueKey>? filtered = LibraDexAggregateExecutor.CreateFilter(indexes, spec.Filter);
        Dictionary<LibraDexRuntimeValueKey, LibraDexRepresentativeGroupState> groups = new();
        List<LibraDexRuntimeValueKey> groupOrder = new();

        foreach (LibraDexGroupingTuple tuple in LibraDexAggregateExecutor.IterateGroupingTuples(
            groupingIndex,
            spec.StringSubIndex))
        {
            LibraDexRuntimeValueKey identity = new(tuple.Identity);
            if (filtered is not null && !filtered.Contains(identity))
                continue;

            LibraDexRuntimeValueKey groupKey = new(tuple.RawGroupKey);
            if (!groups.TryGetValue(groupKey, out LibraDexRepresentativeGroupState? state))
            {
                state = new LibraDexRepresentativeGroupState(tuple.DisplayGroupKey, tuple.Identity);
                groups.Add(groupKey, state);
                groupOrder.Add(groupKey);
                continue;
            }

            state.Consider(tuple.Identity);
        }

        List<LibraDexRepresentativeWinner> winners = new(groupOrder.Count);
        for (int i = 0; i < groupOrder.Count; i++)
        {
            LibraDexRepresentativeGroupState state = groups[groupOrder[i]];
            object identity = spec.Representative == LibraDexGroupRepresentative.First
                ? state.FirstIdentity
                : state.LastIdentity;
            winners.Add(new LibraDexRepresentativeWinner(state.GroupValue, identity, groupingIndex));
        }

        if (sequence.Direction.HasValue)
        {
            int direction = sequence.Direction == QueryDirection.Ascending ? 1 : -1;
            LibraDexAggregateReturnLookup? orderLookup = preparedOrderLookup;
            if (sequence.OrderIndexName is not null &&
                !string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal) &&
                orderLookup is null)
            {
                IIndex orderIndex = indexes.Index(sequence.OrderIndexName);
                EnsureIndexGroup(spec, orderIndex);
                orderLookup = LibraDexAggregateReturnLookup.Create(orderIndex);
            }

            winners.Sort((left, right) =>
            {
                object? leftValue = orderLookup is null
                    ? left.GroupValue
                    : orderLookup.Get(left.Identity).Value;
                object? rightValue = orderLookup is null
                    ? right.GroupValue
                    : orderLookup.Get(right.Identity).Value;
                int comparison = LibraDexAggregateExecutor.Compare(leftValue, rightValue) * direction;
                return comparison != 0
                    ? comparison
                    : LibraDexAggregateExecutor.Compare(left.GroupValue, right.GroupValue);
            });
        }

        int start = 0;
        int count = winners.Count;
        if (sequence.Count.HasValue && sequence.Count.Value < count)
        {
            count = sequence.Count.Value;
            if (sequence.IsBottom)
                start = winners.Count - count;
        }

        for (int i = start, end = start + count; i < end; i++)
            yield return winners[i];
    }

    private static object? ResolveOrderValue(
        CatalogIdentityGroupIndexes indexes,
        LibraDexRepresentativeSpec spec,
        LibraDexConditionResultSequence sequence,
        LibraDexRepresentativeWinner winner,
        Dictionary<string, LibraDexAggregateReturnLookup> lookups)
    {
        if (sequence.OrderIndexName is null ||
            string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal))
        {
            return winner.GroupValue;
        }

        if (!lookups.TryGetValue(sequence.OrderIndexName, out LibraDexAggregateReturnLookup? lookup))
        {
            IIndex orderIndex = indexes.Index(sequence.OrderIndexName);
            EnsureIndexGroup(spec, orderIndex);
            lookup = LibraDexAggregateReturnLookup.Create(orderIndex);
            lookups.Add(sequence.OrderIndexName, lookup);
        }

        return lookup.Get(winner.Identity).Value;
    }

    private static void EnsureIndexGroup(LibraDexRepresentativeSpec spec, IIndex index)
    {
        if (!string.Equals(index.Group, spec.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Index '{index.Name}' belongs to identity group '{index.Group}', not '{spec.Group}'.");
        }
    }
}

internal sealed class LibraDexRepresentativeGroupState
{
    private HashSet<LibraDexRuntimeValueKey>? identities;

    internal LibraDexRepresentativeGroupState(object? groupValue, object firstIdentity)
    {
        GroupValue = groupValue;
        FirstIdentity = firstIdentity;
        LastIdentity = firstIdentity;
    }

    internal object? GroupValue { get; }

    internal object FirstIdentity { get; }

    internal object LastIdentity { get; private set; }

    internal void Consider(object identity)
    {
        LibraDexRuntimeValueKey candidate = new(identity);
        if (identities is null)
        {
            if (candidate.Equals(new LibraDexRuntimeValueKey(FirstIdentity)))
                return;
            identities = new HashSet<LibraDexRuntimeValueKey>
            {
                new(FirstIdentity),
                candidate
            };
            LastIdentity = identity;
            return;
        }

        if (identities.Add(candidate))
            LastIdentity = identity;
    }
}

internal readonly record struct LibraDexRepresentativeWinner(
    object? GroupValue,
    object Identity,
    IIndex GroupingIndex);

internal readonly record struct LibraDexRepresentativeProjectionRow(
    LibraDexResultValue GroupValue,
    LibraDexResultValue Identity,
    LibraDexResultValue[] Keys,
    object? OrderValue);
