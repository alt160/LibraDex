namespace LibraDex;

/// <summary>
/// Describes the public typed insert outcome for an unsigned scalar `SS8-8` index wrapper.<br/>
/// The values mirror the encoded core outcomes while keeping normal unsigned call sites free of encoded type names.<br/>
/// </summary>
public enum UnsignedScalar8Scalar8InsertOutcome
{
    /// <summary>
    /// The unsigned `(key, identity)` tuple was physically inserted into the index.<br/>
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// The exact unsigned `(key, identity)` tuple was already present, so no physical insert was needed.<br/>
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>
    /// The unsigned key conflicted with an existing key under a uniqueness policy that rejects duplicates.<br/>
    /// </summary>
    KeyConflict = 2
}
