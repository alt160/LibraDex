namespace LibraDex;

/// <summary>
/// Carries internal timing counters for one `SS8-8` batch insert path attribution run.<br/>
/// The counters are diagnostic-only and are used by the harness to decide which lower-layer operation should be optimized next.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchInsertAttributionTelemetry
{
    internal long AttemptCount;
    internal long CachedHandledCount;
    internal long CacheMissEmptyRouteCount;
    internal long CacheMissNonShelfTargetCount;
    internal long CacheMissFullShelfCount;
    internal long RouteCacheHitCount;
    internal long RouteCacheMissCount;
    internal long RouteCacheProbeTicks;
    internal long RootLookupTicks;
    internal long RouteWalkTicks;
    internal Scalar8Scalar8RouteWalkAttributionTelemetry RouteWalkAttribution;
    internal long RouteTargetClassificationTicks;
    internal long ShelfCacheLookupTicks;
    internal long ShelfCacheProbeTicks;
    internal long ShelfCacheHitCount;
    internal long ShelfCacheMissCount;
    internal long ShelfCacheReadTicks;
    internal long ShelfCacheSnapshotCloneTicks;
    internal long ShelfCacheEntryCreateTicks;
    internal long ShelfCacheEntryAddTicks;
    internal long ShelfInsertTicks;
    internal long ShelfAppendFastPathTicks;
    internal long ShelfAppendFastPathCount;
    internal long ShelfLowerBoundTicks;
    internal long ShelfDuplicateCheckTicks;
    internal long ShelfUniqueCheckTicks;
    internal long ShelfPayloadWriteTicks;
    internal long ShelfSlotMoveTicks;
    internal long ShelfHeaderWriteTicks;
}
