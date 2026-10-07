using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Identifies the logical key domain used to interpret a captured condition clause.<br/>
/// </summary>
public enum LibraDexConditionValueKind
{
    /// <summary>
    /// The clause has not selected a value kind yet.<br/>
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The clause addresses a string-like key or projection.<br/>
    /// </summary>
    String = 1,

    /// <summary>
    /// The clause addresses a binary key or projection.<br/>
    /// </summary>
    Binary = 2,

    /// <summary>
    /// The clause addresses a Boolean key or projection.<br/>
    /// </summary>
    Boolean = 3,

    /// <summary>
    /// The clause addresses a GUID key or projection.<br/>
    /// </summary>
    Guid = 4,

    /// <summary>
    /// The clause addresses a DateTime key or projection.<br/>
    /// </summary>
    DateTime = 5,

    /// <summary>
    /// The clause addresses a DateOnly key or projection.<br/>
    /// </summary>
    DateOnly = 6,

    /// <summary>
    /// The clause addresses a TimeOnly key or projection.<br/>
    /// </summary>
    TimeOnly = 7,

    /// <summary>
    /// The clause addresses a TimeSpan key or projection.<br/>
    /// </summary>
    TimeSpan = 8,

    /// <summary>
    /// The clause addresses a numeric key or projection.<br/>
    /// </summary>
    Numeric = 9,

    /// <summary>
    /// The clause addresses a routed composite-key shape.<br/>
    /// Composite leaves carry one or more tier predicates so physical execution can traverse component mini-routers instead of flattening parts into one concatenated key.<br/>
    /// </summary>
    Composite = 10
}

/// <summary>
/// Identifies a logical null or empty key sentinel for exact key-state conditions.<br/>
/// LibraDex string and varlen binary encodings preserve null and empty as distinct ordered key states, so callers can express the state directly without allocating placeholder values.<br/>
/// </summary>
public enum NullKey
{
    /// <summary>
    /// Matches the stored null-key sentinel.<br/>
    /// This represents a key value that was explicitly indexed as null; it does not mean the identity has no tuple in the selected index.<br/>
    /// </summary>
    Null = 0,

    /// <summary>
    /// Matches the stored empty-key sentinel.<br/>
    /// For string keys this is the empty string; for binary keys this is an empty byte sequence such as <see cref="Array.Empty{T}"/>.<br/>
    /// </summary>
    Empty = 1,

    /// <summary>
    /// Matches either the stored null-key sentinel or the stored empty-key sentinel.<br/>
    /// This is a convenience state for filters that treat explicit null and explicit empty as equivalent while still excluding ordinary non-empty keys.<br/>
    /// </summary>
    NullOrEmpty = 2
}

/// <summary>
/// Identifies scalar key presence for indexes whose ordinary key domain has no empty value.<br/>
/// Scalar null routes are stored outside the normal value router, so callers can ask for null-only or non-null scalar keys without inventing impossible numeric sentinel values.<br/>
/// </summary>
public enum ScalarNull
{
    /// <summary>
    /// Matches identities stored on the scalar null key route.<br/>
    /// Scalar nulls sort before ordinary non-null key values in LibraDex index-natural order.<br/>
    /// </summary>
    Null = 0,

    /// <summary>
    /// Matches ordinary non-null scalar keys and excludes the scalar null route.<br/>
    /// This state is the scalar-key counterpart to an `IS NOT NULL` predicate and has no empty-key route.<br/>
    /// </summary>
    NonNull = 1
}

/// <summary>
/// Identifies the adopted condition operation captured by one condition leaf.<br/>
/// These names mirror Abraxas operator intent at the descriptor boundary; the execution bridge maps them to LibraDex primitive requests only after an index is resolved.<br/>
/// </summary>
public enum LibraDexConditionOperatorKind
{
    /// <summary>
    /// Matches every identity visible through the selected index.<br/>
    /// </summary>
    All = 0,

    /// <summary>
    /// Matches values equal to the supplied operand.<br/>
    /// </summary>
    EqualTo = 1,

    /// <summary>
    /// Matches values not equal to the supplied operand.<br/>
    /// </summary>
    NotEqualTo = 2,

    /// <summary>
    /// Matches values greater than the supplied operand.<br/>
    /// </summary>
    GreaterThan = 3,

    /// <summary>
    /// Matches values greater than or equal to the supplied operand.<br/>
    /// </summary>
    GreaterOrEqual = 4,

    /// <summary>
    /// Matches values less than the supplied operand.<br/>
    /// </summary>
    LessThan = 5,

    /// <summary>
    /// Matches values less than or equal to the supplied operand.<br/>
    /// </summary>
    LessOrEqual = 6,

    /// <summary>
    /// Matches values between two supplied operands, inclusive.<br/>
    /// </summary>
    Between = 7,

    /// <summary>
    /// Matches values outside two supplied operands.<br/>
    /// </summary>
    NotBetween = 8,

    /// <summary>
    /// Matches values that start with the supplied operand.<br/>
    /// </summary>
    StartsWith = 9,

    /// <summary>
    /// Matches values that end with the supplied operand.<br/>
    /// </summary>
    EndsWith = 10,

    /// <summary>
    /// Matches values that contain the supplied operand.<br/>
    /// </summary>
    Contains = 11,

    /// <summary>
    /// Matches values using the supplied pattern descriptor.<br/>
    /// </summary>
    MatchesPattern = 12,

