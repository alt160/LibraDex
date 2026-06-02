using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace LibraDex;

/// <summary>
/// Provides the Abraxas-adopted entry point for LibraDex condition construction.<br/>
/// The builder is identity-group scoped first, then index-name scoped, so copied Abraxas condition grammar maps to LibraDex indexes without preserving object-property-path terminology.<br/>
/// </summary>
public static class LibraDexCondition
{
    /// <summary>
    /// Starts an Abraxas-adopted condition builder for one LibraDex identity group.<br/>
    /// Index names referenced inside the builder are resolved later against the caller's opened indexes, which lets generated adapters build reusable condition templates before handles are available.<br/>
    /// </summary>
    /// <param name="group">The identity group name shared by every index referenced by the condition.</param>
    /// <returns>A condition clause builder scoped to the supplied identity group.</returns>
    public static LibraDexConditionClause ForGroup(string group)
    {
        return new LibraDexConditionClause(new LibraDexConditionBuilder(group));
    }
}

/// <summary>
/// Identifies the value type selected for one adopted condition clause.<br/>
/// The value kind is descriptor metadata first; physical execution still depends on the resolved LibraDex index and its persisted key contract.<br/>
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
    CompositeMatch = 72
}

/// <summary>
/// Represents one adopted condition operand that can be materialized when a condition is bound to opened indexes.<br/>
/// Static operands preserve ordinary call sites, while deferred operands preserve Abraxas-style late value resolution without keeping the old condition-builder grammar alive.<br/>
/// </summary>
public sealed class LibraDexConditionOperand
{
    private readonly object? staticValue;
    private readonly Func<object?>? valueFactory;

    private LibraDexConditionOperand(object? staticValue, Func<object?>? valueFactory, string? name)
    {
        this.staticValue = staticValue;
        this.valueFactory = valueFactory;
        Name = name;
    }

    /// <summary>
    /// Gets the optional operand name used by replacement-capable adapters.<br/>
    /// The adopted builder records names as descriptor metadata only; it does not expose the deleted old condition-builder replacement API.<br/>
    /// </summary>
    public string? Name { get; }

    internal bool IsDeferred => valueFactory is not null;

    /// <summary>
    /// Creates a static adopted condition operand.<br/>
    /// The value is captured as supplied and validated later against the resolved LibraDex index key contract.<br/>
    /// </summary>
    /// <param name="value">The value to capture.</param>
    /// <param name="name">Optional adapter-level operand name.</param>
    /// <returns>A condition operand descriptor.</returns>
    public static LibraDexConditionOperand Value(object? value, string? name = null)
    {
        return new LibraDexConditionOperand(value, valueFactory: null, name);
    }

    /// <summary>
    /// Creates a deferred adopted condition operand.<br/>
    /// The supplied factory is invoked only during materialization so reusable descriptors can bind to current request values without rebuilding the condition chain.<br/>
    /// </summary>
    /// <param name="valueFactory">The value factory to evaluate during materialization.</param>
    /// <param name="name">Optional adapter-level operand name.</param>
    /// <returns>A condition operand descriptor.</returns>
    public static LibraDexConditionOperand Deferred(Func<object?> valueFactory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return new LibraDexConditionOperand(staticValue: null, valueFactory, name);
    }

    /// <summary>
    /// Materializes the current operand value.<br/>
    /// Static operands return the captured value; deferred operands invoke their factory each time this method is called.<br/>
    /// </summary>
    /// <returns>The current operand value.</returns>
    public object? GetValue()
    {
        return valueFactory is null ? staticValue : valueFactory();
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

    private LibraDexConditionIndexSelector(string? staticIndexName, Func<string>? indexNameFactory, string? name)
    {
        this.staticIndexName = staticIndexName;
        this.indexNameFactory = indexNameFactory;
        Name = name;
    }

    /// <summary>
    /// Gets the optional selector name used by replacement-capable reusable conditions.<br/>
    /// The name is descriptor metadata only and does not change physical index resolution.<br/>
    /// </summary>
    public string? Name { get; }

    internal bool IsDeferred => indexNameFactory is not null;

    /// <summary>
    /// Creates a static index selector.<br/>
    /// The supplied name is still resolved through the caller's index resolver when the condition materializes.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the condition's identity group.</param>
    /// <param name="name">Optional replacement selector name.</param>
    /// <returns>An index selector descriptor.</returns>
    public static LibraDexConditionIndexSelector Static(string indexName, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionIndexSelector(indexName, indexNameFactory: null, name);
    }

    /// <summary>
    /// Creates a deferred index selector.<br/>
    /// The supplied factory is invoked each time the condition needs the current index name, including bridge inspection and materialization.<br/>
    /// </summary>
    /// <param name="indexNameFactory">Factory that returns the current index name inside the condition's identity group.</param>
    /// <param name="name">Optional replacement selector name.</param>
    /// <returns>An index selector descriptor.</returns>
    public static LibraDexConditionIndexSelector Deferred(Func<string> indexNameFactory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(indexNameFactory);
        return new LibraDexConditionIndexSelector(staticIndexName: null, indexNameFactory, name);
    }

    /// <summary>
    /// Resolves the current index name.<br/>
    /// Deferred selectors validate the returned name at the point of use so bad runtime selector state fails close to the materialization request.<br/>
    /// </summary>
    /// <returns>The current index name.</returns>
    public string GetIndexName()
    {
        string? indexName = indexNameFactory is null ? staticIndexName : indexNameFactory();
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return indexName;
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
/// <param name="ValueKind">The selected value kind.</param>
/// <param name="Operator">The adopted operator captured for this leaf.</param>
/// <param name="Operands">The static or deferred operands captured by the operator.</param>
/// <param name="IgnoreCase">Whether the source condition asked for case-insensitive text behavior.</param>
/// <param name="Culture">The source culture name associated with text comparison, when supplied.</param>
/// <param name="StringComparisonPolicy">The optional method-level managed string comparison policy.</param>
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
    public LibraDexConditionLeafDescriptor(
        string indexName,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool IgnoreCase,
        string? Culture,
        LibraDexStringComparisonPolicy? StringComparisonPolicy = null)
        : this(
            LibraDexConditionIndexSelector.Static(indexName),
            valueKind,
            operatorKind,
            operands,
            IgnoreCase,
            Culture,
            StringComparisonPolicy)
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
    public LibraDexConditionLeafDescriptor(
        LibraDexConditionIndexSelector indexSelector,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool IgnoreCase,
        string? Culture,
        LibraDexStringComparisonPolicy? StringComparisonPolicy = null)
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

    internal LibraDexConditionLeafDescriptor WithIndexSelector(LibraDexConditionIndexSelector indexSelector)
    {
        return new LibraDexConditionLeafDescriptor(indexSelector, ValueKind, Operator, Operands, IgnoreCase, Culture, StringComparisonPolicy);
    }

    internal LibraDexConditionLeafDescriptor WithOperands(IReadOnlyList<LibraDexConditionOperand> operands)
    {
        return new LibraDexConditionLeafDescriptor(IndexSelector, ValueKind, Operator, operands, IgnoreCase, Culture, StringComparisonPolicy);
    }
}

/// <summary>
/// Represents a completed Abraxas-adopted LibraDex condition.<br/>
/// A completed condition is still descriptor-shaped until the caller supplies an index resolver for the owning identity group.<br/>
/// </summary>
public sealed class LibraDexConditionEndCondition
{
    private readonly LibraDexConditionNode root;

    internal LibraDexConditionEndCondition(string group, LibraDexConditionNode root)
    {
        Group = group;
        this.root = root;
    }

    /// <summary>
    /// Gets the identity group shared by all indexes referenced by this condition.<br/>
    /// The materializer verifies that resolved indexes remain in this group so reusable descriptors cannot accidentally cross identity universes.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the leaf descriptors in the order they were captured by the builder.<br/>
    /// This is intended for permutation tests, bridge diagnostics, and adapter review before the condition is resolved to physical index handles.<br/>
    /// </summary>
    public IReadOnlyList<LibraDexConditionLeafDescriptor> Leaves => root.GetLeaves();

    /// <summary>
    /// Materializes this adopted condition into the current LibraDex identity-criterion tree.<br/>
    /// The resolver receives each index name captured by the builder and must return the corresponding opened index in this condition's identity group.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion Materialize(Func<string, IIndex> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        return root.Materialize(Group, resolveIndex, resolveProjectionIndex: null);
    }

    /// <summary>
    /// Materializes this adopted condition with an explicit maintained-projection resolver.<br/>
    /// The normal index resolver supplies the logical indexes referenced by the condition, while the projection resolver may supply a physical projection index for leaves classified as projection-backed.<br/>
    /// This keeps projection execution explicit: LibraDex can use a maintained projection when the caller provides one, but it does not silently scan or invent projection storage.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to the logical opened LibraDex index.</param>
    /// <param name="resolveProjectionIndex">Function that resolves a projection-backed leaf to a maintained projection index, or null when no physical projection is available.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion MaterializeWithProjectionBridge(
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?> resolveProjectionIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        ArgumentNullException.ThrowIfNull(resolveProjectionIndex);
        return root.Materialize(Group, resolveIndex, resolveProjectionIndex);
    }

    /// <summary>
    /// Materializes this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload is useful for generated callers and tests that already have the identity-group index handles in a simple lookup table.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>An identity criterion tree executable by the existing LibraDex criteria projection layer.</returns>
    public IIdentityCriterion Materialize(IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return Materialize(indexName =>
        {
            if (!indexes.TryGetValue(indexName, out IIndex? index))
            {
                throw new KeyNotFoundException($"Index '{indexName}' was not supplied for condition group '{Group}'.");
            }

            return index;
        });
    }

    /// <summary>
    /// Builds an identity projection for this condition after resolving its index names.<br/>
    /// This is a low-friction bridge for the first adopted-builder slice; later slices can add richer terminal helpers without changing the descriptor grammar.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>An identity projection descriptor.</returns>
    public IIdentityCriterionProjection IDs(Func<string, IIndex> resolveIndex)
    {
        return Materialize(resolveIndex).IDs;
    }

    /// <summary>
    /// Builds an identity projection for this condition with explicit result-shaping options.<br/>
    /// This keeps adopted-builder callers on the completed condition descriptor when they need ordering, duplicate handling, paging, or bookmark continuation.<br/>
    /// The supplied resolver is evaluated during materialization so reusable condition descriptors can be bound to different opened catalog handles without rebuilding the builder chain.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity projection descriptor over the materialized adopted condition.</returns>
    public IIdentityCriterionProjection IDsWith(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return Materialize(resolveIndex).IDsWith(ordering, deduplication, skip, take, bookmark);
    }

    /// <summary>
    /// Builds an execution-plan descriptor for this adopted condition without materializing matching identities.<br/>
    /// Planning through the adopted condition descriptor makes condition permutations the public contract while keeping the internal identity-criterion tree as an execution detail.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>An identity execution plan descriptor.</returns>
    public LibraDexIdentityExecutionPlan Plan(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Plan();
    }

    /// <summary>
    /// Executes this adopted condition and materializes matching identity objects plus plan diagnostics.<br/>
    /// `Get` is the adopted-builder materialization bridge for callers that need diagnostics and results together without handling the internal criterion tree.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>The materialized identity execution result.</returns>
    public LibraDexIdentityExecutionResult Get(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Execute();
    }

    /// <summary>
    /// Streams matching identity objects for this adopted condition in the selected result shape.<br/>
    /// The current implementation delegates to the internal identity projection iterator; later physical cursors can replace that bridge without changing adopted-builder call sites.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A forward-only sequence of matching identity objects.</returns>
    public IEnumerable<object> Iterate(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).Iterate();
    }

    /// <summary>
    /// Materializes matching identities for this adopted condition as a typed list.<br/>
    /// Runtime identities are validated against <typeparamref name="TIdentity"/> by the underlying projection helper so adapters can fail fast when they bind a condition to the wrong identity type.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected identity type.</typeparam>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of matching identities.</returns>
    public IReadOnlyList<TIdentity> ToList<TIdentity>(
        Func<string, IIndex> resolveIndex,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        return IDsWith(resolveIndex, ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();
    }

    /// <summary>
    /// Determines whether this adopted condition has at least one matching identity.<br/>
    /// This terminal shortcut keeps existence checks condition-scoped and lets the primitive executor stop at the first qualifying identity where the current bridge supports it.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="deduplication">The duplicate identity policy to apply before existence is reported.</param>
    /// <returns><see langword="true"/> when the condition returns at least one identity.</returns>
    public bool Exists(
        Func<string, IIndex> resolveIndex,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return LibraDexIdentityExecutionPlanner.Exists(Materialize(resolveIndex), deduplication);
    }

    /// <summary>
    /// Counts matching identities for this adopted condition.<br/>
    /// Count remains condition-terminal so future shelf/run counters can be introduced beneath the same adopted-builder call shape instead of reviving root index aggregate facades.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="deduplication">The duplicate identity policy to apply before counting.</param>
    /// <returns>The number of matching identities after the selected duplicate policy is applied.</returns>
    public long Count(
        Func<string, IIndex> resolveIndex,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct)
    {
        return LibraDexIdentityExecutionPlanner.Count(Materialize(resolveIndex), deduplication);
    }

    /// <summary>
    /// Deletes tuples matched by this adopted condition through the criteria-scoped mutation bridge.<br/>
    /// The first connected physical path supports conditions that materialize to one primitive leaf whose index implements deletion, such as composite `CompositeMatch` leaves.<br/>
    /// Composed multi-index deletes fail explicitly until the caller-facing target-scope policy is selected, so this method does not silently delete from only part of an identity group.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    public LibraDexIdentityMutationResult Delete(Func<string, IIndex> resolveIndex)
    {
        return Materialize(resolveIndex).Mutate.Delete().Execute();
    }

    /// <summary>
    /// Deletes tuples matched by this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload matches the dictionary materialization helper so generated callers can keep retrieval and mutation binding code in the same shape.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A mutation result describing matched and deleted tuple counts.</returns>
    public LibraDexIdentityMutationResult Delete(IReadOnlyDictionary<string, IIndex> indexes)
    {
        return Materialize(indexes).Mutate.Delete().Execute();
    }

    /// <summary>
    /// Deletes tuples from one explicit target index whose identities are matched by this adopted condition.<br/>
    /// The condition may be composed across indexes in the same identity group; only the named target index is physically mutated, which keeps composed mutation low-ceremony without guessing a target.<br/>
    /// The target index is scanned for matching identities in this first bridge, while exact tuple deletion still preserves non-unique keys and sibling identities.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically deleted.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <returns>A mutation result describing target tuples matched and deleted.</returns>
    public LibraDexIdentityMutationResult DeleteFrom(string targetIndexName, Func<string, IIndex> resolveIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(Materialize(resolveIndex), targetIndex);
    }

    /// <summary>
    /// Deletes tuples from one explicit target index using a dictionary of opened indexes keyed by index name.<br/>
    /// This is the map-bound counterpart to <see cref="DeleteFrom(string, Func{string, IIndex})"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically deleted.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A mutation result describing target tuples matched and deleted.</returns>
    public LibraDexIdentityMutationResult DeleteFrom(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(indexes);
        if (!indexes.TryGetValue(targetIndexName, out IIndex? targetIndex))
        {
            throw new KeyNotFoundException($"Target index '{targetIndexName}' was not supplied for condition group '{Group}'.");
        }

        return LibraDexIdentityExecutionPlanner.ExecuteTargetDelete(Materialize(indexes), targetIndex);
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition through the criteria-scoped mutation bridge.<br/>
    /// This is the condition-terminal convenience form of `Materialize(resolveIndex).Mutate.SetKey(newKey).Execute()` and keeps common update code on the adopted builder surface.<br/>
    /// Connected implementations currently require the condition to materialize to one primitive leaf whose index can capture matching key/identity tuples and delete exact old tuples.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="newKey">The replacement key to assign to every matched tuple.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(Func<string, IIndex> resolveIndex, object newKey)
    {
        return Materialize(resolveIndex).Mutate.SetKey(newKey).Execute();
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a dictionary of opened indexes keyed by index name.<br/>
    /// This overload is the map-bound counterpart to <see cref="SetKey(Func{string, IIndex}, object)"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKey">The replacement key to assign to every matched tuple.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(IReadOnlyDictionary<string, IIndex> indexes, object newKey)
    {
        return Materialize(indexes).Mutate.SetKey(newKey).Execute();
    }

    /// <summary>
    /// Replaces keys on one explicit target index for tuples whose identities are matched by this adopted condition.<br/>
    /// The condition may be composed across indexes in the same identity group; only the named target index is physically re-keyed, which keeps intent explicit without introducing transaction ceremony.<br/>
    /// Replacement tuples are verified or inserted before old target tuples are removed.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <param name="newKey">The replacement key to assign on the target index.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, Func<string, IIndex> resolveIndex, object newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(resolveIndex), targetIndex, newKey, newKeyFactory: null);
    }

    /// <summary>
    /// Replaces keys on one explicit target index using a dictionary of opened indexes keyed by index name.<br/>
    /// This is the map-bound counterpart to <see cref="SetKeyOn(string, Func{string, IIndex}, object)"/> for generated callers that already hold opened group indexes in a lookup table.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKey">The replacement key to assign on the target index.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes, object newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(indexes);
        if (!indexes.TryGetValue(targetIndexName, out IIndex? targetIndex))
        {
            throw new KeyNotFoundException($"Target index '{targetIndexName}' was not supplied for condition group '{Group}'.");
        }

        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(indexes), targetIndex, newKey, newKeyFactory: null);
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a factory evaluated per matched identity.<br/>
    /// The factory receives the identity object for each original tuple and returns the replacement key, allowing callers to express deterministic key migration without opening a separate cursor facade.<br/>
    /// Connected implementations currently require the condition to materialize to one primitive leaf whose index can capture matching key/identity tuples and delete exact old tuples.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(Func<string, IIndex> resolveIndex, Func<object, object?> newKeyFactory)
    {
        return Materialize(resolveIndex).Mutate.SetKey(newKeyFactory).Execute();
    }

    /// <summary>
    /// Replaces the keys of tuples matched by this adopted condition using a map-bound index resolver and per-identity key factory.<br/>
    /// The factory receives the identity object for each original tuple and returns the replacement key, while the dictionary supplies the opened indexes referenced by condition leaves.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its replacement key.</param>
    /// <returns>A mutation result describing matched and re-keyed tuple counts.</returns>
    public LibraDexIdentityMutationResult SetKey(IReadOnlyDictionary<string, IIndex> indexes, Func<object, object?> newKeyFactory)
    {
        return Materialize(indexes).Mutate.SetKey(newKeyFactory).Execute();
    }

    /// <summary>
    /// Replaces keys on one explicit target index using a replacement-key factory evaluated per matched target tuple identity.<br/>
    /// The condition supplies the identity set, the target index supplies the physical tuple stream, and the factory maps each target identity to its new key.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="resolveIndex">Function that resolves condition and target index names to opened LibraDex indexes.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its target-index replacement key.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, Func<string, IIndex> resolveIndex, Func<object, object?> newKeyFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(resolveIndex);
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        IIndex targetIndex = resolveIndex(targetIndexName);
        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(resolveIndex), targetIndex, newKey: null, newKeyFactory);
    }

    /// <summary>
    /// Replaces keys on one explicit target index using a map-bound index resolver and per-identity replacement-key factory.<br/>
    /// This overload keeps generated composed mutation code on dictionary lookups while preserving explicit physical target selection.<br/>
    /// </summary>
    /// <param name="targetIndexName">The index name whose tuples should be physically re-keyed.</param>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <param name="newKeyFactory">Factory that receives a matched identity and returns its target-index replacement key.</param>
    /// <returns>A mutation result describing target tuples matched and re-keyed.</returns>
    public LibraDexIdentityMutationResult SetKeyOn(string targetIndexName, IReadOnlyDictionary<string, IIndex> indexes, Func<object, object?> newKeyFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIndexName);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(newKeyFactory);
        if (!indexes.TryGetValue(targetIndexName, out IIndex? targetIndex))
        {
            throw new KeyNotFoundException($"Target index '{targetIndexName}' was not supplied for condition group '{Group}'.");
        }

        return LibraDexIdentityExecutionPlanner.ExecuteTargetSetKey(Materialize(indexes), targetIndex, newKey: null, newKeyFactory);
    }

    /// <summary>
    /// Creates condition-scoped grouping helpers for this adopted condition.<br/>
    /// The resolver binds index names used by the condition descriptor, while the later `By(...)` call selects the grouping index whose keys define group boundaries.<br/>
    /// Grouping remains condition-terminal so root index handles do not regain a parallel grouped retrieval facade.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index.</param>
    /// <returns>A grouping helper bound to this adopted condition and resolver.</returns>
    public LibraDexConditionGroups Groups(Func<string, IIndex> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        return new LibraDexConditionGroups(this, resolveIndex);
    }

    /// <summary>
    /// Classifies every leaf in this condition against supplied opened indexes without executing the condition.<br/>
    /// Use this before bridge widening so each permutation states whether it is index-backed, projection-backed, composite-backed, visible scan-like, unsupported, or unresolved.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>One classification row per condition leaf in builder order.</returns>
    public IReadOnlyList<LibraDexConditionLeafClassification> Classify(IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return Classify(indexName => indexes.TryGetValue(indexName, out IIndex? index) ? index : null);
    }

    /// <summary>
    /// Classifies every leaf in this condition through a resolver without executing the condition.<br/>
    /// A null resolver result is recorded as a resolution failure instead of throwing so permutation tests can prove missing-index behavior explicitly.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index, or null when the name is unavailable.</param>
    /// <returns>One classification row per condition leaf in builder order.</returns>
    public IReadOnlyList<LibraDexConditionLeafClassification> Classify(Func<string, IIndex?> resolveIndex)
    {
        ArgumentNullException.ThrowIfNull(resolveIndex);
        IReadOnlyList<LibraDexConditionLeafDescriptor> leaves = Leaves;
        LibraDexConditionLeafClassification[] classifications = new LibraDexConditionLeafClassification[leaves.Count];
        for (int i = 0; i < leaves.Count; i++)
        {
            LibraDexConditionLeafDescriptor leaf = leaves[i];
            IIndex? index = resolveIndex(leaf.IndexName);
            classifications[i] = ClassifyResolvedLeaf(Group, leaf, index);
        }

        return classifications;
    }

    /// <summary>
    /// Builds the adopted-condition bridge plan for supplied opened indexes.<br/>
    /// The plan shows which leaves can use the current primitive bridge and which leaves need projection, composite, scan-policy, or caller-resolution work first.<br/>
    /// </summary>
    /// <param name="indexes">The opened indexes keyed by LibraDex index name.</param>
    /// <returns>A bridge readiness plan for this condition.</returns>
    public LibraDexConditionBridgePlan PlanBridge(IReadOnlyDictionary<string, IIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return PlanBridge(indexName => indexes.TryGetValue(indexName, out IIndex? index) ? index : null);
    }

    /// <summary>
    /// Builds the adopted-condition bridge plan through a resolver.<br/>
    /// A null resolver result is planned as a caller-resolution action rather than thrown, which keeps permutation checks inspectable.<br/>
    /// </summary>
    /// <param name="resolveIndex">Function that resolves a condition index name to an opened LibraDex index, or null when the name is unavailable.</param>
    /// <returns>A bridge readiness plan for this condition.</returns>
    public LibraDexConditionBridgePlan PlanBridge(Func<string, IIndex?> resolveIndex)
    {
        IReadOnlyList<LibraDexConditionLeafClassification> classifications = Classify(resolveIndex);
        LibraDexConditionBridgePlanRow[] rows = new LibraDexConditionBridgePlanRow[classifications.Count];
        for (int i = 0; i < classifications.Count; i++)
        {
            rows[i] = CreateBridgePlanRow(classifications[i]);
        }

        return new LibraDexConditionBridgePlan(Group, rows);
    }

    internal LibraDexConditionNode GetRoot()
    {
        return root;
    }

    /// <summary>
    /// Creates a new completed condition by rewriting every leaf descriptor in the tree.<br/>
    /// This keeps reusable partial conditions immutable while supporting Abraxas-style late replacement of named values and deferred index selectors.<br/>
    /// </summary>
    /// <param name="rewriteLeaf">Function that returns the replacement descriptor for each leaf.</param>
    /// <returns>A completed condition with the rewritten descriptor tree.</returns>
    internal LibraDexConditionEndCondition Rewrite(Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafDescriptor> rewriteLeaf)
    {
        ArgumentNullException.ThrowIfNull(rewriteLeaf);
        return new LibraDexConditionEndCondition(Group, root.Rewrite(rewriteLeaf));
    }

    private static LibraDexConditionBridgePlanRow CreateBridgePlanRow(LibraDexConditionLeafClassification classification)
    {
        return classification.ExecutionClass switch
        {
            LibraDexConditionExecutionClass.IndexBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ExecuteCurrentPrimitive,
                CanExecuteWithCurrentBridge: true,
                "The leaf can materialize through the current primitive criteria bridge."),
            LibraDexConditionExecutionClass.ProjectionBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ConnectProjectionPrimitive,
                CanExecuteWithCurrentBridge: false,
                "The leaf requires a maintained projection primitive before it should execute."),
            LibraDexConditionExecutionClass.CompositeBacked => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ConnectCompositePrimitive,
                CanExecuteWithCurrentBridge: false,
                "The leaf requires a composite-key primitive before it should execute."),
            LibraDexConditionExecutionClass.VisibleScanLike => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RequireVisibleScanPolicy,
                CanExecuteWithCurrentBridge: false,
                "The leaf is valid intent but must opt into or report scan-like behavior before execution."),
            LibraDexConditionExecutionClass.Unsupported => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RejectUnsupported,
                CanExecuteWithCurrentBridge: false,
                "The leaf has no supported codec, projection, or primitive rule yet."),
            LibraDexConditionExecutionClass.ResolutionFailure => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.ResolveIndex,
                CanExecuteWithCurrentBridge: false,
                "The caller must supply an opened index for this index name."),
            LibraDexConditionExecutionClass.IdentityGroupMismatch => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.FixIdentityGroup,
                CanExecuteWithCurrentBridge: false,
                "The resolved index must belong to the condition identity group."),
            _ => new LibraDexConditionBridgePlanRow(
                classification,
                LibraDexConditionBridgeAction.RejectUnsupported,
                CanExecuteWithCurrentBridge: false,
                "The classification is not recognized.")
        };
    }

    internal static LibraDexConditionLeafClassification ClassifyResolvedLeaf(string group, LibraDexConditionLeafDescriptor leaf, IIndex? index)
    {
        if (index is null)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ResolutionFailure,
                "No opened index was supplied for this condition index name.",
                ProjectionKind: null);
        }

        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IdentityGroupMismatch,
                "The resolved index belongs to a different identity group.",
                ProjectionKind: null);
        }

        if (index.LogicalShape?.KeyFamily == CatalogIndexKeyFamily.Composite &&
            leaf.Operator == LibraDexConditionOperatorKind.CompositeMatch)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The resolved index is a routed composite-key shape and the leaf carries named tier predicates.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.LogicalShape?.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.CompositeBacked,
                "The resolved index is a composite-key shape.",
                LibraDexIndexProjectionKind.Exact);
        }

        return leaf.ValueKind switch
        {
            LibraDexConditionValueKind.String => ClassifyStringLeaf(leaf, index),
            LibraDexConditionValueKind.Guid => ClassifyGuidLeaf(leaf, index),
            LibraDexConditionValueKind.Binary => ClassifyBinaryLeaf(leaf, index),
            LibraDexConditionValueKind.DateTime or LibraDexConditionValueKind.DateOnly or LibraDexConditionValueKind.TimeOnly => ClassifyDateLeaf(leaf, index),
            _ => ClassifyPrimitiveLeaf(leaf)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyPrimitiveLeaf(LibraDexConditionLeafDescriptor leaf)
    {
        return leaf.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to an ordered-key or exact-key primitive over the selected index.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.BitAndEqualTo or
            LibraDexConditionOperatorKind.BitAndNotEqualTo => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.VisibleScanLike,
                "The bitmask operator is valid scalar intent but is not generally contiguous in ordered key space, so the current bridge scans compact index keys and applies a masked residual test.",
                LibraDexIndexProjectionKind.Exact),
            _ => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.Unsupported,
                "The selected value kind has no primitive or projection rule for this operator.",
                ProjectionKind: null)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyProjectionAwareScalarLeaf(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to the exact ordered-key primitive for this value kind.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.LogicalShape?.HasProjection(projectionKind) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                "The operator requires a maintained value-specific projection and the resolved shape declares one.",
                projectionKind);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The operator requires a maintained value-specific projection that the resolved shape does not declare.",
            projectionKind);
    }

    /// <summary>
    /// Classifies a date-like condition leaf against the resolved LibraDex index shape.<br/>
    /// Exact and chronological boundary operators remain ordinary index-backed operations, year-qualified operators use ordered ranges, and component-only operators use structured scalar shift-and-mask predicates when the key type preserves the requested component.<br/>
    /// </summary>
    /// <param name="leaf">The captured condition leaf.</param>
    /// <param name="index">The resolved logical index for the leaf.</param>
    /// <returns>The execution classification for the date-like condition leaf.</returns>
    private static LibraDexConditionLeafClassification ClassifyDateLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The operator maps to the exact structured date ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.YearEqualTo or
            LibraDexConditionOperatorKind.YearNotEqualTo or
            LibraDexConditionOperatorKind.YearIn or
            LibraDexConditionOperatorKind.YearNotIn or
            LibraDexConditionOperatorKind.YearRange or
            LibraDexConditionOperatorKind.YearNotRange or
            LibraDexConditionOperatorKind.YearOnOrAfter or
            LibraDexConditionOperatorKind.YearOnOrBefore or
            LibraDexConditionOperatorKind.YearMonth or
            LibraDexConditionOperatorKind.YearMonthDay or
            LibraDexConditionOperatorKind.YearMonthIn or
            LibraDexConditionOperatorKind.YearMonthDayIn or
            LibraDexConditionOperatorKind.YearInMonths or
            LibraDexConditionOperatorKind.YearQuarter or
            LibraDexConditionOperatorKind.IsToday or
            LibraDexConditionOperatorKind.IsYesterday or
            LibraDexConditionOperatorKind.IsInLastDays) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            index.KeyType != typeof(TimeOnly))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date codec stores date components in high key bits, so this operator maps to one ordered range or a same-index union of ordered ranges.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.IsInLastHours or
            LibraDexConditionOperatorKind.IsInLastMinutes) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            (index.KeyType == typeof(DateTime) || index.KeyType == typeof(DateTimeOffset)))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date-time codec stores chronological values in ordered form, so this relative window maps to one ordered range at materialization time.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if ((leaf.Operator is LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            index.KeyType == typeof(TimeOnly))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured time codec stores TimeOnly hour in ordered form, so this semantic time-of-day branch maps to one ordered range or a two-range wraparound union.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        if (IsStructuredComponentOperator(leaf.Operator) &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.StructuredDate) == true &&
            SupportsStructuredComponentOperator(index.KeyType, leaf.Operator))
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The structured date/time codec stores this component in fixed key bits, so the operator maps to a condition-derived shift-and-mask primitive over encoded scalar keys.",
                LibraDexIndexProjectionKind.StructuredDate);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The structured date operator needs a multi-range, bit-slice, mask, or companion-projection primitive before it should execute.",
            LibraDexIndexProjectionKind.StructuredDate);
    }

    /// <summary>
    /// Determines whether a date/time operator can be represented as a structured scalar component predicate.<br/>
    /// These operators are not necessarily contiguous in ordered key space, but their requested fields are directly addressable through the Abraxas-compatible packed key layout.<br/>
    /// </summary>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the operator is a component predicate candidate.</returns>
    private static bool IsStructuredComponentOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind is
            LibraDexConditionOperatorKind.MonthEqualTo or
            LibraDexConditionOperatorKind.MonthIn or
            LibraDexConditionOperatorKind.MonthNotIn or
            LibraDexConditionOperatorKind.MonthRange or
            LibraDexConditionOperatorKind.MonthNotRange or
            LibraDexConditionOperatorKind.DayEqualTo or
            LibraDexConditionOperatorKind.DayIn or
            LibraDexConditionOperatorKind.DayRange or
            LibraDexConditionOperatorKind.DayNotRange or
            LibraDexConditionOperatorKind.MonthDay or
            LibraDexConditionOperatorKind.QuarterEqualTo or
            LibraDexConditionOperatorKind.InQuarter or
            LibraDexConditionOperatorKind.InQuarterRange or
            LibraDexConditionOperatorKind.IsQuarterStart or
            LibraDexConditionOperatorKind.IsQuarterEnd or
            LibraDexConditionOperatorKind.IsHalfYearStart or
            LibraDexConditionOperatorKind.IsHalfYearEnd or
            LibraDexConditionOperatorKind.IsFirstOfMonth or
            LibraDexConditionOperatorKind.IsLastOfMonth or
            LibraDexConditionOperatorKind.IsWeekend or
            LibraDexConditionOperatorKind.IsWeekday or
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
    }

    /// <summary>
    /// Determines whether a structured scalar key type preserves the component required by an operator.<br/>
    /// DateOnly supports calendar components, DateTime and DateTimeOffset support calendar plus hour components, and TimeOnly uses the separate ordered time range bridge for semantic time-of-day operators.<br/>
    /// </summary>
    /// <param name="keyType">The resolved index key type.</param>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the key type can execute the operator without projection lookup.</returns>
    private static bool SupportsStructuredComponentOperator(Type keyType, LibraDexConditionOperatorKind operatorKind)
    {
        if (keyType == typeof(TimeOnly))
        {
            return operatorKind is
                LibraDexConditionOperatorKind.IsMorning or
                LibraDexConditionOperatorKind.IsAfternoon or
                LibraDexConditionOperatorKind.IsEvening or
                LibraDexConditionOperatorKind.IsNight;
        }

        bool isDateLike = keyType == typeof(DateTime) ||
            keyType == typeof(DateTimeOffset) ||
            keyType == typeof(DateOnly);
        if (!isDateLike)
        {
            return false;
        }

        bool requiresHour = operatorKind is
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
        return !requiresHour || keyType != typeof(DateOnly);
    }

    private static LibraDexConditionLeafClassification ClassifyGuidLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact GUID comparison can use the selected index's ordinary exact-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (leaf.Operator is LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The GUID operator maps to a condition-derived canonical nibble predicate over encoded GUID bytes, avoiding per-row text conversion.",
                LibraDexIndexProjectionKind.GuidSegments);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The GUID operator is not connected to an exact-key, membership, or encoded GUID pattern primitive.",
            LibraDexIndexProjectionKind.GuidSegments);
    }

    private static LibraDexConditionLeafClassification ClassifyBinaryLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        if (leaf.Operator is LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween or
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact binary comparison can use the selected index's ordinary fixed-byte ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.KeyType == typeof(byte[]) &&
            leaf.Operator == LibraDexConditionOperatorKind.EndsWith &&
            index.LogicalShape?.HasProjection(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                "Binary suffix lookup can use the selected index's maintained reversed exact-byte projection.",
                LibraDexIndexProjectionKind.Exact);
        }

        if (index.KeyType == typeof(byte[]) &&
            leaf.Operator is LibraDexConditionOperatorKind.StartsWith or
                LibraDexConditionOperatorKind.EndsWith or
                LibraDexConditionOperatorKind.Contains or
                LibraDexConditionOperatorKind.MatchesPattern or
                LibraDexConditionOperatorKind.BinarySliceEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan or
                LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceLessThan or
                LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual or
                LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
                LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith or
                LibraDexConditionOperatorKind.BinaryTypedSliceContains or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "The binary operator maps to a condition-derived byte predicate over encoded fixed-width key bytes, avoiding decoded byte-array allocation per candidate row.",
                LibraDexIndexProjectionKind.Exact);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.Unsupported,
            "The binary operator is not connected to an exact-key, membership, or raw byte-slice primitive.",
            LibraDexIndexProjectionKind.Exact);
    }

    private static LibraDexConditionLeafClassification ClassifyStringLeaf(LibraDexConditionLeafDescriptor leaf, IIndex index)
    {
        return leaf.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact string comparison can use the selected index's ordinary ordered-key primitive.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.SortKey, "Case-insensitive string comparison requires a maintained sort-key projection."),
            LibraDexConditionOperatorKind.StartsWith when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "Exact starts-with can be represented as an ordered-key boundary over the selected string index.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.StartsWith => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Forward, "Case-insensitive starts-with requires a maintained folded-text projection."),
            LibraDexConditionOperatorKind.EndsWith when leaf.IgnoreCase => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Reversed, "Case-insensitive ends-with requires a maintained reversed folded-text projection."),
            LibraDexConditionOperatorKind.EndsWith => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, "Case-sensitive ends-with prefers a maintained reversed exact-text projection."),
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.VisibleScanLike,
                "No contains or pattern-capable maintained projection is declared in the current shape model.",
                ProjectionKind: null),
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet when !leaf.IgnoreCase => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.IndexBacked,
                "String membership can use repeated exact-key lookups or a prepared exact-key set.",
                LibraDexIndexProjectionKind.Exact),
            LibraDexConditionOperatorKind.InSet or
            LibraDexConditionOperatorKind.NotInSet => ClassifyTextProjection(leaf, index, LibraDexIndexProjectionKind.SortKey, "Case-insensitive string membership prefers a maintained sort-key projection."),
            _ => new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.Unsupported,
                "The string operator has no current primitive or projection rule.",
                ProjectionKind: null)
        };
    }

    private static LibraDexConditionLeafClassification ClassifyTextProjection(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind,
        string reason)
    {
        return ClassifyTextProjection(leaf, index, projectionKind, LibraDexIndexByteDirection.Forward, reason);
    }

    private static LibraDexConditionLeafClassification ClassifyTextProjection(
        LibraDexConditionLeafDescriptor leaf,
        IIndex index,
        LibraDexIndexProjectionKind projectionKind,
        LibraDexIndexByteDirection direction,
        string reason)
    {
        if (index.LogicalShape?.HasProjection(projectionKind, direction) == true)
        {
            return new LibraDexConditionLeafClassification(
                leaf.IndexName,
                leaf.ValueKind,
                leaf.Operator,
                LibraDexConditionExecutionClass.ProjectionBacked,
                reason,
                projectionKind);
        }

        return new LibraDexConditionLeafClassification(
            leaf.IndexName,
            leaf.ValueKind,
            leaf.Operator,
            LibraDexConditionExecutionClass.VisibleScanLike,
            $"{reason} The resolved shape does not declare that projection.",
            projectionKind);
    }
}

