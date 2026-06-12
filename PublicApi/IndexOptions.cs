namespace LibraDex;

/// <summary>
/// Selects the index-wide duplicate-key contract.<br/>
/// The value is chosen when an index is created or opened through the public facade so insert call sites do not need repetitive duplicate-policy arguments.<br/>
/// </summary>
public enum IndexKeys
{
    /// <summary>
    /// Allows only one identity for each key.<br/>
    /// Insert attempts for an existing key are rejected by the index contract.<br/>
    /// </summary>
    Unique = 0,

    /// <summary>
    /// Allows multiple identities to share the same key.<br/>
    /// This is the low-friction default because secondary indexes commonly map one key to many identities.<br/>
    /// </summary>
    NonUnique = 1
}

/// <summary>
/// Selects how string keys are represented for search and ordering intent.<br/>
/// `Exact` preserves the input text identity, `Folded` supports case-insensitive point lookup, and `SortKey` supports stable no-case range ordering without query-time collation.<br/>
/// </summary>
[Flags]
public enum StringKeys
{
    /// <summary>
    /// Stores the exact encoded input string for case-sensitive matching.<br/>
    /// The public name describes caller intent rather than committing the surface name to a specific byte encoding term.<br/>
    /// </summary>
    Exact = 1,

    /// <summary>
    /// Stores folded text for case-insensitive point lookup.<br/>
    /// This avoids forcing callers to apply ad hoc normalization before every lookup.<br/>
    /// </summary>
    Folded = 2,

    /// <summary>
    /// Stores sort-key bytes for stable range ordering without collation work at query time.<br/>
    /// This can be used alone or as a sub-index beside exact or folded keys depending on query intent.<br/>
    /// </summary>
    SortKey = 4,

    /// <summary>
    /// Stores exact and folded representations.<br/>
    /// This profile favors mixed case-sensitive and case-insensitive point lookup without paying for sort-key storage.<br/>
    /// </summary>
    ExactAndFolded = Exact | Folded,

    /// <summary>
    /// Stores exact and sort-key representations.<br/>
    /// This profile favors case-sensitive identity plus stable range ordering.<br/>
    /// </summary>
    ExactAndSortKey = Exact | SortKey,

    /// <summary>
    /// Stores folded and sort-key representations.<br/>
    /// This profile favors no-case point and range lookup without preserving an exact-key index.<br/>
    /// </summary>
    FoldedAndSortKey = Folded | SortKey,

    /// <summary>
    /// Stores exact, folded, and sort-key representations.<br/>
    /// This is the widest string profile and should be selected deliberately because it creates multiple maintained key projections.<br/>
    /// </summary>
    ExactFoldedAndSortKey = Exact | Folded | SortKey
}

/// <summary>
/// Selects optional GUID key projections beyond exact 16-byte lookup.<br/>
/// Exact lookup is the normal hot path; segment and text profiles exist for developer-facing GUID search and inspection scenarios that should not require caller-side string hacks.<br/>
/// </summary>
[Flags]
public enum GuidKeys
{
    /// <summary>
    /// Stores the canonical 16-byte GUID value for exact lookup.<br/>
    /// This is the default GUID index contract.<br/>
    /// </summary>
    Exact = 1,

    /// <summary>
    /// Enables segment-oriented GUID search affordances.<br/>
    /// This is intended for deliberate diagnostic or application semantics, not as a default cost for every GUID index.<br/>
    /// </summary>
    Segments = 2,

    /// <summary>
    /// Enables canonical-text GUID search affordances such as prefix, suffix, contains, or pattern-style lookup.<br/>
    /// The text projection should be explicit because exact GUID lookup does not need it.<br/>
    /// </summary>
    Text = 4,

    /// <summary>
    /// Stores exact and segment-oriented GUID projections.<br/>
    /// </summary>
    ExactAndSegments = Exact | Segments,

    /// <summary>
    /// Stores exact and canonical-text GUID projections.<br/>
    /// </summary>
    ExactAndText = Exact | Text,

    /// <summary>
    /// Stores exact, segment-oriented, and canonical-text GUID projections.<br/>
    /// </summary>
    ExactSegmentsAndText = Exact | Segments | Text
}

