using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Selects the aggregate operation applied after grouped condition selection.<br/>
/// Min and Max select one winning indexed row per group and retain its identity for optional return shaping.<br/>
/// Count returns one group-key/cardinality row and retains every selected identity in groups that survive result ordering and limiting.<br/>
/// </summary>
public enum AggType
{
    /// <summary>Returns the minimum aggregate-index key in each group.<br/></summary>
    Min = 0,
    /// <summary>Returns the maximum aggregate-index key in each group.<br/></summary>
    Max = 1,
    /// <summary>Returns the selected identity count in each group.<br/></summary>
    Count = 2
}

/// <summary>
/// Selects a string projection for grouping.<br/>
/// A matching maintained subindex is used when available; explicitly selecting a missing projection accepts scan-time conversion over exact stored values.<br/>
/// Ordinary <c>GroupBy(indexName)</c> uses the index's exact stored bytes and does not require this enum.<br/>
/// </summary>
public enum SubIndexType
{
    /// <summary>Groups by the culture-folded string projection.<br/></summary>
    Folded = 0,
    /// <summary>Groups by the culture sort-key byte projection.<br/></summary>
    SortKey = 1,
    /// <summary>Groups by the case-preserving normalized string projection.<br/></summary>
    Normalized = 2
}

/// <summary>
/// Represents the natural result of a grouped aggregate when no explicit return shape is selected.<br/>
/// The group key and aggregate value are the two criteria named by <c>GroupBy</c> and <c>Aggregate</c>.<br/>
/// </summary>
/// <param name="GroupKey">The logical group key, or binary sort key when sort-key grouping was requested.<br/></param>
/// <param name="AggregateValue">The selected minimum or maximum aggregate-index key.<br/></param>
public readonly record struct LibraDexAggregateRow(object? GroupKey, object? AggregateValue);

/// <summary>
/// Describes how a completed typed condition result will be produced.<br/>
/// A blocking plan must inspect the complete logical input before its first result can be returned.<br/>
/// </summary>
/// <param name="Shape">The logical result shape selected by the condition.<br/></param>
/// <param name="IsBlocking">Whether the result requires complete-input inspection before output begins.<br/></param>
/// <param name="Materialization">The intermediate state retained while producing the result.<br/></param>
public readonly record struct LibraDexConditionResultPlan(
    string Shape,
    bool IsBlocking,
    string Materialization);

/// <summary>
/// Represents a completed condition whose selection rules also declare its typed logical result shape.<br/>
/// The condition is immutable and performs no IO; a catalog getter, iterator, or reader executes it later.<br/>
/// </summary>
/// <typeparam name="TResult">The logical result value produced by the condition.<br/></typeparam>
public sealed class LibraDexCondition<TResult>
{
    internal LibraDexCondition(
        string group,
        LibraDexConditionEndCondition? filter,
        ILibraDexConditionResultDescriptor<TResult> result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        Group = group;
        Filter = filter;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Gets the identity-group name shared by the selection rules and result shape.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets a non-executing description of the physical state required by this result shape.<br/>
    /// </summary>
    /// <returns>The result-shape execution plan.<br/></returns>
    public LibraDexConditionResultPlan Plan() => Result.Plan;

    internal LibraDexConditionEndCondition? Filter { get; }

    internal ILibraDexConditionResultDescriptor<TResult> Result { get; }

    internal IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
        => Result.IterateSelectedIdentities(indexes);

    internal LibraDexCondition<TResult> WithSequence(LibraDexConditionResultSequence sequence)
        => new(Group, Filter, Result.WithSequence(sequence));

    internal LibraDexCondition<TResult> FreezeForBookmark(
        out bool hadDeferredSelectors,
        out bool hadDeferredValues)
    {
        if (Filter is null)
        {
            hadDeferredSelectors = false;
            hadDeferredValues = false;
            return this;
        }

        LibraDexConditionEndCondition frozenFilter = Filter.FreezeForBookmark(
            out hadDeferredSelectors,
            out hadDeferredValues);
        return new LibraDexCondition<TResult>(Group, frozenFilter, Result.WithFilter(frozenFilter));
    }

    /// <summary>
    /// Wraps the current logical result descriptor with one caller-owned projection while retaining its filter and identity group.<br/>
    /// The delegate remains deferred until result iteration so condition construction and planning never invoke caller code.<br/>
    /// </summary>
    /// <typeparam name="TTransformed">The projected logical result type.<br/></typeparam>
    /// <param name="transform">The projection applied to each produced source result.<br/></param>
    /// <returns>A condition with the same selection and a transformed result descriptor.<br/></returns>
    internal LibraDexCondition<TTransformed> Transform<TTransformed>(Func<TResult, TTransformed> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new LibraDexCondition<TTransformed>(
            Group,
            Filter,
            new LibraDexTransformedResultDescriptor<TResult, TTransformed>(Result, transform));
    }
}

/// <summary>
/// Selects the index whose stored key bytes define representative or aggregate groups.<br/>
/// Exact grouping is the default; <see cref="AsString(SubIndexType)"/> explicitly selects a string projection and accepts scan conversion when its maintained subindex is absent.<br/>
/// </summary>
public sealed partial class LibraDexConditionGroupBy
{
    private readonly string group;
    private readonly LibraDexConditionEndCondition? filter;
    private readonly string groupingIndexName;
    private readonly SubIndexType? stringSubIndex;

    internal LibraDexConditionGroupBy(
        string group,
        LibraDexConditionEndCondition? filter,
        string groupingIndexName,
        SubIndexType? stringSubIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupingIndexName);
        this.group = group;
        this.filter = filter;
        this.groupingIndexName = groupingIndexName;
        this.stringSubIndex = stringSubIndex;
    }

    /// <summary>
    /// Selects a string projection whose bytes define group equality and order.<br/>
    /// A maintained subindex is used directly when present; otherwise the explicit selection authorizes scan-time conversion over exact values.<br/>
    /// Folded grouping returns folded logical keys; sort-key grouping returns binary sort keys because culture sort keys are not reversible strings.<br/>
    /// </summary>
    /// <param name="subIndex">The Folded or SortKey projection to group through.<br/></param>
    /// <returns>A grouped continuation over the selected string projection.<br/></returns>
    public LibraDexConditionGroupBy AsString(SubIndexType subIndex)
    {
        if (subIndex is not (SubIndexType.Folded or SubIndexType.SortKey or SubIndexType.Normalized))
            throw new ArgumentOutOfRangeException(nameof(subIndex), subIndex, "Unknown string grouping subindex.");

        return new LibraDexConditionGroupBy(group, filter, groupingIndexName, subIndex);
    }

    /// <summary>
    /// Selects the minimum or maximum key from <paramref name="indexName"/> within each group.<br/>
    /// Equal aggregate values retain the first identity in grouping-index order, making repeated execution deterministic.<br/>
    /// </summary>
    /// <param name="indexName">The index whose key supplies the aggregate value.<br/></param>
    /// <param name="aggregate">The minimum or maximum selection rule.<br/></param>
    /// <returns>A result-shaping stage that can end with the natural aggregate row or select identities and keys explicitly.<br/></returns>
    public LibraDexConditionAggregate Aggregate(string indexName, AggType aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (aggregate is not (AggType.Min or AggType.Max))
            throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, "An indexed aggregate must be Min or Max. Use Aggregate(AggType.Count) for group cardinality.");

        return new LibraDexConditionAggregate(
            new LibraDexAggregateSpec(group, filter, groupingIndexName, stringSubIndex, indexName, aggregate));
    }

    /// <summary>
    /// Counts the indexed entries in each group.<br/>
    /// The natural result is one group-key/count row per group in the grouping index's ascending order.<br/>
    /// </summary>
    /// <param name="aggregate">Must be <see cref="AggType.Count"/>.<br/></param>
    /// <returns>A count-aggregate stage that can declare result ordering and a top or bottom group window.<br/></returns>
    public LibraDexConditionCountAggregate Aggregate(AggType aggregate)
    {
        if (aggregate != AggType.Count)
            throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, "A keyless aggregate must be Count. Use Aggregate(indexName, AggType.Min/Max) for scalar extrema.");

        return new LibraDexConditionCountAggregate(
            new LibraDexCountAggregateSpec(group, filter, groupingIndexName, stringSubIndex));
    }
}

/// <summary>
/// Shapes a grouped count result before the condition is terminated.<br/>
/// Natural order follows the grouping index; explicit ordering ranks the groups by count.<br/>
/// </summary>
public sealed class LibraDexConditionCountAggregate
{
    private readonly LibraDexCountAggregateSpec spec;

    internal LibraDexConditionCountAggregate(LibraDexCountAggregateSpec spec)
    {
        this.spec = spec;
    }

    /// <summary>
    /// Completes the condition with one natural group-key/count row per group.<br/>
    /// </summary>
    public LibraDexCondition<LibraDexAggregateRow> EndCondition => Create();

    /// <summary>
    /// Orders groups from the smallest count to the largest count.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexAggregateRow> OrderByAscending()
        => End(Create()).OrderByAscending();

    /// <summary>
    /// Orders groups from the largest count to the smallest count.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexAggregateRow> OrderByDescending()
        => End(Create()).OrderByDescending();

    /// <summary>
    /// Keeps the first <paramref name="count"/> groups in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of groups to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexAggregateRow> Top(int count)
        => End(Create()).Top(count);

    /// <summary>
    /// Keeps the last <paramref name="count"/> groups in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of groups to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexAggregateRow> Bottom(int count)
        => End(Create()).Bottom(count);

    private LibraDexCondition<LibraDexAggregateRow> Create()
        => new(spec.Group, spec.Filter, new LibraDexCountAggregateResultDescriptor(spec));

    private static LibraDexConditionResultEnd<TResult> End<TResult>(LibraDexCondition<TResult> condition)
        => new(condition);
}

/// <summary>
/// Selects the logical return shape of a grouped aggregate condition.<br/>
/// Identity-returning overloads place the identity first; <c>ReturnKeys</c> excludes identity and returns only named indexed keys.<br/>
/// </summary>
public sealed class LibraDexConditionAggregate
{
    private readonly LibraDexAggregateSpec spec;

    internal LibraDexConditionAggregate(LibraDexAggregateSpec spec)
    {
        this.spec = spec;
    }

    /// <summary>
    /// Completes the condition with its natural aggregate result: group key plus selected aggregate value.<br/>
    /// Use <see cref="Return()"/> when only winning identities should be returned.<br/>
    /// </summary>
    public LibraDexCondition<LibraDexAggregateRow> EndCondition => Create(
        Array.Empty<string>(),
        static row => new LibraDexAggregateRow(row.GroupValue.Value, row.AggregateValue.Value),
        "AggregateRow");

