namespace LibraDex;

/// <summary>
/// Describes one persisted multi-byte router route as an outer value.<br/>
/// The route uses an exact stem for the first consumed bytes and the normal route slot start/end range for the final consumed byte.<br/>
/// This keeps the existing 10-byte route slot contract while allowing small-count routers to consume more than one key byte.<br/>
/// </summary>
/// <param name="PrefixStem">The exact prefix stem bytes before the final ranged prefix byte.</param>
/// <param name="PrefixStart">The inclusive final-byte prefix start.</param>
/// <param name="PrefixEnd">The inclusive final-byte prefix end.</param>
/// <param name="TargetOffset">The route target file offset, or zero when unset.</param>
internal readonly record struct RouterMultiByteRouteSnapshot(
    ReadOnlyMemory<byte> PrefixStem,
    byte PrefixStart,
    byte PrefixEnd,
    long TargetOffset);
