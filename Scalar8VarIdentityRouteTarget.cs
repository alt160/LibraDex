namespace LibraDex;

internal enum Scalar8VarIdentityRouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct Scalar8VarIdentityRouteTarget(
    Scalar8VarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct Scalar8VarIdentityRoutePathTarget(
    Scalar8VarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);
