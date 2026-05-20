namespace LibraDex;

internal enum VarKeyVarIdentityRouteTargetKind
{
    None = 0,
    Shelf = 1,
    Router = 2
}

internal readonly record struct VarKeyVarIdentityRouteTarget(
    VarKeyVarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct VarKeyVarIdentityRoutePathTarget(
    VarKeyVarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte PrefixByte,
    int RouteIndex);
