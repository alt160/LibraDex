namespace LibraDex;

/// <summary>
/// Reports the outcome of an encoded `SS8-16` identity range read.<br/>
/// The result keeps the copied identity-pair count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identity pairs copied into the caller-owned output spans.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Scalar8Scalar16RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);