    /// <summary>
    /// Orders aggregate rows from the smallest selected aggregate value to the largest.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexAggregateRow> OrderByAscending()
        => End(EndCondition).OrderByAscending();

    /// <summary>
    /// Orders aggregate rows from the largest selected aggregate value to the smallest.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<LibraDexAggregateRow> OrderByDescending()
        => End(EndCondition).OrderByDescending();

    /// <summary>
    /// Keeps the first <paramref name="count"/> aggregate rows in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of rows to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexAggregateRow> Top(int count)
        => End(EndCondition).Top(count);

    /// <summary>
    /// Keeps the last <paramref name="count"/> aggregate rows in natural grouping-index order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of rows to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<LibraDexAggregateRow> Bottom(int count)
        => End(EndCondition).Bottom(count);

    /// <summary>
    /// Selects winning identities in their binary form.<br/>
    /// Returned arrays are caller-owned and remain stable after the getter, iterator, or reader advances.<br/>
    /// </summary>
    /// <returns>A terminal stage whose result type is a raw identity byte array.<br/></returns>
    public LibraDexConditionResultEnd<byte[]> Return()
        => End(Create(Array.Empty<string>(), static row => row.Identity.As<byte[]>(), "Identity.Bytes"));

    /// <summary>
    /// Selects winning identities converted to <typeparamref name="TIdentity"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <returns>A terminal stage returning one identity per aggregate group.<br/></returns>
    public LibraDexConditionResultEnd<TIdentity> Return<TIdentity>()
        => End(Create(Array.Empty<string>(), static row => row.Identity.As<TIdentity>(), "Identity"));

    /// <summary>
    /// Selects each winning identity followed by one named indexed key.<br/>
    /// Generic parameter order matches result order: identity first, then the key named by <paramref name="index1"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <returns>A terminal stage returning identity/key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1)> Return<TIdentity, TKey1>(string index1)
        => End(Create(Names(index1), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>()), "Identity+1Key"));

    /// <summary>
    /// Selects each winning identity followed by two named indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A terminal stage returning identity/key/key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2)> Return<TIdentity, TKey1, TKey2>(string index1, string index2)
        => End(Create(Names(index1, index2), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()), "Identity+2Keys"));

    /// <summary>
    /// Selects each winning identity followed by three named indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A terminal stage returning one identity and three keys.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3)> Return<TIdentity, TKey1, TKey2, TKey3>(string index1, string index2, string index3)
        => End(Create(Names(index1, index2, index3), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()), "Identity+3Keys"));

    /// <summary>
    /// Selects each winning identity followed by four named indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A terminal stage returning one identity and four keys.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> Return<TIdentity, TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
        => End(Create(Names(index1, index2, index3, index4), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()), "Identity+4Keys"));

    /// <summary>
    /// Selects one named indexed key from each aggregate winner and excludes identity from the result.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested key type.<br/></typeparam>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A terminal stage returning one scalar key per group.<br/></returns>
    public LibraDexConditionResultEnd<TKey1> ReturnKeys<TKey1>(string index1)
        => End(Create(Names(index1), static row => row.Keys[0].As<TKey1>(), "1Key"));

    /// <summary>
    /// Selects two named indexed keys from each aggregate winner and excludes identity from the result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A terminal stage returning two keys per group.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(string index1, string index2)
        => End(Create(Names(index1, index2), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()), "2Keys"));

    /// <summary>
    /// Selects three named indexed keys from each aggregate winner and excludes identity from the result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A terminal stage returning three keys per group.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(string index1, string index2, string index3)
        => End(Create(Names(index1, index2, index3), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()), "3Keys"));

    /// <summary>
    /// Selects four named indexed keys from each aggregate winner and excludes identity from the result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A terminal stage returning four keys per group.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
        => End(Create(Names(index1, index2, index3, index4), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()), "4Keys"));

    private LibraDexCondition<TResult> Create<TResult>(
        string[] returnedIndexNames,
        Func<LibraDexAggregateProjectionRow, TResult> projector,
        string returnShape)
    {
        var descriptor = new LibraDexAggregateResultDescriptor<TResult>(spec, returnedIndexNames, projector, returnShape);
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

/// <summary>
/// Exposes the completed typed condition after its logical result shape has been selected.<br/>
/// </summary>
/// <typeparam name="TResult">The logical result value produced by the condition.<br/></typeparam>
public sealed class LibraDexConditionResultEnd<TResult>
{
    internal LibraDexConditionResultEnd(LibraDexCondition<TResult> condition)
    {
        EndCondition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <summary>
    /// Gets the immutable completed condition containing both selection rules and its typed result shape.<br/>
    /// This is the terminal fluent member; the completed descriptor exposes no further query-building stages.<br/>
    /// </summary>
    public LibraDexCondition<TResult> EndCondition { get; }

    /// <summary>
    /// Requests ascending result order over the condition's declared natural result axis.<br/>
    /// This is the concise complement to <see cref="OrderByDescending()"/> and does not add a sort when the natural index already supplies that order.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderBy()
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.Ascending));

    /// <summary>
    /// Requests ascending result order from one named index without requiring that index to be part of the returned shape.<br/>
    /// Execution traverses the named index in its natural forward order and applies the condition's selected-identity membership during traversal.<br/>
    /// </summary>
    /// <param name="indexName">The index that defines result order.<br/></param>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderBy(string indexName)
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.AscendingBy(indexName)));

    /// <summary>
    /// Requests ascending result order over the condition's declared result axis.<br/>
    /// When this matches the source index's natural order, execution remains a forward traversal without an added sort.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderByAscending()
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.Ascending));

    /// <summary>
    /// Requests ascending result order from one named index without requiring that index to be returned.<br/>
    /// This explicit-name form is equivalent to <see cref="OrderBy(string)"/> and is retained for symmetry with descending ordering.<br/>
    /// </summary>
    /// <param name="indexName">The index that defines result order.<br/></param>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderByAscending(string indexName)
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.AscendingBy(indexName)));

    /// <summary>
    /// Requests descending result order over the condition's declared result axis.<br/>
    /// An aligned index uses reverse traversal rather than materialized sorting.<br/>
    /// </summary>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderByDescending()
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.Descending));

    /// <summary>
    /// Requests descending result order from one named index without requiring that index to be returned.<br/>
    /// An aligned index uses reverse traversal; a cross-index condition applies selected-identity membership while traversing this order index.<br/>
    /// </summary>
    /// <param name="indexName">The index that defines result order.<br/></param>
    /// <returns>An ordered result stage that can be limited with <c>Top</c> or <c>Bottom</c>.<br/></returns>
    public LibraDexConditionOrderedResultEnd<TResult> OrderByDescending(string indexName)
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.DescendingBy(indexName)));

    /// <summary>
    /// Keeps the first <paramref name="count"/> results in the condition's natural order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of results to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<TResult> Top(int count)
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.Natural.Top(count)));

    /// <summary>
    /// Keeps the last <paramref name="count"/> results while preserving their natural order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of results to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<TResult> Bottom(int count)
        => new(EndCondition.WithSequence(LibraDexConditionResultSequence.Natural.Bottom(count)));

    /// <summary>
    /// Transforms each typed logical result without changing condition selection, result ordering, or identity ownership.<br/>
    /// The transform runs only when a getter, iterator, or reader produces a result; building the condition performs no IO and does not invoke caller code.<br/>
    /// Mutation APIs continue to use the source result's selected identities and ignore only the transformed return materialization.<br/>
    /// </summary>
    /// <typeparam name="TTransformed">The caller-defined result type produced from each current logical result.<br/></typeparam>
    /// <param name="transform">The caller function applied once to each produced result.<br/></param>
    /// <returns>A typed result stage that can retain source ordering, apply a result window, or terminate through <see cref="EndCondition"/>.<br/></returns>
    public LibraDexConditionResultEnd<TTransformed> Transform<TTransformed>(Func<TResult, TTransformed> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new LibraDexConditionResultEnd<TTransformed>(EndCondition.Transform(transform));
    }
}

/// <summary>
/// Exposes a completed result whose ordering has been declared but whose optional range has not.<br/>
/// </summary>
/// <typeparam name="TResult">The logical result value produced by the condition.<br/></typeparam>
public sealed class LibraDexConditionOrderedResultEnd<TResult>
{
    internal LibraDexConditionOrderedResultEnd(LibraDexCondition<TResult> condition)
    {
        EndCondition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <summary>
    /// Gets the immutable completed ordered condition.<br/>
    /// </summary>
    public LibraDexCondition<TResult> EndCondition { get; }

    /// <summary>
    /// Keeps the first <paramref name="count"/> results in the declared order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of results to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<TResult> Top(int count)
        => new(EndCondition.WithSequence(EndCondition.Result.Sequence.Top(count)));

    /// <summary>
    /// Keeps the last <paramref name="count"/> results in the declared order.<br/>
    /// </summary>
    /// <param name="count">The non-zero number of results to keep.<br/></param>
    /// <returns>A terminal limited-result stage.<br/></returns>
    public LibraDexConditionLimitedResultEnd<TResult> Bottom(int count)
        => new(EndCondition.WithSequence(EndCondition.Result.Sequence.Bottom(count)));
}

/// <summary>
/// Exposes a completed result after its top or bottom range has been declared.<br/>
/// No further condition grammar is available after this stage except the terminal <c>EndCondition</c>.<br/>
/// </summary>
/// <typeparam name="TResult">The logical result value produced by the condition.<br/></typeparam>
public sealed class LibraDexConditionLimitedResultEnd<TResult>
{
    internal LibraDexConditionLimitedResultEnd(LibraDexCondition<TResult> condition)
    {
        EndCondition = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <summary>
    /// Gets the immutable completed ordered-and-limited condition.<br/>
    /// </summary>
    public LibraDexCondition<TResult> EndCondition { get; }
}

/// <summary>
/// Adds aggregate result shaping to an in-progress filter without making a completed <c>EndCondition</c> composable again.<br/>
/// </summary>
public static class LibraDexConditionResultExtensions
{
    /// <summary>
    /// Groups the identities selected so far by one named index before the condition is terminated.<br/>
    /// The current filter is captured internally; callers continue directly to <c>Aggregate</c> and a single terminal <c>EndCondition</c>.<br/>
    /// </summary>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="indexName">The index whose exact stored key bytes define groups.<br/></param>
    /// <returns>A grouped continuation that can select <c>First</c>, <c>Last</c>, or an aggregate.<br/></returns>
    public static LibraDexConditionGroupBy GroupBy(this LibraDexConditionContinueOrEnd condition, string indexName)
    {
        ArgumentNullException.ThrowIfNull(condition);
        LibraDexConditionEndCondition filter = condition.EndCondition;
        return new LibraDexConditionGroupBy(filter.Group, filter, indexName);
    }

