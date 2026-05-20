namespace LibraDex;

internal enum VarKeyScalar8RouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct VarKeyScalar8RouteTarget(
    VarKeyScalar8RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyScalar8RoutePathTarget(
    VarKeyScalar8RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);
