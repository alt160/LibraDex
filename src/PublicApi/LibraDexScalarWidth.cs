namespace LibraDex;

/// <summary>
/// Selects a supported fixed scalar byte width for generic `byte[]` public index routing.<br/>
/// Numeric scalar types and `Guid` infer their width from the CLR type; `byte[]` requires this enum so public index shape is fixed at creation instead of inferred from the first inserted value.<br/>
/// </summary>
public enum LibraDexScalarWidth
{
    /// <summary>
    /// Uses the fixed 8-byte scalar shape.<br/>
    /// </summary>
    Bytes8 = 8,

    /// <summary>
    /// Uses the fixed 16-byte scalar shape.<br/>
    /// </summary>
    Bytes16 = 16,

    /// <summary>
    /// Uses the fixed 32-byte scalar shape.<br/>
    /// This width is currently valid for explicit `byte[]` keys routed to `FS32-8` generic indexes.<br/>
    /// </summary>
    Bytes32 = 32
}
