namespace LibraDex;

/// <summary>
/// Describes the caller's expected write order for an upcoming mutation group.<br/>
/// The values are optimization hints, not correctness requirements, and `Default` preserves LibraDex's normal adaptive behavior.<br/>
/// </summary>
public enum LibraDexWriteOrder
{
    /// <summary>
    /// Uses the shape's normal write behavior without asserting sorted or random ordering.<br/>
    /// This is the compatibility value used by default-constructed write intent options.<br/>
    /// </summary>
    Default = 0,

    /// <summary>
    /// Indicates that the upcoming mutation group is expected to arrive in key order or mostly key order.<br/>
    /// Shapes may use this to reduce exploratory work that is mainly useful for scattered mutation patterns.<br/>
    /// </summary>
    Sorted = 1,

    /// <summary>
    /// Indicates that the upcoming mutation group is expected to arrive in random or mostly random key order.<br/>
    /// Shapes may use this to favor policies that avoid assuming append-like locality.<br/>
    /// </summary>
    Random = 2
}

/// <summary>
/// Describes the caller's expected write volume for an upcoming mutation group.<br/>
/// The values intentionally stay coarse so application code can express intent without tuning physical shelf internals.<br/>
/// </summary>
public enum LibraDexWriteVolume
{
    /// <summary>
    /// Uses the shape's normal write-volume assumptions.<br/>
    /// This is the compatibility value used by default-constructed write intent options.<br/>
    /// </summary>
    Default = 0,

    /// <summary>
    /// Indicates a small mutation group where setup overhead should stay conservative.<br/>
    /// Shapes may use this to avoid spending extra work on policies that only pay back over larger batches.<br/>
    /// </summary>
    Small = 1,

    /// <summary>
    /// Indicates a mutation group expected to contain thousands of items.<br/>
    /// Shapes may use this to learn and apply write-locality hints earlier than they would for isolated writes.<br/>
    /// </summary>
    Thousands = 2,

    /// <summary>
    /// Indicates a mutation group expected to contain millions of items.<br/>
    /// Shapes may use this to bias toward durable policies that amortize well over long-running bulk writes.<br/>
    /// </summary>
    Millions = 3
}

/// <summary>
/// Describes the caller's expected key-space locality for an upcoming mutation group.<br/>
/// `Clustered` covers same-prefix and mid-stream repeated-byte clusters, while `Broad` covers writes spread across many unrelated routes.<br/>
/// </summary>
public enum LibraDexWriteLocality
{
    /// <summary>
    /// Uses the shape's normal locality assumptions.<br/>
    /// This is the compatibility value used by default-constructed write intent options.<br/>
    /// </summary>
    Default = 0,

    /// <summary>
    /// Indicates that many upcoming keys are expected to share meaningful byte clusters, possibly after one or more already-routed bytes.<br/>
    /// Varlen shapes may use this to sample common-prefix evidence more aggressively outside their hottest comparison loops.<br/>
    /// </summary>
    Clustered = 1,

    /// <summary>
    /// Indicates that upcoming keys are expected to be spread broadly across the key space.<br/>
    /// Shapes may use this to reduce locality-learning work that is unlikely to produce reusable route structure.<br/>
    /// </summary>
    Broad = 2
}

/// <summary>
/// Describes the caller's preferred write tradeoff for an upcoming mutation group.<br/>
/// LibraDex still preserves correctness and format validity; this hint only gives shape policies a lightweight bias when multiple valid strategies exist.<br/>
/// </summary>
public enum LibraDexWritePriority
{
    /// <summary>
    /// Uses the shape's normal performance and compactness balance.<br/>
    /// This is the compatibility value used by default-constructed write intent options.<br/>
    /// </summary>
    Default = 0,

    /// <summary>
    /// Keeps the normal balance between write speed, read speed, and persisted size.<br/>
    /// This value is explicit for call sites that want readable intent without choosing a specialized bias.<br/>
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// Favors mutation throughput when the shape has a practical choice between extra write work and later compactness.<br/>
    /// This is a hint rather than a promise to bypass required persistence or routing maintenance.<br/>
    /// </summary>
    WriteSpeed = 2,

    /// <summary>
    /// Favors persisted compactness when the shape has a practical choice between extra write work and denser layout.<br/>
    /// This is a hint rather than a promise to minimize every intermediate write buffer.<br/>
    /// </summary>
    Compactness = 3
}

/// <summary>
/// Bundles optional developer write-intent hints for a mutation group.<br/>
/// Default construction leaves all fields at `Default`, preserving existing LibraDex behavior and source compatibility for current batch callers.<br/>
/// </summary>
/// <param name="Order">The expected ordering of upcoming writes.</param>
/// <param name="Volume">The expected coarse volume of upcoming writes.</param>
/// <param name="Locality">The expected key-space locality of upcoming writes.</param>
/// <param name="Priority">The caller's preferred write tradeoff when multiple valid physical policies exist.</param>
public readonly record struct LibraDexWriteIntent(
    LibraDexWriteOrder Order = LibraDexWriteOrder.Default,
    LibraDexWriteVolume Volume = LibraDexWriteVolume.Default,
    LibraDexWriteLocality Locality = LibraDexWriteLocality.Default,
    LibraDexWritePriority Priority = LibraDexWritePriority.Default)
{
    /// <summary>
    /// Gets an explicit compatibility intent with every hint set to `Default`.<br/>
    /// This is equivalent to `default(LibraDexWriteIntent)` and is provided for call sites that prefer named options.<br/>
    /// </summary>
    public static LibraDexWriteIntent Default => default;
}