/// <summary>
/// Provides grouping helpers for a completed adopted condition.<br/>
/// The condition supplies candidate identities, and the grouping index supplies the key axis, keeping grouping attached to selection rather than root index retrieval.<br/>
/// </summary>
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
    {
        return WhereCountAtLeast(2);
    }

    /// <summary>
    /// Returns a grouped query containing only singleton groups.<br/>
    /// </summary>
    /// <returns>A new grouped query filtered to groups with exactly one matching identity.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> Singletons()
    {
        return WhereCount(1);
    }

    /// <summary>
    /// Returns a grouped query with explicit group ordering.<br/>
    /// </summary>
    /// <param name="order">The group ordering to apply.</param>
    /// <returns>A new grouped query with the requested group ordering.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> OrderBy(LibraDexGroupOrder order)
    {
        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, order, itemDirection, takeGroups);
    }

    /// <summary>
    /// Returns a grouped query with explicit item ordering inside each group.<br/>
    /// </summary>
    /// <param name="direction">The identity ordering to apply inside each returned group.</param>
    /// <returns>A new grouped query with the requested item ordering.</returns>
    public LibraDexConditionGroupQuery<TKey, TIdentity> OrderItemsBy(QueryDirection direction)
    {
        return new LibraDexConditionGroupQuery<TKey, TIdentity>(condition, resolveIndex, index, minimumCount, maximumCount, groupOrder, direction, takeGroups);
    }

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
    {
        return Duplicates().OrderBy(LibraDexGroupOrder.CountDescending).Take(count);
    }

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
        HashSet<TIdentity> identities = new(condition.ToList<TIdentity>(resolveIndex, ordering, deduplication));
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
public sealed class LibraDexConditionClause
{
    private readonly LibraDexConditionBuilder builder;

    internal LibraDexConditionClause(LibraDexConditionBuilder builder)
    {
        this.builder = builder;
    }

    /// <summary>
    /// Selects the LibraDex index that owns the next condition leaf.<br/>
    /// The name is not resolved immediately; reusable conditions can be built first and resolved against opened indexes later.<br/>
    /// </summary>
    /// <param name="indexName">The index name inside the current identity group.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionValueTypeSelector(builder, LibraDexConditionIndexSelector.Static(indexName));
    }

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a deferred index-name factory.<br/>
    /// The factory is evaluated only when the condition is inspected or materialized, matching Abraxas' deferred proppath behavior while keeping LibraDex resolution index-name based.<br/>
    /// </summary>
    /// <param name="indexNameFactory">Factory that returns the index name inside the current identity group.</param>
    /// <param name="name">Optional selector name for replacement on reusable condition expressions.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(Func<string> indexNameFactory, string? name = null)
    {
        return new LibraDexConditionValueTypeSelector(builder, LibraDexConditionIndexSelector.Deferred(indexNameFactory, name));
    }

    /// <summary>
    /// Selects the LibraDex index for the next condition leaf using a prepared selector.<br/>
    /// This overload is the low-level bridge used by typed public wrappers that need named or deferred selector replacement.<br/>
    /// </summary>
    /// <param name="indexSelector">The static or deferred index selector.</param>
    /// <returns>A value-type selector for the chosen index.</returns>
    public LibraDexConditionValueTypeSelector Index(LibraDexConditionIndexSelector indexSelector)
    {
        return new LibraDexConditionValueTypeSelector(builder, indexSelector);
    }

    /// <summary>
    /// Adds an already completed grouped condition as the next node.<br/>
    /// The grouped condition must belong to the same identity group as this builder.<br/>
    /// </summary>
    /// <param name="groupCondition">The completed grouped condition.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Group(LibraDexConditionEndCondition groupCondition)
    {
        ArgumentNullException.ThrowIfNull(groupCondition);
        return builder.AddGroup(groupCondition);
    }
}

/// <summary>
/// Selects the value family for an adopted condition leaf.<br/>
/// The selected value family records adapter intent and chooses typed operator overloads, while final validation still happens against the resolved LibraDex index key type.<br/>
/// </summary>
public sealed class LibraDexConditionValueTypeSelector
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexConditionValueTypeSelector(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
    {
        ArgumentNullException.ThrowIfNull(indexSelector);
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Selects string operators for the current index.<br/>
    /// </summary>
    public LibraDexStringConditionOperator AsString => new(builder, indexSelector);

    /// <summary>
    /// Selects binary operators for the current index.<br/>
    /// </summary>
    public LibraDexBinaryConditionOperator AsBinary => new(builder, indexSelector);

    /// <summary>
    /// Selects Boolean operators for the current index.<br/>
    /// </summary>
    public LibraDexConditionOperator<bool> AsBoolean => new(builder, indexSelector, LibraDexConditionValueKind.Boolean);

    /// <summary>
    /// Selects GUID operators for the current index.<br/>
    /// </summary>
    public LibraDexGuidConditionOperator AsGuid => new(builder, indexSelector);

    /// <summary>
    /// Selects DateTime operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateTime> AsDate => new(builder, indexSelector, LibraDexConditionValueKind.DateTime);

    /// <summary>
    /// Selects DateTimeOffset operators for the current index.<br/>
    /// Full-value comparisons use the exact encoded date/time key, while structured date branches normalize through the same UTC packed format used by DateTimeOffset indexes.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateTimeOffset> AsDateTimeOffset => new(builder, indexSelector, LibraDexConditionValueKind.DateTime);

    /// <summary>
    /// Selects DateOnly operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<DateOnly> AsDateOnly => new(builder, indexSelector, LibraDexConditionValueKind.DateOnly);

    /// <summary>
    /// Selects TimeOnly operators for the current index.<br/>
    /// </summary>
    public LibraDexDateConditionOperator<TimeOnly> AsTimeOnly => new(builder, indexSelector, LibraDexConditionValueKind.TimeOnly);

    /// <summary>
    /// Selects TimeSpan operators for the current index.<br/>
    /// </summary>
    public LibraDexConditionOperator<TimeSpan> AsTimeSpan => new(builder, indexSelector, LibraDexConditionValueKind.TimeSpan);

    /// <summary>
    /// Selects numeric operators for an Int32 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<int> AsInt32 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a Byte key or projection.<br/>
    /// Byte conditions collapse to the same ordered scalar primitive route as wider numeric keys while preserving the developer-facing operand type in the descriptor.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<byte> AsByte => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for an SByte key or projection.<br/>
    /// Signed byte conditions use the existing sortable signed-scalar encoding at execution time, so range and membership operators remain ordered without query-time conversion.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<sbyte> AsSByte => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for an Int16 key or projection.<br/>
    /// The selector keeps copied/generated condition code strongly typed while materialization still resolves against the opened LibraDex index key contract.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<short> AsInt16 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a UInt16 key or projection.<br/>
    /// UInt16 values route through the same exact, boundary, range, and membership bridge used by other scalar numeric keys.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<ushort> AsUInt16 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for an Int64 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<long> AsInt64 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a UInt32 key or projection.<br/>
    /// This fills the common unsigned-width selector gap without adding a new primitive: materialization remains an ordered scalar condition leaf.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<uint> AsUInt32 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a UInt64 key or projection.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<ulong> AsUInt64 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for an Int128 key or projection.<br/>
    /// Int128 conditions use the same ordered primitive bridge as other scalar keys; the resolved index owns the signed fixed-16 sortable encoding.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<Int128> AsInt128 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a UInt128 key or projection.<br/>
    /// UInt128 conditions use the same ordered primitive bridge as other scalar keys; the resolved index owns the unsigned fixed-16 big-endian encoding.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<UInt128> AsUInt128 => new(builder, indexSelector);

    /// <summary>
    /// Selects numeric operators for a BigInteger key or projection.<br/>
    /// BigInteger conditions use the same ordered primitive condition shape as fixed-width scalar keys, while the resolved index owns the sortable BigInt byte encoding and max-width validation.<br/>
    /// </summary>
    public LibraDexNumericConditionOperator<BigInteger> AsBigInteger => new(builder, indexSelector);

    /// <summary>
    /// Selects scalar operators for a Char key or projection.<br/>
    /// Char keys are treated as ordered scalar code-unit values, matching the current generic scalar codec rather than text collation semantics.<br/>
    /// </summary>
    public LibraDexConditionOperator<char> AsChar => new(builder, indexSelector, LibraDexConditionValueKind.Numeric);

    /// <summary>
    /// Selects routed composite-key operators for the current index.<br/>
    /// Composite conditions capture named tier predicates so execution can enter the first matching component route, then continue through lightweight child tier routes instead of querying a flattened concatenated key.<br/>
    /// </summary>
    public LibraDexCompositeConditionOperator AsComposite => new(builder, indexSelector);
}

/// <summary>
/// Captures composite-key predicates for one logical routed composite index.<br/>
/// The first executable slice records all part predicates as one condition leaf so the physical composite index can traverse tiers in descriptor order rather than intersecting independent scans after the fact.<br/>
/// </summary>
public sealed class LibraDexCompositeConditionOperator
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexCompositeConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Captures one routed composite predicate over one or more named key parts.<br/>
    /// Supplying multiple parts lets the composite executor route from the first constrained tier into the next tier without requiring callers to flatten criteria into a synthetic key.<br/>
    /// </summary>
    /// <param name="parts">The ordered or named part predicates to apply.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Where(params LibraDexCompositePartCriterion[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Composite conditions require at least one part predicate.", nameof(parts));
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Composite,
            LibraDexConditionOperatorKind.CompositeMatch,
            parts.Select(static part => LibraDexConditionOperand.Value(part)).ToArray(),
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Builds named composite-part predicates for routed composite-key conditions.<br/>
/// These helpers keep developer intent part-oriented: callers describe `lastName == Smith` and `firstName starts with J` instead of flattening those values into one string or byte key.<br/>
/// </summary>
public static class LibraDexCompositePart
{
    /// <summary>
    /// Starts a string predicate that may match any string-compatible component tier in the composite key.<br/>
    /// This is not a joined-key search; each string tier is evaluated independently while normal named part predicates still route and filter their own tiers.<br/>
    /// </summary>
    /// <returns>A string predicate builder that targets any string component tier.</returns>
    public static LibraDexCompositeStringPartCondition AnyString()
    {
        return new LibraDexCompositeStringPartCondition(LibraDexCompositePartCriterion.AnyStringPartName);
    }

    /// <summary>
    /// Starts a predicate over the rendered whole composite key using index-order parts and no delimiter.<br/>
    /// This is an explicit joined-key request: LibraDex renders the complete routed path at the terminal node and applies the requested string predicate as a residual operation.<br/>
    /// </summary>
    /// <returns>A joined-key predicate builder with no delimiter.</returns>
    public static LibraDexCompositeJoinedKeyCondition Joined()
    {
        return new LibraDexCompositeJoinedKeyCondition(delimiter: string.Empty);
    }

    /// <summary>
    /// Starts a predicate over the rendered whole composite key using index-order parts and a caller-supplied delimiter.<br/>
    /// The delimiter participates in the rendered text so developers can shape their contains or pattern criteria with explicit part boundaries.<br/>
    /// </summary>
    /// <param name="delimiter">The delimiter inserted between rendered composite parts.</param>
    /// <returns>A joined-key predicate builder using the supplied delimiter.</returns>
    public static LibraDexCompositeJoinedKeyCondition Joined(string delimiter)
    {
        ArgumentNullException.ThrowIfNull(delimiter);
        return new LibraDexCompositeJoinedKeyCondition(delimiter);
    }

    /// <summary>
    /// Starts a string predicate for one composite tier.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A string predicate builder for the named part.</returns>
    public static LibraDexCompositeStringPartCondition String(string name)
    {
        return new LibraDexCompositeStringPartCondition(name);
    }

    /// <summary>
    /// Starts a GUID predicate for one composite tier.<br/>
    /// GUID criteria stay in the stored GUID byte domain; callers that want text-oriented GUID behavior should declare and populate a string composite part instead.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A GUID predicate builder for the named part.</returns>
    public static LibraDexCompositeGuidPartCondition Guid(string name)
    {
        return new LibraDexCompositeGuidPartCondition(name);
    }

    /// <summary>
    /// Starts a structured date/time predicate for one composite tier.<br/>
    /// Date criteria use the same Abraxas-compatible structured date binary layout as top-level date indexes, not text formatting or query-time parsing.<br/>
    /// </summary>
    /// <param name="name">The composite part name.</param>
    /// <returns>A structured date predicate builder for the named part.</returns>
    public static LibraDexCompositeDatePartCondition Date(string name)
    {
        return new LibraDexCompositeDatePartCondition(name);
    }

    /// <summary>
    /// Starts a typed scalar predicate for one composite tier.<br/>
    /// </summary>
    /// <typeparam name="TValue">The scalar value type.</typeparam>
    /// <param name="name">The composite part name.</param>
    /// <returns>A scalar predicate builder for the named part.</returns>
    public static LibraDexCompositeScalarPartCondition<TValue> Scalar<TValue>(string name)
    {
        return new LibraDexCompositeScalarPartCondition<TValue>(name);
    }
}

/// <summary>
/// Describes one named predicate inside a routed composite-key condition.<br/>
/// The descriptor stores the part name, operator, value kind, and operands separately from physical execution so the composite index can bind the predicate to a specific tier descriptor at materialization time.<br/>
/// </summary>
public sealed class LibraDexCompositePartCriterion
{
    internal const string AnyStringPartName = "__libradex_any_string_part";
    internal const string JoinedKeyPartName = "__libradex_joined_key";

    internal LibraDexCompositePartCriterion(
        string partName,
        LibraDexConditionValueKind valueKind,
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<object?> values,
        bool ignoreCase,
        string? culture,
        string? joinedDelimiter = null,
        IReadOnlyList<string>? joinedPartNames = null,
        IReadOnlyList<string>? joinedExcludedPartNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partName);
        PartName = partName;
        ValueKind = valueKind;
        Operator = operatorKind;
        Values = values;
        IgnoreCase = ignoreCase;
        Culture = culture;
        JoinedDelimiter = joinedDelimiter;
        JoinedPartNames = joinedPartNames;
        JoinedExcludedPartNames = joinedExcludedPartNames;
    }

    /// <summary>
    /// Gets the composite key part name targeted by this predicate.<br/>
    /// </summary>
    public string PartName { get; }

    /// <summary>
    /// Gets the captured value kind for this part predicate.<br/>
    /// </summary>
    public LibraDexConditionValueKind ValueKind { get; }

    /// <summary>
    /// Gets the captured operator for this part predicate.<br/>
    /// </summary>
    public LibraDexConditionOperatorKind Operator { get; }

    /// <summary>
    /// Gets the materialized predicate operands.<br/>
    /// </summary>
    public IReadOnlyList<object?> Values { get; }

    /// <summary>
    /// Gets whether text comparison for this part should ignore case when no maintained folded tier is selected.<br/>
    /// </summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// Gets the optional culture name requested for text comparison.<br/>
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Gets the delimiter used by an explicit joined-key composite predicate.<br/>
    /// A null value means the criterion targets a normal named part or the any-string pseudo part.<br/>
    /// </summary>
    public string? JoinedDelimiter { get; }

    /// <summary>
    /// Gets the optional included part names used by an explicit joined-key composite predicate.<br/>
    /// A null or empty list means the joined key renders all composite parts in index order.<br/>
    /// </summary>
    public IReadOnlyList<string>? JoinedPartNames { get; }

    /// <summary>
    /// Gets the optional excluded part names used by an explicit joined-key composite predicate.<br/>
    /// Exclusions are applied after inclusion selection and still preserve composite index order.<br/>
    /// </summary>
    public IReadOnlyList<string>? JoinedExcludedPartNames { get; }
}

