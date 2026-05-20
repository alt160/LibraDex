namespace LibraDex;

/// <summary>
/// Reports the outcome of an encoded `FS32-8` identity range read.<br/>
/// The result keeps the copied identity count explicit and records whether the caller supplied a reusable route-kind cache for the storage walk.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span.</param>
/// <param name="UsedRouteKindCache">Whether the read used a caller-owned route-target-kind cache.</param>
/// <param name="RouteKindCacheCount">The number of classified route targets present in the cache after the read.</param>
internal readonly record struct Fixed32Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedRouteKindCache,
    int RouteKindCacheCount);