    /// <summary>
    /// Selects matching identities in their binary form as the logical result of the current filter.<br/>
    /// Returned arrays are caller-owned and remain stable after a getter, iterator, or reader advances.<br/>
    /// </summary>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <returns>A result-shaping stage returning raw identity bytes.<br/></returns>
    public static LibraDexConditionResultEnd<byte[]> Return(this LibraDexConditionContinueOrEnd condition)
        => Create(condition, Array.Empty<string>(), static row => row.Identity.As<byte[]>(), "Identity.Bytes");

    /// <summary>
    /// Selects matching identities converted to <typeparamref name="TIdentity"/> as the logical result of the current filter.<br/>
    /// A named <c>OrderBy</c> may subsequently choose an ordering index without adding that key to the returned shape.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <returns>A result-shaping stage returning matching identities.<br/></returns>
    public static LibraDexConditionResultEnd<TIdentity> Return<TIdentity>(this LibraDexConditionContinueOrEnd condition)
        => Create(condition, Array.Empty<string>(), static row => row.Identity.As<TIdentity>(), "Identity");

    /// <summary>
    /// Selects each matching identity followed by one named scalar indexed key.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A result-shaping stage returning identity/key tuples.<br/></returns>
    public static LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1)> Return<TIdentity, TKey1>(this LibraDexConditionContinueOrEnd condition, string index1)
        => Create(condition, Names(index1), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>()), "Identity+1Key");

    /// <summary>
    /// Selects each matching identity followed by two named scalar indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage returning identity/key/key tuples.<br/></returns>
    public static LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2)> Return<TIdentity, TKey1, TKey2>(this LibraDexConditionContinueOrEnd condition, string index1, string index2)
        => Create(condition, Names(index1, index2), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()), "Identity+2Keys");

    /// <summary>
    /// Selects each matching identity followed by three named scalar indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage returning one identity and three keys.<br/></returns>
    public static LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3)> Return<TIdentity, TKey1, TKey2, TKey3>(this LibraDexConditionContinueOrEnd condition, string index1, string index2, string index3)
        => Create(condition, Names(index1, index2, index3), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()), "Identity+3Keys");

    /// <summary>
    /// Selects each matching identity followed by four named scalar indexed keys.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage returning one identity and four keys.<br/></returns>
    public static LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> Return<TIdentity, TKey1, TKey2, TKey3, TKey4>(this LibraDexConditionContinueOrEnd condition, string index1, string index2, string index3, string index4)
        => Create(condition, Names(index1, index2, index3, index4), static row => (row.Identity.As<TIdentity>(), row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()), "Identity+4Keys");

    /// <summary>
    /// Selects one indexed key as the logical result of the current filter.<br/>
    /// Omitted ordering preserves that result index's natural persisted order; explicit ordering can use aligned forward or reverse traversal.<br/>
    /// </summary>
    /// <typeparam name="TKey">The requested key result type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="indexName">The index supplying returned keys and natural result order.<br/></param>
    /// <returns>A result-shaping stage that can order, limit, or terminate the condition.<br/></returns>
    public static LibraDexConditionResultEnd<TKey> ReturnKeys<TKey>(this LibraDexConditionContinueOrEnd condition, string indexName)
        => Create(condition, Names(indexName), static row => row.Keys[0].As<TKey>(), "1Key");

    /// <summary>
    /// Selects two named scalar indexed keys and excludes identity from the logical result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage returning two-key tuples.<br/></returns>
    public static LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(this LibraDexConditionContinueOrEnd condition, string index1, string index2)
        => Create(condition, Names(index1, index2), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>()), "2Keys");

    /// <summary>
    /// Selects three named scalar indexed keys and excludes identity from the logical result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage returning three-key tuples.<br/></returns>
    public static LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(this LibraDexConditionContinueOrEnd condition, string index1, string index2, string index3)
        => Create(condition, Names(index1, index2, index3), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>()), "3Keys");

    /// <summary>
    /// Selects four named scalar indexed keys and excludes identity from the logical result.<br/>
    /// Generic parameter and index-name order map positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="condition">The current predicate continuation.<br/></param>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage returning four-key tuples.<br/></returns>
    public static LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(this LibraDexConditionContinueOrEnd condition, string index1, string index2, string index3, string index4)
        => Create(condition, Names(index1, index2, index3, index4), static row => (row.Keys[0].As<TKey1>(), row.Keys[1].As<TKey2>(), row.Keys[2].As<TKey3>(), row.Keys[3].As<TKey4>()), "4Keys");

    private static LibraDexConditionResultEnd<TResult> Create<TResult>(
        LibraDexConditionContinueOrEnd condition,
        string[] returnedIndexNames,
        Func<LibraDexProjectedResultRow, TResult> projector,
        string returnShape)
    {
        ArgumentNullException.ThrowIfNull(condition);
        LibraDexConditionEndCondition filter = condition.EndCondition;
        string? naturalIndexName = returnedIndexNames.Length == 0 ? null : returnedIndexNames[0];
        var result = new LibraDexProjectedResultDescriptor<TResult>(filter.Group, filter, returnedIndexNames, naturalIndexName, projector, returnShape);
        return new LibraDexConditionResultEnd<TResult>(new LibraDexCondition<TResult>(filter.Group, filter, result));
    }

    private static string[] Names(params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
            ArgumentException.ThrowIfNullOrWhiteSpace(names[i], $"index{i + 1}");

        return names;
    }
}

internal readonly record struct LibraDexAggregateSpec(
    string Group,
    LibraDexConditionEndCondition? Filter,
    string GroupingIndexName,
    SubIndexType? StringSubIndex,
    string AggregateIndexName,
    AggType Aggregate);

internal readonly record struct LibraDexCountAggregateSpec(
    string Group,
    LibraDexConditionEndCondition? Filter,
    string GroupingIndexName,
    SubIndexType? StringSubIndex);

internal readonly record struct LibraDexConditionResultSequence(
    QueryDirection? Direction,
    int? Count,
    bool IsBottom,
    string? OrderIndexName = null)
{
    internal static LibraDexConditionResultSequence Natural => new(null, null, false);

    internal static LibraDexConditionResultSequence Ascending => new(QueryDirection.Ascending, null, false);

    internal static LibraDexConditionResultSequence Descending => new(QueryDirection.Descending, null, false);

    internal static LibraDexConditionResultSequence AscendingBy(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionResultSequence(QueryDirection.Ascending, null, false, indexName);
    }

    internal static LibraDexConditionResultSequence DescendingBy(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionResultSequence(QueryDirection.Descending, null, false, indexName);
    }

    internal LibraDexConditionResultSequence Top(int count)
    {
        EnsureCount(count);
        return this with { Count = count, IsBottom = false };
    }

    internal LibraDexConditionResultSequence Bottom(int count)
    {
        EnsureCount(count);
        return this with { Count = count, IsBottom = true };
    }

    private static void EnsureCount(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Top and Bottom require a value greater than zero.");
    }
}

internal interface ILibraDexConditionResultDescriptor<TResult>
{
    LibraDexConditionResultPlan Plan { get; }

    LibraDexConditionResultSequence Sequence { get; }

    IEnumerable<TResult> Iterate(CatalogIdentityGroupIndexes indexes);

    IEnumerable<LibraDexBookmarkResult<TResult>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor);

    IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes);

    ILibraDexConditionResultDescriptor<TResult> WithSequence(LibraDexConditionResultSequence sequence);

    ILibraDexConditionResultDescriptor<TResult> WithFilter(LibraDexConditionEndCondition? filter);

    IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes { get; }
}

internal sealed class LibraDexAggregateResultDescriptor<TResult> : ILibraDexConditionResultDescriptor<TResult>
{
    private readonly LibraDexAggregateSpec spec;
    private readonly string[] returnedIndexNames;
    private readonly Func<LibraDexAggregateProjectionRow, TResult> projector;
    private readonly string returnShape;
    private readonly LibraDexConditionResultSequence sequence;

    internal LibraDexAggregateResultDescriptor(
        LibraDexAggregateSpec spec,
        string[] returnedIndexNames,
        Func<LibraDexAggregateProjectionRow, TResult> projector,
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
        $"GroupBy.Aggregate.{spec.Aggregate}.{returnShape}{DescribeSequence(sequence)}",
        IsBlocking: true,
        "One aggregate value per candidate identity, one deterministic winner per raw-byte group, and only explicitly returned key lookups.");

    public LibraDexConditionResultSequence Sequence => sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes
    {
        get
        {
            List<LibraDexBookmarkIndexReference> indexes = new(returnedIndexNames.Length + 3)
            {
                new(spec.GroupingIndexName, null, LibraDexBookmarkIndexRole.Group | LibraDexBookmarkIndexRole.NaturalOrder),
                new(spec.AggregateIndexName, null, LibraDexBookmarkIndexRole.Aggregate)
            };
            for (int i = 0; i < returnedIndexNames.Length; i++)
                indexes.Add(new LibraDexBookmarkIndexReference(returnedIndexNames[i], null, LibraDexBookmarkIndexRole.Return));

            if (sequence.OrderIndexName is not null)
                indexes.Add(new LibraDexBookmarkIndexReference(sequence.OrderIndexName, null, LibraDexBookmarkIndexRole.Order));
            else if (sequence.Direction.HasValue)
                indexes.Add(new LibraDexBookmarkIndexReference(spec.AggregateIndexName, null, LibraDexBookmarkIndexRole.Order));

            return indexes;
        }
    }

    public IEnumerable<TResult> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexAggregateProjectionRow row in LibraDexAggregateExecutor.Execute(indexes, spec, returnedIndexNames, sequence))
            yield return projector(row);
    }

    public IEnumerable<LibraDexBookmarkResult<TResult>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        foreach (LibraDexAggregateProjectionRow row in LibraDexAggregateExecutor.Execute(indexes, spec, returnedIndexNames, sequence))
        {
            object? orderValue = sequence.Direction.HasValue ? row.AggregateValue.Value : row.GroupValue.Value;
            yield return new LibraDexBookmarkResult<TResult>(
                projector(row),
                new LibraDexBookmarkAnchor(orderValue, row.Identity.Value));
        }
    }

    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexAggregateWinner winner in LibraDexAggregateExecutor.ExecuteWinners(indexes, spec, sequence))
            yield return winner.Identity;
    }

    public ILibraDexConditionResultDescriptor<TResult> WithSequence(LibraDexConditionResultSequence value)
        => new LibraDexAggregateResultDescriptor<TResult>(spec, returnedIndexNames, projector, returnShape, value);

    public ILibraDexConditionResultDescriptor<TResult> WithFilter(LibraDexConditionEndCondition? filter)
        => new LibraDexAggregateResultDescriptor<TResult>(spec with { Filter = filter }, returnedIndexNames, projector, returnShape, sequence);

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