/// <summary>
/// Selects date and time key projections for exact chronological lookup and structured date-part lookup.<br/>
/// The structured profile follows the Abraxas-style idea of packing date parts into sortable numeric fields so year, month, day, weekday, and time-part predicates do not require text formatting or query-time parsing.<br/>
/// </summary>
[Flags]
public enum DateKeys
{
    /// <summary>
    /// Stores the exact chronological value for normal equality and range lookup.<br/>
    /// </summary>
    Exact = 1,

    /// <summary>
    /// Stores structured date and time parts for direct part-based lookup such as year ranges, month filters, and weekday predicates.<br/>
    /// </summary>
    Structured = 2,

    /// <summary>
    /// Stores both exact chronological and structured date-part projections.<br/>
    /// This is the likely high-level default for date-heavy application indexes, but should remain an explicit profile until storage costs are measured.<br/>
    /// </summary>
    ExactAndStructured = Exact | Structured
}

/// <summary>
/// Selects the binary encoding used for DateTime-like values stored as LibraDex date index keys.<br/>
/// The condition builder remains encoding-agnostic; execution reads this index-level contract to decide whether a component predicate can use packed fields directly or must derive a component while scanning the compact index key stream.<br/>
/// </summary>
public enum DateTimeKeyEncoding
{
    /// <summary>
    /// Uses the Abraxas calendar-optimized structured date/time layout.<br/>
    /// This is the default because it directly stores calendar components such as day-of-week for common business filtering, while quantizing sub-millisecond precision to 10-tick slices.<br/>
    /// </summary>
    CalendarSdt = 0,

    /// <summary>
    /// Uses a precision structured date/time layout that preserves full .NET tick precision for DateTime, DateTimeOffset, and TimeOnly values.<br/>
    /// This trades away the packed day-of-week field; weekday and weekend predicates still work, but derive day-of-week from the encoded date components during execution.<br/>
    /// </summary>
    PrecisionSdt = 1
}

/// <summary>
/// Public per-index options resolved at create/open time.<br/>
/// These options represent index contracts or maintained projections; callers should not need to repeat them on every insert or query.<br/>
/// </summary>
public sealed class IndexOptions
{
    /// <summary>
    /// Gets or initializes the index-wide duplicate-key contract.<br/>
    /// `NonUnique` is the default because many index use cases map one logical key to multiple identities.<br/>
    /// </summary>
    public IndexKeys Keys { get; init; } = IndexKeys.NonUnique;

    /// <summary>
    /// Gets or initializes the string key projection profile for string-key indexes.<br/>
    /// The default stores exact keys only; no-case or range-oriented projections should be chosen deliberately.<br/>
    /// </summary>
    public StringKeys StringKeys { get; init; } = StringKeys.Exact;

    /// <summary>
    /// Gets or initializes the GUID key projection profile for GUID-key indexes.<br/>
    /// The default stores exact 16-byte GUID keys only.<br/>
    /// </summary>
    public GuidKeys GuidKeys { get; init; } = GuidKeys.Exact;

    /// <summary>
    /// Gets or initializes the date key projection profile for date-key indexes.<br/>
    /// The default stores exact chronological keys only until the structured date profile is connected to concrete storage.<br/>
    /// </summary>
    public DateKeys DateKeys { get; init; } = DateKeys.Exact;

    /// <summary>
    /// Gets or initializes the binary encoding used for DateTime-like key values in date indexes.<br/>
    /// The default is <see cref="DateTimeKeyEncoding.CalendarSdt"/> because most application filters benefit from direct calendar components more often than sub-millisecond tick distinction.<br/>
    /// </summary>
    public DateTimeKeyEncoding DateTimeKeyEncoding { get; init; } = DateTimeKeyEncoding.CalendarSdt;

    /// <summary>
    /// Gets or initializes the index-level key traversal contract recorded for this index.<br/>
    /// Ascending is the default physical shape; descending records developer intent for index-natural reads and lets catalog metadata preserve that requested order across reopen.<br/>
    /// Query-time direction remains selectable per read so callers can stream an ascending index in descending order without creating a second index.<br/>
    /// </summary>
    public LibraDexIndexSortOrder SortOrder { get; init; } = LibraDexIndexSortOrder.Ascending;

    /// <summary>
    /// Gets or initializes the index-level default string comparison policy.<br/>
    /// This overrides the catalog-level policy for managed residual comparison or prepared membership on this index, but it does not replace encoded-key, folded-text, sort-key, or structured projection routes.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicy? StringComparisonPolicy { get; init; }
}