    /// <summary>
    /// Matches values in a supplied reusable membership set.<br/>
    /// </summary>
    InSet = 13,

    /// <summary>
    /// Matches values not in a supplied reusable membership set.<br/>
    /// </summary>
    NotInSet = 14,

    /// <summary>
    /// Matches a structured date/time year component.<br/>
    /// </summary>
    YearEqualTo = 15,

    /// <summary>
    /// Matches a structured date/time month component.<br/>
    /// </summary>
    MonthEqualTo = 16,

    /// <summary>
    /// Matches a structured date/time day component.<br/>
    /// </summary>
    DayEqualTo = 17,

    /// <summary>
    /// Matches a structured date/time quarter component.<br/>
    /// </summary>
    QuarterEqualTo = 18,

    /// <summary>
    /// Matches a structured date/time year and month component pair.<br/>
    /// </summary>
    YearMonth = 19,

    /// <summary>
    /// Matches a structured date/time year, month, and day component tuple.<br/>
    /// </summary>
    YearMonthDay = 20,

    /// <summary>
    /// Matches a contiguous structured date/time year range.<br/>
    /// </summary>
    YearRange = 21,

    /// <summary>
    /// Matches one of several structured date/time year and month component pairs.<br/>
    /// </summary>
    YearMonthIn = 22,

    /// <summary>
    /// Matches one of several structured date/time year, month, and day component tuples.<br/>
    /// </summary>
    YearMonthDayIn = 23,

    /// <summary>
    /// Matches structured date/time values whose year component does not equal the supplied operand.<br/>
    /// </summary>
    YearNotEqualTo = 24,

    /// <summary>
    /// Matches structured date/time values whose year component is in the supplied reusable membership set.<br/>
    /// </summary>
    YearIn = 25,

    /// <summary>
    /// Matches structured date/time values whose year component is not in the supplied reusable membership set.<br/>
    /// </summary>
    YearNotIn = 26,

    /// <summary>
    /// Matches structured date/time values whose year component is outside a supplied contiguous range.<br/>
    /// </summary>
    YearNotRange = 27,

    /// <summary>
    /// Matches structured date/time values whose year component is greater than or equal to the supplied operand.<br/>
    /// </summary>
    YearOnOrAfter = 28,

    /// <summary>
    /// Matches structured date/time values whose year component is less than or equal to the supplied operand.<br/>
    /// </summary>
    YearOnOrBefore = 29,

    /// <summary>
    /// Matches structured date/time values whose month component is in the supplied reusable membership set.<br/>
    /// </summary>
    MonthIn = 30,

    /// <summary>
    /// Matches structured date/time values whose month component is not in the supplied reusable membership set.<br/>
    /// </summary>
    MonthNotIn = 31,

    /// <summary>
    /// Matches structured date/time values whose month component is in the supplied contiguous component range.<br/>
    /// </summary>
    MonthRange = 32,

    /// <summary>
    /// Matches structured date/time values whose month component is outside the supplied contiguous component range.<br/>
    /// </summary>
    MonthNotRange = 33,

    /// <summary>
    /// Matches structured date/time values whose day component is in the supplied reusable membership set.<br/>
    /// </summary>
    DayIn = 34,

    /// <summary>
    /// Matches structured date/time values whose day component is in the supplied contiguous component range.<br/>
    /// </summary>
    DayRange = 35,

    /// <summary>
    /// Matches structured date/time values whose day component is outside the supplied contiguous component range.<br/>
    /// </summary>
    DayNotRange = 36,

    /// <summary>
    /// Matches one year and one of several month components.<br/>
    /// </summary>
    YearInMonths = 37,

    /// <summary>
    /// Matches one month/day component tuple across all years.<br/>
    /// </summary>
    MonthDay = 38,

    /// <summary>
    /// Matches structured date/time values whose month component belongs to one quarter.<br/>
    /// </summary>
    InQuarter = 39,

    /// <summary>
    /// Matches structured date/time values whose month component belongs to a contiguous quarter range.<br/>
    /// </summary>
    InQuarterRange = 40,

    /// <summary>
    /// Matches one year and one quarter.<br/>
    /// </summary>
    YearQuarter = 41,

    /// <summary>
    /// Matches dates at the start of a quarter.<br/>
    /// </summary>
    IsQuarterStart = 42,

    /// <summary>
    /// Matches dates at the end of a quarter.<br/>
    /// </summary>
    IsQuarterEnd = 43,

    /// <summary>
    /// Matches dates at the start of a half-year.<br/>
    /// </summary>
    IsHalfYearStart = 44,

    /// <summary>
    /// Matches dates at the end of a half-year.<br/>
    /// </summary>
    IsHalfYearEnd = 45,

    /// <summary>
    /// Matches dates at the first day of a month.<br/>
    /// </summary>
    IsFirstOfMonth = 46,

    /// <summary>
    /// Matches dates at the last day of a month.<br/>
    /// </summary>
    IsLastOfMonth = 47,

    /// <summary>
    /// Matches the current UTC date or day range at execution time.<br/>
    /// </summary>
    IsToday = 48,

    /// <summary>
    /// Matches the previous UTC date or day range at execution time.<br/>
    /// </summary>
    IsYesterday = 49,

    /// <summary>
    /// Matches a UTC-relative trailing day window at execution time.<br/>
    /// </summary>
    IsInLastDays = 50,

    /// <summary>
    /// Matches a UTC-relative trailing hour window at execution time.<br/>
    /// </summary>
    IsInLastHours = 51,