internal sealed class LibraDexCountAggregateResultDescriptor : ILibraDexConditionResultDescriptor<LibraDexAggregateRow>
{
    private readonly LibraDexCountAggregateSpec spec;
    private readonly LibraDexConditionResultSequence sequence;

    internal LibraDexCountAggregateResultDescriptor(
        LibraDexCountAggregateSpec spec,
        LibraDexConditionResultSequence sequence = default)
    {
        this.spec = spec;
        this.sequence = sequence;
    }

    public LibraDexConditionResultPlan Plan => new(
        $"GroupBy.Aggregate.Count.AggregateRow{DescribeSequence(sequence)}",
        IsBlocking: true,
        "One running count and selected-identity list per raw-byte group; explicit count ordering is applied only after grouping completes.");

    public LibraDexConditionResultSequence Sequence => sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes
        => new[]
        {
            new LibraDexBookmarkIndexReference(
                spec.GroupingIndexName,
                null,
                LibraDexBookmarkIndexRole.Group |
                (sequence.Direction.HasValue ? LibraDexBookmarkIndexRole.Order : LibraDexBookmarkIndexRole.NaturalOrder))
        };

    public IEnumerable<LibraDexAggregateRow> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexCountAggregateGroup group in Execute(indexes))
            yield return new LibraDexAggregateRow(group.GroupValue, group.Count);
    }

    public IEnumerable<LibraDexBookmarkResult<LibraDexAggregateRow>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        foreach (LibraDexCountAggregateGroup group in Execute(indexes))
        {
            object? orderValue = sequence.Direction.HasValue ? group.Count : group.GroupValue;
            yield return new LibraDexBookmarkResult<LibraDexAggregateRow>(
                new LibraDexAggregateRow(group.GroupValue, group.Count),
                new LibraDexBookmarkAnchor(orderValue, group.GroupValue));
        }
    }

    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexCountAggregateGroup group in Execute(indexes))
        {
            for (int i = 0; i < group.Identities.Count; i++)
                yield return group.Identities[i];
        }
    }

    public ILibraDexConditionResultDescriptor<LibraDexAggregateRow> WithSequence(LibraDexConditionResultSequence value)
        => new LibraDexCountAggregateResultDescriptor(spec, value);

    public ILibraDexConditionResultDescriptor<LibraDexAggregateRow> WithFilter(LibraDexConditionEndCondition? filter)
        => new LibraDexCountAggregateResultDescriptor(spec with { Filter = filter }, sequence);

    private IReadOnlyList<LibraDexCountAggregateGroup> Execute(CatalogIdentityGroupIndexes indexes)
    {
        if (sequence.OrderIndexName is not null &&
            !string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Grouped Count results can be ordered by their implicit count or grouping index '{spec.GroupingIndexName}', not unrelated index '{sequence.OrderIndexName}'.");
        }

        IIndex groupingIndex = indexes.Index(spec.GroupingIndexName);
        if (!string.Equals(groupingIndex.Group, spec.Group, StringComparison.Ordinal))
            throw new InvalidOperationException($"Index '{groupingIndex.Name}' belongs to identity group '{groupingIndex.Group}', not '{spec.Group}'.");

        HashSet<LibraDexRuntimeValueKey>? filtered = LibraDexAggregateExecutor.CreateFilter(indexes, spec.Filter);
        Dictionary<LibraDexRuntimeValueKey, LibraDexCountAggregateGroup> byKey = new();
        List<LibraDexCountAggregateGroup> groups = new();
        foreach (LibraDexGroupingTuple tuple in LibraDexAggregateExecutor.IterateGroupingTuples(groupingIndex, spec.StringSubIndex))
        {
            if (filtered is not null && !filtered.Contains(new LibraDexRuntimeValueKey(tuple.Identity)))
                continue;

            LibraDexRuntimeValueKey key = new(tuple.RawGroupKey);
            if (!byKey.TryGetValue(key, out LibraDexCountAggregateGroup? group))
            {
                group = new LibraDexCountAggregateGroup(tuple.DisplayGroupKey);
                byKey.Add(key, group);
                groups.Add(group);
            }

            group.Identities.Add(tuple.Identity);
        }

        if (sequence.Direction.HasValue)
        {
            int direction = sequence.Direction == QueryDirection.Ascending ? 1 : -1;
            groups.Sort((left, right) =>
            {
                int comparison = sequence.OrderIndexName is null
                    ? left.Count.CompareTo(right.Count) * direction
                    : LibraDexAggregateExecutor.Compare(left.GroupValue, right.GroupValue) * direction;
                return comparison != 0
                    ? comparison
                    : LibraDexAggregateExecutor.Compare(left.GroupValue, right.GroupValue);
            });
        }

        return ApplyWindow(groups, sequence);
    }

    private static IReadOnlyList<LibraDexCountAggregateGroup> ApplyWindow(
        List<LibraDexCountAggregateGroup> groups,
        LibraDexConditionResultSequence value)
    {
        if (!value.Count.HasValue || value.Count.Value >= groups.Count)
            return groups;

        int start = value.IsBottom ? groups.Count - value.Count.Value : 0;
        return groups.GetRange(start, value.Count.Value);
    }

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

internal sealed class LibraDexIndexKeyResultDescriptor<TKey> : ILibraDexConditionResultDescriptor<TKey>
{
    private readonly string group;
    private readonly LibraDexConditionEndCondition? filter;
    private readonly string indexName;
    private readonly LibraDexConditionResultSequence sequence;

    internal LibraDexIndexKeyResultDescriptor(
        string group,
        LibraDexConditionEndCondition? filter,
        string indexName,
        LibraDexConditionResultSequence sequence = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        this.group = group;
        this.filter = filter;
        this.indexName = indexName;
        this.sequence = sequence;
    }

    public LibraDexConditionResultPlan Plan => new(
        $"ReturnKeys.1Key.{DescribeSequence(sequence)}",
        IsBlocking: filter is not null || sequence.IsBottom,
        sequence.IsBottom
            ? "At most the requested bottom window is buffered while the aligned index is traversed in the opposite direction."
            : filter is null
                ? "The selected index is traversed directly in the requested direction without result sorting."
                : "Filtered identities are materialized once, then the selected result index is traversed in the requested direction.");

    public LibraDexConditionResultSequence Sequence => sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes
        => new[]
        {
            new LibraDexBookmarkIndexReference(
                indexName,
                null,
                LibraDexBookmarkIndexRole.Return |
                (sequence.Direction.HasValue ? LibraDexBookmarkIndexRole.Order : LibraDexBookmarkIndexRole.NaturalOrder))
        };

    public IEnumerable<TKey> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        IIndex index = indexes.Index(indexName);
        foreach (LibraDexObjectTuple tuple in IterateTuples(indexes, index))
            yield return new LibraDexResultValue(tuple.Key, index, IsIdentity: false).As<TKey>();
    }

    public IEnumerable<LibraDexBookmarkResult<TKey>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        IIndex index = indexes.Index(indexName);
        LibraDexBookmarkAnchor? seekAnchor = sequence.Count.HasValue || sequence.IsBottom ? null : anchor;
        foreach (LibraDexObjectTuple tuple in IterateTuples(indexes, index, seekAnchor))
        {
            yield return new LibraDexBookmarkResult<TKey>(
                new LibraDexResultValue(tuple.Key, index, IsIdentity: false).As<TKey>(),
                new LibraDexBookmarkAnchor(tuple.Key, tuple.Identity));
        }
    }

    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
    {
        IIndex index = indexes.Index(indexName);
        foreach (LibraDexObjectTuple tuple in IterateTuples(indexes, index))
            yield return tuple.Identity;
    }

    public ILibraDexConditionResultDescriptor<TKey> WithSequence(LibraDexConditionResultSequence value)
        => new LibraDexIndexKeyResultDescriptor<TKey>(group, filter, indexName, value);

    public ILibraDexConditionResultDescriptor<TKey> WithFilter(LibraDexConditionEndCondition? value)
        => new LibraDexIndexKeyResultDescriptor<TKey>(group, value, indexName, sequence);

    private IEnumerable<LibraDexObjectTuple> IterateTuples(
        CatalogIdentityGroupIndexes indexes,
        IIndex index,
        LibraDexBookmarkAnchor? anchor = null)
    {
        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
            throw new InvalidOperationException($"Index '{index.Name}' belongs to identity group '{index.Group}', not '{group}'.");
        if (index is not IIdentityPrimitiveTupleStreamer streamer)
            throw new NotSupportedException($"Index '{index.Name}' does not expose the tuple stream required by condition result shaping.");

        HashSet<LibraDexRuntimeValueKey>? selected = LibraDexAggregateExecutor.CreateFilter(indexes, filter);
        QueryDirection resultDirection = sequence.Direction ?? QueryDirection.Ascending;
        QueryDirection scanDirection = sequence.IsBottom
            ? resultDirection == QueryDirection.Ascending ? QueryDirection.Descending : QueryDirection.Ascending
            : resultDirection;
        int? primitiveTake = selected is null ? sequence.Count : null;
        LibraDexCriteriaKind criteriaKind = anchor.HasValue
            ? scanDirection == QueryDirection.Ascending
                ? LibraDexCriteriaKind.AtOrAfter
                : LibraDexCriteriaKind.AtOrBefore
            : LibraDexCriteriaKind.All;
        object?[] values = anchor.HasValue ? new[] { anchor.Value.OrderValue } : Array.Empty<object?>();
        IEnumerable<LibraDexObjectTuple> tuples = streamer.IterateTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(criteriaKind, values, primitiveTake, scanDirection));

        if (sequence.IsBottom)
        {
            List<LibraDexObjectTuple> bottom = new(sequence.Count!.Value);
            foreach (LibraDexObjectTuple tuple in tuples)
            {
                if (selected is not null && !selected.Contains(new LibraDexRuntimeValueKey(tuple.Identity)))
                    continue;

                bottom.Add(tuple);
                if (bottom.Count == sequence.Count.Value)
                    break;
            }

            for (int i = bottom.Count - 1; i >= 0; i--)
                yield return bottom[i];
            yield break;
        }

        int returned = 0;
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            if (selected is not null && !selected.Contains(new LibraDexRuntimeValueKey(tuple.Identity)))
                continue;

            yield return tuple;
            returned++;
            if (sequence.Count.HasValue && returned == sequence.Count.Value)
                yield break;
        }
    }

    private static string DescribeSequence(LibraDexConditionResultSequence value)
    {
        string order = value.Direction switch
        {
            QueryDirection.Ascending => "OrderAscending",
            QueryDirection.Descending => "OrderDescending",
            _ => "NaturalOrder"
        };
        if (!value.Count.HasValue)
            return order;

        return $"{order}.{(value.IsBottom ? "Bottom" : "Top")}({value.Count.Value})";
    }
}