/// <summary>
/// Captures string predicates over a rendered whole composite key.<br/>
/// The rendered key uses the composite index's part order, all parts, and the delimiter selected by the developer; this is residual matching, not a hidden flattened physical key.<br/>
/// </summary>
public sealed class LibraDexCompositeJoinedKeyCondition
{
    private readonly string delimiter;
    private readonly IReadOnlyList<string>? partNames;
    private readonly IReadOnlyList<string>? excludedPartNames;

    internal LibraDexCompositeJoinedKeyCondition(string delimiter)
        : this(delimiter, partNames: null, excludedPartNames: null)
    {
    }

    private LibraDexCompositeJoinedKeyCondition(
        string delimiter,
        IReadOnlyList<string>? partNames,
        IReadOnlyList<string>? excludedPartNames)
    {
        this.delimiter = delimiter;
        this.partNames = partNames;
        this.excludedPartNames = excludedPartNames;
    }

    /// <summary>
    /// Narrows the rendered joined composite key to selected part names while preserving composite index order.<br/>
    /// The names describe inclusion only; rendering still follows the index descriptor order so generated callers do not have to sort their input names.<br/>
    /// </summary>
    /// <param name="partNames">The composite part names to include in the joined value.</param>
    /// <returns>A joined-key predicate builder that renders only the selected parts.</returns>
    public LibraDexCompositeJoinedKeyCondition Parts(params string[] partNames)
    {
        ArgumentNullException.ThrowIfNull(partNames);
        if (partNames.Length == 0)
        {
            throw new ArgumentException("Joined composite key part selection requires at least one part name.", nameof(partNames));
        }

        string[] copy = new string[partNames.Length];
        for (int i = 0; i < partNames.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partNames[i]);
            copy[i] = partNames[i];
        }

        return new LibraDexCompositeJoinedKeyCondition(delimiter, Array.AsReadOnly(copy), excludedPartNames);
    }

    /// <summary>
    /// Excludes selected part names from the rendered joined composite key while preserving composite index order for the remaining parts.<br/>
    /// This is useful when a composite index has a routing prefix such as tenant or partition that should not participate in user-facing joined text search.<br/>
    /// </summary>
    /// <param name="partNames">The composite part names to exclude from the joined value.</param>
    /// <returns>A joined-key predicate builder that omits the selected parts.</returns>
    public LibraDexCompositeJoinedKeyCondition Excluding(params string[] partNames)
    {
        ArgumentNullException.ThrowIfNull(partNames);
        if (partNames.Length == 0)
        {
            throw new ArgumentException("Joined composite key exclusion requires at least one part name.", nameof(partNames));
        }

        string[] copy = new string[partNames.Length];
        for (int i = 0; i < partNames.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partNames[i]);
            copy[i] = partNames[i];
        }

        return new LibraDexCompositeJoinedKeyCondition(delimiter, this.partNames, Array.AsReadOnly(copy));
    }

    /// <summary>
    /// Captures equality against the rendered joined composite key.<br/>
    /// </summary>
    /// <param name="value">The rendered key value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.EqualTo, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures equality against an encoded typed value inside the joined composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, making this suitable for GUID, scalar, date/time, and raw byte values without per-row text conversion.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(object value)
    {
        return CreateTyped(LibraDexConditionOperatorKind.EqualTo, value);
    }

    /// <summary>
    /// Captures a prefix predicate against the rendered joined composite key.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.StartsWith, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a prefix predicate against an encoded typed value inside the joined composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, so callers can express typed binary-prefix intent without formatting the value as text.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare as a prefix.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(object value)
    {
        return CreateTyped(LibraDexConditionOperatorKind.StartsWith, value);
    }

    /// <summary>
    /// Captures a suffix predicate against the rendered joined composite key.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.EndsWith, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a suffix predicate against an encoded typed value inside the joined composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, preserving typed search intent without text rendering.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and compare as a suffix.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(object value)
    {
        return CreateTyped(LibraDexConditionOperatorKind.EndsWith, value);
    }

    /// <summary>
    /// Captures a containment predicate against the rendered joined composite key.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion Contains(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.Contains, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a containment predicate against an encoded typed value inside the joined composite key.<br/>
    /// The operand is encoded once into the same byte-domain used by composite components, which is the preferred path for typed GUID, scalar, date/time, or raw byte containment checks.<br/>
    /// </summary>
    /// <param name="value">The typed operand to encode and search for.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion Contains(object value)
    {
        return CreateTyped(LibraDexConditionOperatorKind.Contains, value);
    }

    /// <summary>
    /// Captures a wildcard pattern predicate against the rendered joined composite key.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern where `*` spans zero or more characters and `?` matches one character.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A joined-key composite predicate.</returns>
    public LibraDexCompositePartCriterion MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.MatchesPattern, ignoreCase, culture, pattern);
    }

    private LibraDexCompositePartCriterion Create(
        LibraDexConditionOperatorKind operatorKind,
        bool ignoreCase,
        string? culture,
        params object?[] values)
    {
        return new LibraDexCompositePartCriterion(
            LibraDexCompositePartCriterion.JoinedKeyPartName,
            LibraDexConditionValueKind.String,
            operatorKind,
            Array.AsReadOnly(values),
            ignoreCase,
            culture,
            delimiter,
            partNames,
            excludedPartNames);
    }

    /// <summary>
    /// Creates a joined-key predicate over a non-text typed operand.<br/>
    /// The runtime executor validates and encodes the operand into the composite byte domain once before comparing it against encoded joined paths.<br/>
    /// </summary>
    /// <param name="operatorKind">The joined-key operator to apply.</param>
    /// <param name="value">The typed operand to encode.</param>
    /// <returns>A joined-key composite predicate.</returns>
    private LibraDexCompositePartCriterion CreateTyped(LibraDexConditionOperatorKind operatorKind, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LibraDexCompositePartCriterion(
            LibraDexCompositePartCriterion.JoinedKeyPartName,
            ClassifyJoinedValueKind(value),
            operatorKind,
            Array.AsReadOnly(new object?[] { value }),
            ignoreCase: false,
            culture: null,
            delimiter,
            partNames,
            excludedPartNames);
    }

    private static LibraDexConditionValueKind ClassifyJoinedValueKind(object value)
    {
        Type type = value.GetType();
        if (type == typeof(byte[]))
        {
            return LibraDexConditionValueKind.Binary;
        }

        if (type == typeof(Guid))
        {
            return LibraDexConditionValueKind.Guid;
        }

        if (type == typeof(DateTime))
        {
            return LibraDexConditionValueKind.DateTime;
        }

        if (type == typeof(DateOnly))
        {
            return LibraDexConditionValueKind.DateOnly;
        }

        if (type == typeof(TimeOnly))
        {
            return LibraDexConditionValueKind.TimeOnly;
        }

        return type == typeof(string)
            ? LibraDexConditionValueKind.String
            : LibraDexConditionValueKind.Numeric;
    }
}

/// <summary>
/// Captures string predicates for one named routed composite tier.<br/>
/// </summary>
public sealed class LibraDexCompositeStringPartCondition
{
    private readonly string name;

    internal LibraDexCompositeStringPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a string component value.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.EqualTo, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string prefix predicate for one component tier.<br/>
    /// </summary>
    /// <param name="value">The prefix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.StartsWith, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string suffix predicate for one component tier.<br/>
    /// This is a scan-backed tier predicate unless the composite part is later connected to a maintained reversed projection.<br/>
    /// </summary>
    /// <param name="value">The suffix value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EndsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.EndsWith, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string containment predicate for one component tier.<br/>
    /// The predicate is intentionally part-scoped; whole-composite containment requires an explicit joined-key policy so LibraDex does not invent hidden flattening semantics.<br/>
    /// </summary>
    /// <param name="value">The contained value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Contains(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.Contains, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a wildcard pattern predicate for one string component tier.<br/>
    /// The pattern uses the same wildcard rules as other LibraDex string patterns: `*` spans zero or more characters and `?` matches one character.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern to apply to the component value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.MatchesPattern, ignoreCase, culture, pattern);
    }

    /// <summary>
    /// Captures a string component predicate greater than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterThan(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.GreaterThan, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string component predicate greater than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterOrEqual(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.GreaterOrEqual, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string component predicate less than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessThan(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.LessThan, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures a string component predicate less than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessOrEqual(string value, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.LessOrEqual, ignoreCase, culture, value);
    }

    /// <summary>
    /// Captures an inclusive string range predicate for one component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <param name="ignoreCase">Whether comparison should ignore case.</param>
    /// <param name="culture">Optional culture name for managed comparison.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Between(string lower, string upper, bool ignoreCase = false, string? culture = null)
    {
        return Create(LibraDexConditionOperatorKind.Between, ignoreCase, culture, lower, upper);
    }

    private LibraDexCompositePartCriterion Create(
        LibraDexConditionOperatorKind operatorKind,
        bool ignoreCase,
        string? culture,
        params object?[] values)
    {
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.String, operatorKind, Array.AsReadOnly(values), ignoreCase, culture);
    }
}

/// <summary>
/// Captures GUID predicates for one named routed composite tier.<br/>
/// GUID values remain binary GUID values; text matching belongs on a caller-declared string part.<br/>
/// </summary>
public sealed class LibraDexCompositeGuidPartCondition
{
    private readonly string name;

    internal LibraDexCompositeGuidPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a GUID component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(Guid value)
    {
        return Create(LibraDexConditionOperatorKind.EqualTo, value);
    }

    /// <summary>
    /// Captures inequality against a GUID component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value to exclude.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NotEqualTo(Guid value)
    {
        return Create(LibraDexConditionOperatorKind.NotEqualTo, value);
    }

    /// <summary>
    /// Captures a prefix predicate over the stored GUID byte-domain representation.<br/>
    /// The GUID operand is encoded once and the first <paramref name="byteCount"/> bytes are compared against each routed component value.<br/>
    /// </summary>
    /// <param name="value">The GUID value that supplies the prefix bytes.</param>
    /// <param name="byteCount">The number of leading GUID bytes to compare, from 1 through 16.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion StartsWith(Guid value, int byteCount)
    {
        if (byteCount is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "GUID prefix byte count must be 1 through 16.");
        }

        return Create(LibraDexConditionOperatorKind.StartsWith, value, byteCount);
    }

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
    {
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Guid, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);
    }
}

/// <summary>
/// Captures structured date/time predicates for one named routed composite tier.<br/>
/// The predicates preserve date intent while the composite executor applies the same packed structured date layout used by top-level date indexes.<br/>
/// </summary>
public sealed class LibraDexCompositeDatePartCondition
{
    private readonly string name;

    internal LibraDexCompositeDatePartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures a structured date/time year equality predicate.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearEqualTo(int year)
    {
        return Create(LibraDexConditionOperatorKind.YearEqualTo, year);
    }

    /// <summary>
    /// Captures a structured date/time year equality predicate using Abraxas-style naming.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearEqual(int year)
    {
        return YearEqualTo(year);
    }

    /// <summary>
    /// Captures a structured date/time inclusive year range predicate.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive starting year.</param>
    /// <param name="endYear">The inclusive ending year.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearRange(int startYear, int endYear)
    {
        return Create(LibraDexConditionOperatorKind.YearRange, startYear, endYear);
    }

    /// <summary>
    /// Captures a structured date/time year and month predicate.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearMonth(int year, int month)
    {
        return Create(LibraDexConditionOperatorKind.YearMonth, year, month);
    }

    /// <summary>
    /// Captures a structured date/time year, month, and day predicate.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <param name="day">The day component, from 1 through 31 subject to the month and year.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion YearMonthDay(int year, int month, int day)
    {
        return Create(LibraDexConditionOperatorKind.YearMonthDay, year, month, day);
    }

    /// <summary>
    /// Captures a structured date/time month equality predicate across any year.<br/>
    /// </summary>
    /// <param name="month">The month component, from 1 through 12.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion MonthEqualTo(int month)
    {
        return Create(LibraDexConditionOperatorKind.MonthEqualTo, month);
    }

    /// <summary>
    /// Captures a structured date/time day equality predicate across any month and year.<br/>
    /// </summary>
    /// <param name="day">The day component, from 1 through 31.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion DayEqualTo(int day)
    {
        return Create(LibraDexConditionOperatorKind.DayEqualTo, day);
    }

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
    {
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.DateTime, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);
    }
}

/// <summary>
/// Captures scalar predicates for one named routed composite tier.<br/>
/// </summary>
/// <typeparam name="TValue">The scalar value type.</typeparam>
public sealed class LibraDexCompositeScalarPartCondition<TValue>
{
    private readonly string name;

    internal LibraDexCompositeScalarPartCondition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
    }

    /// <summary>
    /// Captures equality against a scalar component value.<br/>
    /// </summary>
    /// <param name="value">The scalar value to match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion EqualTo(TValue value)
    {
        return Create(LibraDexConditionOperatorKind.EqualTo, value);
    }

    /// <summary>
    /// Captures a scalar component predicate greater than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterThan(TValue value)
    {
        return Create(LibraDexConditionOperatorKind.GreaterThan, value);
    }

    /// <summary>
    /// Captures a scalar component predicate greater than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion GreaterOrEqual(TValue value)
    {
        return Create(LibraDexConditionOperatorKind.GreaterOrEqual, value);
    }

    /// <summary>
    /// Captures a scalar component predicate less than the supplied value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessThan(TValue value)
    {
        return Create(LibraDexConditionOperatorKind.LessThan, value);
    }

    /// <summary>
    /// Captures a scalar component predicate less than or equal to the supplied value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion LessOrEqual(TValue value)
    {
        return Create(LibraDexConditionOperatorKind.LessOrEqual, value);
    }

    /// <summary>
    /// Captures an inclusive scalar range predicate for one component tier.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower value.</param>
    /// <param name="upper">The inclusive upper value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion Between(TValue lower, TValue upper)
    {
        return Create(LibraDexConditionOperatorKind.Between, lower, upper);
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) == equalTo` and is evaluated as a residual predicate inside the routed composite scan for the selected tier path.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAnd(TValue bitMask, TValue equalTo)
    {
        return Create(LibraDexConditionOperatorKind.BitAndEqualTo, bitMask, equalTo);
    }

    /// <summary>
    /// Captures a bitwise-AND zero predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) == default(TValue)` and preserves Abraxas-style default comparison behavior.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAnd(TValue bitMask)
    {
        return BitAnd(bitMask, default!);
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate for one scalar component tier.<br/>
    /// The condition means `(componentValue &amp; bitMask) != notEqualTo` and is the primitive form behind any-bit-set checks.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each component value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
    {
        return Create(LibraDexConditionOperatorKind.BitAndNotEqualTo, bitMask, notEqualTo);
    }

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion AllBitsSet(TValue bitMask)
    {
        return BitAnd(bitMask, bitMask);
    }

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for `(componentValue &amp; bitMask) != default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for overlap.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion AnyBitsSet(TValue bitMask)
    {
        return BitAndNotEqualTo(bitMask, default!);
    }

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set for one scalar component tier.<br/>
    /// This is a readability wrapper for `(componentValue &amp; bitMask) == default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each component value.</param>
    /// <returns>A composite part predicate.</returns>
    public LibraDexCompositePartCriterion NoBitsSet(TValue bitMask)
    {
        return BitAnd(bitMask, default!);
    }

    private LibraDexCompositePartCriterion Create(LibraDexConditionOperatorKind operatorKind, params object?[] values)
    {
        return new LibraDexCompositePartCriterion(name, LibraDexConditionValueKind.Numeric, operatorKind, Array.AsReadOnly(values), ignoreCase: false, culture: null);
    }
}

/// <summary>
/// Captures numeric comparison and bitmask operators for one adopted condition leaf.<br/>
/// Bitmask operators intentionally stay on numeric selectors rather than string, date, GUID, or binary selectors so they do not leak into unrelated condition grammar.<br/>
/// </summary>
/// <typeparam name="TValue">The numeric operand value accepted by this operator chain.</typeparam>
public sealed class LibraDexNumericConditionOperator<TValue> : LibraDexConditionOperator<TValue>
{
    internal LibraDexNumericConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
        : base(builder, indexSelector, LibraDexConditionValueKind.Numeric)
    {
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) == equalTo` and materializes as an explicit compact-index scan unless a future optimizer can narrow the key space safely.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, TValue equalTo)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(equalTo));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with deferred operands.<br/>
    /// Both factories are invoked only when the completed condition is materialized, preserving reusable condition fragments without re-recording the builder chain.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="equalTo">The deferred expected masked-value factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(Func<TValue> bitMask, Func<TValue> equalTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        ArgumentNullException.ThrowIfNull(equalTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => equalTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with a deferred mask.<br/>
    /// The mask factory is invoked when the condition materializes; the comparison operand is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(Func<TValue> bitMask, TValue equalTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(equalTo));
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate over the selected numeric index with a deferred comparison value.<br/>
    /// The comparison factory is invoked when the condition materializes; the mask is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="equalTo">The deferred expected masked-value factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, Func<TValue> equalTo)
    {
        ArgumentNullException.ThrowIfNull(equalTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Deferred(() => equalTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND zero predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) == default(TValue)` and mirrors Abraxas' default comparison operand while keeping the scan-backed execution explicit.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index.<br/>
    /// The condition means `(storedValue &amp; bitMask) != notEqualTo`; this is the primitive form used for "any selected bit is set" without inventing a separate index structure.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(notEqualTo));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with deferred operands.<br/>
    /// Both factories are invoked only when the completed condition is materialized.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="notEqualTo">The deferred masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(Func<TValue> bitMask, Func<TValue> notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        ArgumentNullException.ThrowIfNull(notEqualTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => notEqualTo()));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with a deferred mask.<br/>
    /// The mask factory is invoked when the condition materializes; the comparison operand is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(Func<TValue> bitMask, TValue notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(notEqualTo));
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate over the selected numeric index with a deferred comparison value.<br/>
    /// The comparison factory is invoked when the condition materializes; the mask is captured statically.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each stored key value.</param>
    /// <param name="notEqualTo">The deferred masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, Func<TValue> notEqualTo)
    {
        ArgumentNullException.ThrowIfNull(notEqualTo);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Deferred(() => notEqualTo()));
    }

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(TValue bitMask)
    {
        return BitAnd(bitMask, bitMask);
    }

    /// <summary>
    /// Captures a deferred predicate requiring every bit in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes and is used for both the AND mask and comparison operand.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Deferred(() => bitMask()));
    }

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for `(storedValue &amp; bitMask) != default(TValue)` and normally executes as a visible compact-index scan.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for any overlap.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a deferred predicate requiring at least one bit in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndNotEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set.<br/>
    /// This is a readability wrapper for `(storedValue &amp; bitMask) == default(TValue)` and preserves the caller's bit-pattern intent for signed numeric keys.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each stored key value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(TValue bitMask)
    {
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Value(bitMask),
            LibraDexConditionOperand.Value(default(TValue)!));
    }

    /// <summary>
    /// Captures a deferred predicate requiring no bits in the runtime mask to be set.<br/>
    /// The mask factory is evaluated when the completed condition materializes.<br/>
    /// </summary>
    /// <param name="bitMask">The deferred mask factory.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(Func<TValue> bitMask)
    {
        ArgumentNullException.ThrowIfNull(bitMask);
        return Add(
            LibraDexConditionOperatorKind.BitAndEqualTo,
            LibraDexConditionOperand.Deferred(() => bitMask()),
            LibraDexConditionOperand.Value(default(TValue)!));
    }
}

