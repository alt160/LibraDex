namespace LibraDex;

/// <summary>
/// Describes the public encoded insert outcome for the first `SS8-8` index wrapper.<br/>
/// The values intentionally stay operation-facing rather than exposing every internal routed split kind.<br/>
/// </summary>
public enum Scalar8Scalar8EncodedInsertOutcome
{
    /// <summary>
    /// The encoded tuple was physically inserted into the index.<br/>
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// The exact encoded `(key, identity)` tuple was already present, so no physical insert was needed.<br/>
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>
    /// The encoded key conflicted with an existing key under a uniqueness policy that rejects duplicates.<br/>
    /// </summary>
    KeyConflict = 2
}
