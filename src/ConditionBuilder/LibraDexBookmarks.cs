namespace LibraDex;

/// <summary>
/// Selects how a detached bookmark resumes after the catalog has changed.<br/>
/// Generation-bound continuation is the safe default; live continuation deliberately follows the current result stream after the captured anchor.<br/>
/// </summary>
public enum LibraDexBookmarkConsistency
{
    /// <summary>
    /// Requires the catalog mutation version captured by the bookmark to remain current.<br/>
    /// This preserves the exact logical result stream against which the bookmark was created.<br/>
    /// </summary>
    GenerationBound = 0,

    /// <summary>
    /// Re-materializes the condition against current catalog state and continues after the captured logical anchor.<br/>
    /// The anchor must still be present in the current result stream in this implementation.<br/>
    /// </summary>
    LiveContinuation = 1
}

/// <summary>
/// Identifies why one resolved index participates in a condition-result bookmark.<br/>
/// Multiple roles may apply to the same index and are combined into one value.<br/>
/// </summary>
[Flags]
public enum LibraDexBookmarkIndexRole
{
    /// <summary>
    /// The index participates in a condition filter.<br/>
    /// </summary>
    Filter = 1,

    /// <summary>
    /// The index supplies grouped result boundaries.<br/>
    /// </summary>
    Group = 2,

    /// <summary>
    /// The index supplies an aggregate value or aggregate winner.<br/>
    /// </summary>
    Aggregate = 4,

    /// <summary>
    /// The index supplies a returned key value.<br/>
    /// </summary>
    Return = 8,

    /// <summary>
    /// The index supplies explicit result ordering.<br/>
    /// </summary>
    Order = 16,

    /// <summary>
    /// The index supplies the result shape's implicit natural order.<br/>
    /// </summary>
    NaturalOrder = 32
}

/// <summary>
/// Describes one resolved index related to a condition-result bookmark.<br/>
/// This is a disconnected inspection snapshot; reading it never opens an index or invokes a deferred selector.<br/>
/// </summary>
/// <param name="Name">The resolved index name used by the materialized condition or result shape.<br/></param>
/// <param name="SelectorName">The optional reusable selector label from the source condition.<br/></param>
/// <param name="Roles">The combined roles this index serves in the logical result stream.<br/></param>
/// <param name="Generation">The catalog directory generation recorded for the resolved index.<br/></param>
public readonly record struct LibraDexBookmarkIndexInfo(
    string Name,
    string? SelectorName,
    LibraDexBookmarkIndexRole Roles,
    long Generation);

/// <summary>
/// Provides passive structure-only information about the materialized condition associated with a bookmark.<br/>
/// Operand values and executable selector delegates are intentionally excluded so inspection cannot disclose values or run caller code.<br/>
/// </summary>
public sealed class LibraDexBookmarkConditionInfo
{
    internal LibraDexBookmarkConditionInfo(
        string group,
        string shape,
        bool hasDeferredSelectors,
        bool hasDeferredValues)
    {
        Group = group;
        Shape = shape;
        HasDeferredSelectors = hasDeferredSelectors;
        HasDeferredValues = hasDeferredValues;
    }

    /// <summary>
    /// Gets the identity group from which the logical result stream was materialized.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets a structure-only condition and return-shape description.<br/>
    /// The description contains resolved index names and operators but never operand values.<br/>
    /// </summary>
    public string Shape { get; }

    /// <summary>
    /// Gets whether the source descriptor contained at least one deferred index selector.<br/>
    /// Resolved names are available through the bookmark's index metadata without invoking those selectors again.<br/>
    /// </summary>
    public bool HasDeferredSelectors { get; }

    /// <summary>
    /// Gets whether the source descriptor contained at least one deferred operand value.<br/>
    /// The materialized values remain private continuation state and are never exposed by bookmark inspection.<br/>
    /// </summary>
    public bool HasDeferredValues { get; }
}

/// <summary>
/// Represents an opaque continuation point in the logical result stream produced by a materialized condition.<br/>
/// Public properties expose disconnected structure-only provenance; continuation state and materialized values remain private to LibraDex.<br/>
/// </summary>
public sealed class LibraDexBookmark
{
    internal const int CurrentFormatVersion = 1;
    private readonly LibraDexBookmarkState state;