/// <summary>
/// Captures typed comparison operators for one adopted condition leaf.<br/>
/// The operator methods intentionally mirror Abraxas' low-friction grammar, but they only record descriptor intent until a LibraDex index resolver is supplied.<br/>
/// </summary>
/// <typeparam name="TValue">The typed operand value accepted by this operator chain.</typeparam>
public class LibraDexConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexConditionValueKind valueKind;

    internal LibraDexConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, LibraDexConditionValueKind valueKind)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
    }

    /// <summary>
    /// Captures all identities visible through the selected index.<br/>
    /// This is mainly useful for condition-scoped grouping and complement/universe operations; ordinary targeted selection should prefer a narrower operator.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd All()
    {
        return Add(LibraDexConditionOperatorKind.All);
    }

    /// <summary>
    /// Captures equality against a static value.<br/>
    /// </summary>
    /// <param name="value">The value to compare with the selected index key.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures equality against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, matching Abraxas' parameter materialization behavior.<br/>
    /// </summary>
    /// <param name="value">The deferred value factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.EqualTo, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures inequality against a static value.<br/>
    /// </summary>
    /// <param name="value">The value to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures inequality against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred value factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.NotEqualTo, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a greater-than comparison.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a greater-than comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred exclusive lower boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.GreaterThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred inclusive lower boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.GreaterOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than comparison.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a less-than comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred exclusive upper boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.LessThan, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary value.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(TValue value, string? name = null)
    {
        return Add(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Value(value, name));
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison against a deferred value factory.<br/>
    /// The factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="value">The deferred inclusive upper boundary factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(Func<TValue> value, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(LibraDexConditionOperatorKind.LessOrEqual, LibraDexConditionOperand.Deferred(() => value(), name));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with deferred boundary factories.<br/>
    /// The factories are invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(Func<TValue> startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with a deferred lower boundary and static upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(Func<TValue> startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison with a static lower boundary and deferred upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.Between,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value of the excluded window.</param>
    /// <param name="endValue">The inclusive upper boundary value of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(TValue startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with deferred boundary factories.<br/>
    /// The factories are invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory of the excluded window.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(Func<TValue> startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with a deferred lower boundary and static upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The deferred inclusive lower boundary factory of the excluded window.</param>
    /// <param name="endValue">The inclusive upper boundary value of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(Func<TValue> startValue, TValue endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(startValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Deferred(() => startValue(), startName),
            LibraDexConditionOperand.Value(endValue, endName));
    }

    /// <summary>
    /// Captures an excluded two-boundary comparison with a static lower boundary and deferred upper boundary.<br/>
    /// The deferred factory is invoked only when the completed condition is materialized, preserving reusable condition templates.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value of the excluded window.</param>
    /// <param name="endValue">The deferred inclusive upper boundary factory of the excluded window.</param>
    /// <param name="startName">Optional replacement name for the lower boundary.</param>
    /// <param name="endName">Optional replacement name for the upper boundary.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(TValue startValue, Func<TValue> endValue, string? startName = null, string? endName = null)
    {
        ArgumentNullException.ThrowIfNull(endValue);
        return Add(
            LibraDexConditionOperatorKind.NotBetween,
            LibraDexConditionOperand.Value(startValue, startName),
            LibraDexConditionOperand.Deferred(() => endValue(), endName));
    }

    /// <summary>
    /// Captures membership in a supplied value set.<br/>
    /// The set is captured as a descriptor operand so the execution bridge can later choose repeated lookup, prepared encoding, or visible scan behavior.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<TValue> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Value(CaptureMembershipInput(values), name));
    }

    /// <summary>
    /// Captures membership in a deferred value set factory.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred value set factory.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<TValue>> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.InSet, LibraDexConditionOperand.Deferred(() => CaptureMembershipInput(values()), name));
    }

    /// <summary>
    /// Captures membership in a supplied value set.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{TValue})"/>; unlike Abraxas, LibraDex does not need a separate serialized-set route because membership stays in managed .NET execution.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<TValue> values)
    {
        return InSet(values);
    }

    /// <summary>
    /// Captures membership in a deferred value set factory.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{TValue}})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<TValue>> values)
    {
        return InSet(values);
    }

    /// <summary>
    /// Captures Abraxas-style membership in a supplied value set.<br/>
    /// This is an alias for <see cref="InSet(IEnumerable{TValue})"/> so copied condition-builder call sites do not need terminology rewrites before bridge planning.<br/>
    /// </summary>
    /// <param name="values">The values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(IEnumerable<TValue> values)
    {
        return InSet(values);
    }

    /// <summary>
    /// Captures Abraxas-style membership in a deferred value set factory.<br/>
    /// This is an alias for <see cref="InSet(Func{IEnumerable{TValue}})"/> so copied condition-builder call sites can preserve source grammar.<br/>
    /// </summary>
    /// <param name="values">The deferred values to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(Func<IEnumerable<TValue>> values)
    {
        return InSet(values);
    }

    /// <summary>
    /// Captures membership exclusion in a supplied value set.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<TValue> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.NotInSet, LibraDexConditionOperand.Value(CaptureMembershipInput(values), name));
    }

    /// <summary>
    /// Captures membership exclusion from a deferred value set factory.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <param name="name">Optional replacement name for reusable condition templates.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<TValue>> values, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Add(LibraDexConditionOperatorKind.NotInSet, LibraDexConditionOperand.Deferred(() => CaptureMembershipInput(values()), name));
    }

    /// <summary>
    /// Captures membership exclusion from a supplied value set.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{TValue})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<TValue> values)
    {
        return NotInSet(values);
    }

    /// <summary>
    /// Captures membership exclusion from a deferred value set factory.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{TValue}})"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<TValue>> values)
    {
        return NotInSet(values);
    }

    /// <summary>
    /// Captures Abraxas-style membership exclusion from a supplied value set.<br/>
    /// This is an alias for <see cref="NotInSet(IEnumerable{TValue})"/> so copied condition-builder call sites can preserve source grammar while LibraDex plans through one descriptor kind.<br/>
    /// </summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(IEnumerable<TValue> values)
    {
        return NotInSet(values);
    }

    /// <summary>
    /// Captures Abraxas-style membership exclusion from a deferred value set factory.<br/>
    /// This is an alias for <see cref="NotInSet(Func{IEnumerable{TValue}})"/> so copied condition-builder call sites can preserve source grammar.<br/>
    /// </summary>
    /// <param name="values">The deferred values to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(Func<IEnumerable<TValue>> values)
    {
        return NotInSet(values);
    }

    protected LibraDexConditionContinueOrEnd Add(
        LibraDexConditionOperatorKind operatorKind,
        params LibraDexConditionOperand[] operands)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            operatorKind,
            operands,
            IgnoreCase: false,
            Culture: null));
    }

    private static object CaptureMembershipInput(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is ISet<TValue> or IReadOnlyCollection<TValue>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures string-specific adopted condition operators for one selected LibraDex index.<br/>
/// Text options are preserved as descriptor metadata so the bridge can require exact, folded-text, or sort-key projections instead of hiding query-time scans.<br/>
/// </summary>
public sealed class LibraDexStringConditionOperator : LibraDexConditionOperator<string>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexStringConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
        : base(builder, indexSelector, LibraDexConditionValueKind.String)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Captures a string equality condition with optional case-insensitive projection intent.<br/>
    /// When <paramref name="ignoreCase"/> is true, materialization requires a maintained sort-key projection instead of normalizing or scanning the exact string index at query time.<br/>
    /// </summary>
    /// <param name="value">The string value to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.EqualTo, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string inequality condition with optional case-insensitive projection intent.<br/>
    /// When <paramref name="ignoreCase"/> is true, materialization requires a maintained sort-key projection and maps the exclusion to ordered extents over that projection.<br/>
    /// </summary>
    /// <param name="value">The string value to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.NotEqualTo, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string greater-than condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so range readers compare stored projection bytes rather than performing query-time collation.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.GreaterThan, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string greater-than-or-equal condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.GreaterOrEqual, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string less-than condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.LessThan, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string less-than-or-equal condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ordered comparison uses a maintained sort-key projection so the primitive route remains an ordered key boundary.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.LessOrEqual, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures an inclusive string range condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive ranges use maintained sort-key projection operands so retrieval collapses to the existing ordered range primitive.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower string bound.</param>
    /// <param name="endValue">The inclusive upper string bound.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(string startValue, string endValue, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.Between, new[] { LibraDexConditionOperand.Value(startValue), LibraDexConditionOperand.Value(endValue) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a string outside-range condition with optional case-insensitive projection intent.<br/>
    /// Case-insensitive outside-ranges use maintained sort-key projection operands and materialize as lower/upper ordered extents.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower string bound to exclude.</param>
    /// <param name="endValue">The inclusive upper string bound to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotBetween(string startValue, string endValue, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.NotBetween, new[] { LibraDexConditionOperand.Value(startValue), LibraDexConditionOperand.Value(endValue) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// Case-insensitive membership uses maintained projection operands so the bridge can reuse membership execution rather than scanning the exact string index.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{string}, bool, string?)"/>; managed execution removes Abraxas' need to distinguish serialized set membership from enumerable membership.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        return InSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{string}}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        return InSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string membership with an explicit method-level managed comparison policy.<br/>
    /// The policy is used only when membership needs managed residual comparison; maintained projections and encoded exact routes still have first refusal.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InSet(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.InSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="InSet(IEnumerable{string}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        return InSet(values, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="InSet(Func{IEnumerable{string}}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd In(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        return InSet(values, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string membership with optional case-insensitive projection intent.<br/>
    /// This alias preserves the Abraxas-style `IsIn` spelling while keeping the descriptor shape identical to `InSet`.<br/>
    /// </summary>
    /// <param name="values">The string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        return InSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures Abraxas-style string membership with a deferred value set factory.<br/>
    /// This alias preserves the Abraxas-style `IsIn` spelling while keeping the descriptor shape identical to `InSet`.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to match.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        return InSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// Case-insensitive non-membership uses maintained projection operands and then negates the resulting membership leaf.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{string}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        return NotInSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and optional case-insensitive projection intent.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{string}}, bool, string?)"/> and shares the same managed membership descriptor.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        return NotInSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures string non-membership with an explicit method-level managed comparison policy.<br/>
    /// The policy is used only when non-membership needs managed residual comparison; maintained projections and encoded exact routes still have first refusal.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Value(CaptureStringMembershipInput(values)) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// The factory is invoked only when the completed condition is materialized so request-scoped sets can be reused without rebuilding the condition.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotInSet(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stringComparisonPolicy);
        return AddText(LibraDexConditionOperatorKind.NotInSet, new[] { LibraDexConditionOperand.Deferred(() => CaptureStringMembershipInput(values())) }, stringComparisonPolicy.IgnoreCase, stringComparisonPolicy.CultureName, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(IEnumerable{string}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(IEnumerable<string> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        return NotInSet(values, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with a deferred value set factory and an explicit method-level managed comparison policy.<br/>
    /// This is a LibraDex alias for <see cref="NotInSet(Func{IEnumerable{string}}, LibraDexStringComparisonPolicy)"/> and uses the same prepared membership path.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="stringComparisonPolicy">The method-level string comparison policy.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotIn(Func<IEnumerable<string>> values, LibraDexStringComparisonPolicy stringComparisonPolicy)
    {
        return NotInSet(values, stringComparisonPolicy);
    }

    /// <summary>
    /// Captures string non-membership with optional case-insensitive projection intent.<br/>
    /// This alias preserves the Abraxas-style `IsNotIn` spelling while keeping the descriptor shape identical to `NotInSet`.<br/>
    /// </summary>
    /// <param name="values">The string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(IEnumerable<string> values, bool ignoreCase = false, string? culture = null)
    {
        return NotInSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures Abraxas-style string non-membership with a deferred value set factory.<br/>
    /// This alias preserves the Abraxas-style `IsNotIn` spelling while keeping the descriptor shape identical to `NotInSet`.<br/>
    /// </summary>
    /// <param name="values">The deferred string values to exclude.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNotIn(Func<IEnumerable<string>> values, bool ignoreCase = false, string? culture = null)
    {
        return NotInSet(values, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a starts-with text condition.<br/>
    /// The descriptor preserves case and culture options so the resolver can require a matching maintained projection or report visible scan behavior.<br/>
    /// </summary>
    /// <param name="value">The text prefix value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.StartsWith, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures an ends-with text condition.<br/>
    /// </summary>
    /// <param name="value">The text suffix value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.EndsWith, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a contains text condition.<br/>
    /// </summary>
    /// <param name="value">The contained text value.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.Contains, value, ignoreCase, culture);
    }

    /// <summary>
    /// Captures a pattern text condition.<br/>
    /// Pattern execution is descriptor-only in this slice; the resolver decides later whether a maintained projection, predicate, or unsupported case applies.<br/>
    /// </summary>
    /// <param name="pattern">The pattern descriptor.</param>
    /// <param name="ignoreCase">Whether case-insensitive text behavior was requested.</param>
    /// <param name="culture">The culture name for case or sort behavior, when supplied.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(string pattern, bool ignoreCase = false, string? culture = null)
    {
        return AddText(LibraDexConditionOperatorKind.MatchesPattern, pattern, ignoreCase, culture);
    }

    private LibraDexConditionContinueOrEnd AddText(
        LibraDexConditionOperatorKind operatorKind,
        string value,
        bool ignoreCase,
        string? culture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return AddText(operatorKind, new[] { LibraDexConditionOperand.Value(value) }, ignoreCase, culture, stringComparisonPolicy);
    }

    private LibraDexConditionContinueOrEnd AddText(
        LibraDexConditionOperatorKind operatorKind,
        IReadOnlyList<LibraDexConditionOperand> operands,
        bool ignoreCase,
        string? culture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.String,
            operatorKind,
            operands,
            ignoreCase,
            culture,
            stringComparisonPolicy));
    }

    private static object CaptureStringMembershipInput(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values is HashSet<string> or ISet<string> or IReadOnlyCollection<string>
            ? values
            : values.ToArray();
    }
}

/// <summary>
/// Captures structured date/time adopted condition operators for one selected LibraDex index.<br/>
/// Date-part methods capture Abraxas-compatible descriptors; materialization chooses ordered ranges, same-index multi-ranges, or packed-field structured component predicates based on the selected operator.<br/>
/// </summary>
/// <typeparam name="TValue">The date-like CLR type accepted by the exact comparison operators.</typeparam>
public sealed class LibraDexDateConditionOperator<TValue> : LibraDexConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexConditionValueKind valueKind;

    internal LibraDexDateConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector, LibraDexConditionValueKind valueKind)
        : base(builder, indexSelector, valueKind)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
    }

    /// <summary>
    /// Captures a structured year-part condition.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearEqualTo(int year)
    {
        return AddDatePart(LibraDexConditionOperatorKind.YearEqualTo, year);
    }

    /// <summary>
    /// Captures the Abraxas-style structured year equality branch.<br/>
    /// This is an alias for <see cref="YearEqualTo(int)"/> so copied date-condition code can preserve the source grammar while LibraDex keeps one execution bridge.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearEqual(int year)
    {
        return YearEqualTo(year);
    }

    /// <summary>
    /// Captures a structured year inequality condition.<br/>
    /// The current bridge can execute this as the complement of one ordered year range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotEqual(int year)
    {
        return AddDatePart(LibraDexConditionOperatorKind.YearNotEqualTo, year);
    }

    /// <summary>
    /// Captures a structured year membership condition.<br/>
    /// Each year is one contiguous ordered range; the bridge can execute the set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="years">The year components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearIn(params int[] years)
    {
        return AddDateComponentSet(LibraDexConditionOperatorKind.YearIn, years);
    }

    /// <summary>
    /// Captures a structured year non-membership condition.<br/>
    /// The current bridge can execute this as the complement of a same-index union of ordered year ranges.<br/>
    /// </summary>
    /// <param name="years">The year components to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotIn(params int[] years)
    {
        return AddDateComponentSet(LibraDexConditionOperatorKind.YearNotIn, years);
    }

    /// <summary>
    /// Captures a structured contiguous year-range condition.<br/>
    /// The structured date codec stores year in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearRange(int startYear, int endYear)
    {
        return AddDateParts(LibraDexConditionOperatorKind.YearRange, startYear, endYear);
    }

    /// <summary>
    /// Captures a structured year non-range condition.<br/>
    /// The current bridge can execute this as the complement of one contiguous ordered year range.<br/>
    /// </summary>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearNotRange(int startYear, int endYear)
    {
        return AddDateParts(LibraDexConditionOperatorKind.YearNotRange, startYear, endYear);
    }

    /// <summary>
    /// Captures a structured lower year boundary condition.<br/>
    /// The bridge expands this to a single ordered range from the supplied year through the maximum supported date value.<br/>
    /// </summary>
    /// <param name="year">The inclusive lower year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd OnOrAfterYear(int year)
    {
        return AddDatePart(LibraDexConditionOperatorKind.YearOnOrAfter, year);
    }

    /// <summary>
    /// Captures a structured upper year boundary condition.<br/>
    /// The bridge expands this to a single ordered range from the minimum supported date value through the supplied year.<br/>
    /// </summary>
    /// <param name="year">The inclusive upper year component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd OnOrBeforeYear(int year)
    {
        return AddDatePart(LibraDexConditionOperatorKind.YearOnOrBefore, year);
    }

    /// <summary>
    /// Captures a structured month-part condition.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthEqualTo(int month)
    {
        return AddDatePart(LibraDexConditionOperatorKind.MonthEqualTo, month);
    }

    /// <summary>
    /// Captures the Abraxas-style month equality branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by reading the packed month bits.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthEqual(int month)
    {
        return MonthEqualTo(month);
    }

    /// <summary>
    /// Captures a month component membership branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by reading the packed month bits.<br/>
    /// </summary>
    /// <param name="months">The month components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthIn(params int[] months)
    {
        return AddDateComponentSet(LibraDexConditionOperatorKind.MonthIn, months);
    }

    /// <summary>
    /// Captures a month component non-membership branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by negating a packed month membership test.<br/>
    /// </summary>
    /// <param name="months">The month components to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthNotIn(params int[] months)
    {
        return AddDateComponentSet(LibraDexConditionOperatorKind.MonthNotIn, months);
    }

    /// <summary>
    /// Captures a month component range branch.<br/>
    /// This branch is not contiguous in the full ordered date key across all years, so it executes through the structured component primitive rather than an ordered range.<br/>
    /// </summary>
    /// <param name="startMonth">The inclusive lower month component.</param>
    /// <param name="endMonth">The inclusive upper month component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthRange(int startMonth, int endMonth)
    {
        return AddDateParts(LibraDexConditionOperatorKind.MonthRange, startMonth, endMonth);
    }

    /// <summary>
    /// Captures a month component non-range branch.<br/>
    /// This branch is component-only across all years and executes through the structured component primitive by negating a packed month range test.<br/>
    /// </summary>
    /// <param name="startMonth">The inclusive lower month component to exclude.</param>
    /// <param name="endMonth">The inclusive upper month component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthNotInRange(int startMonth, int endMonth)
    {
        return AddDateParts(LibraDexConditionOperatorKind.MonthNotRange, startMonth, endMonth);
    }

    /// <summary>
    /// Captures a structured day-part condition.<br/>
    /// </summary>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayEqualTo(int day)
    {
        return AddDatePart(LibraDexConditionOperatorKind.DayEqualTo, day);
    }

    /// <summary>
    /// Captures the Abraxas-style day equality branch.<br/>
    /// This branch is component-only across all months and years and executes through the structured component primitive by reading the packed day bits.<br/>
    /// </summary>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayEqual(int day)
    {
        return DayEqualTo(day);
    }

    /// <summary>
    /// Captures a day component membership branch.<br/>
    /// This branch is component-only across all months and years and executes through the structured component primitive by reading the packed day bits.<br/>
    /// </summary>
    /// <param name="days">The day components to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayIn(params int[] days)
    {
        return AddDateComponentSet(LibraDexConditionOperatorKind.DayIn, days);
    }

    /// <summary>
    /// Captures a day component range branch.<br/>
    /// This branch is component-only and executes through the structured component primitive rather than an ordered range.<br/>
    /// </summary>
    /// <param name="startDay">The inclusive lower day component.</param>
    /// <param name="endDay">The inclusive upper day component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd DayRange(int startDay, int endDay)
    {
        return AddDateParts(LibraDexConditionOperatorKind.DayRange, startDay, endDay);
    }

    /// <summary>
    /// Captures a day component non-range branch.<br/>
    /// This branch is component-only and executes through the structured component primitive by negating a packed day range test.<br/>
    /// </summary>
    /// <param name="startDay">The inclusive lower day component to exclude.</param>
    /// <param name="endDay">The inclusive upper day component to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotDayRange(int startDay, int endDay)
    {
        return AddDateParts(LibraDexConditionOperatorKind.DayNotRange, startDay, endDay);
    }

    /// <summary>
    /// Captures a structured quarter-part condition.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd QuarterEqualTo(int quarter)
    {
        return AddDatePart(LibraDexConditionOperatorKind.QuarterEqualTo, quarter);
    }

    /// <summary>
    /// Captures the Abraxas-style quarter branch across all years.<br/>
    /// This branch is month-derived and component-only, so it executes through the structured component primitive over packed month bits.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InQuarter(int quarter)
    {
        return AddDatePart(LibraDexConditionOperatorKind.InQuarter, quarter);
    }

    /// <summary>
    /// Captures the Abraxas-style quarter range branch across all years.<br/>
    /// This branch is month-derived and component-only, so it executes through the structured component primitive over packed month bits.<br/>
    /// </summary>
    /// <param name="startQuarter">The inclusive lower quarter component.</param>
    /// <param name="endQuarter">The inclusive upper quarter component.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd InQuarterRange(int startQuarter, int endQuarter)
    {
        return AddDateParts(LibraDexConditionOperatorKind.InQuarterRange, startQuarter, endQuarter);
    }

    /// <summary>
    /// Captures a structured year/month condition.<br/>
    /// The structured date codec stores year and month in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonth(int year, int month)
    {
        return AddDateParts(LibraDexConditionOperatorKind.YearMonth, year, month);
    }

    /// <summary>
    /// Captures a structured year/month membership condition.<br/>
    /// Each supplied year/month pair is one contiguous range over the logical date index; the bridge can execute the full set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="values">The year/month pairs to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthIn(IEnumerable<(int year, int month)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(LibraDexConditionOperatorKind.YearMonthIn, values.ToArray());
    }

    /// <summary>
    /// Captures a structured year/month/day condition.<br/>
    /// The structured date codec stores year, month, and day in the high ordered key bits, so this condition can execute as one contiguous range over the logical date index.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthDay(int year, int month, int day)
    {
        return AddDateParts(LibraDexConditionOperatorKind.YearMonthDay, year, month, day);
    }

    /// <summary>
    /// Captures a structured year/month/day membership condition.<br/>
    /// Each supplied year/month/day tuple is one contiguous range over the logical date index; the bridge can execute the full set as a same-index union of those ranges.<br/>
    /// </summary>
    /// <param name="values">The year/month/day tuples to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearMonthDayIn(IEnumerable<(int year, int month, int day)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(LibraDexConditionOperatorKind.YearMonthDayIn, values.ToArray());
    }

    /// <summary>
    /// Captures one year with several month components.<br/>
    /// Each month inside the year is a contiguous ordered range, so the bridge can execute this as a same-index union without scanning.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="months">The month components to match inside the supplied year.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearInMonths(int year, params int[] months)
    {
        return AddDateTupleSet(LibraDexConditionOperatorKind.YearInMonths, (year, months));
    }

    /// <summary>
    /// Captures one month/day tuple across all years.<br/>
    /// This branch is not contiguous in the ordered date key across all years, so it executes through the structured component primitive over packed month and day bits.<br/>
    /// </summary>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MonthDay(int month, int day)
    {
        return AddDateParts(LibraDexConditionOperatorKind.MonthDay, month, day);
    }

    /// <summary>
    /// Captures one year and quarter.<br/>
    /// The selected quarter inside one year is a contiguous ordered range and can execute through the current date range bridge.<br/>
    /// </summary>
    /// <param name="year">The year component to match.</param>
    /// <param name="quarter">The quarter component to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd YearQuarter(int year, int quarter)
    {
        return AddDateParts(LibraDexConditionOperatorKind.YearQuarter, year, quarter);
    }

    /// <summary>
    /// Captures the quarter-start calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month/day component tests.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsQuarterStart()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsQuarterStart);
    }

    /// <summary>
    /// Captures the quarter-end calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month bits plus a packed year/month/day last-day check.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsQuarterEnd()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsQuarterEnd);
    }

    /// <summary>
    /// Captures the half-year-start calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month/day component tests.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsHalfYearStart()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsHalfYearStart);
    }

    /// <summary>
    /// Captures the half-year-end calendar branch.<br/>
    /// This branch is component-derived across all years and executes through packed month bits plus a packed year/month/day last-day check.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsHalfYearEnd()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsHalfYearEnd);
    }

    /// <summary>
    /// Captures the first-of-month calendar branch.<br/>
    /// This branch is component-only across all months and years and executes through the packed day component.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsFirstOfMonth()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsFirstOfMonth);
    }

    /// <summary>
    /// Captures the last-of-month calendar branch.<br/>
    /// This branch depends on month length and leap-year semantics, so it executes through packed year/month/day fields and integer calendar arithmetic.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsLastOfMonth()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsLastOfMonth);
    }

    /// <summary>
    /// Captures the current UTC day branch.<br/>
    /// The bridge evaluates the current UTC date at materialization time and expands it to the appropriate DateTime, DateTimeOffset, or DateOnly range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsToday()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsToday);
    }

    /// <summary>
    /// Captures the previous UTC day branch.<br/>
    /// The bridge evaluates the previous UTC date at materialization time and expands it to the appropriate DateTime, DateTimeOffset, or DateOnly range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsYesterday()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsYesterday);
    }

    /// <summary>
    /// Captures a trailing UTC day-window branch.<br/>
    /// The bridge evaluates the current UTC date at materialization time and expands the supplied day count to a contiguous ordered range.<br/>
    /// </summary>
    /// <param name="days">The number of trailing UTC days to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastDays(int days)
    {
        return AddDatePart(LibraDexConditionOperatorKind.IsInLastDays, days);
    }

    /// <summary>
    /// Captures a trailing UTC hour-window branch.<br/>
    /// This branch applies only to DateTime and DateTimeOffset indexes because DateOnly has no hour component.<br/>
    /// </summary>
    /// <param name="hours">The number of trailing UTC hours to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastHours(int hours)
    {
        return AddDatePart(LibraDexConditionOperatorKind.IsInLastHours, hours);
    }

    /// <summary>
    /// Captures a trailing UTC minute-window branch.<br/>
    /// This branch applies only to DateTime and DateTimeOffset indexes because DateOnly has no minute component.<br/>
    /// </summary>
    /// <param name="minutes">The number of trailing UTC minutes to include.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsInLastMinutes(int minutes)
    {
        return AddDatePart(LibraDexConditionOperatorKind.IsInLastMinutes, minutes);
    }

    /// <summary>
    /// Captures the weekend calendar branch.<br/>
    /// This branch is day-of-week derived and executes through the packed day-of-week bits.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsWeekend()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsWeekend);
    }

    /// <summary>
    /// Captures the weekday calendar branch.<br/>
    /// This branch is day-of-week derived and executes through the packed day-of-week bits.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsWeekday()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsWeekday);
    }

    /// <summary>
    /// Captures the morning time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsMorning()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsMorning);
    }

    /// <summary>
    /// Captures the afternoon time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsAfternoon()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsAfternoon);
    }

    /// <summary>
    /// Captures the evening time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through the packed hour component; TimeOnly indexes use an ordered time range.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsEvening()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsEvening);
    }

    /// <summary>
    /// Captures the night time-of-day branch.<br/>
    /// DateTime and DateTimeOffset indexes execute this through packed hour membership; TimeOnly indexes use two ordered time ranges around midnight.<br/>
    /// </summary>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd IsNight()
    {
        return AddDateParts(LibraDexConditionOperatorKind.IsNight);
    }

    private LibraDexConditionContinueOrEnd AddDatePart(LibraDexConditionOperatorKind operatorKind, int value)
    {
        return AddDateParts(operatorKind, value);
    }

    private LibraDexConditionContinueOrEnd AddDateParts(LibraDexConditionOperatorKind operatorKind, params int[] values)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            operatorKind,
            values.Select(static value => LibraDexConditionOperand.Value(value)).ToArray(),
            IgnoreCase: false,
            Culture: null));
    }

    /// <summary>
    /// Captures a date component membership set as one operand so later planning can distinguish one set-valued condition from several scalar component operands.<br/>
    /// This matters for branches such as `YearIn`, where the current bridge can union contiguous year ranges, and branches such as `MonthIn`, where the structured component primitive compiles the set into a packed-field bit mask.<br/>
    /// </summary>
    /// <param name="operatorKind">The date component set operator to capture.</param>
    /// <param name="values">The component values to capture.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    private LibraDexConditionContinueOrEnd AddDateComponentSet(LibraDexConditionOperatorKind operatorKind, int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return AddDateTupleSet(operatorKind, values);
    }

    /// <summary>
    /// Captures a date tuple set as one operand so the bridge can preserve the caller's selected pairs or tuples as a single condition payload.<br/>
    /// The tuple payload is intentionally separate from scalar component operands used by single-range date operators.<br/>
    /// </summary>
    /// <param name="operatorKind">The date tuple-set operator to capture.</param>
    /// <param name="value">The tuple-set payload.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    private LibraDexConditionContinueOrEnd AddDateTupleSet(LibraDexConditionOperatorKind operatorKind, object value)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            valueKind,
            operatorKind,
            new[] { LibraDexConditionOperand.Value(value) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures binary-specific adopted condition operators for one selected LibraDex index.<br/>
/// </summary>
public sealed class LibraDexBinaryConditionOperator : LibraDexConditionOperator<byte[]>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexBinaryConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
        : base(builder, indexSelector, LibraDexConditionValueKind.Binary)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Captures a raw binary starts-with condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The byte prefix to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(byte[] value)
    {
        return AddBinary(LibraDexConditionOperatorKind.StartsWith, value);
    }

    /// <summary>
    /// Captures a raw binary starts-with condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal prefix pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWithHex(string hexPattern)
    {
        return AddBinaryPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.StartsWith, hexPattern));
    }

    /// <summary>
    /// Captures a raw binary ends-with condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The byte suffix to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(byte[] value)
    {
        return AddBinary(LibraDexConditionOperatorKind.EndsWith, value);
    }

    /// <summary>
    /// Captures a raw binary ends-with condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal suffix pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWithHex(string hexPattern)
    {
        return AddBinaryPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.EndsWith, hexPattern));
    }

    /// <summary>
    /// Captures a raw binary contains condition.<br/>
    /// The descriptor materializes as an encoded key-byte predicate over fixed-width byte-array keys.<br/>
    /// </summary>
    /// <param name="value">The contiguous byte sequence to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(byte[] value)
    {
        return AddBinary(LibraDexConditionOperatorKind.Contains, value);
    }

    /// <summary>
    /// Captures a raw binary contains condition from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares stored key bytes directly.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned hexadecimal contained pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd ContainsHex(string hexPattern)
    {
        return AddBinaryPattern(LibraDexConditionOperatorKind.Contains, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.Contains, hexPattern));
    }

    /// <summary>
    /// Captures a raw binary full-key pattern from a readable hexadecimal pattern.<br/>
    /// The cleaned pattern must be byte-aligned and match the fixed binary key length; `x` or `X` are wildcard nibbles.<br/>
    /// </summary>
    /// <param name="hexPattern">The byte-aligned full-key hexadecimal pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesHexPattern(string hexPattern)
    {
        return AddBinaryPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.MatchesPattern, hexPattern));
    }

    /// <summary>
    /// Captures a raw binary fixed-slice equality condition.<br/>
    /// The zero-based offset and expected bytes mirror Abraxas binary slice intent while keeping LibraDex execution over encoded key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset at which the expected sequence must start.</param>
    /// <param name="value">The byte sequence that must equal the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd SliceEqual(int offset, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            LibraDexConditionOperatorKind.BinarySliceEqual,
            new[]
            {
                LibraDexConditionOperand.Value(offset),
                LibraDexConditionOperand.Value((byte[])value.Clone())
            },
            IgnoreCase: false,
            Culture: null));
    }

    /// <summary>
    /// Captures a raw binary fixed-slice pattern from a readable hexadecimal pattern.<br/>
    /// Hex digits select nibbles, `x` or `X` select wildcard nibbles, and common separators are ignored before execution compares the selected stored key slice directly.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset at which the pattern must start.</param>
    /// <param name="hexPattern">The byte-aligned hexadecimal slice pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd SliceMatchesHex(int offset, string hexPattern)
    {
        return AddBinaryPattern(
            LibraDexConditionOperatorKind.BinarySliceEqual,
            LibraDexBinaryPatternPredicate.CreateHex(LibraDexBinaryPatternMode.SliceEqual, hexPattern, offset));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int32.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int32 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<int> SlicedAsInt32(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<int>(builder, indexSelector, LibraDexBinarySliceValueKind.Int32, offset, sizeof(int));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as an Int8.<br/>
    /// The returned operator records typed comparisons that execute over the selected key byte without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int8 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<sbyte> SlicedAsSByte(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<sbyte>(builder, indexSelector, LibraDexBinarySliceValueKind.Int8, offset, sizeof(byte));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a UInt8.<br/>
    /// The returned operator records typed comparisons that execute over the selected key byte without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt8 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<byte> SlicedAsByte(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<byte>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt8, offset, sizeof(byte));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int16.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int16 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<short> SlicedAsInt16(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<short>(builder, indexSelector, LibraDexBinarySliceValueKind.Int16, offset, sizeof(short));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt16.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt16 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<ushort> SlicedAsUInt16(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<ushort>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt16, offset, sizeof(ushort));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt32.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt32 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<uint> SlicedAsUInt32(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<uint>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt32, offset, sizeof(uint));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int64.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int64 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<long> SlicedAsInt64(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<long>(builder, indexSelector, LibraDexBinarySliceValueKind.Int64, offset, sizeof(long));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt64.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt64 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<ulong> SlicedAsUInt64(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<ulong>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt64, offset, sizeof(ulong));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Single.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Single slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<float> SlicedAsSingle(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<float>(builder, indexSelector, LibraDexBinarySliceValueKind.Single, offset, sizeof(float));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Double.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Double slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<double> SlicedAsDouble(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<double>(builder, indexSelector, LibraDexBinarySliceValueKind.Double, offset, sizeof(double));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a Decimal.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Decimal slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<decimal> SlicedAsDecimal(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<decimal>(builder, indexSelector, LibraDexBinarySliceValueKind.Decimal, offset, 16);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian Int128.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Int128 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<Int128> SlicedAsInt128(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<Int128>(builder, indexSelector, LibraDexBinarySliceValueKind.Int128, offset, 16);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian UInt128.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UInt128 slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<UInt128> SlicedAsUInt128(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<UInt128>(builder, indexSelector, LibraDexBinarySliceValueKind.UInt128, offset, 16);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a little-endian BigInteger.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the BigInteger slice.</param>
    /// <param name="byteLength">The byte length of the BigInteger slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<BigInteger> SlicedAsBigInteger(int offset, int byteLength)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<BigInteger>(builder, indexSelector, LibraDexBinarySliceValueKind.BigInteger, offset, byteLength);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a Guid.<br/>
    /// The returned operator records typed comparisons that execute over the selected 16 key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Guid slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<Guid> SlicedAsGuid(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<Guid>(builder, indexSelector, LibraDexBinarySliceValueKind.Guid, offset, 16);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as little-endian DateTime ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the DateTime tick slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateTime> SlicedAsDateTime(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<DateTime>(builder, indexSelector, LibraDexBinarySliceValueKind.DateTimeTicks, offset, sizeof(long));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as a DateOnly day number.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the DateOnly slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateOnly> SlicedAsDateOnly(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<DateOnly>(builder, indexSelector, LibraDexBinarySliceValueKind.DateOnly, offset, sizeof(int));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as TimeOnly ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the TimeOnly slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<TimeOnly> SlicedAsTimeOnly(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<TimeOnly>(builder, indexSelector, LibraDexBinarySliceValueKind.TimeOnly, offset, sizeof(long));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as TimeSpan ticks.<br/>
    /// The returned operator records typed comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the TimeSpan slice.</param>
    /// <returns>A typed binary slice operator.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<TimeSpan> SlicedAsTimeSpan(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<TimeSpan>(builder, indexSelector, LibraDexBinarySliceValueKind.TimeSpanTicks, offset, sizeof(long));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as DateTimeOffset ticks plus offset ticks and compared as UTC DateTime.<br/>
    /// This mirrors Abraxas' DateTimeOperator return from a DateTimeOffset binary slice.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the 16-byte DateTimeOffset pair.</param>
    /// <returns>A typed binary slice operator over UTC DateTime values.</returns>
    public LibraDexBinaryTypedSliceConditionOperator<DateTime> SlicedAsDateTimeOffset(int offset)
    {
        return new LibraDexBinaryTypedSliceConditionOperator<DateTime>(builder, indexSelector, LibraDexBinarySliceValueKind.DateTimeOffsetPair, offset, 16);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-8 text.<br/>
    /// The returned operator records text comparisons that execute over the selected key bytes without decoding the whole binary key.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-8 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-8 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf8String(int offset, int byteLength)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf8String, offset, byteLength);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as Latin1 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the Latin1 slice.</param>
    /// <param name="byteLength">The byte length of the Latin1 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsLatin1String(int offset, int byteLength)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Latin1String, offset, byteLength);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-16 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-16 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-16 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf16String(int offset, int byteLength)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf16String, offset, byteLength);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as UTF-32 text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-32 slice.</param>
    /// <param name="byteLength">The byte length of the UTF-32 slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsUtf32String(int offset, int byteLength)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.Utf32String, offset, byteLength);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as ASCII text.<br/>
    /// The returned operator records ordinal text comparisons over the selected key bytes.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the ASCII slice.</param>
    /// <param name="byteLength">The byte length of the ASCII slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsAsciiString(int offset, int byteLength)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.AsciiString, offset, byteLength);
    }

    /// <summary>
    /// Captures a binary slice interpreted with a caller-supplied text encoding.<br/>
    /// LibraDex stores the supplied <see cref="Encoding"/> instance in the condition descriptor and uses it directly during .NET-native execution; no encoding inference or name reconstruction is performed.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the encoded text slice.</param>
    /// <param name="byteLength">The byte length of the encoded text slice.</param>
    /// <param name="encoding">The caller-supplied text encoding used to decode the slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsEncodedString(int offset, int byteLength, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.CustomEncodingString, offset, byteLength, encoding);
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as one UTF-16 char.<br/>
    /// The returned operator records ordinal text comparisons over that one-character slice.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-16 char slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsCharUtf16(int offset)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.CharUtf16, offset, sizeof(char));
    }

    /// <summary>
    /// Captures an Abraxas-compatible binary slice interpreted as one UTF-32 rune.<br/>
    /// The returned operator records ordinal text comparisons over that rune's string representation.<br/>
    /// </summary>
    /// <param name="offset">The zero-based byte offset of the UTF-32 rune slice.</param>
    /// <returns>A typed binary string slice operator.</returns>
    public LibraDexBinaryStringSliceConditionOperator SlicedAsRuneUtf32(int offset)
    {
        return new LibraDexBinaryStringSliceConditionOperator(builder, indexSelector, LibraDexBinarySliceValueKind.RuneUtf32, offset, sizeof(int));
    }

    private LibraDexConditionContinueOrEnd AddBinary(LibraDexConditionOperatorKind operatorKind, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            new[] { LibraDexConditionOperand.Value((byte[])value.Clone()) },
            IgnoreCase: false,
            Culture: null));
    }

    private LibraDexConditionContinueOrEnd AddBinaryPattern(LibraDexConditionOperatorKind operatorKind, LibraDexBinaryPatternPredicate predicate)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            new[] { LibraDexConditionOperand.Value(predicate) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures typed comparison operators for one binary slice selected from a fixed-width byte-array index key.<br/>
/// The slice metadata is kept with each descriptor so materialization can build an encoded-byte predicate instead of routing through a separate decoded projection.<br/>
/// </summary>
/// <typeparam name="TValue">The typed slice value accepted by this operator chain.</typeparam>
public sealed class LibraDexBinaryTypedSliceConditionOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly int offset;
    private readonly int length;

    internal LibraDexBinaryTypedSliceConditionOperator(
        LibraDexConditionBuilder builder,
        LibraDexConditionIndexSelector indexSelector,
        LibraDexBinarySliceValueKind valueKind,
        int offset,
        int length)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary slice offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary slice length must be positive.");
        }

        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
        this.offset = offset;
        this.length = length;
    }

    /// <summary>
    /// Captures equality against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The typed value to compare with the selected binary slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(TValue value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo, value);
    }

    /// <summary>
    /// Captures a greater-than comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The exclusive lower boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterThan(TValue value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan, value);
    }

    /// <summary>
    /// Captures a greater-than-or-equal comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The inclusive lower boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd GreaterOrEqual(TValue value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual, value);
    }

    /// <summary>
    /// Captures a less-than comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The exclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessThan(TValue value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceLessThan, value);
    }

    /// <summary>
    /// Captures a less-than-or-equal comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="value">The inclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd LessOrEqual(TValue value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual, value);
    }

    /// <summary>
    /// Captures an inclusive two-boundary comparison against a typed binary slice value.<br/>
    /// </summary>
    /// <param name="startValue">The inclusive lower boundary value.</param>
    /// <param name="endValue">The inclusive upper boundary value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Between(TValue startValue, TValue endValue)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceBetween, startValue, endValue);
    }

    /// <summary>
    /// Captures a bitwise-AND equality predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) == equalTo` and executes as an encoded-key byte-slice residual predicate.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <param name="equalTo">The expected masked value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask, TValue equalTo)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo, bitMask, equalTo);
    }

    /// <summary>
    /// Captures a bitwise-AND zero predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) == default(TValue)` and mirrors scalar bitmask grammar.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAnd(TValue bitMask)
    {
        return BitAnd(bitMask, default!);
    }

    /// <summary>
    /// Captures a bitwise-AND inequality predicate against an integral typed binary slice.<br/>
    /// The condition means `(sliceValue &amp; bitMask) != notEqualTo` and is the primitive form behind any-bit-set checks.<br/>
    /// </summary>
    /// <param name="bitMask">The mask applied to each interpreted slice value.</param>
    /// <param name="notEqualTo">The masked value that must not match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd BitAndNotEqualTo(TValue bitMask, TValue notEqualTo)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo, bitMask, notEqualTo);
    }

    /// <summary>
    /// Captures a predicate requiring every bit in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for <see cref="BitAnd(TValue, TValue)"/> where the comparison value is the same mask.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must all be present in each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AllBitsSet(TValue bitMask)
    {
        return BitAnd(bitMask, bitMask);
    }

    /// <summary>
    /// Captures a predicate requiring at least one bit in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for `(sliceValue &amp; bitMask) != default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits are tested for overlap.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd AnyBitsSet(TValue bitMask)
    {
        return BitAndNotEqualTo(bitMask, default!);
    }

    /// <summary>
    /// Captures a predicate requiring no bits in <paramref name="bitMask"/> to be set on an integral typed binary slice.<br/>
    /// This is a readability wrapper for `(sliceValue &amp; bitMask) == default(TValue)`.<br/>
    /// </summary>
    /// <param name="bitMask">The mask whose bits must not overlap each interpreted slice value.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NoBitsSet(TValue bitMask)
    {
        return BitAnd(bitMask, default!);
    }

    private LibraDexConditionContinueOrEnd Add(
        LibraDexConditionOperatorKind operatorKind,
        TValue value,
        TValue? upperValue = default)
    {
        List<LibraDexConditionOperand> operands = new()
        {
            LibraDexConditionOperand.Value(valueKind),
            LibraDexConditionOperand.Value(offset),
            LibraDexConditionOperand.Value(length),
            LibraDexConditionOperand.Value(value!)
        };
        if (operatorKind is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo)
        {
            operands.Add(LibraDexConditionOperand.Value(upperValue!));
        }

        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            operands.ToArray(),
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Captures text comparison operators for one UTF-8 binary slice selected from a fixed-width byte-array index key.<br/>
/// The current bridge uses ordinal string semantics to avoid introducing folded-text or culture projection behavior into raw binary slices.<br/>
/// </summary>
public sealed class LibraDexBinaryStringSliceConditionOperator
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;
    private readonly LibraDexBinarySliceValueKind valueKind;
    private readonly int offset;
    private readonly int length;
    private readonly Encoding? encoding;

    internal LibraDexBinaryStringSliceConditionOperator(
        LibraDexConditionBuilder builder,
        LibraDexConditionIndexSelector indexSelector,
        LibraDexBinarySliceValueKind valueKind,
        int offset,
        int length,
        Encoding? encoding = null)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Binary string slice offset cannot be negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Binary string slice length must be positive.");
        }

        this.builder = builder;
        this.indexSelector = indexSelector;
        this.valueKind = valueKind;
        this.offset = offset;
        this.length = length;
        this.encoding = encoding;
    }

    /// <summary>
    /// Captures ordinal equality against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string value to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo, value);
    }

    /// <summary>
    /// Captures ordinal starts-with comparison against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string prefix to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith, value);
    }

    /// <summary>
    /// Captures ordinal contains comparison against a UTF-8 binary slice.<br/>
    /// </summary>
    /// <param name="value">The string fragment to compare with the selected slice.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value)
    {
        return Add(LibraDexConditionOperatorKind.BinaryTypedSliceContains, value);
    }

    private LibraDexConditionContinueOrEnd Add(LibraDexConditionOperatorKind operatorKind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Binary,
            operatorKind,
            CreateOperands(value),
            IgnoreCase: false,
            Culture: null));
    }

    private LibraDexConditionOperand[] CreateOperands(string value)
    {
        if (valueKind == LibraDexBinarySliceValueKind.CustomEncodingString)
        {
            return new[]
            {
                LibraDexConditionOperand.Value(valueKind),
                LibraDexConditionOperand.Value(offset),
                LibraDexConditionOperand.Value(length),
                LibraDexConditionOperand.Value(value),
                LibraDexConditionOperand.Value(encoding!)
            };
        }

        return new[]
        {
            LibraDexConditionOperand.Value(valueKind),
            LibraDexConditionOperand.Value(offset),
            LibraDexConditionOperand.Value(length),
            LibraDexConditionOperand.Value(value)
        };
    }
}