    /// <summary>
    /// Matches a UTC-relative trailing minute window at execution time.<br/>
    /// </summary>
    IsInLastMinutes = 52,

    /// <summary>
    /// Matches dates whose day-of-week component is Saturday or Sunday.<br/>
    /// </summary>
    IsWeekend = 53,

    /// <summary>
    /// Matches dates whose day-of-week component is Monday through Friday.<br/>
    /// </summary>
    IsWeekday = 54,

    /// <summary>
    /// Matches DateTime values whose hour component is in the morning interval.<br/>
    /// </summary>
    IsMorning = 55,

    /// <summary>
    /// Matches DateTime values whose hour component is in the afternoon interval.<br/>
    /// </summary>
    IsAfternoon = 56,

    /// <summary>
    /// Matches DateTime values whose hour component is in the evening interval.<br/>
    /// </summary>
    IsEvening = 57,

    /// <summary>
    /// Matches DateTime values whose hour component is in the night interval.<br/>
    /// </summary>
    IsNight = 58,

    /// <summary>
    /// Matches raw binary values whose byte slice at a zero-based offset equals the supplied byte sequence.<br/>
    /// </summary>
    BinarySliceEqual = 59,

    /// <summary>
    /// Matches scalar keys whose bitwise AND with a supplied mask equals the supplied comparison value.<br/>
    /// </summary>
    BitAndEqualTo = 60,

    /// <summary>
    /// Matches scalar keys whose bitwise AND with a supplied mask does not equal the supplied comparison value.<br/>
    /// </summary>
    BitAndNotEqualTo = 61,

    /// <summary>
    /// Matches raw binary values whose typed byte slice equals the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceEqualTo = 62,

    /// <summary>
    /// Matches raw binary values whose typed byte slice is greater than the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceGreaterThan = 63,

    /// <summary>
    /// Matches raw binary values whose typed byte slice is greater than or equal to the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceGreaterOrEqual = 64,

    /// <summary>
    /// Matches raw binary values whose typed byte slice is less than the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceLessThan = 65,

    /// <summary>
    /// Matches raw binary values whose typed byte slice is less than or equal to the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceLessOrEqual = 66,

    /// <summary>
    /// Matches raw binary values whose typed byte slice is between two supplied values.<br/>
    /// </summary>
    BinaryTypedSliceBetween = 67,

    /// <summary>
    /// Matches raw binary values whose typed text byte slice starts with the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceStartsWith = 68,

    /// <summary>
    /// Matches raw binary values whose typed text byte slice contains the supplied value.<br/>
    /// </summary>
    BinaryTypedSliceContains = 69,

    /// <summary>
    /// Matches raw binary values whose typed integral byte slice bitwise-ANDs with a supplied mask to the supplied comparison value.<br/>
    /// </summary>
    BinaryTypedSliceBitAndEqualTo = 70,

    /// <summary>
    /// Matches raw binary values whose typed integral byte slice bitwise-ANDs with a supplied mask to a different value than the supplied comparison value.<br/>
    /// </summary>
    BinaryTypedSliceBitAndNotEqualTo = 71,

    /// <summary>
    /// Matches a routed composite-key shape using one or more ordered tier predicates.<br/>
    /// </summary>
    CompositeMatch = 72,

    /// <summary>
    /// Matches string keys whose regex match or capture group equals the supplied value.<br/>
    /// </summary>
    MatchesWith = 73,

    /// <summary>
    /// Matches string keys whose regex match or capture group does not equal the supplied value.<br/>
    /// </summary>
    NotMatchesWith = 74,

    /// <summary>
    /// Matches string keys whose regex match or capture group is in a supplied value set.<br/>
    /// </summary>
    MatchesInSet = 75,

    /// <summary>
    /// Matches string keys whose regex match or capture group is not in a supplied value set.<br/>
    /// </summary>
    NotMatchesInSet = 76,

    /// <summary>
    /// Matches scalar key presence through root-level null-route metadata.<br/>
    /// </summary>
    ScalarNullState = 77,

    /// <summary>
    /// Matches string keys that satisfy the supplied regular expression.<br/>
    /// </summary>
    RegexMatches = 78,

    /// <summary>
    /// Matches string keys that do not satisfy the supplied regular expression.<br/>
    /// </summary>
    NotRegexMatches = 79,

    /// <summary>
    /// Matches string keys that do not satisfy the supplied wildcard pattern descriptor.<br/>
    /// </summary>
    NotMatchesPattern = 80,

    /// <summary>
    /// Matches identities that are associated with every distinct key supplied by one execution-time enumerable.<br/>
    /// The enumerable is materialized only when the condition executes, then LibraDex intersects exact-key routes without hydrating caller objects.<br/>
    /// </summary>
    KeysExistAll = 81,

    /// <summary>
    /// Matches the selected logical index's maintained null or non-null route without requiring the caller to select a CLR value family.<br/>
    /// Materialization resolves scalar-null versus string/binary null-key storage from the opened index contract.<br/>
    /// </summary>
    NullState = 82,

    /// <summary>
    /// Matches non-null string keys that do not start with the supplied text.<br/>
    /// </summary>
    NotStartsWith = 83,

    /// <summary>
    /// Matches non-null string keys that do not end with the supplied text.<br/>
    /// </summary>
    NotEndsWith = 84,

