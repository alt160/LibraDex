namespace LibraDex;

internal enum VarKeyScalar16RouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct VarKeyScalar16RouteTarget(
    VarKeyScalar16RouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyScalar16RoutePathTarget(
    VarKeyScalar16RouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);
