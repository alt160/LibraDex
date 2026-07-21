namespace LibraDex;

/// <summary>
/// Describes the managed memory shape of a memory-backed <see cref="DataKernel"/> arena.<br/>
/// The counters are diagnostic-only and let workbench tooling separate live catalog bytes from free/reusable arena space and process heap overhead.<br/>
/// </summary>
/// <param name="PageCount">The number of managed arena pages currently allocated.</param>
/// <param name="PageSize">The size of each managed arena page.</param>
/// <param name="ArenaBytes">The total bytes held by managed arena pages.</param>
/// <param name="LiveRangeCount">The number of committed live byte ranges tracked by the arena.</param>
/// <param name="LiveBytes">The total bytes covered by committed live ranges.</param>
/// <param name="FreeExtentCount">The number of reusable released extents tracked by the arena.</param>
/// <param name="FreeBytes">The total bytes covered by reusable released extents.</param>
/// <param name="EndOffset">The highest committed arena end offset observed by the append cursor.</param>
internal readonly record struct DataKernelMemoryDiagnostics(
    int PageCount,
    int PageSize,
    long ArenaBytes,
    int LiveRangeCount,
    long LiveBytes,
    int FreeExtentCount,
    long FreeBytes,
    long EndOffset);
