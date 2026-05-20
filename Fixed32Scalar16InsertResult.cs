namespace LibraDex;

/// <summary>
/// Reports the structural effect of inserting an item into a `Fixed32Scalar16` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior after routed/public `FS32-16` support exists.<br/>
/// </summary>
internal enum Fixed32Scalar16InsertResult
{
    /// <summary>
    /// The item was inserted and shelf bytes were mutated.<br/>
    /// </summary>
    Inserted,

    /// <summary>
    /// The exact `(keyHigh, keyLow, identity)` tuple already existed, so no shelf bytes were mutated.<br/>
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