internal sealed class LibraDexProjectedResultDescriptor<TResult> : ILibraDexConditionResultDescriptor<TResult>
{
    private readonly string group;
    private readonly LibraDexConditionEndCondition? filter;
    private readonly string[] returnedIndexNames;
    private readonly string? naturalOrderIndexName;
    private readonly Func<LibraDexProjectedResultRow, TResult> projector;
    private readonly string returnShape;
    private readonly LibraDexConditionResultSequence sequence;

    internal LibraDexProjectedResultDescriptor(
        string group,
        LibraDexConditionEndCondition? filter,
        string[] returnedIndexNames,
        string? naturalOrderIndexName,
        Func<LibraDexProjectedResultRow, TResult> projector,
        string returnShape,
        LibraDexConditionResultSequence sequence = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        this.group = group;
        this.filter = filter;
        this.returnedIndexNames = returnedIndexNames;
        this.naturalOrderIndexName = naturalOrderIndexName;
        this.projector = projector;
        this.returnShape = returnShape;
        this.sequence = sequence;
    }

    public LibraDexConditionResultPlan Plan
    {
        get
        {
            bool conditionNaturalIdentityOrder = sequence.OrderIndexName is null && naturalOrderIndexName is null && filter is not null;
            string orderIndexName = conditionNaturalIdentityOrder
                ? "<condition-plan>"
                : sequence.OrderIndexName ?? naturalOrderIndexName ?? "<none>";
            bool blocking = filter is not null || sequence.IsBottom;
            string materialization = sequence.IsBottom
                ? "The selected bottom window is buffered while the order index is traversed in the opposite direction."
                : conditionNaturalIdentityOrder
                    ? "The condition plan supplies distinct identities directly; descending output buffers that stream for reversal."
                : filter is null
                    ? "The order index is traversed directly without result sorting."
                    : "The condition executor applies selected-identity membership while the named order index is traversed without result sorting.";
            return new LibraDexConditionResultPlan(
                $"Return.{returnShape}.OrderIndex({orderIndexName}){DescribeSequence(sequence)}",
                blocking,
                materialization);
        }
    }

    public LibraDexConditionResultSequence Sequence => sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes
    {
        get
        {
            List<LibraDexBookmarkIndexReference> indexes = new(returnedIndexNames.Length + 1);
            for (int i = 0; i < returnedIndexNames.Length; i++)
                indexes.Add(new LibraDexBookmarkIndexReference(returnedIndexNames[i], null, LibraDexBookmarkIndexRole.Return));

            string? orderIndexName = sequence.OrderIndexName ?? naturalOrderIndexName;
            if (orderIndexName is not null)
            {
                LibraDexBookmarkIndexRole role = sequence.OrderIndexName is null
                    ? LibraDexBookmarkIndexRole.NaturalOrder
                    : LibraDexBookmarkIndexRole.Order;
                indexes.Add(new LibraDexBookmarkIndexReference(orderIndexName, null, role));
            }

            return indexes;
        }
    }

    public IEnumerable<TResult> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        LibraDexAggregateReturnLookup[] lookups = CreateLookups(indexes);
        foreach (LibraDexProjectedResultRow row in IterateRows(indexes, lookups))
            yield return projector(row);
    }

    public IEnumerable<LibraDexBookmarkResult<TResult>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        LibraDexAggregateReturnLookup[] lookups = CreateLookups(indexes);
        LibraDexBookmarkAnchor? seekAnchor = sequence.Count.HasValue || sequence.IsBottom ? null : anchor;
        foreach (LibraDexProjectedResultRow row in IterateRows(indexes, lookups, seekAnchor))
        {
            yield return new LibraDexBookmarkResult<TResult>(
                projector(row),
                new LibraDexBookmarkAnchor(row.OrderValue, row.Identity.Value));
        }
    }

    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
    {
        foreach (LibraDexProjectedResultRow row in IterateRows(indexes, Array.Empty<LibraDexAggregateReturnLookup>()))
            yield return row.Identity.Value!;
    }

    public ILibraDexConditionResultDescriptor<TResult> WithSequence(LibraDexConditionResultSequence value)
        => new LibraDexProjectedResultDescriptor<TResult>(group, filter, returnedIndexNames, naturalOrderIndexName, projector, returnShape, value);

    public ILibraDexConditionResultDescriptor<TResult> WithFilter(LibraDexConditionEndCondition? value)
        => new LibraDexProjectedResultDescriptor<TResult>(group, value, returnedIndexNames, naturalOrderIndexName, projector, returnShape, sequence);

    private IEnumerable<LibraDexProjectedResultRow> IterateRows(
        CatalogIdentityGroupIndexes indexes,
        LibraDexAggregateReturnLookup[] lookups,
        LibraDexBookmarkAnchor? anchor = null)
    {
        string? orderIndexName = sequence.OrderIndexName ?? naturalOrderIndexName;
        if (orderIndexName is null && filter is not null)
        {
            foreach (LibraDexProjectedResultRow row in IterateConditionNaturalRows(indexes, lookups))
                yield return row;
            yield break;
        }

        if (orderIndexName is null)
        {
            throw new InvalidOperationException("An identity-only result without a filter requires a named ordering index.");
        }

        IIndex orderIndex = indexes.Index(orderIndexName);
        if (!string.Equals(orderIndex.Group, group, StringComparison.Ordinal))
            throw new InvalidOperationException($"Index '{orderIndex.Name}' belongs to identity group '{orderIndex.Group}', not '{group}'.");

        QueryDirection resultDirection = sequence.Direction ?? QueryDirection.Ascending;
        QueryDirection scanDirection = sequence.IsBottom
            ? resultDirection == QueryDirection.Ascending ? QueryDirection.Descending : QueryDirection.Ascending
            : resultDirection;
        IEnumerable<LibraDexRuntimeTuple> tuples = IterateOrderTuples(indexes, orderIndex, scanDirection, anchor);
        Dictionary<LibraDexRuntimeValueKey, object?> seen = new();

        if (sequence.IsBottom)
        {
            List<LibraDexProjectedResultRow> bottom = new(sequence.Count!.Value);
            foreach (LibraDexRuntimeTuple tuple in tuples)
            {
                if (!TryAddScalarOrderTuple(seen, tuple, orderIndex))
                    continue;

                bottom.Add(CreateRow(tuple.Key, tuple.Identity, orderIndex, lookups));
                if (bottom.Count == sequence.Count.Value)
                    break;
            }

            for (int i = bottom.Count - 1; i >= 0; i--)
                yield return bottom[i];
            yield break;
        }

        int returned = 0;
        foreach (LibraDexRuntimeTuple tuple in tuples)
        {
            if (!TryAddScalarOrderTuple(seen, tuple, orderIndex))
                continue;

            yield return CreateRow(tuple.Key, tuple.Identity, orderIndex, lookups);
            returned++;
            if (sequence.Count.HasValue && returned == sequence.Count.Value)
                yield break;
        }
    }

    private IEnumerable<LibraDexProjectedResultRow> IterateConditionNaturalRows(
        CatalogIdentityGroupIndexes indexes,
        LibraDexAggregateReturnLookup[] lookups)
    {
        if (filter is null || filter.Leaves.Count == 0)
            throw new InvalidOperationException("An identity-only result requires at least one filter criterion.");

        IIndex identitySource = indexes.Index(filter.Leaves[0].IndexName);
        if (!string.Equals(identitySource.Group, group, StringComparison.Ordinal))
            throw new InvalidOperationException($"Index '{identitySource.Name}' belongs to identity group '{identitySource.Group}', not '{group}'.");

        IEnumerable<object> identities = filter.Iterate(
            name => indexes.Index(name),
            IdentityResultOrdering.PlanNatural,
            IdentityDeduplication.Distinct);

        if (sequence.Direction == QueryDirection.Descending)
        {
            List<LibraDexProjectedResultRow> reversed = new();
            foreach (object identity in identities)
                reversed.Add(CreateRow(identity, identity, identitySource, lookups));

            if (sequence.IsBottom)
            {
                int start = Math.Min(sequence.Count!.Value, reversed.Count) - 1;
                for (int i = start; i >= 0; i--)
                    yield return reversed[i];
                yield break;
            }

            int stop = sequence.Count.HasValue
                ? Math.Max(-1, reversed.Count - sequence.Count.Value - 1)
                : -1;
            for (int i = reversed.Count - 1; i > stop; i--)
                yield return reversed[i];
            yield break;
        }

        if (sequence.IsBottom)
        {
            int count = sequence.Count!.Value;
            LibraDexProjectedResultRow[] tail = new LibraDexProjectedResultRow[count];
            int seen = 0;
            foreach (object identity in identities)
            {
                tail[seen % count] = CreateRow(identity, identity, identitySource, lookups);
                seen++;
            }

            int returned = Math.Min(seen, count);
            int first = seen <= count ? 0 : seen % count;
            for (int i = 0; i < returned; i++)
                yield return tail[(first + i) % count];
            yield break;
        }

        int emitted = 0;
        foreach (object identity in identities)
        {
            yield return CreateRow(identity, identity, identitySource, lookups);
            emitted++;
            if (sequence.Count.HasValue && emitted == sequence.Count.Value)
                yield break;
        }
    }

    private IEnumerable<LibraDexRuntimeTuple> IterateOrderTuples(
        CatalogIdentityGroupIndexes indexes,
        IIndex orderIndex,
        QueryDirection direction,
        LibraDexBookmarkAnchor? anchor)
    {
        if (filter is not null)
        {
            IEnumerable<LibraDexRuntimeTuple> tuples = anchor.HasValue
                ? indexes.IterateTuplesFrom(orderIndex, filter, anchor.Value.OrderValue, direction)
                : indexes.IterateTuples(orderIndex, filter, direction);
            foreach (LibraDexRuntimeTuple tuple in tuples)
                yield return tuple;
            yield break;
        }

        if (orderIndex is not IIdentityPrimitiveTupleStreamer streamer)
            throw new NotSupportedException($"Index '{orderIndex.Name}' does not expose tuple streaming for result ordering.");

        LibraDexCriteriaKind criteriaKind = anchor.HasValue
            ? direction == QueryDirection.Ascending
                ? LibraDexCriteriaKind.AtOrAfter
                : LibraDexCriteriaKind.AtOrBefore
            : LibraDexCriteriaKind.All;
        object?[] values = anchor.HasValue ? new[] { anchor.Value.OrderValue } : Array.Empty<object?>();
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(criteriaKind, values, TakeLimit: null, direction)))
        {
            yield return new LibraDexRuntimeTuple(tuple.Key, tuple.Identity);
        }
    }

    private LibraDexProjectedResultRow CreateRow(
        object? orderValue,
        object identity,
        IIndex identitySource,
        LibraDexAggregateReturnLookup[] lookups)
    {
        LibraDexResultValue[] keys = lookups.Length == 0
            ? Array.Empty<LibraDexResultValue>()
            : new LibraDexResultValue[lookups.Length];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = lookups[i].Get(identity);

        return new LibraDexProjectedResultRow(
            orderValue,
            new LibraDexResultValue(identity, identitySource, IsIdentity: true),
            keys);
    }

    private LibraDexAggregateReturnLookup[] CreateLookups(CatalogIdentityGroupIndexes indexes)
    {
        LibraDexAggregateReturnLookup[] lookups = new LibraDexAggregateReturnLookup[returnedIndexNames.Length];
        for (int i = 0; i < lookups.Length; i++)
        {
            IIndex index = indexes.Index(returnedIndexNames[i]);
            if (!string.Equals(index.Group, group, StringComparison.Ordinal))
                throw new InvalidOperationException($"Index '{index.Name}' belongs to identity group '{index.Group}', not '{group}'.");

            lookups[i] = LibraDexAggregateReturnLookup.Create(index);
        }

        return lookups;
    }

    private static bool TryAddScalarOrderTuple(
        Dictionary<LibraDexRuntimeValueKey, object?> seen,
        LibraDexRuntimeTuple tuple,
        IIndex orderIndex)
    {
        LibraDexRuntimeValueKey identity = new(tuple.Identity);
        if (!seen.TryGetValue(identity, out object? existing))
        {
            seen.Add(identity, tuple.Key);
            return true;
        }

        if (!LibraDexObjectTuple.ValueEquals(existing, tuple.Key))
        {
            throw new InvalidOperationException(
                $"Order shape requested scalar key '{orderIndex.Name}', but one selected identity maps to multiple keys in that index.");
        }

        return false;
    }

    private static string DescribeSequence(LibraDexConditionResultSequence value)
    {
        string order = value.Direction switch
        {
            QueryDirection.Ascending => ".Ascending",
            QueryDirection.Descending => ".Descending",
            _ => ".Natural"
        };
        if (!value.Count.HasValue)
            return order;

        return $"{order}.{(value.IsBottom ? "Bottom" : "Top")}({value.Count.Value})";
    }
}