/// <summary>
/// Captures GUID-specific adopted condition operators for one selected LibraDex index.<br/>
/// Text-oriented GUID methods compile to encoded canonical-nibble predicates; exact GUID methods use the ordinary exact-key bridge.<br/>
/// </summary>
public sealed class LibraDexGuidConditionOperator : LibraDexConditionOperator<Guid>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly LibraDexConditionIndexSelector indexSelector;

    internal LibraDexGuidConditionOperator(LibraDexConditionBuilder builder, LibraDexConditionIndexSelector indexSelector)
        : base(builder, indexSelector, LibraDexConditionValueKind.Guid)
    {
        this.builder = builder;
        this.indexSelector = indexSelector;
    }

    /// <summary>
    /// Captures a GUID text or segment starts-with condition.<br/>
    /// The descriptor materializes as an encoded canonical-nibble predicate over stored GUID bytes.<br/>
    /// </summary>
    /// <param name="value">The GUID text or segment prefix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(string value)
    {
        return AddGuidPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.StartsWith));
    }

    /// <summary>
    /// Captures a GUID byte-domain starts-with condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte prefix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd StartsWith(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.StartsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.StartsWith));
    }

    /// <summary>
    /// Captures exact GUID equality from text input.<br/>
    /// The text is parsed once at descriptor creation so execution can use the ordinary exact-key primitive.<br/>
    /// </summary>
    /// <param name="value">The GUID text to match.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EqualTo(string value)
    {
        return EqualTo(Guid.Parse(value));
    }

    /// <summary>
    /// Captures exact GUID inequality from text input.<br/>
    /// The text is parsed once at descriptor creation so execution can use the ordered exact-key exclusion bridge.<br/>
    /// </summary>
    /// <param name="value">The GUID text to exclude.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd NotEqualTo(string value)
    {
        return NotEqualTo(Guid.Parse(value));
    }

    /// <summary>
    /// Captures a GUID text ends-with condition.<br/>
    /// </summary>
    /// <param name="value">The GUID text suffix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(string value)
    {
        return AddGuidPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.EndsWith));
    }

    /// <summary>
    /// Captures a GUID byte-domain ends-with condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte suffix.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd EndsWith(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.EndsWith, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.EndsWith));
    }

    /// <summary>
    /// Captures a GUID text contains condition.<br/>
    /// </summary>
    /// <param name="value">The contained GUID text fragment.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(string value)
    {
        return AddGuidPattern(LibraDexConditionOperatorKind.Contains, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.Contains));
    }

    /// <summary>
    /// Captures a GUID byte-domain contains condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and the descriptor stores a compiled nibble predicate rather than text.<br/>
    /// </summary>
    /// <param name="value">The stored GUID byte segment.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd Contains(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddGuidPattern(LibraDexConditionOperatorKind.Contains, LibraDexGuidPatternPredicate.Create(value, LibraDexGuidPatternMode.Contains));
    }

    /// <summary>
    /// Captures a GUID text pattern condition.<br/>
    /// </summary>
    /// <param name="pattern">The GUID text pattern descriptor.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(string pattern)
    {
        return AddGuidPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexGuidPatternPredicate.Create(pattern, LibraDexGuidPatternMode.MatchesPattern));
    }

    /// <summary>
    /// Captures a full 16-byte GUID byte-domain pattern condition.<br/>
    /// The byte order is the stored GUID byte order produced by `Guid.TryWriteBytes`, and every nibble is compared.<br/>
    /// </summary>
    /// <param name="pattern">The full stored GUID byte pattern.</param>
    /// <returns>A continuation for adding more clauses or ending the condition.</returns>
    public LibraDexConditionContinueOrEnd MatchesPattern(byte[] pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return AddGuidPattern(LibraDexConditionOperatorKind.MatchesPattern, LibraDexGuidPatternPredicate.Create(pattern, LibraDexGuidPatternMode.MatchesPattern));
    }

    private LibraDexConditionContinueOrEnd AddGuidPattern(LibraDexConditionOperatorKind operatorKind, LibraDexGuidPatternPredicate predicate)
    {
        return builder.AddLeaf(new LibraDexConditionLeafDescriptor(
            indexSelector,
            LibraDexConditionValueKind.Guid,
            operatorKind,
            new[] { LibraDexConditionOperand.Value(predicate) },
            IgnoreCase: false,
            Culture: null));
    }
}

/// <summary>
/// Continues or ends an adopted condition after one leaf or group has been captured.<br/>
/// The uppercase `AND` and `OR` members intentionally preserve Abraxas grammar shape for low-friction copy-then-modify adoption.<br/>
/// </summary>
public sealed class LibraDexConditionContinueOrEnd
{
    private readonly LibraDexConditionBuilder builder;

    internal LibraDexConditionContinueOrEnd(LibraDexConditionBuilder builder)
    {
        this.builder = builder;
    }

    /// <summary>
    /// Adds an intersection operator and starts the next clause.<br/>
    /// </summary>
    public LibraDexConditionClause AND
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.And);
            return new LibraDexConditionClause(builder);
        }
    }

    /// <summary>
    /// Adds a union operator and starts the next clause.<br/>
    /// </summary>
    public LibraDexConditionClause OR
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.Or);
            return new LibraDexConditionClause(builder);
        }
    }

    /// <summary>
    /// Completes the adopted condition descriptor.<br/>
    /// The returned condition can be inspected as leaves or materialized by resolving index names to opened LibraDex indexes.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => builder.End();

    /// <summary>
    /// Convenience alias for <see cref="EndCondition"/> that matches Abraxas' short `ec` alias.<br/>
    /// </summary>
    public LibraDexConditionEndCondition ec => EndCondition;
}

internal enum LibraDexConditionNodeKind
{
    Leaf,
    And,
    Or
}

internal sealed class LibraDexConditionBuilder
{
    private LibraDexConditionNode? current;
    private LibraDexConditionNodeKind? pendingOperation;

    internal LibraDexConditionBuilder(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        Group = group;
    }

    internal string Group { get; }

    internal LibraDexConditionContinueOrEnd AddLeaf(LibraDexConditionLeafDescriptor leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return AddNode(LibraDexConditionNode.Leaf(leaf));
    }

    internal LibraDexConditionContinueOrEnd AddGroup(LibraDexConditionEndCondition groupCondition)
    {
        if (!string.Equals(Group, groupCondition.Group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A LibraDex condition group can only contain child groups from the same identity group.");
        }

        return AddNode(groupCondition.GetRoot());
    }

    internal void SetNextOperation(LibraDexConditionNodeKind operation)
    {
        if (operation == LibraDexConditionNodeKind.Leaf)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot start with a composition operator.");
        }

        pendingOperation = operation;
    }

    internal LibraDexConditionEndCondition End()
    {
        if (current is null)
        {
            throw new InvalidOperationException("A LibraDex condition must contain at least one clause.");
        }

        if (pendingOperation is not null)
        {
            throw new InvalidOperationException("A LibraDex condition cannot end with a composition operator.");
        }

        return new LibraDexConditionEndCondition(Group, current);
    }

    private LibraDexConditionContinueOrEnd AddNode(LibraDexConditionNode node)
    {
        if (current is null)
        {
            current = node;
        }
        else
        {
            LibraDexConditionNodeKind operation = pendingOperation
                ?? throw new InvalidOperationException("A LibraDex condition requires AND or OR between clauses.");
            current = LibraDexConditionNode.Compose(operation, current, node);
            pendingOperation = null;
        }

        return new LibraDexConditionContinueOrEnd(this);
    }
}

internal sealed class LibraDexConditionNode
{
    private readonly LibraDexConditionLeafDescriptor? leaf;
    private readonly LibraDexConditionNode? left;
    private readonly LibraDexConditionNode? right;

    private LibraDexConditionNode(
        LibraDexConditionNodeKind kind,
        LibraDexConditionLeafDescriptor? leaf,
        LibraDexConditionNode? left,
        LibraDexConditionNode? right)
    {
        Kind = kind;
        this.leaf = leaf;
        this.left = left;
        this.right = right;
    }

    internal LibraDexConditionNodeKind Kind { get; }

    internal static LibraDexConditionNode Leaf(LibraDexConditionLeafDescriptor leaf)
    {
        return new LibraDexConditionNode(LibraDexConditionNodeKind.Leaf, leaf, left: null, right: null);
    }

    internal static LibraDexConditionNode Compose(
        LibraDexConditionNodeKind kind,
        LibraDexConditionNode left,
        LibraDexConditionNode right)
    {
        if (kind == LibraDexConditionNodeKind.Leaf)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new LibraDexConditionNode(kind, leaf: null, left, right);
    }

    internal IReadOnlyList<LibraDexConditionLeafDescriptor> GetLeaves()
    {
        List<LibraDexConditionLeafDescriptor> leaves = new();
        AddLeaves(leaves);
        return leaves;
    }

    internal LibraDexConditionNode Rewrite(Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafDescriptor> rewriteLeaf)
    {
        return Kind switch
        {
            LibraDexConditionNodeKind.Leaf => Leaf(rewriteLeaf(RequireLeaf())),
            LibraDexConditionNodeKind.And => Compose(LibraDexConditionNodeKind.And, RequireLeft().Rewrite(rewriteLeaf), RequireRight().Rewrite(rewriteLeaf)),
            LibraDexConditionNodeKind.Or => Compose(LibraDexConditionNodeKind.Or, RequireLeft().Rewrite(rewriteLeaf), RequireRight().Rewrite(rewriteLeaf)),
            _ => throw new InvalidOperationException($"Unsupported condition node kind {Kind}.")
        };
    }

    internal IIdentityCriterion Materialize(
        string group,
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?>? resolveProjectionIndex)
    {
        return Kind switch
        {
            LibraDexConditionNodeKind.Leaf => MaterializeLeaf(group, resolveIndex, resolveProjectionIndex),
            LibraDexConditionNodeKind.And => RequireLeft().Materialize(group, resolveIndex, resolveProjectionIndex).And(RequireRight().Materialize(group, resolveIndex, resolveProjectionIndex)),
            LibraDexConditionNodeKind.Or => RequireLeft().Materialize(group, resolveIndex, resolveProjectionIndex).Or(RequireRight().Materialize(group, resolveIndex, resolveProjectionIndex)),
            _ => throw new InvalidOperationException($"Unsupported condition node kind {Kind}.")
        };
    }

    private void AddLeaves(List<LibraDexConditionLeafDescriptor> leaves)
    {
        if (Kind == LibraDexConditionNodeKind.Leaf)
        {
            leaves.Add(RequireLeaf());
            return;
        }

        RequireLeft().AddLeaves(leaves);
        RequireRight().AddLeaves(leaves);
    }

