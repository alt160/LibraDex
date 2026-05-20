namespace LibraDex;

internal enum Scalar16VarIdentityRouteTargetKind
{
    None = 0,
    Router = 1,
    Shelf = 2
}

internal readonly record struct Scalar16VarIdentityRouteTarget(
    Scalar16VarIdentityRouteTargetKind Kind,
    long Offset,
    ushort RouterDepth,
    ushort AllocationClassId);

internal readonly record struct Scalar16VarIdentityRoutePathTarget(
    Scalar16VarIdentityRouteTarget Target,
    long ParentRouterOffset,
    byte RoutePrefixByte,
    int RouteIndex);