internal static class LibraDexAggregateExecutor
{
    internal static IEnumerable<LibraDexAggregateProjectionRow> Execute(
        CatalogIdentityGroupIndexes indexes,
        LibraDexAggregateSpec spec,
        string[] returnedIndexNames,
        LibraDexConditionResultSequence sequence)
    {
        LibraDexAggregateReturnLookup?[] lookups = CreateLookups(indexes, spec, returnedIndexNames);
        foreach (LibraDexAggregateWinner winner in ExecuteWinners(indexes, spec, sequence))
        {
            LibraDexResultValue[] keys = returnedIndexNames.Length == 0
                ? Array.Empty<LibraDexResultValue>()
                : new LibraDexResultValue[returnedIndexNames.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string indexName = returnedIndexNames[i];
                if (string.Equals(indexName, spec.GroupingIndexName, StringComparison.Ordinal))
                {
                    keys[i] = new LibraDexResultValue(winner.GroupValue, winner.GroupingIndex, IsIdentity: false);
                    continue;
                }

                if (string.Equals(indexName, spec.AggregateIndexName, StringComparison.Ordinal))
                {
                    keys[i] = new LibraDexResultValue(winner.AggregateValue, winner.AggregateIndex, IsIdentity: false);
                    continue;
                }

                LibraDexAggregateReturnLookup lookup = lookups[i]
                    ?? throw new InvalidOperationException("A returned-key lookup was not prepared.");
                keys[i] = lookup.Get(winner.Identity);
            }

            yield return new LibraDexAggregateProjectionRow(
                new LibraDexResultValue(winner.GroupValue, winner.GroupingIndex, IsIdentity: false),
                new LibraDexResultValue(winner.AggregateValue, winner.AggregateIndex, IsIdentity: false),
                new LibraDexResultValue(winner.Identity, winner.GroupingIndex, IsIdentity: true),
                keys);
        }
    }

    internal static IEnumerable<LibraDexAggregateWinner> ExecuteWinners(
        CatalogIdentityGroupIndexes indexes,
        LibraDexAggregateSpec spec,
        LibraDexConditionResultSequence sequence)
    {
        if (sequence.OrderIndexName is not null &&
            !string.Equals(sequence.OrderIndexName, spec.AggregateIndexName, StringComparison.Ordinal) &&
            !string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Grouped aggregate results can be ordered by aggregate index '{spec.AggregateIndexName}' or grouping index '{spec.GroupingIndexName}', not unrelated index '{sequence.OrderIndexName}'.");
        }

        IIndex groupingIndex = indexes.Index(spec.GroupingIndexName);
        IIndex aggregateIndex = indexes.Index(spec.AggregateIndexName);
        EnsureIndexGroup(spec, groupingIndex);
        EnsureIndexGroup(spec, aggregateIndex);

        HashSet<LibraDexRuntimeValueKey>? filtered = CreateFilter(indexes, spec.Filter);
        Dictionary<LibraDexRuntimeValueKey, LibraDexAggregateIdentityValue> values = BuildAggregateValues(
            aggregateIndex,
            filtered,
            spec.Aggregate);
        if (values.Count == 0)
            yield break;

        Dictionary<LibraDexRuntimeValueKey, LibraDexAggregateWinner> winners = new();
        List<LibraDexRuntimeValueKey> groupOrder = new();
        foreach (LibraDexGroupingTuple tuple in IterateGroupingTuples(groupingIndex, spec.StringSubIndex))
        {
            LibraDexRuntimeValueKey identityKey = new(tuple.Identity);
            if ((filtered is not null && !filtered.Contains(identityKey)) ||
                !values.TryGetValue(identityKey, out LibraDexAggregateIdentityValue aggregate))
            {
                continue;
            }

            LibraDexRuntimeValueKey groupKey = new(tuple.RawGroupKey);
            if (!winners.TryGetValue(groupKey, out LibraDexAggregateWinner current))
            {
                groupOrder.Add(groupKey);
                winners[groupKey] = new LibraDexAggregateWinner(
                    tuple.DisplayGroupKey,
                    aggregate.Value,
                    tuple.Identity,
                    groupingIndex,
                    aggregateIndex);
                continue;
            }

            if (ShouldReplace(current.AggregateValue, aggregate.Value, spec.Aggregate))
            {
                winners[groupKey] = new LibraDexAggregateWinner(
                    tuple.DisplayGroupKey,
                    aggregate.Value,
                    tuple.Identity,
                    groupingIndex,
                    aggregateIndex);
            }
        }

        List<LibraDexAggregateWinner> ordered = new(groupOrder.Count);
        for (int i = 0; i < groupOrder.Count; i++)
            ordered.Add(winners[groupOrder[i]]);
        if (sequence.Direction.HasValue)
        {
            int direction = sequence.Direction == QueryDirection.Ascending ? 1 : -1;
            ordered.Sort((left, right) =>
            {
                int comparison = string.Equals(sequence.OrderIndexName, spec.GroupingIndexName, StringComparison.Ordinal)
                    ? Compare(left.GroupValue, right.GroupValue) * direction
                    : Compare(left.AggregateValue, right.AggregateValue) * direction;
                return comparison != 0 ? comparison : Compare(left.GroupValue, right.GroupValue);
            });
        }

        int start = 0;
        int count = ordered.Count;
        if (sequence.Count.HasValue && sequence.Count.Value < count)
        {
            count = sequence.Count.Value;
            if (sequence.IsBottom)
                start = ordered.Count - count;
        }

        int end = start + count;
        for (int i = start; i < end; i++)
        {
            LibraDexAggregateWinner winner = ordered[i];
            yield return winner;
        }
    }

    internal static HashSet<LibraDexRuntimeValueKey>? CreateFilter(
        CatalogIdentityGroupIndexes indexes,
        LibraDexConditionEndCondition? filter)
    {
        if (filter is null)
            return null;

        HashSet<LibraDexRuntimeValueKey> identities = new();
        foreach (object identity in filter.Iterate(name => indexes.Index(name)))
            identities.Add(new LibraDexRuntimeValueKey(identity));

        return identities;
    }

    private static Dictionary<LibraDexRuntimeValueKey, LibraDexAggregateIdentityValue> BuildAggregateValues(
        IIndex aggregateIndex,
        HashSet<LibraDexRuntimeValueKey>? filtered,
        AggType aggregate)
    {
        Dictionary<LibraDexRuntimeValueKey, LibraDexAggregateIdentityValue> values = new();
        foreach (LibraDexObjectTuple tuple in IterateAll(aggregateIndex))
        {
            LibraDexRuntimeValueKey identityKey = new(tuple.Identity);
            if (filtered is not null && !filtered.Contains(identityKey))
                continue;

            if (!values.TryGetValue(identityKey, out LibraDexAggregateIdentityValue current) ||
                ShouldReplace(current.Value, tuple.Key, aggregate))
            {
                values[identityKey] = new LibraDexAggregateIdentityValue(tuple.Key);
            }
        }

        return values;
    }

