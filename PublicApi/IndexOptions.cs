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
/// Selects whether one identity may be associated with multiple keys inside one logical index.<br/>
/// This is separate from <see cref="IndexKeys"/>: key uniqueness controls how many identities can sit under one key, while identity-key multiplicity controls how many keys can point at one identity.<br/>
/// </summary>
public enum IdentityKeyMultiplicity
{
    /// <summary>
    /// Allows the same identity to be stored under multiple keys in the same index.<br/>
    /// This is the default secondary-index behavior and preserves current LibraDex tuple semantics.<br/>
    /// </summary>
    MultipleKeysPerIdentity = 0,

    /// <summary>
    /// Allows each identity to appear under only one key in the same logical index.<br/>
    /// This contract must be enforced by mutation paths before planners may treat same-index tuple/range cardinality as identity-set cardinality.<br/>
    /// </summary>
    SingleKeyPerIdentity = 1
}

/// <summary>
/// Selects how string keys are represented for search and ordering intent.<br/>
/// `Exact` preserves the input text identity, `Folded` supports case-insensitive point lookup, and `SortKey` supports stable no-case range ordering without query-time collation.<br/>
/// Every profile includes exact string storage because the current string facade uses exact storage for tuple capture, mutation, and projection maintenance.<br/>
/// </summary>
public enum StringKeys
{
    /// <summary>
    /// Stores the exact encoded input string for case-sensitive matching.<br/>
    /// The public name describes caller intent rather than committing the surface name to a specific byte encoding term.<br/>
    /// </summary>
    Exact = 1,

    /// <summary>
    /// Stores exact and folded representations.<br/>
    /// This profile favors mixed case-sensitive and case-insensitive point lookup without paying for sort-key storage.<br/>
    /// </summary>
    ExactAndFolded = 3,

    /// <summary>
    /// Stores exact and sort-key representations.<br/>
    /// This profile favors case-sensitive identity plus stable range ordering.<br/>
    /// </summary>
    ExactAndSortKey = 5,

    /// <summary>
    /// Stores exact, folded, and sort-key representations.<br/>
    /// This is the widest string profile and should be selected deliberately because it creates multiple maintained key projections.<br/>
    /// </summary>
    ExactFoldedAndSortKey = 7
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
/// Selects the caller-facing concurrency mode for LibraDex write helpers that explicitly accept concurrent caller traffic.<br/>
/// The default single-owner mode preserves the normal low-overhead index contract; queued writer mode admits overlapping insert calls and uses shelf-local writer contexts where the physical shape supports them, while serializing same-shelf contention, topology fallback, and publication as needed.<br/>
/// </summary>
public enum LibraDexConcurrencyMode
{
    /// <summary>
    /// Uses the normal single-owner index contract.<br/>
    /// Callers or an owner such as Abraxas are responsible for serializing writes.<br/>
    /// </summary>
    SingleOwner = 0,

    /// <summary>
    /// Uses an explicit concurrent-write admission facade for overlapping caller writes.<br/>
    /// In this slice the connected implementation is limited to `SS8-8` inserts and may fall back to serialized insertion for same-shelf contention, publication, or unsupported topology route shapes.<br/>
    /// </summary>
    QueuedWriter = 1
}

/// <summary>
/// Provides explicit options for write helpers that opt into a LibraDex concurrency mode.<br/>
/// These options are deliberately separate from index creation options because they describe a runtime writer facade, not persisted index shape or projection policy.<br/>
/// </summary>
public sealed class LibraDexConcurrencyOptions
{
    /// <summary>
    /// Gets or initializes the requested concurrency mode.<br/>
    /// `SingleOwner` preserves the default ownership contract; helper methods that create queued writers require <see cref="LibraDexConcurrencyMode.QueuedWriter"/> explicitly or by their own default.<br/>
    /// </summary>
    public LibraDexConcurrencyMode Mode { get; init; } = LibraDexConcurrencyMode.SingleOwner;

    /// <summary>
    /// Gets or initializes an expert override for the maximum number of active writers admitted to one file session.<br/>
    /// A null value uses LibraDex's shape-specific policy: immediate writer actions default to one active publisher, while concurrent batches use a runtime CPU-derived staging budget.<br/>
    /// Values must be positive and are intentionally scoped to runtime admission rather than persisted index metadata.<br/>
    /// </summary>
    public int? MaxActiveWriters { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of write actions retained in the session queue before new admission is rejected.<br/>
    /// The default of 1,024 protects the process from an unbounded producer backlog while remaining well above ordinary desktop and service concurrency.<br/>
    /// </summary>
    public int MaxQueuedWriters { get; init; } = 1024;

    /// <summary>
    /// Gets or initializes the maximum time an action may wait for admission.<br/>
    /// The default is infinite because cancellation is the normal shutdown mechanism; applications may set a finite timeout to impose a service deadline.<br/>
    /// </summary>
    public TimeSpan QueueTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets or initializes the number of tuple operations an admitted action may perform before it cooperatively releases and reacquires its queue position.<br/>
    /// The default of 1,000 prevents one long-running producer from monopolizing immediate-writer admission without adding a clock read to every tuple.<br/>
    /// </summary>
    public int MaxActionItems { get; init; } = 1000;

    /// <summary>
    /// Gets a reusable options instance for queued-writer mode.<br/>
    /// This keeps internal integration call sites compact while still making the selected mode explicit.<br/>
    /// </summary>
    public static LibraDexConcurrencyOptions QueuedWriter { get; } = new()
    {
        Mode = LibraDexConcurrencyMode.QueuedWriter
    };
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
    /// Gets or initializes whether one identity can be stored under multiple keys in this index.<br/>
    /// The default keeps normal secondary-index semantics; `SingleKeyPerIdentity` is an opt-in contract that can support stronger same-index count planning after every active write path enforces it.<br/>
    /// </summary>
    public IdentityKeyMultiplicity IdentityKeyMultiplicity { get; init; } = IdentityKeyMultiplicity.MultipleKeysPerIdentity;

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

    /// <summary>
    /// Gets or initializes the session-local immutable-shelf read-cache ceiling for this index.<br/>
    /// Zero keeps the design-intent default of no limit; a positive value bounds retained shelf bytes for this physical index only.<br/>
    /// </summary>
    public long ReadCacheMaxBytes { get; init; }
}
