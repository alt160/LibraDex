namespace LibraDex;

public sealed partial class CatalogIdentityGroupIndexes
{
    /// <summary>
    /// Starts an unfiltered condition that returns keys from one named index.<br/>
    /// Selecting the result index implicitly selects every entry visible through that index, so no public <c>All()</c> predicate is required.<br/>
    /// </summary>
    /// <typeparam name="TKey">The requested key result type.<br/></typeparam>
    /// <param name="indexName">The index supplying returned keys and natural result order.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<TKey> ReturnKeys<TKey>(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var result = new LibraDexProjectedResultDescriptor<TKey>(
            Name,
            filter: null,
            new[] { indexName },
            indexName,
            static row => row.Keys[0].As<TKey>(),
            "1Key");
        return new LibraDexConditionResultEnd<TKey>(new LibraDexCondition<TKey>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns two scalar keys for each identity visible through the first named index.<br/>
    /// The first index supplies natural order unless a later named <c>OrderBy</c> selects another index.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(string index1, string index2)
    {
        string[] names = Names(index1, index2);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()),
            "2Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns three scalar keys for each identity visible through the first named index.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(string index1, string index2, string index3)
    {
        string[] names = Names(index1, index2, index3);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()),
            "3Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2, TKey3 Key3)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered condition that returns four scalar keys for each identity visible through the first named index.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key and natural order.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
    {
        string[] names = Names(index1, index2, index3, index4);
        var result = new LibraDexProjectedResultDescriptor<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(
            Name,
            filter: null,
            names,
            index1,
            static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()),
            "4Keys");
        return new LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(new LibraDexCondition<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)>(Name, filter: null, result));
    }

    /// <summary>
    /// Starts an unfiltered grouped aggregate condition over one named index in this identity group.<br/>
    /// Exact stored key bytes define groups by default; use <c>AsString(SubIndexType)</c> only when a maintained Folded or SortKey projection is intended.<br/>
    /// </summary>
    /// <param name="indexName">The index whose exact stored key bytes define groups.<br/></param>
    /// <returns>A grouped continuation that requires an aggregate selection before termination.<br/></returns>
    public LibraDexConditionGroupBy GroupBy(string indexName)
        => new(Name, filter: null, indexName);

    /// <summary>
    /// Materializes a caller-owned result collection from a completed typed condition.<br/>
    /// Skip and take define the requested collection window up front; use <see cref="OpenReader{TResult}(LibraDexCondition{TResult})"/> for adaptive movement during consumption.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <param name="skip">The number of logical results to skip before collection begins.<br/></param>
    /// <param name="take">The optional maximum number of logical results to return.<br/></param>
    /// <returns>A caller-owned result array for the requested window.<br/></returns>
    public IReadOnlyList<TResult> Get<TResult>(
        LibraDexCondition<TResult> condition,
        int skip = 0,
        int? take = null)
    {
        ValidateResultCondition(condition, skip, take);
        if (take == 0)
            return Array.Empty<TResult>();

        List<TResult> results = take.HasValue ? new List<TResult>(take.Value) : new List<TResult>();
        int skipped = 0;
        foreach (TResult result in ExecuteResultCondition(condition))
        {
            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            results.Add(result);
            if (take.HasValue && results.Count >= take.Value)
                break;
        }

        return results.ToArray();
    }

    /// <summary>
    /// Streams the logical sequence described by a completed typed condition.<br/>
    /// The sequence may internally buffer when <see cref="LibraDexCondition{TResult}.Plan"/> reports a blocking result shape, but it does not create the caller-owned collection used by <see cref="Get{TResult}(LibraDexCondition{TResult}, int, int?)"/>.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <returns>The condition's logical result sequence.<br/></returns>
    public IEnumerable<TResult> Iterate<TResult>(LibraDexCondition<TResult> condition)
    {
        ValidateResultCondition(condition, skip: 0, take: null);
        return ExecuteResultCondition(condition);
    }

    /// <summary>
    /// Opens a live forward-only reader over the logical sequence described by a completed typed condition.<br/>
    /// The reader owns adaptive <c>Skip</c> and <c>Pull</c> movement while the condition remains an immutable selection-and-result descriptor.<br/>
    /// </summary>
    /// <typeparam name="TResult">The logical result type declared by the condition.<br/></typeparam>
    /// <param name="condition">The completed typed condition to execute.<br/></param>
    /// <returns>A live reader over the condition's logical results.<br/></returns>
    public LibraDexResultReader<TResult> OpenReader<TResult>(LibraDexCondition<TResult> condition)
    {
        ValidateResultCondition(condition, skip: 0, take: null);
        return new LibraDexResultReader<TResult>(ExecuteResultCondition(condition));
    }

    private IEnumerable<TResult> ExecuteResultCondition<TResult>(LibraDexCondition<TResult> condition)
        => condition.Result.Iterate(this);

    private void ValidateResultCondition<TResult>(LibraDexCondition<TResult> condition, int skip, int? take)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.Equals(condition.Group, Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied result condition belongs to a different LibraDex index set.");

        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");

        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be zero or greater.");
    }

    private static string[] Names(params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
            ArgumentException.ThrowIfNullOrWhiteSpace(names[i], $"index{i + 1}");

        return names;
    }
}