    /// <summary>
    /// Matches non-null string keys that do not contain the supplied text.<br/>
    /// </summary>
    NotContains = 85,

    /// <summary>
    /// Matches structured date/time values whose month component differs from the supplied value.<br/>
    /// </summary>
    MonthNotEqualTo = 86,

    /// <summary>
    /// Matches structured date/time values whose day component differs from the supplied value.<br/>
    /// </summary>
    DayNotEqualTo = 87,

    /// <summary>
    /// Matches structured date/time values whose day component is not in the supplied reusable membership set.<br/>
    /// </summary>
    DayNotIn = 88,

    /// <summary>
    /// Matches structured date/time values whose hour component equals the supplied value.<br/>
    /// </summary>
    HourEqualTo = 89,

    /// <summary>
    /// Matches structured date/time values whose hour component is in the supplied reusable membership set.<br/>
    /// </summary>
    HourIn = 90,

    /// <summary>
    /// Matches structured date/time values whose hour component is not in the supplied reusable membership set.<br/>
    /// </summary>
    HourNotIn = 91,

    /// <summary>
    /// Matches structured date/time values whose hour component is in the supplied inclusive range.<br/>
    /// </summary>
    HourRange = 92,

    /// <summary>
    /// Matches structured date/time values whose hour component is outside the supplied inclusive range.<br/>
    /// </summary>
    HourNotRange = 93
}

/// <summary>
/// Represents one adopted condition operand that can be materialized when a condition is bound to opened indexes.<br/>
/// Static operands preserve ordinary call sites, while deferred operands preserve Abraxas-style late value resolution without keeping the old condition-builder grammar alive.<br/>
/// </summary>
public sealed class LibraDexConditionOperand
{
    private readonly object? staticValue;
    private readonly Func<object?>? valueFactory;
    private readonly ILibraDexParameter? parameter;

    private LibraDexConditionOperand(object? staticValue, Func<object?>? valueFactory, ILibraDexParameter? parameter)
    {
        this.staticValue = staticValue;
        this.valueFactory = valueFactory;
        this.parameter = parameter;
    }

    internal bool IsDeferred => valueFactory is not null || parameter is not null;

    internal bool HasExplicitParameter
        => parameter is not null ||
           (valueFactory is null && staticValue is ILibraDexParameterSnapshotValue nested && nested.HasParameters);

    /// <summary>
    /// Creates a static adopted condition operand.<br/>
    /// The value is captured as supplied and validated later against the resolved LibraDex index key contract.<br/>
    /// </summary>
    /// <param name="value">The value to capture.</param>
    /// <returns>A condition operand descriptor.</returns>
    public static LibraDexConditionOperand Value(object? value)
        => new LibraDexConditionOperand(value, valueFactory: null, parameter: null);

    /// <summary>
    /// Creates a deferred adopted condition operand.<br/>
    /// The supplied factory is invoked only during materialization so reusable descriptors can bind to current request values without rebuilding the condition chain.<br/>
    /// </summary>
    /// <param name="valueFactory">The value factory to evaluate during materialization.</param>
    /// <returns>A condition operand descriptor.</returns>
    public static LibraDexConditionOperand Deferred(Func<object?> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return new LibraDexConditionOperand(staticValue: null, valueFactory, parameter: null);
    }

    /// <summary>
    /// Creates an operand backed directly by a reusable typed parameter.<br/>
    /// The parameter is read through the execution-local snapshot without allocating a caller-side lambda or an internal closure.<br/>
    /// </summary>
    /// <typeparam name="T">The parameter value type.</typeparam>
    /// <param name="parameter">The parameter to read when the condition executes.</param>
    /// <returns>A condition operand descriptor.</returns>
    internal static LibraDexConditionOperand Parameter<T>(LibraDexParameter<T> parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new LibraDexConditionOperand(staticValue: null, valueFactory: null, parameter);
    }

    /// <summary>
    /// Materializes the current operand value.<br/>
    /// Static operands return the captured value; deferred operands invoke their factory each time this method is called.<br/>
    /// </summary>
    /// <returns>The current operand value.</returns>
    public object? GetValue()
        => parameter is null ? (valueFactory is null ? staticValue : valueFactory()) : parameter.ReadValue();

    internal object? GetValue(LibraDexParameterSnapshot snapshot)
    {
        object? value = parameter is null ? (valueFactory is null ? staticValue : valueFactory()) : snapshot.Read(parameter);
        return value is ILibraDexParameterSnapshotValue nested ? nested.Snapshot(snapshot) : value;
    }

    internal void CaptureParameter(LibraDexParameterSnapshot snapshot)
    {
        if (parameter is not null)
        {
            _ = snapshot.Read(parameter);
            return;
        }

        if (valueFactory is null && staticValue is ILibraDexParameterSnapshotValue nested)
            nested.CaptureParameters(snapshot);
    }
}

/// <summary>
/// Selects the logical LibraDex index name for one condition leaf.<br/>
/// Static selectors preserve ordinary index names, while deferred selectors mirror Abraxas `PropPath(Func&lt;string&gt;)` semantics by resolving the index only when the condition is inspected or materialized.<br/>
/// </summary>
public sealed class LibraDexConditionIndexSelector
{
    private readonly string? staticIndexName;
    private readonly Func<string>? indexNameFactory;
    private readonly ILibraDexParameter? parameter;

