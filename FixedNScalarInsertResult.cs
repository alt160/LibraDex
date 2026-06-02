namespace LibraDex;

/// <summary>
/// Reports the structural effect of inserting an item into a programmable fixed-key shelf.<br/>
/// The result intentionally mirrors the fixed scalar shelf outcomes so future routed `FSN` paths can share insert decision terminology.<br/>
/// </summary>
internal enum FixedNScalarInsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(key, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The key already existed in a unique index, so no shelf bytes were mutated.<br/>
    /// </summary>
    KeyConflict,

    /// <summary>
    /// The shelf had no remaining item capacity.<br/>
    /// </summary>
    Full
}
