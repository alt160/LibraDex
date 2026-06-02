namespace LibraDex;

/// <summary>
/// Represents the result of inserting into a programmable fixed-key / variable-identity shelf.<br/>
/// </summary>
internal enum FixedNVarIdentityInsertResult
{
    Invalid = 0,
    Inserted = 1,
    AlreadyPresent = 2,
    KeyConflict = 3,
    Full = 4
}