    internal LibraDexBookmark(LibraDexBookmarkState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
    }

    internal static LibraDexBookmark Legacy(long generation, long position)
        => new(new LibraDexBookmarkState(
            new LibraDexBookmarkConditionInfo(string.Empty, "Legacy.Identity", false, false),
            Array.Empty<LibraDexBookmarkIndexInfo>(),
            position,
            generation,
            LibraDexBookmarkConsistency.GenerationBound,
            new LibraDexBookmarkConditionKey(string.Empty, "Legacy.Identity", Array.Empty<LibraDexBookmarkLeafKey>()),
            anchor: null,
            DateTimeOffset.UtcNow));

    /// <summary>
    /// Gets the bookmark payload format version.<br/>
    /// This is inspection metadata; callers should pass bookmarks back to LibraDex rather than branching on private payload layout.<br/>
    /// </summary>
    public int FormatVersion => CurrentFormatVersion;

    /// <summary>
    /// Gets the identity group associated with this bookmark.<br/>
    /// </summary>
    public string Group => state.Condition.Group;

    /// <summary>
    /// Gets the passive structure-only condition description captured at materialization time.<br/>
    /// Reading this property never invokes deferred selectors or values.<br/>
    /// </summary>
    public LibraDexBookmarkConditionInfo Condition => state.Condition;

    /// <summary>
    /// Gets disconnected metadata for every logical index related to the bookmark.<br/>
    /// Names are the resolved materialization-time names, including names supplied by deferred selectors.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexBookmarkIndexInfo> Indexes => state.Indexes;

    /// <summary>
    /// Gets the number of logical results consumed before the bookmark's next unread result.<br/>
    /// Individual reads, skips, and pulls all advance this count.<br/>
    /// </summary>
    public long ResultsConsumed => state.ResultsConsumed;

    /// <summary>
    /// Gets the consistency mode under which this bookmark was most recently captured or resumed.<br/>
    /// </summary>
    public LibraDexBookmarkConsistency Consistency => state.Consistency;

    /// <summary>
    /// Gets when the bookmark snapshot was captured in UTC.<br/>
    /// </summary>
    public DateTimeOffset CapturedAtUtc => state.CapturedAtUtc;

    internal long CatalogMutationVersion => state.CatalogMutationVersion;

    internal LibraDexBookmarkConditionKey ConditionKey => state.ConditionKey;

    internal LibraDexBookmarkAnchor? Anchor => state.Anchor;
}

internal sealed class LibraDexBookmarkState
{
    internal LibraDexBookmarkState(
        LibraDexBookmarkConditionInfo condition,
        LibraDexBookmarkIndexInfo[] indexes,
        long resultsConsumed,
        long catalogMutationVersion,
        LibraDexBookmarkConsistency consistency,
        LibraDexBookmarkConditionKey conditionKey,
        LibraDexBookmarkAnchor? anchor,
        DateTimeOffset capturedAtUtc)
    {
        Condition = condition;
        Indexes = Array.AsReadOnly((LibraDexBookmarkIndexInfo[])indexes.Clone());
        ResultsConsumed = resultsConsumed;
        CatalogMutationVersion = catalogMutationVersion;
        Consistency = consistency;
        ConditionKey = conditionKey;
        Anchor = anchor;
        CapturedAtUtc = capturedAtUtc;
    }

    internal LibraDexBookmarkConditionInfo Condition { get; }

    internal IReadOnlyList<LibraDexBookmarkIndexInfo> Indexes { get; }

    internal long ResultsConsumed { get; }

    internal long CatalogMutationVersion { get; }

    internal LibraDexBookmarkConsistency Consistency { get; }

    internal LibraDexBookmarkConditionKey ConditionKey { get; }

    internal LibraDexBookmarkAnchor? Anchor { get; }

    internal DateTimeOffset CapturedAtUtc { get; }
}

internal readonly record struct LibraDexBookmarkAnchor(object? OrderValue, object? IdentityValue);