    private LibraDexConditionIndexSelector(string? staticIndexName, Func<string>? indexNameFactory, ILibraDexParameter? parameter, string? name)
    {
        this.staticIndexName = staticIndexName;
        this.indexNameFactory = indexNameFactory;
        this.parameter = parameter;
        Name = name;
    }

    /// <summary>
    /// Gets the optional diagnostic selector name retained in bookmark provenance.<br/>
    /// The name does not participate in physical index resolution.<br/>
    /// </summary>
    public string? Name { get; }

    internal bool IsDeferred => indexNameFactory is not null || parameter is not null;

    internal bool HasExplicitParameter => parameter is not null;

    /// <summary>
    /// Creates a static index selector.<br/>
    /// The supplied name is still resolved through the caller's index resolver when the condition materializes.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the condition's identity group.</param>
    /// <param name="name">Optional diagnostic selector name retained in bookmark provenance.</param>
    /// <returns>An index selector descriptor.</returns>
    public static LibraDexConditionIndexSelector Static(string indexName, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionIndexSelector(indexName, indexNameFactory: null, parameter: null, name);
    }

    /// <summary>
    /// Creates a deferred index selector.<br/>
    /// The supplied factory is invoked each time the condition needs the current index name, including bridge inspection and materialization.<br/>
    /// </summary>
    /// <param name="indexNameFactory">Factory that returns the current index name inside the condition's identity group.</param>
    /// <param name="name">Optional diagnostic selector name retained in bookmark provenance.</param>
    /// <returns>An index selector descriptor.</returns>
    public static LibraDexConditionIndexSelector Deferred(Func<string> indexNameFactory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(indexNameFactory);
        return new LibraDexConditionIndexSelector(staticIndexName: null, indexNameFactory, parameter: null, name);
    }

    /// <summary>
    /// Creates an index selector backed by a reusable string parameter.<br/>
    /// The parameter's current index name is snapshotted once when execution begins.<br/>
    /// </summary>
    /// <param name="parameter">The parameter containing the current index name.</param>
    /// <param name="name">The optional selector parameter name; defaults to the supplied parameter name.<br/></param>
    /// <returns>An index selector descriptor.</returns>
    internal static LibraDexConditionIndexSelector Parameter(LibraDexParameter<string> parameter, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new LibraDexConditionIndexSelector(staticIndexName: null, indexNameFactory: null, parameter, name ?? parameter.Name);
    }