    private static LibraDexAggregateReturnLookup?[] CreateLookups(
        CatalogIdentityGroupIndexes indexes,
        LibraDexAggregateSpec spec,
        string[] returnedIndexNames)
    {
        LibraDexAggregateReturnLookup?[] lookups = new LibraDexAggregateReturnLookup?[returnedIndexNames.Length];
        Dictionary<string, LibraDexAggregateReturnLookup> byName = new(StringComparer.Ordinal);
        for (int i = 0; i < returnedIndexNames.Length; i++)
        {
            string name = returnedIndexNames[i];
            if (string.Equals(name, spec.GroupingIndexName, StringComparison.Ordinal) ||
                string.Equals(name, spec.AggregateIndexName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!byName.TryGetValue(name, out LibraDexAggregateReturnLookup? lookup))
            {
                IIndex index = indexes.Index(name);
                EnsureIndexGroup(spec, index);
                lookup = LibraDexAggregateReturnLookup.Create(index);
                byName.Add(name, lookup);
            }

            lookups[i] = lookup;
        }

        return lookups;
    }

    internal static IEnumerable<LibraDexGroupingTuple> IterateGroupingTuples(
        IIndex index,
        SubIndexType? stringSubIndex)
    {
        if (index is LibraDexStringScalar8Index stringIndex)
        {
            IEnumerable<LibraDexObjectTuple> tuples = stringSubIndex switch
            {
                null => stringIndex.IterateExactEncodedGroupingTuplePrimitive(),
                SubIndexType.Folded => stringIndex.IterateFoldedEncodedGroupingTuplePrimitive(),
                SubIndexType.SortKey => stringIndex.IterateSortKeyTuplePrimitive(),
                SubIndexType.Normalized => stringIndex.IterateNormalizedEncodedGroupingTuplePrimitive(),
                _ => throw new ArgumentOutOfRangeException(nameof(stringSubIndex), stringSubIndex, "Unknown string grouping subindex.")
            };

            foreach (LibraDexObjectTuple tuple in tuples)
            {
                byte[] raw = tuple.Key as byte[]
                    ?? throw new InvalidDataException($"String index '{index.Name}' returned a non-binary grouping key.");
                object display = stringSubIndex == SubIndexType.SortKey
                    ? raw.ToArray()
                    : LibraDexStringScalar8Index.DecodeEncodedGroupingKey(raw)!;
                yield return new LibraDexGroupingTuple(raw, display, tuple.Identity);
            }

            yield break;
        }

        if (stringSubIndex is not null)
            throw new InvalidOperationException($"Index '{index.Name}' is not a string index and cannot select {stringSubIndex} grouping.");

        foreach (LibraDexObjectTuple tuple in IterateAll(index))
            yield return new LibraDexGroupingTuple(tuple.Key, tuple.Key, tuple.Identity);
    }

    private static IEnumerable<LibraDexObjectTuple> IterateAll(IIndex index)
    {
        if (index is not IIdentityPrimitiveTupleStreamer streamer)
            throw new NotSupportedException($"Index '{index.Name}' does not expose the tuple stream required by grouped aggregate conditions.");

        return streamer.IterateTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    private static bool ShouldReplace(object? current, object? candidate, AggType aggregate)
    {
        int comparison = Compare(candidate, current);
        return aggregate == AggType.Min ? comparison < 0 : comparison > 0;
    }

    internal static int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
        if (left.GetType() != right.GetType())
            throw new InvalidDataException($"Aggregate comparison encountered mixed key types {left.GetType().FullName} and {right.GetType().FullName}.");
        if (left is IComparable comparable)
            return comparable.CompareTo(right);

        throw new NotSupportedException($"Aggregate key type {left.GetType().FullName} does not expose ordering.");
    }

    private static void EnsureIndexGroup(LibraDexAggregateSpec spec, IIndex index)
    {
        if (!string.Equals(index.Group, spec.Group, StringComparison.Ordinal))
            throw new InvalidOperationException($"Index '{index.Name}' belongs to identity group '{index.Group}', not '{spec.Group}'.");
    }
}

internal sealed class LibraDexAggregateReturnLookup
{
    private readonly IIndex index;
    private readonly Dictionary<LibraDexRuntimeValueKey, object?> values;

    private LibraDexAggregateReturnLookup(IIndex index, Dictionary<LibraDexRuntimeValueKey, object?> values)
    {
        this.index = index;
        this.values = values;
    }

    internal static LibraDexAggregateReturnLookup Create(IIndex index)
    {
        if (index is not IIdentityPrimitiveTupleStreamer streamer)
            throw new NotSupportedException($"Index '{index.Name}' does not expose tuple streaming for returned-key materialization.");

        Dictionary<LibraDexRuntimeValueKey, object?> values = new();
        foreach (LibraDexObjectTuple tuple in streamer.IterateTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
        {
            LibraDexRuntimeValueKey identity = new(tuple.Identity);
            if (values.TryGetValue(identity, out object? existing) && !LibraDexObjectTuple.ValueEquals(existing, tuple.Key))
            {
                throw new InvalidOperationException(
                    $"Return shape requested scalar key '{index.Name}', but one winning identity can map to multiple keys in that index.");
            }

            values[identity] = tuple.Key;
        }

        return new LibraDexAggregateReturnLookup(index, values);
    }

    internal LibraDexResultValue Get(object identity)
    {
        if (!values.TryGetValue(new LibraDexRuntimeValueKey(identity), out object? value))
            throw new KeyNotFoundException($"Winning identity has no key in returned index '{index.Name}'.");

        return new LibraDexResultValue(value, index, IsIdentity: false);
    }
}

internal readonly record struct LibraDexAggregateProjectionRow(
    LibraDexResultValue GroupValue,
    LibraDexResultValue AggregateValue,
    LibraDexResultValue Identity,
    LibraDexResultValue[] Keys);

internal readonly record struct LibraDexProjectedResultRow(
    object? OrderValue,
    LibraDexResultValue Identity,
    LibraDexResultValue[] Keys);

internal readonly record struct LibraDexGroupingTuple(object? RawGroupKey, object? DisplayGroupKey, object Identity);

internal readonly record struct LibraDexAggregateIdentityValue(object? Value);

internal sealed class LibraDexCountAggregateGroup
{
    internal LibraDexCountAggregateGroup(object? groupValue)
    {
        GroupValue = groupValue;
    }

    internal object? GroupValue { get; }

    internal List<object> Identities { get; } = new();

    internal long Count => Identities.Count;
}

internal readonly record struct LibraDexAggregateWinner(
    object? GroupValue,
    object? AggregateValue,
    object Identity,
    IIndex GroupingIndex,
    IIndex AggregateIndex);

internal readonly struct LibraDexRuntimeValueKey : IEquatable<LibraDexRuntimeValueKey>
{
    private readonly object? value;

    internal LibraDexRuntimeValueKey(object? value)
    {
        this.value = value;
    }

    public bool Equals(LibraDexRuntimeValueKey other)
        => LibraDexObjectTuple.ValueEquals(value, other.value);

    public override bool Equals(object? obj)
        => obj is LibraDexRuntimeValueKey other && Equals(other);

    public override int GetHashCode()
    {
        if (value is null)
            return 0;
        if (value is byte[] bytes)
        {
            HashCode hash = new();
            for (int i = 0; i < bytes.Length; i++)
                hash.Add(bytes[i]);
            return hash.ToHashCode();
        }

        return value.GetHashCode();
    }
}

internal readonly record struct LibraDexResultValue(object? Value, IIndex Source, bool IsIdentity)
{
    internal T As<T>() => LibraDexResultValueConverter.Convert<T>(Value, Source, IsIdentity);
}

internal static class LibraDexResultValueConverter
{
    internal static T Convert<T>(object? value, IIndex source, bool isIdentity)
    {
        if (typeof(T) == typeof(byte[]))
            return (T)(object)EncodeBytes(value, source, isIdentity);

        if (value is T typed)
            return typed;

        if (value is null)
        {
            if (default(T) is null)
                return default!;

            throw new InvalidCastException($"Index '{source.Name}' returned null for non-nullable result type {typeof(T).FullName}.");
        }

        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

        throw new InvalidCastException(
            $"Index '{source.Name}' returned {value.GetType().FullName}, which cannot materialize as {typeof(T).FullName}.");
    }

    internal static byte[]? EncodeRawBytes(object? value, IIndex source, bool isIdentity)
    {
        if (value is null)
        {
            if (!isIdentity && source.KeyFamily == CatalogIndexKeyFamily.String)
                return LibraDexStringScalar8Index.EncodeGroupingKey(null);
            return null;
        }

        return EncodeBytes(value, source, isIdentity);
    }

    private static byte[] EncodeBytes(object? value, IIndex source, bool isIdentity)
    {
        if (value is null)
        {
            if (!isIdentity && source.KeyFamily == CatalogIndexKeyFamily.String)
                return LibraDexStringScalar8Index.EncodeGroupingKey(null);

            throw new InvalidCastException($"Index '{source.Name}' cannot return a null value as raw identity/key bytes.");
        }

        if (value is byte[] bytes)
            return bytes.ToArray();
        if (!isIdentity && source.KeyFamily == CatalogIndexKeyFamily.String && value is string text)
            return LibraDexStringScalar8Index.EncodeGroupingKey(text);
        if (value is string stringValue)
            return Encoding.UTF8.GetBytes(stringValue);

        DateTimeKeyEncoding dateEncoding = isIdentity
            ? DateTimeKeyEncoding.CalendarSdt
            : source.DateTimeKeyEncoding;
        if (TryEncode8(value, dateEncoding, out ulong encoded8))
        {
            byte[] result = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(result, encoded8);
            return result;
        }

        if (TryEncode16(value, out ulong high, out ulong low))
        {
            byte[] result = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(0, 8), high);
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), low);
            return result;
        }

        if (value is BigInteger bigInteger)
            return bigInteger.ToByteArray(isUnsigned: false, isBigEndian: true);

