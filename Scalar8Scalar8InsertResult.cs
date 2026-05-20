namespace LibraDex;

/// <summary>
/// Reports the structural effect of inserting an item into a `Scalar8Scalar8` shelf.<br/>
/// Outer APIs can map these storage-level outcomes to public insert/update behavior later.<br/>
/// </summary>
internal enum Scalar8Scalar8InsertResult
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