    /// <summary>
    /// Creates an index selector backed by a reusable opened-index parameter.<br/>
    /// LibraDex reads the handle once per execution and uses its current <see cref="IIndex.Name"/> while retaining ordinary group validation at resolution.<br/>
    /// </summary>
    /// <typeparam name="TIndex">The opened index handle type.</typeparam>
    /// <param name="parameter">The parameter containing the current opened index.</param>
    /// <param name="name">The optional selector parameter name; defaults to the supplied parameter name.<br/></param>
    /// <returns>An index selector descriptor.</returns>
    internal static LibraDexConditionIndexSelector Parameter<TIndex>(LibraDexParameter<TIndex> parameter, string? name = null)
        where TIndex : IIndex
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new LibraDexConditionIndexSelector(staticIndexName: null, indexNameFactory: null, parameter, name ?? parameter.Name);
    }

    /// <summary>
    /// Resolves the current index name.<br/>
    /// Deferred selectors validate the returned name at the point of use so bad runtime selector state fails close to the materialization request.<br/>
    /// </summary>
    /// <returns>The current index name.</returns>
    public string GetIndexName()
    {
        object? selected = parameter?.ReadValue();
        string? indexName = selected switch
        {
            null => indexNameFactory is null ? staticIndexName : indexNameFactory(),
            string name => name,
            IIndex index => index.Name,
            _ => throw new InvalidOperationException($"Index selector parameter '{Name ?? "<unnamed>"}' returned unsupported type '{selected.GetType().FullName}'.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return indexName;
    }

    internal string GetIndexName(LibraDexParameterSnapshot snapshot, string? expectedGroup = null)
    {
        object? selected = parameter is null ? null : snapshot.Read(parameter);
        if (selected is IIndex selectedIndex &&
            !string.IsNullOrEmpty(expectedGroup) &&
            !string.Equals(selectedIndex.Group, expectedGroup, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Parameterized index '{selectedIndex.Name}' belongs to group '{selectedIndex.Group}', not condition group '{expectedGroup}'.");
        }

        string? indexName = selected switch
        {
            null => indexNameFactory is null ? staticIndexName : indexNameFactory(),
            string name => name,
            IIndex index => index.Name,
            _ => throw new InvalidOperationException($"Index selector parameter '{Name ?? "<unnamed>"}' returned unsupported type '{selected.GetType().FullName}'.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return indexName;
    }

    internal void CaptureParameter(LibraDexParameterSnapshot snapshot)
    {
        if (parameter is not null)
            _ = snapshot.Read(parameter);
    }
}

/// <summary>
/// Identifies how condition-scoped group results should be ordered after candidate identities are materialized.<br/>
/// This belongs to the adopted grouping terminal and is not a root-index grouping facade.<br/>
/// </summary>
public enum LibraDexGroupOrder
{
    /// <summary>
    /// Orders groups by key in ascending index order.<br/>
    /// </summary>
    KeyAscending = 0,

    /// <summary>
    /// Orders groups by key in descending index order.<br/>
    /// </summary>
    KeyDescending = 1,

    /// <summary>
    /// Orders groups by member count from smallest to largest.<br/>
    /// </summary>
    CountAscending = 2,

    /// <summary>
    /// Orders groups by member count from largest to smallest.<br/>
    /// </summary>
    CountDescending = 3
}

/// <summary>
/// Identifies which representative identity should be returned for each condition-scoped group.<br/>
/// Representatives are selected after the requested identity ordering and duplicate policy have been applied.<br/>
/// </summary>
public enum LibraDexGroupRepresentative
{
    /// <summary>
    /// Uses the first identity in each materialized group.<br/>
    /// </summary>
    First = 0,

    /// <summary>
    /// Uses the last identity in each materialized group.<br/>
    /// </summary>
    Last = 1
}

/// <summary>
/// Describes one group produced by an adopted condition grouping terminal without exposing the full member list.<br/>
/// Metadata keeps count and edge identities available for common duplicate/singleton and first/last workflows while leaving full materialization opt-in.<br/>
/// </summary>
/// <typeparam name="TKey">The grouping index key type.</typeparam>
/// <typeparam name="TIdentity">The identity type shared by the condition and grouping index.</typeparam>
/// <param name="Key">The grouping key.</param>
/// <param name="Count">The number of identities in the group.</param>
/// <param name="FirstIdentity">The first identity in the group after requested ordering.</param>
/// <param name="LastIdentity">The last identity in the group after requested ordering.</param>
public readonly record struct LibraDexGroupMetadata<TKey, TIdentity>(
    TKey Key,
    long Count,
    TIdentity FirstIdentity,
    TIdentity LastIdentity);

/// <summary>
/// Classifies what a completed adopted condition leaf needs from LibraDex before it can execute efficiently and honestly.<br/>
/// The classification is intentionally about required execution shape, not whether the current scaffold can already run the request.<br/>
/// </summary>
public enum LibraDexConditionExecutionClass
{
    /// <summary>
    /// The leaf can use the selected index's ordinary ordered-key primitive or exact key-run primitive.<br/>
    /// </summary>
    IndexBacked = 0,

    /// <summary>
    /// The leaf needs a maintained projection, such as folded text, sort-key bytes, reversed bytes, or GUID segment/text keys.<br/>
    /// </summary>
    ProjectionBacked = 1,

    /// <summary>
    /// The leaf targets a composite index or composite-key descriptor rather than a single primitive key.<br/>
    /// </summary>
    CompositeBacked = 2,

    /// <summary>
    /// The leaf is valid descriptor intent but currently needs visible scan-like behavior because no aligned maintained index/projection is known.<br/>
    /// </summary>
    VisibleScanLike = 3,

    /// <summary>
    /// The leaf should be rejected until a codec, projection, or primitive execution rule exists.<br/>
    /// </summary>
    Unsupported = 4,

    /// <summary>
    /// The leaf references an index name that was not supplied by the resolver.<br/>
    /// </summary>
    ResolutionFailure = 5,

    /// <summary>
    /// The resolved index belongs to a different identity group than the condition.<br/>
    /// </summary>
    IdentityGroupMismatch = 6
}

/// <summary>
/// Describes the execution requirement classified for one adopted condition leaf.<br/>
/// This is the first permutation-review artifact: it lets tests and adapters inspect whether a condition is index-backed, projection-backed, composite-backed, scan-like, or unsupported before calling an executor.<br/>
/// </summary>
/// <param name="IndexName">The index name captured by the condition leaf.</param>
/// <param name="ValueKind">The value kind selected by the builder.</param>
/// <param name="Operator">The adopted operator captured by the builder.</param>
/// <param name="ExecutionClass">The classified execution requirement.</param>
/// <param name="Reason">Short reason explaining the classification.</param>
/// <param name="ProjectionKind">The maintained projection kind required or found, when applicable.</param>
public readonly record struct LibraDexConditionLeafClassification(
    string IndexName,
    LibraDexConditionValueKind ValueKind,
    LibraDexConditionOperatorKind Operator,
    LibraDexConditionExecutionClass ExecutionClass,
    string Reason,
    LibraDexIndexProjectionKind? ProjectionKind);

/// <summary>
/// Identifies the next bridge action needed for one adopted condition leaf.<br/>
/// This converts classification into an implementation decision: execute through today's primitive spine, connect a projection/composite primitive, require an explicit scan policy, or reject/repair the descriptor.<br/>
/// </summary>
public enum LibraDexConditionBridgeAction
{
    /// <summary>
    /// The leaf can materialize into the current `IIdentityCriterion` primitive bridge now.<br/>
    /// </summary>
    ExecuteCurrentPrimitive = 0,

    /// <summary>
    /// The leaf needs a maintained projection primitive before it should execute as a fast path.<br/>
    /// </summary>
    ConnectProjectionPrimitive = 1,

    /// <summary>
    /// The leaf needs a composite-key primitive before it should execute as a fast path.<br/>
    /// </summary>
    ConnectCompositePrimitive = 2,

    /// <summary>
    /// The leaf is valid intent but needs an explicit visible scan policy before execution is allowed.<br/>
    /// </summary>
    RequireVisibleScanPolicy = 3,

    /// <summary>
    /// The leaf should be rejected until LibraDex has a codec, projection, or primitive rule for it.<br/>
    /// </summary>
    RejectUnsupported = 4,

    /// <summary>
    /// The caller must supply an opened index for the captured index name.<br/>
    /// </summary>
    ResolveIndex = 5,

    /// <summary>
    /// The caller must resolve the condition only against indexes in the condition's identity group.<br/>
    /// </summary>
    FixIdentityGroup = 6
}

/// <summary>
/// Describes the bridge decision for one adopted condition leaf.<br/>
/// Rows are ordered the same way as <see cref="LibraDexConditionEndCondition.Leaves"/> so permutation tests can explain exactly which condition piece blocks execution.<br/>
/// </summary>
/// <param name="Classification">The prior execution-shape classification.</param>
/// <param name="Action">The next bridge action required by the classification.</param>
/// <param name="CanExecuteWithCurrentBridge">Whether the current `IIdentityCriterion` materializer can execute this leaf without adding a new projection, composite, or scan-policy bridge.</param>
/// <param name="Reason">Short reason explaining the bridge decision.</param>
public readonly record struct LibraDexConditionBridgePlanRow(
    LibraDexConditionLeafClassification Classification,
    LibraDexConditionBridgeAction Action,
    bool CanExecuteWithCurrentBridge,
    string Reason);

/// <summary>
/// Describes the bridge readiness of a completed adopted condition.<br/>
/// This plan is intentionally smaller than a physical execution plan: it answers what code must exist before the condition should execute, not which shelves or routers will be touched.<br/>
/// </summary>
/// <param name="Group">The condition identity group.</param>
/// <param name="Rows">Bridge decisions for each leaf in builder order.</param>
public sealed record LibraDexConditionBridgePlan(
    string Group,
    IReadOnlyList<LibraDexConditionBridgePlanRow> Rows)
{
    /// <summary>
    /// Gets whether every leaf can execute through the current primitive bridge.<br/>
    /// </summary>
    public bool CanExecuteWithCurrentBridge => Rows.Count != 0 && Rows.All(static row => row.CanExecuteWithCurrentBridge);

    /// <summary>
    /// Gets whether at least one row requires a maintained projection primitive.<br/>
    /// </summary>
    public bool RequiresProjectionBridge => Rows.Any(static row => row.Action == LibraDexConditionBridgeAction.ConnectProjectionPrimitive);

    /// <summary>
    /// Gets whether at least one row requires a composite-key primitive.<br/>
    /// </summary>
    public bool RequiresCompositeBridge => Rows.Any(static row => row.Action == LibraDexConditionBridgeAction.ConnectCompositePrimitive);

    /// <summary>
    /// Gets whether at least one row requires an explicit scan policy before execution.<br/>
    /// </summary>
    public bool RequiresVisibleScanPolicy => Rows.Any(static row => row.Action == LibraDexConditionBridgeAction.RequireVisibleScanPolicy);

    /// <summary>
    /// Gets whether the plan contains any row that cannot be executed until the caller or implementation changes something.<br/>
    /// </summary>
    public bool HasBlockingRows => Rows.Any(static row => !row.CanExecuteWithCurrentBridge);
}

/// <summary>
/// Describes one adopted condition leaf before it is resolved to an opened LibraDex index.<br/>
/// The descriptor keeps source builder intent separate from physical execution so permutation tests can inspect what each condition requires before bridge code is widened.<br/>
/// </summary>
public sealed class LibraDexConditionLeafDescriptor
{
    /// <summary>
    /// Creates a leaf descriptor with a static index name.<br/>
    /// The index name remains logical and is still resolved against opened LibraDex indexes at materialization time.<br/>
    /// </summary>
    /// <param name="indexName">The LibraDex index name inside the identity group.</param>
    /// <param name="valueKind">The selected value kind.</param>
    /// <param name="operatorKind">The adopted operator captured for this leaf.</param>
    /// <param name="operands">The static or deferred operands captured by the operator.</param>
    /// <param name="IgnoreCase">Whether the source condition asked for case-insensitive text behavior.</param>
    /// <param name="Culture">The source culture name associated with text comparison, when supplied.</param>
    /// <param name="StringComparisonPolicy">The optional method-level managed string comparison policy.</param>
    /// <param name="TextNormalization">The optional selector-level text normalization applied before comparison.</param>
    public LibraDexConditionLeafDescriptor(
        string indexName,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool IgnoreCase,
        string? Culture,
        LibraDexStringComparisonPolicy? StringComparisonPolicy = null,
        LibraDexTextNormalization TextNormalization = LibraDexTextNormalization.None)
        : this(
            LibraDexConditionIndexSelector.Static(indexName),
            valueKind,
            operatorKind,
            operands,
            IgnoreCase,
            Culture,
            StringComparisonPolicy,
            TextNormalization)
    {
    }

    /// <summary>
    /// Creates a leaf descriptor with an index selector.<br/>
    /// Deferred selectors are evaluated when the descriptor is inspected or materialized, preserving reusable condition variables that can bind to different aligned indexes.<br/>
    /// </summary>
    /// <param name="indexSelector">The static or deferred index selector.</param>
    /// <param name="valueKind">The selected value kind.</param>
    /// <param name="operatorKind">The adopted operator captured for this leaf.</param>
    /// <param name="operands">The static or deferred operands captured by the operator.</param>
    /// <param name="IgnoreCase">Whether the source condition asked for case-insensitive text behavior.</param>
    /// <param name="Culture">The source culture name associated with text comparison, when supplied.</param>
    /// <param name="StringComparisonPolicy">The optional method-level managed string comparison policy.</param>
    /// <param name="TextNormalization">The optional selector-level text normalization applied before comparison.</param>
    public LibraDexConditionLeafDescriptor(
        LibraDexConditionIndexSelector indexSelector,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool IgnoreCase,
        string? Culture,
        LibraDexStringComparisonPolicy? StringComparisonPolicy = null,
        LibraDexTextNormalization TextNormalization = LibraDexTextNormalization.None)
        : this(
            indexSelector,
            valueKind,
            operatorKind,
            operands,
            IgnoreCase,
            Culture,
            StringComparisonPolicy,
            numericTransform: null,
            textNormalization: TextNormalization)
    {
    }

    /// <summary>
    /// Initializes a condition leaf with an optional planner-visible numeric transform.<br/>
    /// The transform is retained across deferred selector and operand freezing so reusable conditions preserve their declared value semantics.<br/>
    /// </summary>
    /// <param name="indexSelector">The static, deferred, or parameter-backed index selector.<br/></param>
    /// <param name="valueKind">The logical value family selected by the fluent grammar.<br/></param>
    /// <param name="operatorKind">The comparison or state operator.<br/></param>
    /// <param name="operands">The static, deferred, or parameter-backed operands.<br/></param>
    /// <param name="IgnoreCase">Whether text comparison ignores case.<br/></param>
    /// <param name="Culture">The optional text-comparison culture.<br/></param>
    /// <param name="StringComparisonPolicy">The optional managed string-comparison policy.<br/></param>
    /// <param name="numericTransform">The optional native numeric transform applied before comparison.<br/></param>
    /// <param name="textNormalization">The optional selector-level normalization applied before comparison.<br/></param>
    internal LibraDexConditionLeafDescriptor(
        LibraDexConditionIndexSelector indexSelector,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool IgnoreCase,
        string? Culture,
        LibraDexStringComparisonPolicy? StringComparisonPolicy,
        LibraDexNumericTransformDescriptor? numericTransform,
        LibraDexTextNormalization textNormalization = LibraDexTextNormalization.None)
    {
        ArgumentNullException.ThrowIfNull(indexSelector);
        ArgumentNullException.ThrowIfNull(operands);
        IndexSelector = indexSelector;
        ValueKind = valueKind;
        Operator = operatorKind;
        Operands = operands;
        this.IgnoreCase = IgnoreCase;
        this.Culture = Culture;
        this.StringComparisonPolicy = StringComparisonPolicy;
        NumericTransform = numericTransform;
        TextNormalization = textNormalization;
    }

    /// <summary>
    /// Gets the index selector captured by this leaf.<br/>
    /// Deferred selectors should be resolved only at inspection or materialization time, not while composing reusable expressions.<br/>
    /// </summary>
    public LibraDexConditionIndexSelector IndexSelector { get; }

    /// <summary>
    /// Gets the current resolved index name inside the identity group.<br/>
    /// For deferred selectors this property invokes the selector factory, so callers should not cache it across materialization boundaries.<br/>
    /// </summary>
    public string IndexName => IndexSelector.GetIndexName();

    /// <summary>
    /// Gets the selected value kind.<br/>
    /// </summary>
    public LibraDexConditionValueKind ValueKind { get; }

    /// <summary>
    /// Gets the adopted operator captured for this leaf.<br/>
    /// </summary>
    public LibraDexConditionOperatorKind Operator { get; }

    /// <summary>
    /// Gets the static or deferred operands captured by the operator.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexConditionOperand> Operands { get; }

    /// <summary>
    /// Gets whether the source condition asked for case-insensitive text behavior.<br/>
    /// </summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// Gets the source culture name associated with text comparison, when supplied.<br/>
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Gets the optional method-level managed string comparison policy.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicy? StringComparisonPolicy { get; }

    /// <summary>
    /// Gets the selector-level canonical text normalization applied before the condition operator.<br/>
    /// This remains distinct from case or culture comparison policy so a maintained normalized projection and its exact-index fallback share one explicit semantic contract.<br/>
    /// </summary>
    public LibraDexTextNormalization TextNormalization { get; }

    internal LibraDexNumericTransformDescriptor? NumericTransform { get; }

    internal LibraDexConditionLeafDescriptor WithIndexSelector(LibraDexConditionIndexSelector indexSelector)
        => new LibraDexConditionLeafDescriptor(indexSelector, ValueKind, Operator, Operands, IgnoreCase, Culture, StringComparisonPolicy, NumericTransform, TextNormalization);

    internal LibraDexConditionLeafDescriptor WithOperands(IReadOnlyList<LibraDexConditionOperand> operands)
        => new LibraDexConditionLeafDescriptor(IndexSelector, ValueKind, Operator, operands, IgnoreCase, Culture, StringComparisonPolicy, NumericTransform, TextNormalization);
}

/// <summary>
/// Identifies the native numeric operation applied to an indexed real value before comparison.<br/>
/// </summary>
internal enum LibraDexNumericTransformKind
{
    Round = 0,
    Floor = 1,
    Ceiling = 2,
    Truncate = 3
}

/// <summary>
/// Captures one native numeric transform and the rounding arguments needed to reproduce it during execution.<br/>
/// </summary>
/// <param name="Kind">The native transform operation.<br/></param>
/// <param name="Digits">The decimal digits retained by Round; other transforms store zero.<br/></param>
/// <param name="MidpointRounding">The midpoint policy used by Round.<br/></param>
internal readonly record struct LibraDexNumericTransformDescriptor(
    LibraDexNumericTransformKind Kind,
    int Digits,
    MidpointRounding MidpointRounding);
