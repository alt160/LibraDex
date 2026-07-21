namespace LibraDex;

/// <summary>
/// Describes one persisted router route slot as an outer value.<br/>
/// The same 10-byte route shape is used by expanded, compressed, root, and later multi-byte routers.<br/>
/// </summary>
/// <param name="PrefixStart">The inclusive prefix start byte.</param>
/// <param name="PrefixEnd">The inclusive prefix end byte.</param>
/// <param name="TargetOffset">The route target file offset, or zero when unset.</param>
internal readonly record struct RouterRouteSnapshot(
    byte PrefixStart,
    byte PrefixEnd,
    long TargetOffset);