        throw new NotSupportedException(
            $"Raw byte materialization is not connected for {value.GetType().FullName} from index '{source.Name}'.");
    }

    private static bool TryEncode8(object value, DateTimeKeyEncoding dateEncoding, out ulong encoded)
    {
        if (value.GetType().IsEnum)
            value = System.Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);

        encoded = value switch
        {
            bool typed => LibraDexGenericScalarCodec<bool>.Encode8(typed, dateEncoding),
            byte typed => LibraDexGenericScalarCodec<byte>.Encode8(typed, dateEncoding),
            sbyte typed => LibraDexGenericScalarCodec<sbyte>.Encode8(typed, dateEncoding),
            short typed => LibraDexGenericScalarCodec<short>.Encode8(typed, dateEncoding),
            ushort typed => LibraDexGenericScalarCodec<ushort>.Encode8(typed, dateEncoding),
            char typed => LibraDexGenericScalarCodec<char>.Encode8(typed, dateEncoding),
            int typed => LibraDexGenericScalarCodec<int>.Encode8(typed, dateEncoding),
            uint typed => LibraDexGenericScalarCodec<uint>.Encode8(typed, dateEncoding),
            long typed => LibraDexGenericScalarCodec<long>.Encode8(typed, dateEncoding),
            ulong typed => LibraDexGenericScalarCodec<ulong>.Encode8(typed, dateEncoding),
            float typed => LibraDexGenericScalarCodec<float>.Encode8(typed, dateEncoding),
            double typed => LibraDexGenericScalarCodec<double>.Encode8(typed, dateEncoding),
            DateTime typed => LibraDexGenericScalarCodec<DateTime>.Encode8(typed, dateEncoding),
            DateTimeOffset typed => LibraDexGenericScalarCodec<DateTimeOffset>.Encode8(typed, dateEncoding),
            DateOnly typed => LibraDexGenericScalarCodec<DateOnly>.Encode8(typed, dateEncoding),
            TimeOnly typed => LibraDexGenericScalarCodec<TimeOnly>.Encode8(typed, dateEncoding),
            TimeSpan typed => LibraDexGenericScalarCodec<TimeSpan>.Encode8(typed, dateEncoding),
            _ => 0
        };

        return value is bool or byte or sbyte or short or ushort or char or int or uint or long or ulong or float or double or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan;
    }

    private static bool TryEncode16(object value, out ulong high, out ulong low)
    {
        high = 0;
        low = 0;
        switch (value)
        {
            case Guid typed:
                LibraDexGenericScalarCodec<Guid>.Encode16(typed, out high, out low);
                return true;
            case Int128 typed:
                LibraDexGenericScalarCodec<Int128>.Encode16(typed, out high, out low);
                return true;
            case UInt128 typed:
                LibraDexGenericScalarCodec<UInt128>.Encode16(typed, out high, out low);
                return true;
            case decimal typed:
                LibraDexGenericScalarCodec<decimal>.Encode16(typed, out high, out low);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Carries the ordinary condition result-shaping surface through named and typed composite-index continuations.<br/>
/// Composite part selection remains responsible only for matching; this shared stage lets the completed match select identities, identity/key tuples, or key-only tuples before the single terminal <c>EndCondition</c>.<br/>
/// </summary>
public abstract class LibraDexCompositeResultContinuation
{
    private readonly LibraDexConditionContinueOrEnd condition;

    internal LibraDexCompositeResultContinuation(LibraDexConditionContinueOrEnd condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        this.condition = condition;
    }

    internal LibraDexConditionContinueOrEnd ResultSource => condition;

    /// <summary>
    /// Selects matching composite identities in their binary form.<br/>
    /// Returned arrays are caller-owned and remain stable after the getter, iterator, or reader advances.<br/>
    /// </summary>
    /// <returns>A result-shaping stage returning raw identity bytes.<br/></returns>
    public LibraDexConditionResultEnd<byte[]> Return()
        => LibraDexConditionResultExtensions.Return(condition);

    /// <summary>
    /// Selects matching composite identities converted to <typeparamref name="TIdentity"/>.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity result type.<br/></typeparam>
    /// <returns>A result-shaping stage returning typed identities.<br/></returns>
    public LibraDexConditionResultEnd<TIdentity> Return<TIdentity>()
        => LibraDexConditionResultExtensions.Return<TIdentity>(condition);

    /// <summary>
    /// Selects each matching composite identity followed by one named indexed key.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested key type.<br/></typeparam>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A result-shaping stage returning identity/key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1)> Return<TIdentity, TKey1>(string index1)
        => LibraDexConditionResultExtensions.Return<TIdentity, TKey1>(condition, index1);

    /// <summary>
    /// Selects each matching composite identity followed by two named indexed keys.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage returning identity/key/key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2)> Return<TIdentity, TKey1, TKey2>(string index1, string index2)
        => LibraDexConditionResultExtensions.Return<TIdentity, TKey1, TKey2>(condition, index1, index2);

    /// <summary>
    /// Selects each matching composite identity followed by three named indexed keys.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage returning one identity and three keys.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3)> Return<TIdentity, TKey1, TKey2, TKey3>(string index1, string index2, string index3)
        => LibraDexConditionResultExtensions.Return<TIdentity, TKey1, TKey2, TKey3>(condition, index1, index2, index3);

    /// <summary>
    /// Selects each matching composite identity followed by four named indexed keys.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The requested identity type.<br/></typeparam>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage returning one identity and four keys.<br/></returns>
    public LibraDexConditionResultEnd<(TIdentity Identity, TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> Return<TIdentity, TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
        => LibraDexConditionResultExtensions.Return<TIdentity, TKey1, TKey2, TKey3, TKey4>(condition, index1, index2, index3, index4);

    /// <summary>
    /// Selects one named indexed key from each matching composite identity and excludes identity from the result.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested key type.<br/></typeparam>
    /// <param name="index1">The index supplying the returned key.<br/></param>
    /// <returns>A result-shaping stage returning scalar keys.<br/></returns>
    public LibraDexConditionResultEnd<TKey1> ReturnKeys<TKey1>(string index1)
        => LibraDexConditionResultExtensions.ReturnKeys<TKey1>(condition, index1);

    /// <summary>
    /// Selects two named indexed keys from each matching composite identity and excludes identity from the result.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <returns>A result-shaping stage returning two-key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2)> ReturnKeys<TKey1, TKey2>(string index1, string index2)
        => LibraDexConditionResultExtensions.ReturnKeys<TKey1, TKey2>(condition, index1, index2);

    /// <summary>
    /// Selects three named indexed keys from each matching composite identity and excludes identity from the result.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <returns>A result-shaping stage returning three-key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3)> ReturnKeys<TKey1, TKey2, TKey3>(string index1, string index2, string index3)
        => LibraDexConditionResultExtensions.ReturnKeys<TKey1, TKey2, TKey3>(condition, index1, index2, index3);

    /// <summary>
    /// Selects four named indexed keys from each matching composite identity and excludes identity from the result.<br/>
    /// Generic and index-name order maps positionally to the returned tuple.<br/>
    /// </summary>
    /// <typeparam name="TKey1">The requested first key type.<br/></typeparam>
    /// <typeparam name="TKey2">The requested second key type.<br/></typeparam>
    /// <typeparam name="TKey3">The requested third key type.<br/></typeparam>
    /// <typeparam name="TKey4">The requested fourth key type.<br/></typeparam>
    /// <param name="index1">The index supplying the first returned key.<br/></param>
    /// <param name="index2">The index supplying the second returned key.<br/></param>
    /// <param name="index3">The index supplying the third returned key.<br/></param>
    /// <param name="index4">The index supplying the fourth returned key.<br/></param>
    /// <returns>A result-shaping stage returning four-key tuples.<br/></returns>
    public LibraDexConditionResultEnd<(TKey1 Key1, TKey2 Key2, TKey3 Key3, TKey4 Key4)> ReturnKeys<TKey1, TKey2, TKey3, TKey4>(string index1, string index2, string index3, string index4)
        => LibraDexConditionResultExtensions.ReturnKeys<TKey1, TKey2, TKey3, TKey4>(condition, index1, index2, index3, index4);
}

/// <summary>
/// Decorates one logical result descriptor with a deferred caller-defined projection.<br/>
/// Selection, identity ownership, bookmarks, filters, and sequence changes remain delegated to the source descriptor.<br/>
/// </summary>
/// <typeparam name="TSource">The source descriptor's logical result type.<br/></typeparam>
/// <typeparam name="TResult">The caller-projected logical result type.<br/></typeparam>
internal sealed class LibraDexTransformedResultDescriptor<TSource, TResult> : ILibraDexConditionResultDescriptor<TResult>
{
    private readonly ILibraDexConditionResultDescriptor<TSource> source;
    private readonly Func<TSource, TResult> transform;

    /// <summary>
    /// Initializes a transformed descriptor over one immutable source descriptor.<br/>
    /// </summary>
    /// <param name="source">The descriptor that owns selection and source materialization.<br/></param>
    /// <param name="transform">The caller projection invoked once per produced source result.<br/></param>
    internal LibraDexTransformedResultDescriptor(
        ILibraDexConditionResultDescriptor<TSource> source,
        Func<TSource, TResult> transform)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.transform = transform ?? throw new ArgumentNullException(nameof(transform));
    }

    public LibraDexConditionResultPlan Plan
    {
        get
        {
            LibraDexConditionResultPlan plan = source.Plan;
            return new LibraDexConditionResultPlan(
                $"{plan.Shape}.Transform({typeof(TResult).Name})",
                plan.IsBlocking,
                $"{plan.Materialization} A caller transform is applied once as each logical result is produced.");
        }
    }

    public LibraDexConditionResultSequence Sequence => source.Sequence;

    public IReadOnlyList<LibraDexBookmarkIndexReference> BookmarkIndexes => source.BookmarkIndexes;

    /// <summary>
    /// Streams projected logical results from the source descriptor.<br/>
    /// </summary>
    /// <param name="indexes">The opened identity-group indexes used by the source descriptor.<br/></param>
    /// <returns>A deferred projected result sequence.<br/></returns>
    public IEnumerable<TResult> Iterate(CatalogIdentityGroupIndexes indexes)
    {
        foreach (TSource value in source.Iterate(indexes))
            yield return transform(value);
    }

    /// <summary>
    /// Streams projected bookmark results while preserving each source anchor unchanged.<br/>
    /// </summary>
    /// <param name="indexes">The opened identity-group indexes used by the source descriptor.<br/></param>
    /// <param name="anchor">The optional source bookmark anchor.<br/></param>
    /// <returns>A deferred sequence of projected values paired with source anchors.<br/></returns>
    public IEnumerable<LibraDexBookmarkResult<TResult>> IterateBookmark(
        CatalogIdentityGroupIndexes indexes,
        LibraDexBookmarkAnchor? anchor)
    {
        foreach (LibraDexBookmarkResult<TSource> value in source.IterateBookmark(indexes, anchor))
            yield return new LibraDexBookmarkResult<TResult>(transform(value.Value), value.Anchor);
    }

    /// <summary>
    /// Streams the source descriptor's selected identities without invoking the return projection.<br/>
    /// </summary>
    /// <param name="indexes">The opened identity-group indexes used by the source descriptor.<br/></param>
    /// <returns>The selected source identities used by condition-scoped mutation.<br/></returns>
    public IEnumerable<object> IterateSelectedIdentities(CatalogIdentityGroupIndexes indexes)
        => source.IterateSelectedIdentities(indexes);

    /// <summary>
    /// Applies a result sequence to the source descriptor and retains this projection.<br/>
    /// </summary>
    /// <param name="sequence">The requested ordering or result window.<br/></param>
    /// <returns>An equivalent transformed descriptor over the resequenced source.<br/></returns>
    public ILibraDexConditionResultDescriptor<TResult> WithSequence(LibraDexConditionResultSequence sequence)
        => new LibraDexTransformedResultDescriptor<TSource, TResult>(source.WithSequence(sequence), transform);

    /// <summary>
    /// Applies a replacement filter to the source descriptor and retains this projection.<br/>
    /// </summary>
    /// <param name="filter">The replacement completed filter, or null for an unfiltered result.<br/></param>
    /// <returns>An equivalent transformed descriptor over the refiltered source.<br/></returns>
    public ILibraDexConditionResultDescriptor<TResult> WithFilter(LibraDexConditionEndCondition? filter)
        => new LibraDexTransformedResultDescriptor<TSource, TResult>(source.WithFilter(filter), transform);
}