    private IIdentityCriterion MaterializeLeaf(
        string group,
        Func<string, IIndex> resolveIndex,
        Func<LibraDexConditionLeafDescriptor, LibraDexConditionLeafClassification, IIndex?>? resolveProjectionIndex)
    {
        LibraDexConditionLeafDescriptor descriptor = RequireLeaf();
        IIndex index = resolveIndex(descriptor.IndexName);
        if (!string.Equals(index.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Resolved index '{descriptor.IndexName}' belongs to group '{index.Group}', not condition group '{group}'.");
        }

        LibraDexConditionLeafClassification classification = LibraDexConditionEndCondition.ClassifyResolvedLeaf(group, descriptor, index);
        if (classification.ExecutionClass == LibraDexConditionExecutionClass.ProjectionBacked)
        {
            if (resolveProjectionIndex is null)
            {
                throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a maintained {classification.ProjectionKind} projection bridge.");
            }

            IIndex? projectionIndex = resolveProjectionIndex(descriptor, classification);
            if (projectionIndex is not null)
            {
                return MaterializeProjectionLeaf(group, descriptor, classification, projectionIndex);
            }

            throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a maintained {classification.ProjectionKind} projection bridge, but the resolver did not return one.");
        }

        object?[] values = descriptor.Operands.Select(static operand => operand.GetValue()).ToArray();
        if (descriptor.ValueKind == LibraDexConditionValueKind.String &&
            RequiresManagedStringComparison(descriptor, index) &&
            IsStringComparisonOperator(descriptor.Operator))
        {
            return MaterializeStringComparisonLeaf(index, values, descriptor);
        }

        if (descriptor.ValueKind == LibraDexConditionValueKind.String &&
            RequiresManagedStringComparison(descriptor, index) &&
            IsStringMembershipOperator(descriptor.Operator))
        {
            return MaterializeStringMembershipLeaf(index, values, descriptor);
        }

        IIdentityCriterion criterion = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.All => CreateConditionLeaf(index, LibraDexCriteriaKind.All),
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(index, LibraDexCriteriaKind.Find, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(index, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.GreaterThan => CreateConditionLeaf(index, LibraDexCriteriaKind.After, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.GreaterOrEqual => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessThan => CreateConditionLeaf(index, LibraDexCriteriaKind.Before, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.LessOrEqual => CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrBefore, RequireValue(values, 0, descriptor)),
            LibraDexConditionOperatorKind.Between => CreateConditionLeaf(index, LibraDexCriteriaKind.Between, RequireValue(values, 0, descriptor), RequireValue(values, 1, descriptor)),
            LibraDexConditionOperatorKind.NotBetween => CreateOrderedRangeExclusionLeaf(index, RequireValue(values, 0, descriptor), RequireValue(values, 1, descriptor)),
            LibraDexConditionOperatorKind.BitAndEqualTo => MaterializeBitmaskLeaf(index, values, descriptor, LibraDexBitmaskComparisonMode.EqualTo),
            LibraDexConditionOperatorKind.BitAndNotEqualTo => MaterializeBitmaskLeaf(index, values, descriptor, LibraDexBitmaskComparisonMode.NotEqualTo),
            LibraDexConditionOperatorKind.StartsWith when descriptor.ValueKind == LibraDexConditionValueKind.String && !RequiresManagedStringComparison(descriptor, index) => MaterializeExactStringPrefixLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern when descriptor.ValueKind == LibraDexConditionValueKind.Guid => MaterializeGuidPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern or
            LibraDexConditionOperatorKind.BinarySliceEqual when descriptor.ValueKind == LibraDexConditionValueKind.Binary => MaterializeBinaryPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan or
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual or
            LibraDexConditionOperatorKind.BinaryTypedSliceLessThan or
            LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual or
            LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith or
            LibraDexConditionOperatorKind.BinaryTypedSliceContains or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo when descriptor.ValueKind == LibraDexConditionValueKind.Binary => MaterializeBinaryTypedSliceLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.CompositeMatch when descriptor.ValueKind == LibraDexConditionValueKind.Composite => MaterializeCompositeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern when descriptor.ValueKind == LibraDexConditionValueKind.String => MaterializeStringPatternLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.StartsWith or
            LibraDexConditionOperatorKind.EndsWith or
            LibraDexConditionOperatorKind.Contains or
            LibraDexConditionOperatorKind.MatchesPattern => throw new NotSupportedException($"Condition operator {descriptor.Operator} requires an explicit maintained projection bridge before it can materialize."),
            LibraDexConditionOperatorKind.InSet => CreateMembershipLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.NotInSet => CreateMembershipLeaf(index, values, descriptor).Not(),
            LibraDexConditionOperatorKind.YearEqualTo => MaterializeStructuredDateYearLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotEqualTo => MaterializeStructuredDateYearExclusionLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearIn => MaterializeStructuredDateYearInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotIn => MaterializeStructuredDateYearInLeaf(index, values, descriptor).Not(),
            LibraDexConditionOperatorKind.YearRange => MaterializeStructuredDateYearRangeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearNotRange => MaterializeStructuredDateYearRangeExclusionLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearOnOrAfter => MaterializeStructuredDateYearOnOrAfterLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearOnOrBefore => MaterializeStructuredDateYearOnOrBeforeLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonth => MaterializeStructuredDateYearMonthLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthDay => MaterializeStructuredDateYearMonthDayLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthIn => MaterializeStructuredDateYearMonthInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearMonthDayIn => MaterializeStructuredDateYearMonthDayInLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearInMonths => MaterializeStructuredDateYearInMonthsLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.YearQuarter => MaterializeStructuredDateYearQuarterLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsToday => MaterializeStructuredDateRelativeDayLeaf(index, DateTime.UtcNow.Date, descriptor),
            LibraDexConditionOperatorKind.IsYesterday => MaterializeStructuredDateRelativeDayLeaf(index, DateTime.UtcNow.Date.AddDays(-1), descriptor),
            LibraDexConditionOperatorKind.IsInLastDays => MaterializeStructuredDateInLastDaysLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsInLastHours => MaterializeStructuredDateInLastHoursLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.IsInLastMinutes => MaterializeStructuredDateInLastMinutesLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.MonthEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Set(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthNotIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Set(values, 0, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.MonthRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Range(values, descriptor))), descriptor),
            LibraDexConditionOperatorKind.MonthNotRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(RequireInt32Range(values, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.DayEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayIn => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Set(values, 0, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Range(values, descriptor))), descriptor),
            LibraDexConditionOperatorKind.DayNotRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(RequireInt32Range(values, descriptor), negate: true)), descriptor),
            LibraDexConditionOperatorKind.MonthDay => MaterializeStructuredDateMonthDayComponentLeaf(index, values, descriptor),
            LibraDexConditionOperatorKind.QuarterEqualTo => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarter(RequireInt32(values, 0, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.InQuarter => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarter(RequireInt32(values, 0, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.InQuarterRange => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(MonthsForQuarterRange(RequireInt32(values, 0, descriptor), RequireInt32(values, 1, descriptor), descriptor))), descriptor),
            LibraDexConditionOperatorKind.IsQuarterStart => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(new[] { 1, 4, 7, 10 }), DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsQuarterEnd => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(requireLastDayOfMonth: true, MonthTest(new[] { 3, 6, 9, 12 })), descriptor),
            LibraDexConditionOperatorKind.IsHalfYearStart => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(new[] { 1, 7 }), DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsHalfYearEnd => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(requireLastDayOfMonth: true, MonthTest(new[] { 6, 12 })), descriptor),
            LibraDexConditionOperatorKind.IsFirstOfMonth => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayTest(1)), descriptor),
            LibraDexConditionOperatorKind.IsLastOfMonth => MaterializeStructuredDateComponentLeaf(index, new LibraDexStructuredComponentPredicate(Array.Empty<LibraDexStructuredComponentTest>(), requireLastDayOfMonth: true), descriptor),
            LibraDexConditionOperatorKind.IsWeekend => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayOfWeekTest(new[] { 0, 6 })), descriptor),
            LibraDexConditionOperatorKind.IsWeekday => MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(DayOfWeekTest(new[] { 1, 2, 3, 4, 5 })), descriptor),
            LibraDexConditionOperatorKind.IsMorning => MaterializeStructuredTimeOfDayLeaf(index, 5, 11, descriptor),
            LibraDexConditionOperatorKind.IsAfternoon => MaterializeStructuredTimeOfDayLeaf(index, 12, 16, descriptor),
            LibraDexConditionOperatorKind.IsEvening => MaterializeStructuredTimeOfDayLeaf(index, 17, 21, descriptor),
            LibraDexConditionOperatorKind.IsNight => MaterializeStructuredNightLeaf(index, descriptor),
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not connected to the LibraDex criteria bridge yet.")
        };

        return criterion;
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf without using the purged public criteria-builder surface.<br/>
    /// This is an adapter-private bridge from adopted condition operators to the current low-level identity primitive request shape; it validates runtime key operands against the resolved index before execution.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="values">The already materialized primitive operands.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateConditionLeaf(IIndex index, LibraDexCriteriaKind criteriaKind, params object?[] values)
    {
        object?[] validatedValues = values.Select(value => ValidateConditionLeafValue(index, criteriaKind, value)).ToArray();
        return LibraDexIdentityCriterion.Leaf(index, criteriaKind, CreateConditionDiagnostics(criteriaKind), validatedValues);
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf against a hidden projection index while preserving the owning logical group.<br/>
    /// This is used when the projection index is intentionally not listed as a normal group member but still executes over the same identity values.<br/>
    /// </summary>
    /// <param name="group">The logical identity group for the condition.</param>
    /// <param name="index">The physical projection index that owns the primitive route.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="values">The already materialized primitive operands.</param>
    /// <returns>An identity criterion leaf for the condition executor.</returns>
    private static IIdentityCriterion CreateProjectionConditionLeaf(string group, IIndex index, LibraDexCriteriaKind criteriaKind, params object?[] values)
    {
        object?[] validatedValues = values.Select(value => ValidateConditionLeafValue(index, criteriaKind, value)).ToArray();
        return LibraDexIdentityCriterion.Leaf(group, index, criteriaKind, CreateConditionDiagnostics(criteriaKind), validatedValues);
    }

    /// <summary>
    /// Creates an ordered-key exclusion for one exact value without falling back to a complement over the full identity universe.<br/>
    /// The bridge uses the two ordered extents that can contain valid matches: keys before the excluded value and keys after the excluded value.<br/>
    /// This keeps `NotEqualTo` tied to range-reader primitives and exposes the physical need as ordered extent union rather than hidden post-filtering.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="value">The exact key value to exclude.</param>
    /// <returns>An identity criterion union over the lower and upper key extents.</returns>
    private static IIdentityCriterion CreateOrderedPointExclusionLeaf(IIndex index, object value)
    {
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Before, value)
            .Or(CreateConditionLeaf(index, LibraDexCriteriaKind.After, value));
    }

    /// <summary>
    /// Creates an ordered-key exclusion for one inclusive range without falling back to a complement over the full identity universe.<br/>
    /// The bridge maps `not between lower and upper` to the union of keys before the lower boundary and keys after the upper boundary.<br/>
    /// This is the efficient extent shape forced by the condition permutation and keeps range exclusion independent from old direct retrieval facades.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="lower">The inclusive lower key boundary to exclude.</param>
    /// <param name="upper">The inclusive upper key boundary to exclude.</param>
    /// <returns>An identity criterion union over the lower and upper key extents.</returns>
    private static IIdentityCriterion CreateOrderedRangeExclusionLeaf(IIndex index, object lower, object upper)
    {
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Before, lower)
            .Or(CreateConditionLeaf(index, LibraDexCriteriaKind.After, upper));
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf for a same-index multi-range request.<br/>
    /// This is the first primitive shape derived from condition permutations rather than inherited from the old direct criteria surface.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the ranges.</param>
    /// <param name="ranges">The inclusive key ranges to execute in order.</param>
    /// <returns>An identity criterion leaf for condition-driven multi-range execution.</returns>
    private static IIdentityCriterion CreateConditionMultiRangeLeaf(IIndex index, IEnumerable<LibraDexIdentityKeyRange> ranges)
    {
        LibraDexIdentityKeyRange[] captured = ranges.ToArray();
        if (captured.Length == 0)
        {
            throw new InvalidOperationException("Condition multi-range retrieval requires at least one key range.");
        }

        for (int i = 0; i < captured.Length; i++)
        {
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Between, captured[i].LowerKey);
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Between, captured[i].UpperKey);
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.MultiRange, CreateConditionDiagnostics(LibraDexCriteriaKind.MultiRange), captured);
    }

    /// <summary>
    /// Creates one internal condition retrieval leaf for membership without discarding the caller's managed set object.<br/>
    /// The method validates every member against the resolved key type, then passes the original enumerable as a single primitive operand so the executor can consume sets, prepared sets, or arrays without another descriptor-layer copy.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the membership leaf.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for membership execution.</returns>
    private static IIdentityCriterion CreateMembershipLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        object source = RequireValue(values, 0, descriptor);
        if (source is LibraDexPreparedObjectSet prepared)
        {
            if (prepared.KeyType != index.KeyType)
            {
                throw new ArgumentException($"Prepared membership key type {prepared.KeyType.FullName} does not match index key type {index.KeyType.FullName}.");
            }

            return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), prepared);
        }

        IEnumerable enumerable = source as IEnumerable
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable membership operand.");
        if (source is string)
        {
            throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable membership operand.");
        }

        foreach (object? item in enumerable)
        {
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Find, RequireNonNullMembershipValue(item, descriptor));
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), source);
    }

    /// <summary>
    /// Materializes a numeric bitmask condition as an explicit compact-index scan predicate.<br/>
    /// The predicate normalizes the mask and comparison value once against the resolved key type, then the primitive executor applies `(key &amp; mask)` to decoded scalar keys.<br/>
    /// </summary>
    /// <param name="index">The resolved logical scalar index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <param name="mode">Whether the masked result must equal or not equal the comparison operand.</param>
    /// <returns>An identity criterion leaf for bitmask execution.</returns>
    private static IIdentityCriterion MaterializeBitmaskLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexBitmaskComparisonMode mode)
    {
        if (descriptor.ValueKind != LibraDexConditionValueKind.Numeric)
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} can only materialize against numeric scalar indexes.");
        }

        object mask = RequireValue(values, 0, descriptor);
        object compareValue = RequireValue(values, 1, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Bitmask, LibraDexBitmaskPredicate.Create(index.KeyType, mask, compareValue, mode));
    }

    /// <summary>
    /// Creates one projection-backed membership leaf after validating transformed projection keys individually.<br/>
    /// Projection membership receives a generated enumerable such as `byte[][]` or `string[]`; validating the collection itself would confuse the enumerable container with one key, so this helper validates each member while preserving the generated collection as the primitive operand.<br/>
    /// </summary>
    /// <param name="index">The resolved projection index that owns the membership leaf.</param>
    /// <param name="source">The transformed projection-key enumerable.</param>
    /// <returns>An identity criterion leaf for projection membership execution.</returns>
    private static IIdentityCriterion CreateProjectionMembershipLeaf(IIndex index, object source)
    {
        IEnumerable enumerable = source as IEnumerable
            ?? throw new InvalidOperationException("Projection membership requires an enumerable projection-key operand.");
        if (source is string)
        {
            throw new InvalidOperationException("Projection membership requires an enumerable projection-key operand.");
        }

        foreach (object? item in enumerable)
        {
            _ = ValidateConditionLeafValue(index, LibraDexCriteriaKind.Find, item ?? throw new InvalidOperationException("Projection membership does not allow null keys."));
        }

        return LibraDexIdentityCriterion.Leaf(index, LibraDexCriteriaKind.InSet, CreateConditionDiagnostics(LibraDexCriteriaKind.InSet), source);
    }

    /// <summary>
    /// Creates diagnostics for an internal condition retrieval leaf.<br/>
    /// The diagnostics describe the current execution bridge only; they are not public query grammar and should not drive future condition-builder terminology.<br/>
    /// </summary>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <returns>The diagnostics attached to the generated leaf.</returns>
    private static LibraDexQueryDiagnostics CreateConditionDiagnostics(LibraDexCriteriaKind criteriaKind)
    {
        LibraDexExecutionKind executionKind = criteriaKind switch
        {
            LibraDexCriteriaKind.In or
            LibraDexCriteriaKind.InSet or
            LibraDexCriteriaKind.MultiRange => LibraDexExecutionKind.Projection,
            LibraDexCriteriaKind.StructuredComponent => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.GuidPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.BinaryPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.BinaryTypedSlice => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.StringPattern => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.Bitmask => LibraDexExecutionKind.Scan,
            LibraDexCriteriaKind.CompositeMatch => LibraDexExecutionKind.FastPath,
            _ => LibraDexExecutionKind.FastPath
        };

        return new LibraDexQueryDiagnostics(executionKind);
    }

    /// <summary>
    /// Validates one internal condition retrieval operand against the resolved index key type.<br/>
    /// Membership operands are expanded before this helper is called, so every non-null value should be a concrete key value except opaque pattern descriptors for future projection-backed pattern branches.<br/>
    /// </summary>
    /// <param name="index">The resolved logical index that owns the leaf.</param>
    /// <param name="criteriaKind">The internal primitive request kind.</param>
    /// <param name="value">The operand value to validate.</param>
    /// <returns>The validated value.</returns>
    private static object? ValidateConditionLeafValue(IIndex index, LibraDexCriteriaKind criteriaKind, object? value)
    {
        if (value is null || criteriaKind == LibraDexCriteriaKind.All)
        {
            return value;
        }

        if (criteriaKind == LibraDexCriteriaKind.StructuredComponent)
        {
            return value is LibraDexStructuredComponentPredicate
                ? value
                : throw new ArgumentException("Structured component conditions require a compiled structured component predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.GuidPattern)
        {
            return value is LibraDexGuidPatternPredicate
                ? value
                : throw new ArgumentException("GUID pattern conditions require a compiled GUID pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.BinaryPattern)
        {
            return value is LibraDexBinaryPatternPredicate
                ? value
                : throw new ArgumentException("Binary pattern conditions require a compiled binary pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.BinaryTypedSlice)
        {
            return value is LibraDexBinaryTypedSlicePredicate
                ? value
                : throw new ArgumentException("Binary typed-slice conditions require a compiled binary typed-slice predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.StringPattern)
        {
            return value is LibraDexStringPatternPredicate
                ? value
                : throw new ArgumentException("String pattern conditions require a compiled string pattern predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.Bitmask)
        {
            return value is LibraDexBitmaskPredicate
                ? value
                : throw new ArgumentException("Bitmask conditions require a compiled bitmask predicate.");
        }

        if (criteriaKind == LibraDexCriteriaKind.CompositeMatch)
        {
            return value is LibraDexCompositePredicate
                ? value
                : throw new ArgumentException("Composite match conditions require a compiled composite predicate.");
        }

        if (!index.KeyType.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Runtime key type {value.GetType().FullName} does not match index key type {index.KeyType.FullName}.");
        }

        return value;
    }

    /// <summary>
    /// Determines whether the resolved key type preserves the component required by a structured component materializer.<br/>
    /// This duplicate of the classifier-side support check keeps materialization defensive when descriptors are generated without first calling classification.<br/>
    /// </summary>
    /// <param name="keyType">The resolved index key type.</param>
    /// <param name="operatorKind">The condition operator to inspect.</param>
    /// <returns><see langword="true"/> when the key type can execute the operator without projection lookup.</returns>
    private static bool SupportsStructuredComponentOperator(Type keyType, LibraDexConditionOperatorKind operatorKind)
    {
        if (keyType == typeof(TimeOnly))
        {
            return operatorKind is
                LibraDexConditionOperatorKind.IsMorning or
                LibraDexConditionOperatorKind.IsAfternoon or
                LibraDexConditionOperatorKind.IsEvening or
                LibraDexConditionOperatorKind.IsNight;
        }

        bool isDateLike = keyType == typeof(DateTime) ||
            keyType == typeof(DateTimeOffset) ||
            keyType == typeof(DateOnly);
        if (!isDateLike)
        {
            return false;
        }

        bool requiresHour = operatorKind is
            LibraDexConditionOperatorKind.IsMorning or
            LibraDexConditionOperatorKind.IsAfternoon or
            LibraDexConditionOperatorKind.IsEvening or
            LibraDexConditionOperatorKind.IsNight;
        return !requiresHour || keyType != typeof(DateOnly);
    }

    /// <summary>
    /// Materializes a GUID partial/pattern branch as an encoded canonical-nibble predicate.<br/>
    /// The predicate reads the stored 16-byte GUID key directly and applies the Abraxas-compatible comparison mode without converting every candidate to text.<br/>
    /// </summary>
    /// <param name="index">The logical GUID index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for GUID pattern execution.</returns>
    private static IIdentityCriterion MaterializeGuidPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(Guid))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a GUID pattern predicate against index key type {index.KeyType.FullName}.");
        }

        object operand = RequireValue(values, 0, descriptor);
        LibraDexGuidPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexGuidPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexGuidPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexGuidPatternMode.Contains,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexGuidPatternMode.MatchesPattern,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a GUID pattern operator.")
        };
        LibraDexGuidPatternPredicate predicate = operand switch
        {
            LibraDexGuidPatternPredicate compiled => compiled,
            string pattern => LibraDexGuidPatternPredicate.Create(pattern, mode),
            byte[] bytes => LibraDexGuidPatternPredicate.Create(bytes, mode),
            _ => throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a GUID pattern operand.")
        };
        return CreateConditionLeaf(index, LibraDexCriteriaKind.GuidPattern, predicate);
    }

    /// <summary>
    /// Materializes a raw binary byte-slice branch as an encoded key-byte predicate.<br/>
    /// The predicate reads fixed-width byte-array key bytes directly and applies the Abraxas-style slice intent without decoding every candidate key to a caller-facing array.<br/>
    /// </summary>
    /// <param name="index">The logical binary index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for binary byte-pattern execution.</returns>
    private static IIdentityCriterion MaterializeBinaryPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a binary byte predicate against index key type {index.KeyType.FullName}.");
        }

        object operand = RequireValue(values, descriptor.Operator == LibraDexConditionOperatorKind.BinarySliceEqual && values.Length > 1 ? 1 : 0, descriptor);
        LibraDexBinaryPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexBinaryPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexBinaryPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexBinaryPatternMode.Contains,
            LibraDexConditionOperatorKind.BinarySliceEqual => LibraDexBinaryPatternMode.SliceEqual,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexBinaryPatternMode.MatchesPattern,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a binary pattern operator.")
        };
        if (operand is LibraDexBinaryPatternPredicate compiled)
        {
            return CreateConditionLeaf(index, LibraDexCriteriaKind.BinaryPattern, compiled);
        }

        byte[] pattern = operand as byte[]
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a binary byte pattern operand.");
        int offset = descriptor.Operator == LibraDexConditionOperatorKind.BinarySliceEqual && values.Length > 1
            ? RequireInt32(values, 0, descriptor)
            : 0;
        return CreateConditionLeaf(index, LibraDexCriteriaKind.BinaryPattern, LibraDexBinaryPatternPredicate.Create(mode, pattern, offset));
    }

    /// <summary>
    /// Materializes a string pattern branch as an exact-index candidate scan with residual text comparison.<br/>
    /// Maintained projections still win when available; this fallback honors developer intent by scanning compact indexed keys rather than source records.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for string-pattern execution.</returns>
    private static IIdentityCriterion MaterializeStringPatternLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.StartsWith => LibraDexStringPatternMode.StartsWith,
            LibraDexConditionOperatorKind.EndsWith => LibraDexStringPatternMode.EndsWith,
            LibraDexConditionOperatorKind.Contains => LibraDexStringPatternMode.Contains,
            LibraDexConditionOperatorKind.MatchesPattern => LibraDexStringPatternMode.MatchesPattern,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string pattern operator.")
        };
        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, RequireString(values, 0, descriptor), ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes a case-sensitive string prefix condition as a direct ordered range over the exact string index.<br/>
    /// This keeps exact `StartsWith` on the cheapest encoded-key path instead of using the residual `StringPattern` fallback that exists for scan-backed text conditions.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf over the exact string key range.</returns>
    private static IIdentityCriterion MaterializeExactStringPrefixLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as an exact string prefix range against index key type {index.KeyType.FullName}.");
        }

        string value = RequireString(values, 0, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Between, value, value + '\uffff');
    }

    /// <summary>
    /// Determines whether an adopted string operator needs comparison semantics instead of text-pattern semantics.<br/>
    /// The check is used only after maintained projection routing has had first refusal, so a positive answer means the exact string index must be scanned or bounded and residual-compared.<br/>
    /// </summary>
    /// <param name="operatorKind">The adopted condition operator.</param>
    /// <returns><see langword="true"/> when the operator compares whole string values.</returns>
    private static bool IsStringComparisonOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind is
            LibraDexConditionOperatorKind.EqualTo or
            LibraDexConditionOperatorKind.NotEqualTo or
            LibraDexConditionOperatorKind.GreaterThan or
            LibraDexConditionOperatorKind.GreaterOrEqual or
            LibraDexConditionOperatorKind.LessThan or
            LibraDexConditionOperatorKind.LessOrEqual or
            LibraDexConditionOperatorKind.Between or
            LibraDexConditionOperatorKind.NotBetween;
    }

    /// <summary>
    /// Determines whether an adopted string operator evaluates membership over a value set.<br/>
    /// The check is used after projection routing, so it identifies the exact-index residual fallback for no-case membership conditions.<br/>
    /// </summary>
    /// <param name="operatorKind">The adopted condition operator.</param>
    /// <returns><see langword="true"/> when the operator is a membership operator.</returns>
    private static bool IsStringMembershipOperator(LibraDexConditionOperatorKind operatorKind)
    {
        return operatorKind is LibraDexConditionOperatorKind.InSet or LibraDexConditionOperatorKind.NotInSet;
    }

    private static bool RequiresManagedStringComparison(LibraDexConditionLeafDescriptor descriptor, IIndex index)
    {
        if (descriptor.IgnoreCase || !string.IsNullOrEmpty(descriptor.Culture) || descriptor.StringComparisonPolicy is not null)
        {
            return true;
        }

        return index is ILibraDexStringComparisonPolicyProvider provider &&
            provider.StringComparisonPolicy is { Kind: not LibraDexStringComparisonPolicyKind.Invariant };
    }

    /// <summary>
    /// Materializes a case-insensitive string comparison as an exact-index residual predicate when no maintained sort-key projection is available.<br/>
    /// Equality can still create bounded exact-key candidate ranges from simple case variants; ordered comparisons intentionally scan compact indexed keys and apply .NET culture-aware comparison to preserve correctness.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for scan-backed string comparison execution.</returns>
    private static IIdentityCriterion MaterializeStringComparisonLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string comparison predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => LibraDexStringPatternMode.EqualTo,
            LibraDexConditionOperatorKind.NotEqualTo => LibraDexStringPatternMode.NotEqualTo,
            LibraDexConditionOperatorKind.GreaterThan => LibraDexStringPatternMode.GreaterThan,
            LibraDexConditionOperatorKind.GreaterOrEqual => LibraDexStringPatternMode.GreaterOrEqual,
            LibraDexConditionOperatorKind.LessThan => LibraDexStringPatternMode.LessThan,
            LibraDexConditionOperatorKind.LessOrEqual => LibraDexStringPatternMode.LessOrEqual,
            LibraDexConditionOperatorKind.Between => LibraDexStringPatternMode.Between,
            LibraDexConditionOperatorKind.NotBetween => LibraDexStringPatternMode.NotBetween,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string comparison operator.")
        };
        string lower = RequireString(values, 0, descriptor);
        return descriptor.Operator is LibraDexConditionOperatorKind.Between or LibraDexConditionOperatorKind.NotBetween
            ? CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, lower, RequireString(values, 1, descriptor), ResolveStringComparisonPolicy(descriptor, index)))
            : CreateConditionLeaf(
                index,
                LibraDexCriteriaKind.StringPattern,
                LibraDexStringPatternPredicate.Create(mode, lower, ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes case-insensitive string membership as an exact-index residual predicate when no maintained sort-key projection is available.<br/>
    /// `InSet` may still narrow to exact-key case-variant candidates; `NotInSet` scans the compact exact index and keeps identities whose decoded keys do not match the membership set.<br/>
    /// </summary>
    /// <param name="index">The logical string index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for scan-backed string membership execution.</returns>
    private static IIdentityCriterion MaterializeStringMembershipLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(string))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a string membership predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexStringPatternMode mode = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.InSet => LibraDexStringPatternMode.InSet,
            LibraDexConditionOperatorKind.NotInSet => LibraDexStringPatternMode.NotInSet,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a string membership operator.")
        };
        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.StringPattern,
            LibraDexStringPatternPredicate.CreateSet(mode, RequireStringEnumerable(values, 0, descriptor), ResolveStringComparisonPolicy(descriptor, index)));
    }

    /// <summary>
    /// Materializes one routed composite condition as a single composite-match primitive.<br/>
    /// Keeping all tier predicates in one primitive lets the composite executor traverse mini-router tiers in order rather than intersecting independent part scans after materialization.<br/>
    /// </summary>
    /// <param name="index">The logical composite index selected by the condition.</param>
    /// <param name="values">The materialized composite part predicates.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for routed composite execution.</returns>
    private static IIdentityCriterion MaterializeCompositeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.LogicalShape?.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} requires a composite index shape.");
        }

        LibraDexCompositePartCriterion[] parts = values
            .Select(value => value as LibraDexCompositePartCriterion ?? throw new ArgumentException("Composite conditions require composite part predicates."))
            .ToArray();
        LibraDexCompositePredicate predicate = new(parts);
        predicate.ValidateAgainst(index.LogicalShape);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.CompositeMatch, predicate);
    }

    /// <summary>
    /// Resolves the managed string comparison policy for a materialized string leaf.<br/>
    /// Method-level policy wins, then the opened index's runtime policy, then legacy ignore-case/culture operands, then the LibraDex default.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf descriptor.</param>
    /// <param name="index">The resolved index.</param>
    /// <returns>The resolved managed string comparison policy.</returns>
    private static LibraDexStringComparisonPolicy ResolveStringComparisonPolicy(LibraDexConditionLeafDescriptor descriptor, IIndex index)
    {
        if (descriptor.StringComparisonPolicy is not null)
        {
            return descriptor.StringComparisonPolicy;
        }

        if (index is ILibraDexStringComparisonPolicyProvider provider &&
            provider.StringComparisonPolicy is not null)
        {
            return provider.StringComparisonPolicy;
        }

        return descriptor.IgnoreCase || !string.IsNullOrEmpty(descriptor.Culture)
            ? LibraDexStringComparisonPolicy.FromLegacy(descriptor.IgnoreCase, descriptor.Culture)
            : LibraDexStringComparisonPolicy.Default;
    }

    /// <summary>
    /// Materializes a typed binary-slice branch as an encoded key-byte predicate.<br/>
    /// The predicate interprets only the selected byte slice, using Abraxas-compatible little-endian numeric and date/time layouts where applicable.<br/>
    /// </summary>
    /// <param name="index">The logical binary index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for typed binary-slice execution.</returns>
    private static IIdentityCriterion MaterializeBinaryTypedSliceLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a binary typed-slice predicate against index key type {index.KeyType.FullName}.");
        }

        LibraDexBinarySliceValueKind valueKind = RequireEnum<LibraDexBinarySliceValueKind>(values, 0, descriptor);
        int offset = RequireInt32(values, 1, descriptor);
        int length = RequireInt32(values, 2, descriptor);
        object value = RequireValue(values, 3, descriptor);
        object? upperValue = descriptor.Operator is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo
            ? RequireValue(values, 4, descriptor)
            : null;
        Encoding? encoding = valueKind == LibraDexBinarySliceValueKind.CustomEncodingString
            ? RequireValue(values, descriptor.Operator is LibraDexConditionOperatorKind.BinaryTypedSliceBetween or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo or
                LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo ? 5 : 4, descriptor) as Encoding
                ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an Encoding operand for custom encoded binary string slices.")
            : null;
        LibraDexBinarySliceComparisonKind comparisonKind = descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.BinaryTypedSliceEqualTo => LibraDexBinarySliceComparisonKind.EqualTo,
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterThan => LibraDexBinarySliceComparisonKind.GreaterThan,
            LibraDexConditionOperatorKind.BinaryTypedSliceGreaterOrEqual => LibraDexBinarySliceComparisonKind.GreaterOrEqual,
            LibraDexConditionOperatorKind.BinaryTypedSliceLessThan => LibraDexBinarySliceComparisonKind.LessThan,
            LibraDexConditionOperatorKind.BinaryTypedSliceLessOrEqual => LibraDexBinarySliceComparisonKind.LessOrEqual,
            LibraDexConditionOperatorKind.BinaryTypedSliceBetween => LibraDexBinarySliceComparisonKind.Between,
            LibraDexConditionOperatorKind.BinaryTypedSliceStartsWith => LibraDexBinarySliceComparisonKind.StartsWith,
            LibraDexConditionOperatorKind.BinaryTypedSliceContains => LibraDexBinarySliceComparisonKind.Contains,
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndEqualTo => LibraDexBinarySliceComparisonKind.BitAndEqualTo,
            LibraDexConditionOperatorKind.BinaryTypedSliceBitAndNotEqualTo => LibraDexBinarySliceComparisonKind.BitAndNotEqualTo,
            _ => throw new NotSupportedException($"Condition operator {descriptor.Operator} is not a binary typed-slice operator.")
        };
        return CreateConditionLeaf(
            index,
            LibraDexCriteriaKind.BinaryTypedSlice,
            LibraDexBinaryTypedSlicePredicate.Create(valueKind, comparisonKind, offset, length, value, upperValue, encoding));
    }

    /// <summary>
    /// Creates one condition leaf for an encoded structured date/time component predicate.<br/>
    /// This bridge is used when a condition is not a contiguous ordered extent but the requested component is directly addressable in the packed scalar key by shift-and-mask.<br/>
    /// </summary>
    /// <param name="index">The logical structured date/time index selected by the condition.</param>
    /// <param name="predicate">The compiled component predicate.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for structured component execution.</returns>
    private static IIdentityCriterion MaterializeStructuredDateComponentLeaf(
        IIndex index,
        LibraDexStructuredComponentPredicate predicate,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (!SupportsStructuredComponentOperator(index.KeyType, descriptor.Operator))
        {
            throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize as a structured component predicate against index key type {index.KeyType.FullName}.");
        }

        return CreateConditionLeaf(index, LibraDexCriteriaKind.StructuredComponent, predicate.WithEncoding(index.DateTimeKeyEncoding));
    }

    /// <summary>
    /// Materializes a month/day component tuple across all years as two packed-field tests.<br/>
    /// The resulting primitive reads month and day directly from the structured scalar key and does not generate one range per year.<br/>
    /// </summary>
    /// <param name="index">The logical structured date/time index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion leaf for the month/day component predicate.</returns>
    private static IIdentityCriterion MaterializeStructuredDateMonthDayComponentLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        (int month, int day) = RequireMonthDay(values, 0, descriptor);
        return MaterializeStructuredDateComponentLeaf(index, CreateComponentPredicate(MonthTest(month), DayTest(day)), descriptor);
    }

    /// <summary>
    /// Creates a structured component predicate from tests that must all match.<br/>
    /// The predicate is a compiled physical payload for the condition bridge rather than a public query grammar object.<br/>
    /// </summary>
    /// <param name="tests">The component tests to apply.</param>
    /// <returns>The compiled structured component predicate.</returns>
    private static LibraDexStructuredComponentPredicate CreateComponentPredicate(params LibraDexStructuredComponentTest[] tests)
    {
        return new LibraDexStructuredComponentPredicate(tests);
    }

    /// <summary>
    /// Creates a structured component predicate from tests plus an optional last-day-of-month requirement.<br/>
    /// The last-day requirement is evaluated from packed year, month, and day fields and avoids CLR date reconstruction.<br/>
    /// </summary>
    /// <param name="requireLastDayOfMonth">Whether the predicate also requires the encoded day to be the last valid day of its month.</param>
    /// <param name="tests">The component tests to apply before the last-day check.</param>
    /// <returns>The compiled structured component predicate.</returns>
    private static LibraDexStructuredComponentPredicate CreateComponentPredicate(bool requireLastDayOfMonth, params LibraDexStructuredComponentTest[] tests)
    {
        return new LibraDexStructuredComponentPredicate(tests, requireLastDayOfMonth);
    }

    /// <summary>
    /// Creates a month component test for one allowed month.<br/>
    /// </summary>
    /// <param name="month">The allowed month component.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest(int month)
    {
        ValidateMonth(month, descriptor: null);
        return MonthTest(new[] { month });
    }

    /// <summary>
    /// Creates a month component test for a set of allowed months.<br/>
    /// </summary>
    /// <param name="months">The allowed month components.</param>
    /// <param name="negate">Whether to match months outside the supplied set.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest(IEnumerable<int> months, bool negate = false)
    {
        return new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Month, BuildComponentBits(months, 1, 12, "month"), negate);
    }

    /// <summary>
    /// Creates a month component test from an inclusive component range.<br/>
    /// </summary>
    /// <param name="range">The inclusive component range.</param>
    /// <param name="negate">Whether to match months outside the supplied range.</param>
    /// <returns>The compiled month component test.</returns>
    private static LibraDexStructuredComponentTest MonthTest((int start, int end) range, bool negate = false)
    {
        return MonthTest(EnumerateInclusive(range.start, range.end), negate);
    }

    /// <summary>
    /// Creates a day-of-month component test for one allowed day.<br/>
    /// </summary>
    /// <param name="day">The allowed day component.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest(int day)
    {
        ValidateDay(day, descriptor: null);
        return DayTest(new[] { day });
    }

    /// <summary>
    /// Creates a day-of-month component test for a set of allowed days.<br/>
    /// </summary>
    /// <param name="days">The allowed day components.</param>
    /// <param name="negate">Whether to match days outside the supplied set.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest(IEnumerable<int> days, bool negate = false)
    {
        return new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Day, BuildComponentBits(days, 1, 31, "day"), negate);
    }

    /// <summary>
    /// Creates a day-of-month component test from an inclusive component range.<br/>
    /// </summary>
    /// <param name="range">The inclusive component range.</param>
    /// <param name="negate">Whether to match days outside the supplied range.</param>
    /// <returns>The compiled day component test.</returns>
    private static LibraDexStructuredComponentTest DayTest((int start, int end) range, bool negate = false)
    {
        return DayTest(EnumerateInclusive(range.start, range.end), negate);
    }

    /// <summary>
    /// Creates an hour component test from a set of allowed hours.<br/>
    /// </summary>
    /// <param name="hours">The allowed hour components.</param>
    /// <param name="negate">Whether to match hours outside the supplied set.</param>
    /// <returns>The compiled hour component test.</returns>
    private static LibraDexStructuredComponentTest HourTest(IEnumerable<int> hours, bool negate = false)
    {
        return new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.Hour, BuildComponentBits(hours, 0, 23, "hour"), negate);
    }

    /// <summary>
    /// Creates a day-of-week component test from a set of allowed Abraxas/DateTime day values.<br/>
    /// Sunday is 0 and Saturday is 6, matching <see cref="DayOfWeek"/> and the structured scalar layout.<br/>
    /// </summary>
    /// <param name="days">The allowed day-of-week components.</param>
    /// <returns>The compiled day-of-week component test.</returns>
    private static LibraDexStructuredComponentTest DayOfWeekTest(IEnumerable<int> days)
    {
        return new LibraDexStructuredComponentTest(LibraDexStructuredDateComponent.DayOfWeek, BuildComponentBits(days, 0, 6, "day-of-week"));
    }

    /// <summary>
    /// Builds a compact bit set for small-domain structured date/time component values.<br/>
    /// Each component value becomes one bit position so matching can use a single integer bit test after field extraction.<br/>
    /// </summary>
    /// <param name="values">The component values to allow.</param>
    /// <param name="minimum">The inclusive minimum component value.</param>
    /// <param name="maximum">The inclusive maximum component value.</param>
    /// <param name="name">The component name for diagnostics.</param>
    /// <returns>The compiled allowed-value bit set.</returns>
    private static ulong BuildComponentBits(IEnumerable<int> values, int minimum, int maximum, string name)
    {
        ulong bits = 0;
        foreach (int value in values)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"{name} component value {value} is outside {minimum} through {maximum}.");
            }

            bits |= 1UL << value;
        }

        if (bits == 0)
        {
            throw new ArgumentException($"At least one {name} component value is required.", nameof(values));
        }

        return bits;
    }

    /// <summary>
    /// Enumerates an inclusive integer component range after validating the lower/upper ordering.<br/>
    /// This helper keeps component range expansion explicit at bridge materialization time rather than hiding it inside query execution.<br/>
    /// </summary>
    /// <param name="start">The inclusive start value.</param>
    /// <param name="end">The inclusive end value.</param>
    /// <returns>The inclusive value sequence.</returns>
    private static IEnumerable<int> EnumerateInclusive(int start, int end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Component range end must be greater than or equal to start.");
        }

        for (int value = start; value <= end; value++)
        {
            yield return value;
        }
    }

    /// <summary>
    /// Reads two Int32 operands as an inclusive component range.<br/>
    /// Component range operators store their lower and upper values as adjacent operands.<br/>
    /// </summary>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The inclusive component range.</returns>
    private static (int start, int end) RequireInt32Range(object?[] values, LibraDexConditionLeafDescriptor descriptor)
    {
        int start = RequireInt32(values, 0, descriptor);
        int end = RequireInt32(values, 1, descriptor);
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires range end greater than or equal to start.");
        }

        return (start, end);
    }

    /// <summary>
    /// Reads the required month/day tuple payload from a component-only condition leaf.<br/>
    /// The tuple is used for same-day-every-year style predicates that should execute by masking month and day fields, not by generating ranges for every possible year.<br/>
    /// </summary>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The required month/day payload.</returns>
    private static (int month, int day) RequireMonthDay(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (values.Length > ordinal + 1 &&
            values[ordinal] is int monthOperand &&
            values[ordinal + 1] is int dayOperand)
        {
            ValidateMonth(monthOperand, descriptor);
            ValidateDay(dayOperand, descriptor);
            return (monthOperand, dayOperand);
        }

        object value = RequireValue(values, ordinal, descriptor);
        if (value is ValueTuple<int, int> tuple)
        {
            ValidateMonth(tuple.Item1, descriptor);
            ValidateDay(tuple.Item2, descriptor);
            return (tuple.Item1, tuple.Item2);
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a month/day tuple operand.");
    }

    /// <summary>
    /// Expands one quarter to its three month components.<br/>
    /// The result feeds the structured component primitive rather than an ordered range because quarter-only predicates repeat every year.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The month components in the quarter.</returns>
    private static IReadOnlyList<int> MonthsForQuarter(int quarter, LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateQuarter(quarter, descriptor);
        int firstMonth = ((quarter - 1) * 3) + 1;
        return new[] { firstMonth, firstMonth + 1, firstMonth + 2 };
    }

    /// <summary>
    /// Expands an inclusive quarter range to the represented month components.<br/>
    /// This preserves the condition's component-only meaning while exposing the physical mask predicate used by LibraDex.<br/>
    /// </summary>
    /// <param name="startQuarter">The inclusive starting quarter.</param>
    /// <param name="endQuarter">The inclusive ending quarter.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The month components in the quarter range.</returns>
    private static IReadOnlyList<int> MonthsForQuarterRange(int startQuarter, int endQuarter, LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateQuarter(startQuarter, descriptor);
        ValidateQuarter(endQuarter, descriptor);
        if (endQuarter < startQuarter)
        {
            throw new ArgumentOutOfRangeException(nameof(endQuarter), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires ending quarter greater than or equal to starting quarter.");
        }

        List<int> months = new();
        for (int quarter = startQuarter; quarter <= endQuarter; quarter++)
        {
            months.AddRange(MonthsForQuarter(quarter, descriptor));
        }

        return months;
    }

    /// <summary>
    /// Materializes a structured date `YearEqualTo` leaf as an inclusive range over the selected logical index.<br/>
    /// This relies on the Abraxas-compatible structured date codec placing the year in the high key bits, which makes all values for one year contiguous in encoded order.<br/>
    /// Unsupported date-like key types are rejected instead of routed through a scan-shaped fallback.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(year), CreateDateTimeYearUpper(year))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(year), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(year), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearRange` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year before all lower-resolution components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRangeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int startYear = RequireInt32(values, 0, descriptor);
        int endYear = RequireInt32(values, 1, descriptor);
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearNotEqualTo` leaf as two ordered extents outside the excluded year.<br/>
    /// Because structured date encoding stores year before lower-resolution parts, all values for the excluded year are contiguous and can be skipped with before/after primitives.<br/>
    /// This avoids the older complement-universe path and keeps negated year equality as explicit ordered range work.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over dates before and after the excluded year.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearExclusionLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        LibraDexIdentityKeyRange range = CreateStructuredDateYearKeyRange(index, year, year, descriptor);
        return CreateOrderedRangeExclusionLeaf(index, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a structured date `YearNotRange` leaf as two ordered extents outside the excluded year span.<br/>
    /// The excluded year span is contiguous in the Abraxas-compatible structured date codec, so the efficient bridge is keys before the lower year and keys after the upper year.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over dates before and after the excluded year span.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRangeExclusionLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int startYear = RequireInt32(values, 0, descriptor);
        int endYear = RequireInt32(values, 1, descriptor);
        LibraDexIdentityKeyRange range = CreateStructuredDateYearKeyRange(index, startYear, endYear, descriptor);
        return CreateOrderedRangeExclusionLeaf(index, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a structured date `YearIn` leaf as a same-index union of inclusive year ranges.<br/>
    /// Each year is contiguous in the Abraxas-compatible structured date codec, so this branch uses current ordered-range primitives without a component scan.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<int> years = RequireInt32Set(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, years.Select(year => CreateStructuredDateYearKeyRange(index, year, year, descriptor)));
    }

    /// <summary>
    /// Materializes a structured date lower year-boundary leaf as one ordered range.<br/>
    /// The range starts at the first representable tick/day of the supplied year and extends to the maximum value supported by the resolved key type.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the year-on-or-after range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearOnOrAfterLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(year), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(year), TimeSpan.Zero), new DateTimeOffset(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(year, 1, 1), DateOnly.MaxValue)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date upper year-boundary leaf as one ordered range.<br/>
    /// The range starts at the minimum value supported by the resolved key type and ends at the last representable tick/day of the supplied year.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the year-on-or-before range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearOnOrBeforeLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), CreateDateTimeYearUpper(year))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(year), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.MinValue, new DateOnly(year, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes one structured date year span as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by scalar, set-valued, and negated year operators so all paths preserve identical DateTimeOffset UTC normalization.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested structured date year span.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearRange(
        IIndex index,
        int startYear,
        int endYear,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year range payload for condition-driven multi-range retrieval.<br/>
    /// The bounds use the same DateTimeOffset UTC-normalized values as the single-range materializer so multi-range and single-range date branches stay byte-order equivalent.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="startYear">The inclusive lower year component.</param>
    /// <param name="endYear">The inclusive upper year component.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearKeyRange(
        IIndex index,
        int startYear,
        int endYear,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (endYear < startYear)
        {
            throw new ArgumentOutOfRangeException(nameof(endYear), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end year greater than or equal to start year.");
        }

        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(CreateDateTimeYearLower(startYear), CreateDateTimeYearUpper(endYear))
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(CreateDateTimeYearLower(startYear), TimeSpan.Zero), new DateTimeOffset(CreateDateTimeYearUpper(endYear), TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(new DateOnly(startYear, 1, 1), new DateOnly(endYear, 12, 31))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearMonth` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year and month before all lower-resolution components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year/month range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int month = RequireInt32(values, 1, descriptor);
        return MaterializeStructuredDateYearMonthRange(index, year, month, descriptor);
    }

    /// <summary>
    /// Materializes a structured date `YearMonthIn` leaf as a same-index union of inclusive ranges.<br/>
    /// This keeps the public condition contract aligned with Abraxas tuple membership while using only current LibraDex ordered-range primitives.<br/>
    /// Empty tuple sets are rejected because they would otherwise hide a likely caller or descriptor-generation error.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year/month ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<(int year, int month)> pairs = RequireYearMonthSet(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, pairs.Select(pair => CreateStructuredDateYearMonthKeyRange(index, pair.year, pair.month, descriptor)));
    }

    /// <summary>
    /// Materializes one structured date year/month pair as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by single-pair and tuple-set operators so both paths validate and encode range bounds identically.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date year/month range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthRange(
        IIndex index,
        int year,
        int month,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredDateYearMonthKeyRange(index, year, month, descriptor);
        object lower = range.LowerKey;
        object upper = range.UpperKey;
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year/month range payload for condition-driven multi-range retrieval.<br/>
    /// The month component is validated once here so single-range and multi-range branches share identical bounds and diagnostics.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearMonthKeyRange(
        IIndex index,
        int year,
        int month,
        LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateMonth(month, descriptor);
        DateTime lower = CreateDateTimeMonthLower(year, month);
        DateTime upper = CreateDateTimeMonthUpper(year, month);
        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearMonthDay` leaf as an inclusive range over the selected logical index.<br/>
    /// This is a single ordered range because the Abraxas-compatible structured date codec stores year, month, and day before all time components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date day range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int month = RequireInt32(values, 1, descriptor);
        int day = RequireInt32(values, 2, descriptor);
        return MaterializeStructuredDateYearMonthDayRange(index, year, month, day, descriptor);
    }

    /// <summary>
    /// Materializes a structured date `YearMonthDayIn` leaf as a same-index union of inclusive ranges.<br/>
    /// Each tuple remains a contiguous date range over the selected index, which avoids text conversion, scans, or companion indexes for these exact tuple permutations.<br/>
    /// Empty tuple sets are rejected because a zero-tuple condition has no useful primitive execution shape in the current contract.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of all requested year/month/day ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayInLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        IReadOnlyList<(int year, int month, int day)> tuples = RequireYearMonthDaySet(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, tuples.Select(tuple => CreateStructuredDateYearMonthDayKeyRange(index, tuple.year, tuple.month, tuple.day, descriptor)));
    }

    /// <summary>
    /// Materializes one structured date year/month/day tuple as an inclusive range over the selected logical index.<br/>
    /// The helper is shared by single-tuple and tuple-set operators so both paths keep identical UTC-normalized bounds for DateTimeOffset indexes.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the structured date day range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearMonthDayRange(
        IIndex index,
        int year,
        int month,
        int day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredDateYearMonthDayKeyRange(index, year, month, day, descriptor);
        object lower = range.LowerKey;
        object upper = range.UpperKey;
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured-date year/month/day range payload for condition-driven multi-range retrieval.<br/>
    /// The helper preserves the same day bounds for single tuple and tuple-set branches so they can differ only by primitive shape, not encoded range semantics.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="year">The year component to match.</param>
    /// <param name="month">The month component to match.</param>
    /// <param name="day">The day component to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredDateYearMonthDayKeyRange(
        IIndex index,
        int year,
        int month,
        int day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        DateTime lower = CreateDateTimeDayLower(year, month, day);
        DateTime upper = CreateDateTimeDayUpper(year, month, day);
        return index.KeyType == typeof(DateTime)
            ? new LibraDexIdentityKeyRange(lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? new LibraDexIdentityKeyRange(new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? new LibraDexIdentityKeyRange(DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Creates one inclusive structured `TimeOnly` hour range payload for condition-driven range retrieval.<br/>
    /// The lower bound starts at the first tick of <paramref name="startHour"/> and the upper bound ends at the last tick of <paramref name="endHour"/> so the range includes every time inside the selected semantic interval.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="startHour">The inclusive lower hour.</param>
    /// <param name="endHour">The inclusive upper hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>The runtime key range payload.</returns>
    private static LibraDexIdentityKeyRange CreateStructuredTimeOnlyHourKeyRange(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        ValidateHour(startHour, descriptor);
        ValidateHour(endHour, descriptor);
        if (endHour < startHour)
        {
            throw new ArgumentOutOfRangeException(nameof(endHour), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires end hour greater than or equal to start hour.");
        }

        return index.KeyType == typeof(TimeOnly)
            ? new LibraDexIdentityKeyRange(new TimeOnly(startHour, 0), CreateTimeOnlyHourUpper(endHour))
            : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured date `YearInMonths` leaf as a same-index union of month ranges inside one year.<br/>
    /// This is executable through the current ordered-range bridge because each selected year/month pair is contiguous in the encoded date order.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion representing the union of the selected month ranges inside the requested year.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearInMonthsLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        (int year, IReadOnlyList<int> months) = RequireYearMonths(values, 0, descriptor);
        return CreateConditionMultiRangeLeaf(index, months.Select(month => CreateStructuredDateYearMonthKeyRange(index, year, month, descriptor)));
    }

    /// <summary>
    /// Materializes a structured date `YearQuarter` leaf as one ordered range inside a single year.<br/>
    /// Quarter-without-year branches are not contiguous across the full key space, but one quarter inside one year is contiguous and can use the current range primitive.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the selected year/quarter range.</returns>
    private static IIdentityCriterion MaterializeStructuredDateYearQuarterLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int year = RequireInt32(values, 0, descriptor);
        int quarter = RequireInt32(values, 1, descriptor);
        ValidateQuarter(quarter, descriptor);
        int startMonth = ((quarter - 1) * 3) + 1;
        int endMonth = startMonth + 2;
        DateTime lower = CreateDateTimeMonthLower(year, startMonth);
        DateTime upper = CreateDateTimeMonthUpper(year, endMonth);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.FromDateTime(lower), DateOnly.FromDateTime(upper))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative day branch as an inclusive day range over the selected logical index.<br/>
    /// DateTime and DateTimeOffset use the full day tick range; DateOnly uses the exact corresponding date value.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="day">The UTC day to match.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested UTC day.</returns>
    private static IIdentityCriterion MaterializeStructuredDateRelativeDayLeaf(
        IIndex index,
        DateTime day,
        LibraDexConditionLeafDescriptor descriptor)
    {
        DateTime lower = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
        DateTime upper = lower.AddDays(1).AddTicks(-1);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, lower, upper)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, new DateTimeOffset(lower, TimeSpan.Zero), new DateTimeOffset(upper, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Find, DateOnly.FromDateTime(lower))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing day-window branch over the selected logical index.<br/>
    /// DateTime and DateTimeOffset match Abraxas' lower-bound behavior from `UtcNow - days`; DateOnly uses an inclusive date range from that UTC date through today.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing day window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastDaysLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int days = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime now = DateTime.UtcNow;
        DateTime from = DateTime.SpecifyKind(now.AddDays(-days), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : index.KeyType == typeof(DateOnly)
                    ? CreateConditionLeaf(index, LibraDexCriteriaKind.Between, DateOnly.FromDateTime(from), DateOnly.FromDateTime(now))
                    : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing hour-window branch over the selected logical index.<br/>
    /// This branch is valid only for DateTime and DateTimeOffset indexes because DateOnly does not preserve hour components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing hour window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastHoursLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int hours = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime from = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-hours), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a UTC-relative trailing minute-window branch over the selected logical index.<br/>
    /// This branch is valid only for DateTime and DateTimeOffset indexes because DateOnly does not preserve minute components.<br/>
    /// </summary>
    /// <param name="index">The logical date index selected by the condition.</param>
    /// <param name="values">The materialized condition operands.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested trailing minute window.</returns>
    private static IIdentityCriterion MaterializeStructuredDateInLastMinutesLeaf(
        IIndex index,
        object?[] values,
        LibraDexConditionLeafDescriptor descriptor)
    {
        int minutes = RequireNonNegativeInt32(values, 0, descriptor);
        DateTime from = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-minutes), DateTimeKind.Utc);
        return index.KeyType == typeof(DateTime)
            ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, from)
            : index.KeyType == typeof(DateTimeOffset)
                ? CreateConditionLeaf(index, LibraDexCriteriaKind.AtOrAfter, new DateTimeOffset(from, TimeSpan.Zero))
                : throw new NotSupportedException($"Condition operator {descriptor.Operator} cannot materialize against index key type {index.KeyType.FullName}.");
    }

    /// <summary>
    /// Materializes a structured `TimeOnly` semantic hour branch as one inclusive ordered time range.<br/>
    /// Abraxas defines morning as hours 5 through 11, afternoon as 12 through 16, and evening as 17 through 21; each is contiguous in the structured time key.<br/>
    /// This bridge is intentionally limited to `TimeOnly` indexes because the same hour branch over `DateTime` is component-only across all dates and needs a different primitive or projection.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="startHour">The inclusive starting hour.</param>
    /// <param name="endHour">The inclusive ending hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion over the requested semantic time range.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOnlyHourRangeLeaf(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange range = CreateStructuredTimeOnlyHourKeyRange(index, startHour, endHour, descriptor);
        return CreateConditionLeaf(index, LibraDexCriteriaKind.Between, range.LowerKey, range.UpperKey);
    }

    /// <summary>
    /// Materializes a semantic time-of-day branch against either a `TimeOnly` index or a full date/time index.<br/>
    /// TimeOnly keys can use one ordered range because their encoded key space is one day; DateTime and DateTimeOffset keys use the structured component primitive because the hour interval repeats across dates.<br/>
    /// </summary>
    /// <param name="index">The logical time or date/time index selected by the condition.</param>
    /// <param name="startHour">The inclusive starting hour.</param>
    /// <param name="endHour">The inclusive ending hour.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion for the semantic time-of-day branch.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOfDayLeaf(
        IIndex index,
        int startHour,
        int endHour,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType == typeof(TimeOnly))
        {
            return MaterializeStructuredTimeOnlyHourRangeLeaf(index, startHour, endHour, descriptor);
        }

        return MaterializeStructuredDateComponentLeaf(
            index,
            CreateComponentPredicate(HourTest(EnumerateInclusive(startHour, endHour))),
            descriptor);
    }

    /// <summary>
    /// Materializes Abraxas' structured `TimeOnly` night branch as two ordered ranges around midnight.<br/>
    /// Night is defined as hours 22 through 23 or 0 through 4, so the efficient ordered-key bridge is a union of those two extents.<br/>
    /// </summary>
    /// <param name="index">The logical time index selected by the condition.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion union over the two night ranges.</returns>
    private static IIdentityCriterion MaterializeStructuredTimeOnlyNightLeaf(
        IIndex index,
        LibraDexConditionLeafDescriptor descriptor)
    {
        LibraDexIdentityKeyRange late = CreateStructuredTimeOnlyHourKeyRange(index, 22, 23, descriptor);
        LibraDexIdentityKeyRange early = CreateStructuredTimeOnlyHourKeyRange(index, 0, 4, descriptor);
        return CreateConditionMultiRangeLeaf(index, new[] { early, late });
    }

    /// <summary>
    /// Materializes Abraxas' semantic night branch against either a `TimeOnly` index or a full date/time index.<br/>
    /// TimeOnly keys use two ordered ranges around midnight, while DateTime and DateTimeOffset keys use an hour-component mask because night recurs for every encoded date.<br/>
    /// </summary>
    /// <param name="index">The logical time or date/time index selected by the condition.</param>
    /// <param name="descriptor">The source condition leaf descriptor.</param>
    /// <returns>An identity criterion for the night branch.</returns>
    private static IIdentityCriterion MaterializeStructuredNightLeaf(
        IIndex index,
        LibraDexConditionLeafDescriptor descriptor)
    {
        if (index.KeyType == typeof(TimeOnly))
        {
            return MaterializeStructuredTimeOnlyNightLeaf(index, descriptor);
        }

        return MaterializeStructuredDateComponentLeaf(
            index,
            CreateComponentPredicate(HourTest(new[] { 0, 1, 2, 3, 4, 22, 23 })),
            descriptor);
    }

    private static IIdentityCriterion MaterializeProjectionLeaf(
        string group,
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexConditionLeafClassification classification,
        IIndex projectionIndex)
    {
        if (projectionIndex.Group.Length != 0 &&
            !string.Equals(projectionIndex.Group, group, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Projection index '{projectionIndex.Name}' belongs to group '{projectionIndex.Group}', not condition group '{group}'.");
        }

        object?[] values = descriptor.Operands.Select(static operand => operand.GetValue()).ToArray();
        return classification.ProjectionKind switch
        {
            LibraDexIndexProjectionKind.Exact => MaterializeExactProjectionLeaf(group, projectionIndex, descriptor, values),
            LibraDexIndexProjectionKind.SortKey => MaterializeSortKeyProjectionLeaf(projectionIndex, descriptor, values),
            LibraDexIndexProjectionKind.FoldedText => MaterializeFoldedTextProjectionLeaf(projectionIndex, descriptor, values),
            _ => throw new NotSupportedException($"Projection bridge for condition operator {descriptor.Operator} with projection {classification.ProjectionKind} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes an exact projection condition through a caller-supplied physical projection index.<br/>
    /// String suffixes use reversed text bounds, and binary suffixes use reversed byte bounds or a reversed masked predicate fallback.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained exact projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original operands.</param>
    /// <returns>An identity criterion leaf over the exact projection index.</returns>
    private static IIdentityCriterion MaterializeExactProjectionLeaf(
        string group,
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType == typeof(byte[]) && descriptor.ValueKind == LibraDexConditionValueKind.Binary)
        {
            return MaterializeExactBinaryProjectionLeaf(group, projectionIndex, descriptor, values);
        }

        if (projectionIndex.KeyType != typeof(string))
        {
            throw new NotSupportedException("Exact-text projection conditions currently require a string projection index so suffix bounds can stay ordered.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EndsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateReversedExactTextProjectionValue(RequireString(values, 0, descriptor)),
                CreateReversedExactTextPrefixUpperBound(RequireString(values, 0, descriptor))),
            _ => throw new NotSupportedException($"Exact-text projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes a binary suffix condition through a maintained reversed exact-byte projection.<br/>
    /// Unmasked suffixes become ordered byte ranges; masked suffix patterns execute as reversed prefix predicates over the projection.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained reversed exact-byte projection index.</param>
    /// <param name="descriptor">The original binary condition leaf descriptor.</param>
    /// <param name="values">The materialized original operands.</param>
    /// <returns>An identity criterion leaf over the binary projection index.</returns>
    private static IIdentityCriterion MaterializeExactBinaryProjectionLeaf(
        string group,
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (descriptor.Operator != LibraDexConditionOperatorKind.EndsWith)
        {
            throw new NotSupportedException($"Exact binary projection bridge for condition operator {descriptor.Operator} is not connected yet.");
        }

        object operand = RequireValue(values, 0, descriptor);
        if (operand is LibraDexBinaryPatternPredicate compiled)
        {
            if (compiled.TryGetUnmaskedValue(LibraDexBinaryPatternMode.EndsWith, out byte[] unmaskedValue))
            {
                return CreateExactBinarySuffixProjectionRange(group, projectionIndex, unmaskedValue);
            }

            return CreateProjectionConditionLeaf(group, projectionIndex, LibraDexCriteriaKind.BinaryPattern, compiled.ToReversedStartsWith());
        }

        byte[] suffix = operand as byte[]
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a binary suffix operand.");
        return CreateExactBinarySuffixProjectionRange(group, projectionIndex, suffix);
    }

    /// <summary>
    /// Builds the ordered range over reversed fixed-width binary keys for an original-key suffix value.<br/>
    /// For example, suffix `CC DD` over a 16-byte key becomes reversed-prefix range `DD CC 00...` through `DD CC FF...`.<br/>
    /// </summary>
    /// <param name="group">The logical identity group that owns the condition.</param>
    /// <param name="projectionIndex">The maintained reversed exact-byte projection index.</param>
    /// <param name="suffix">The original forward suffix bytes.</param>
    /// <returns>An identity criterion leaf over the reversed projection range.</returns>
    private static IIdentityCriterion CreateExactBinarySuffixProjectionRange(string group, IIndex projectionIndex, byte[] suffix)
    {
        int width = projectionIndex.FixedKeyByteWidth
            ?? throw new NotSupportedException("Binary suffix projections require a fixed-width byte[] projection index.");
        if (suffix.Length > width)
        {
            byte[] reversedSuffix = (byte[])suffix.Clone();
            Array.Reverse(reversedSuffix);
            return CreateProjectionConditionLeaf(
                group,
                projectionIndex,
                LibraDexCriteriaKind.BinaryPattern,
                LibraDexBinaryPatternPredicate.Create(LibraDexBinaryPatternMode.StartsWith, reversedSuffix));
        }

        byte[] lower = new byte[width];
        byte[] upper = new byte[width];
        Array.Fill(upper, (byte)0xFF);
        for (int i = 0; i < suffix.Length; i++)
        {
            byte value = suffix[suffix.Length - 1 - i];
            lower[i] = value;
            upper[i] = value;
        }

        return CreateProjectionConditionLeaf(group, projectionIndex, LibraDexCriteriaKind.Between, lower, upper);
    }

    /// <summary>
    /// Materializes a case-insensitive string comparison through a caller-supplied sort-key projection index.<br/>
    /// The original condition operands stay developer-facing strings, while the projection leaf receives `CompareInfo.GetSortKey(..., IgnoreCase).KeyData` byte keys so execution can reuse the ordinary exact and ordered primitive routes.<br/>
    /// </summary>
    /// <param name="projectionIndex">The maintained sort-key projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original string operands.</param>
    /// <returns>An identity criterion leaf or ordered exclusion tree over the sort-key projection index.</returns>
    private static IIdentityCriterion MaterializeSortKeyProjectionLeaf(
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType != typeof(byte[]))
        {
            throw new NotSupportedException("Sort-key projection conditions require a byte[] projection index.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Find, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(projectionIndex, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.GreaterThan => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.After, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.GreaterOrEqual => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.AtOrAfter, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.LessThan => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Before, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.LessOrEqual => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.AtOrBefore, CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.Between => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor),
                CreateSortKeyProjectionValue(RequireString(values, 1, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotBetween => CreateOrderedRangeExclusionLeaf(
                projectionIndex,
                CreateSortKeyProjectionValue(RequireString(values, 0, descriptor), descriptor),
                CreateSortKeyProjectionValue(RequireString(values, 1, descriptor), descriptor)),
            LibraDexConditionOperatorKind.InSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireStringSet(values, 0, descriptor).Select(value => CreateSortKeyProjectionValue(value, descriptor)).ToArray()),
            LibraDexConditionOperatorKind.NotInSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireStringSet(values, 0, descriptor).Select(value => CreateSortKeyProjectionValue(value, descriptor)).ToArray()).Not(),
            _ => throw new NotSupportedException($"Sort-key projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Materializes a folded-text condition through a caller-supplied folded projection index.<br/>
    /// Folded string projection keys use the same invariant-or-named culture lower-casing convention as the adopted Abraxas string operator; starts-with is represented as an ordered string extent when the projection key is string-backed.<br/>
    /// </summary>
    /// <param name="projectionIndex">The maintained folded-text projection index selected by the caller's projection resolver.</param>
    /// <param name="descriptor">The original condition leaf descriptor.</param>
    /// <param name="values">The materialized original string operands.</param>
    /// <returns>An identity criterion leaf or ordered exclusion tree over the folded-text projection index.</returns>
    private static IIdentityCriterion MaterializeFoldedTextProjectionLeaf(
        IIndex projectionIndex,
        LibraDexConditionLeafDescriptor descriptor,
        object?[] values)
    {
        if (projectionIndex.KeyType != typeof(string))
        {
            throw new NotSupportedException("Folded-text projection conditions currently require a string projection index so prefix bounds can stay ordered.");
        }

        return descriptor.Operator switch
        {
            LibraDexConditionOperatorKind.EqualTo => CreateConditionLeaf(projectionIndex, LibraDexCriteriaKind.Find, CreateFoldedTextProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.NotEqualTo => CreateOrderedPointExclusionLeaf(projectionIndex, CreateFoldedTextProjectionValue(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.StartsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateFoldedTextProjectionValue(RequireString(values, 0, descriptor), descriptor),
                CreateFoldedTextPrefixUpperBound(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.EndsWith => CreateConditionLeaf(
                projectionIndex,
                LibraDexCriteriaKind.Between,
                CreateReversedFoldedTextProjectionValue(RequireString(values, 0, descriptor), descriptor),
                CreateReversedFoldedTextPrefixUpperBound(RequireString(values, 0, descriptor), descriptor)),
            LibraDexConditionOperatorKind.InSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireStringSet(values, 0, descriptor).Select(value => CreateFoldedTextProjectionValue(value, descriptor)).ToArray()),
            LibraDexConditionOperatorKind.NotInSet => CreateProjectionMembershipLeaf(
                projectionIndex,
                (object)RequireStringSet(values, 0, descriptor).Select(value => CreateFoldedTextProjectionValue(value, descriptor)).ToArray()).Not(),
            _ => throw new NotSupportedException($"Folded-text projection bridge for condition operator {descriptor.Operator} is not connected yet.")
        };
    }

    /// <summary>
    /// Creates one sort-key projection operand using Abraxas-compatible culture selection and ignore-case comparison options.<br/>
    /// </summary>
    /// <param name="value">The original string value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The stable sort-key bytes for the supplied string.</returns>
    private static byte[] CreateSortKeyProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        return ResolveConditionCulture(descriptor).CompareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;
    }

    /// <summary>
    /// Creates one folded-text projection operand using Abraxas-compatible invariant-or-named culture lower-casing.<br/>
    /// </summary>
    /// <param name="value">The original string value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The folded string value.</returns>
    private static string CreateFoldedTextProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        return value.ToLower(ResolveConditionCulture(descriptor));
    }

    /// <summary>
    /// Creates the exclusive-like upper sentinel used to represent a folded-text prefix as an inclusive LibraDex range leaf.<br/>
    /// The current range primitive is inclusive, so the sentinel mirrors Abraxas' high Unicode suffix and remains a projection bridge detail rather than new public retrieval vocabulary.<br/>
    /// </summary>
    /// <param name="value">The original prefix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The folded prefix plus the high sentinel character.</returns>
    private static string CreateFoldedTextPrefixUpperBound(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        return CreateFoldedTextProjectionValue(value, descriptor) + '\uffff';
    }

    /// <summary>
    /// Creates one reversed folded-text projection operand for suffix matching.<br/>
    /// A suffix condition such as `EndsWith("son")` becomes a prefix-shaped ordered extent over the maintained reversed folded projection key `nos`.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The reversed folded suffix value.</returns>
    private static string CreateReversedFoldedTextProjectionValue(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        string folded = CreateFoldedTextProjectionValue(value, descriptor);
        return string.Create(folded.Length, folded, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    /// <summary>
    /// Creates one reversed exact-text projection operand.<br/>
    /// A suffix condition such as `EndsWith("son")` becomes a prefix-shaped ordered extent over the maintained reversed exact projection key `nos` while preserving case-sensitive semantics.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <returns>The reversed exact suffix value.</returns>
    private static string CreateReversedExactTextProjectionValue(string value)
    {
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    /// <summary>
    /// Creates the inclusive upper sentinel for a reversed exact suffix extent.<br/>
    /// The current range primitive is inclusive, so the high sentinel mirrors the forward prefix bridge while targeting reversed exact keys.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <returns>The reversed exact suffix plus the high sentinel character.</returns>
    private static string CreateReversedExactTextPrefixUpperBound(string value)
    {
        return CreateReversedExactTextProjectionValue(value) + '\uffff';
    }

    /// <summary>
    /// Creates the inclusive upper sentinel for a reversed folded suffix extent.<br/>
    /// The current range primitive is inclusive, so the high sentinel mirrors the forward folded-prefix bridge while targeting the reversed projection.<br/>
    /// </summary>
    /// <param name="value">The original suffix value.</param>
    /// <param name="descriptor">The source descriptor carrying the optional culture name.</param>
    /// <returns>The reversed folded suffix plus the high sentinel character.</returns>
    private static string CreateReversedFoldedTextPrefixUpperBound(string value, LibraDexConditionLeafDescriptor descriptor)
    {
        return CreateReversedFoldedTextProjectionValue(value, descriptor) + '\uffff';
    }

    /// <summary>
    /// Resolves the culture metadata captured by a string condition descriptor.<br/>
    /// Empty or null culture values follow Abraxas' convention and use <see cref="CultureInfo.InvariantCulture"/>.<br/>
    /// </summary>
    /// <param name="descriptor">The source condition descriptor.</param>
    /// <returns>The resolved culture.</returns>
    private static CultureInfo ResolveConditionCulture(LibraDexConditionLeafDescriptor descriptor)
    {
        return string.IsNullOrEmpty(descriptor.Culture)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(descriptor.Culture);
    }

    /// <summary>
    /// Reads one required string operand from a condition leaf.<br/>
    /// Projection bridges intentionally fail before primitive execution if a generated descriptor supplies a non-string operand for a string projection.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand index.</param>
    /// <param name="descriptor">The source descriptor used for error context.</param>
    /// <returns>The required string operand.</returns>
    private static string RequireString(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        return RequireValue(values, ordinal, descriptor) as string
            ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires string operand {ordinal}.");
    }

    /// <summary>
    /// Reads one required string set operand from a condition leaf.<br/>
    /// The adopted condition builder stores membership operands as an enumerable so projection bridges can transform every member into the maintained projection key type in one place.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand index.</param>
    /// <param name="descriptor">The source descriptor used for error context.</param>
    /// <returns>The required string values.</returns>
    private static IReadOnlyList<string> RequireStringSet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        List<string> strings = new();
        foreach (string text in RequireStringEnumerable(values, ordinal, descriptor))
        {
            strings.Add(text);
        }

        return strings;
    }

    private static IEnumerable<string> RequireStringEnumerable(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        foreach (object? value in RequireEnumerable(values, ordinal, descriptor))
        {
            if (value is not string text)
            {
                throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires only string set operands.");
            }

            yield return text;
        }
    }

    private static object RequireValue(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        if (ordinal >= values.Length || values[ordinal] is null)
        {
            throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires operand {ordinal}.");
        }

        return values[ordinal]!;
    }

    private static object RequireNonNullMembershipValue(object? value, LibraDexConditionLeafDescriptor descriptor)
    {
        return value ?? throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' does not allow null membership values.");
    }

    /// <summary>
    /// Reads one required Int32 operand from a condition leaf.<br/>
    /// Date-part operators use Int32 component values so invalid generated descriptors fail before reaching physical criteria execution.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required Int32 operand.</returns>
    private static int RequireInt32(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is int typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires Int32 operand {ordinal}.");
    }

    private static TEnum RequireEnum<TEnum>(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
        where TEnum : struct, Enum
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is TEnum typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires {typeof(TEnum).Name} operand {ordinal}.");
    }

    /// <summary>
    /// Reads one required non-negative Int32 operand from a condition leaf.<br/>
    /// Relative date-window operators use non-negative component counts so invalid generated descriptors fail before physical criteria execution.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required non-negative Int32 operand.</returns>
    private static int RequireNonNegativeInt32(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        int value = RequireInt32(values, ordinal, descriptor);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a non-negative Int32 operand {ordinal}.");
        }

        return value;
    }

    /// <summary>
    /// Reads the required structured date component set from a condition leaf.<br/>
    /// Component-set operators store the caller's selected values as one operand so materialization can distinguish set membership from scalar component operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required component set.</returns>
    private static IReadOnlyList<int> RequireInt32Set(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<int> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an Int32 component set operand.");
    }

    /// <summary>
    /// Reads the required year-plus-month-set payload from a condition leaf.<br/>
    /// The payload is captured as one tuple so the bridge can build same-index month-range unions without conflating the year with membership operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year and month set payload.</returns>
    private static (int year, IReadOnlyList<int> months) RequireYearMonths(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        if (value is ValueTuple<int, int[]> tuple)
        {
            return (tuple.Item1, tuple.Item2);
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year and month-set operand.");
    }

    /// <summary>
    /// Reads the required structured date year/month tuple set from a condition leaf.<br/>
    /// Tuple-set operators store the caller's selected pairs as one operand so materialization can build a same-index union without reinterpreting scalar operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year/month tuple set.</returns>
    private static IReadOnlyList<(int year, int month)> RequireYearMonthSet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<(int year, int month)> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year/month tuple set operand.");
    }

    /// <summary>
    /// Reads the required structured date year/month/day tuple set from a condition leaf.<br/>
    /// Tuple-set operators store the caller's selected tuples as one operand so materialization can build a same-index union without reinterpreting scalar operands.<br/>
    /// </summary>
    /// <param name="values">The materialized operand values.</param>
    /// <param name="ordinal">The operand ordinal to read.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    /// <returns>The required year/month/day tuple set.</returns>
    private static IReadOnlyList<(int year, int month, int day)> RequireYearMonthDaySet(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        return value is IReadOnlyList<(int year, int month, int day)> typed
            ? typed
            : throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires a year/month/day tuple set operand.");
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date year.<br/>
    /// The returned value uses UTC kind so DateTimeOffset materialization can preserve Abraxas-style UTC normalization without changing the DateTime key contract.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <returns>The first representable tick in the requested year.</returns>
    private static DateTime CreateDateTimeYearLower(int year)
    {
        return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date year.<br/>
    /// The method uses the tick before the next year, with a `DateTime.MaxValue` guard for year 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <returns>The last representable tick in the requested year.</returns>
    private static DateTime CreateDateTimeYearUpper(int year)
    {
        return year == 9999
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date month.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <returns>The first representable tick in the requested month.</returns>
    private static DateTime CreateDateTimeMonthLower(int year, int month)
    {
        return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date month.<br/>
    /// The method uses the tick before the next month, with a `DateTime.MaxValue` guard for December 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <returns>The last representable tick in the requested month.</returns>
    private static DateTime CreateDateTimeMonthUpper(int year, int month)
    {
        return year == 9999 && month == 12
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : new DateTime(month == 12 ? year + 1 : year, month == 12 ? 1 : month + 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive lower DateTime bound for one structured date day.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <param name="day">The day component.</param>
    /// <returns>The first representable tick in the requested day.</returns>
    private static DateTime CreateDateTimeDayLower(int year, int month, int day)
    {
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Creates the inclusive upper DateTime bound for one structured date day.<br/>
    /// The method uses the tick before the next day, with a `DateTime.MaxValue` guard for December 31, 9999.<br/>
    /// </summary>
    /// <param name="year">The year component.</param>
    /// <param name="month">The month component.</param>
    /// <param name="day">The day component.</param>
    /// <returns>The last representable tick in the requested day.</returns>
    private static DateTime CreateDateTimeDayUpper(int year, int month, int day)
    {
        DateTime lower = CreateDateTimeDayLower(year, month, day);
        return lower.Date == DateTime.MaxValue.Date
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : lower.AddDays(1).AddTicks(-1);
    }

    /// <summary>
    /// Creates the inclusive upper `TimeOnly` bound for one structured time hour.<br/>
    /// The method returns the tick before the next hour, with a `TimeOnly.MaxValue` guard for hour 23.<br/>
    /// </summary>
    /// <param name="hour">The hour component.</param>
    /// <returns>The last representable tick in the requested hour.</returns>
    private static TimeOnly CreateTimeOnlyHourUpper(int hour)
    {
        ValidateHour(hour, descriptor: null);
        return hour == 23
            ? TimeOnly.MaxValue
            : new TimeOnly(hour + 1, 0).Add(TimeSpan.FromTicks(-1));
    }

    /// <summary>
    /// Validates one month component before constructing DateTime range bounds.<br/>
    /// This keeps generated condition descriptor errors close to the condition bridge rather than leaking a less specific DateTime constructor exception.<br/>
    /// </summary>
    /// <param name="month">The month component to validate.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    private static void ValidateMonth(int month, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (month < 1 || month > 12)
        {
            string context = descriptor is null
                ? "Structured date condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(month), $"{context} requires month 1 through 12.");
        }
    }

    /// <summary>
    /// Validates one day-of-month component before constructing structured date component predicates.<br/>
    /// The check validates the component domain only; month-specific calendar validity remains the responsibility of tuple/range materializers that know the month and year context.<br/>
    /// </summary>
    /// <param name="day">The day component to validate.</param>
    /// <param name="descriptor">The optional source condition leaf descriptor for diagnostics.</param>
    private static void ValidateDay(int day, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (day < 1 || day > 31)
        {
            string context = descriptor is null
                ? "Structured date condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(day), $"{context} requires day 1 through 31.");
        }
    }

    /// <summary>
    /// Validates one quarter component before constructing date range bounds.<br/>
    /// This keeps generated condition descriptor errors close to the condition bridge rather than leaking an imprecise arithmetic or range exception later.<br/>
    /// </summary>
    /// <param name="quarter">The quarter component to validate.</param>
    /// <param name="descriptor">The source condition leaf descriptor for diagnostics.</param>
    private static void ValidateQuarter(int quarter, LibraDexConditionLeafDescriptor descriptor)
    {
        if (quarter < 1 || quarter > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(quarter), $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires quarter 1 through 4.");
        }
    }

    /// <summary>
    /// Validates one hour component before constructing `TimeOnly` range bounds.<br/>
    /// This keeps generated descriptor errors close to the condition bridge rather than leaking a less specific `TimeOnly` constructor exception.<br/>
    /// </summary>
    /// <param name="hour">The hour component to validate.</param>
    /// <param name="descriptor">The optional source condition leaf descriptor for diagnostics.</param>
    private static void ValidateHour(int hour, LibraDexConditionLeafDescriptor? descriptor)
    {
        if (hour < 0 || hour > 23)
        {
            string context = descriptor is null
                ? "Structured time condition"
                : $"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}'";
            throw new ArgumentOutOfRangeException(nameof(hour), $"{context} requires hour 0 through 23.");
        }
    }

    private static IEnumerable<object> RequireEnumerable(object?[] values, int ordinal, LibraDexConditionLeafDescriptor descriptor)
    {
        object value = RequireValue(values, ordinal, descriptor);
        if (value is IEnumerable<object> objectValues)
        {
            return objectValues;
        }

        if (value is IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object>();
        }

        throw new InvalidOperationException($"Condition leaf '{descriptor.IndexName}' operator '{descriptor.Operator}' requires an enumerable operand.");
    }

    private LibraDexConditionLeafDescriptor RequireLeaf()
    {
        return leaf ?? throw new InvalidOperationException("Condition node is not a leaf.");
    }

    private LibraDexConditionNode RequireLeft()
    {
        return left ?? throw new InvalidOperationException("Condition node is missing its left child.");
    }

    private LibraDexConditionNode RequireRight()
    {
        return right ?? throw new InvalidOperationException("Condition node is missing its right child.");
    }
}