internal readonly record struct LibraDexBookmarkResult<TResult>(
    TResult Value,
    LibraDexBookmarkAnchor Anchor);

internal sealed class LibraDexBookmarkTemplate
{
    internal LibraDexBookmarkTemplate(
        LibraDexBookmarkConditionInfo condition,
        LibraDexBookmarkIndexInfo[] indexes,
        long catalogMutationVersion,
        LibraDexBookmarkConditionKey conditionKey)
    {
        Condition = condition;
        Indexes = indexes;
        CatalogMutationVersion = catalogMutationVersion;
        ConditionKey = conditionKey;
    }

    internal LibraDexBookmarkConditionInfo Condition { get; }

    internal LibraDexBookmarkIndexInfo[] Indexes { get; }

    internal long CatalogMutationVersion { get; }

    internal LibraDexBookmarkConditionKey ConditionKey { get; }

    internal LibraDexBookmark Create(
        long resultsConsumed,
        LibraDexBookmarkAnchor? anchor,
        LibraDexBookmarkConsistency consistency)
        => new(new LibraDexBookmarkState(
            Condition,
            Indexes,
            resultsConsumed,
            CatalogMutationVersion,
            consistency,
            ConditionKey,
            anchor,
            DateTimeOffset.UtcNow));
}

internal readonly record struct LibraDexBookmarkIndexReference(
    string Name,
    string? SelectorName,
    LibraDexBookmarkIndexRole Roles);

internal sealed class LibraDexBookmarkConditionKey
{
    private readonly LibraDexBookmarkLeafKey[] leaves;

    internal LibraDexBookmarkConditionKey(string group, string resultShape, LibraDexBookmarkLeafKey[] leaves)
    {
        Group = group;
        ResultShape = resultShape;
        this.leaves = leaves;
    }

    internal string Group { get; }

    internal string ResultShape { get; }

    internal bool Matches(LibraDexBookmarkConditionKey other)
    {
        if (!string.Equals(Group, other.Group, StringComparison.Ordinal) ||
            !string.Equals(ResultShape, other.ResultShape, StringComparison.Ordinal) ||
            leaves.Length != other.leaves.Length)
        {
            return false;
        }

        for (int i = 0; i < leaves.Length; i++)
        {
            if (!leaves[i].Matches(other.leaves[i]))
                return false;
        }

        return true;
    }
}

internal sealed class LibraDexBookmarkLeafKey
{
    private readonly object?[] values;

    internal LibraDexBookmarkLeafKey(
        string indexName,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operation,
        bool ignoreCase,
        string? culture,
        object?[] values)
    {
        IndexName = indexName;
        ValueKind = valueKind;
        Operation = operation;
        IgnoreCase = ignoreCase;
        Culture = culture;
        this.values = values;
    }

    internal string IndexName { get; }

    internal LibraDexConditionValueKind ValueKind { get; }

    internal LibraDexConditionOperatorKind Operation { get; }

    internal bool IgnoreCase { get; }

    internal string? Culture { get; }

    internal bool Matches(LibraDexBookmarkLeafKey other)
    {
        if (!string.Equals(IndexName, other.IndexName, StringComparison.Ordinal) ||
            ValueKind != other.ValueKind ||
            Operation != other.Operation ||
            IgnoreCase != other.IgnoreCase ||
            !string.Equals(Culture, other.Culture, StringComparison.Ordinal) ||
            values.Length != other.values.Length)
        {
            return false;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!OperandEquals(values[i], other.values[i]))
                return false;
        }

        return true;
    }

    private static bool OperandEquals(object? left, object? right)
    {
        if (LibraDexObjectTuple.ValueEquals(left, right))
            return true;

        if (left is not Array leftArray || right is not Array rightArray ||
            leftArray.Rank != 1 || rightArray.Rank != 1 ||
            leftArray.Length != rightArray.Length)
        {
            return false;
        }

        for (int i = 0; i < leftArray.Length; i++)
        {
            if (!OperandEquals(leftArray.GetValue(i), rightArray.GetValue(i)))
                return false;
        }

        return true;
    }
}
