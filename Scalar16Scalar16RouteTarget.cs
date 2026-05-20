namespace LibraDex;

/// <summary>
/// Identifies the concrete structure found at a routed `Scalar16Scalar16` file offset.<br/>
/// This is an internal storage classification, not a public object model.<br/>
/// </summary>
internal enum Scalar16Scalar16RouteTargetKind
{
    /// <summary>
    /// No target exists or the offset is zero.<br/>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target bytes begin with the router page magic.<br/>
    /// </summary>
    Router = 1,

    /// <summary>
    /// The target bytes begin with the `Scalar16Scalar16` shelf magic.<br/>
    /// </summary>
    Shelf = 2
}

/// <summary>
/// Captures the result of routing an encoded `Scalar16Scalar16` key to a classified storage target.<br/>
/// The route depth records the router depth that produced the final target so split logic can later choose the correct structural mutation.<br/>
/// </summary>
/// <param name="Kind">The classified target kind.</param>
/// <param name="Offset">The file offset of the classified target.</param>
/// <param name="RouterDepth">The key byte depth of the router that produced the target.</param>
/// <param name="AllocationClassId">The allocation class id of the router that produced the target.</param>
internal readonly record struct Scalar16Scalar16RouteTarget(
    Scalar16Scalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

/// <summary>
/// Captures a routed `Scalar16Scalar16` target plus the parent router route that selected it.<br/>
/// Parent-route split logic uses this to rewrite the exact router that currently fans multiple prefixes into one full shelf.<br/>
/// </summary>
/// <param name="Target">The classified final route target.</param>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected <paramref name="Target"/>.</param>
/// <param name="ParentPrefixByte">The parent-router prefix byte that selected <paramref name="Target"/>.</param>
internal readonly record struct Scalar16Scalar16RoutePathTarget(
    Scalar16Scalar16RouteTarget Target,
    long ParentRouterOffset,
    byte ParentPrefixByte);
