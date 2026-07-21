namespace LibraDex;

/// <summary>
/// Reports the raw write shape of a DataKernel commit.<br/>
/// The counters describe the syscalls LibraDex issued, not lower-level operating-system cache behavior.<br/>
/// </summary>
/// <param name="Level">The diagnostics level used for the commit.</param>
/// <param name="StagedExtentCount">The number of logical append extents represented by the commit.</param>
/// <param name="StagedSegmentCount">The number of staged write buffers committed.</param>
/// <param name="WriteCallCount">The number of positional write calls issued by the DataKernel.</param>
/// <param name="BackingWriteCallCount">The number of logical backing-store write calls issued by the DataKernel.</param>
/// <param name="SetLengthCallCount">The number of file length changes issued by the DataKernel.</param>
/// <param name="FlushCallCount">The number of flush calls issued by the DataKernel.</param>
/// <param name="BytesWritten">The number of staged bytes written.</param>
/// <param name="ElapsedTicks">The Stopwatch ticks spent inside commit.</param>
/// <param name="CoalescedAdjacentSegmentCount">The number of staged segments joined to a previous segment with no byte gap.</param>
/// <param name="CoalescedGapCount">The number of unchanged byte gaps bridged to reduce file-backed write calls.</param>
/// <param name="CoalescedGapBytes">The number of unchanged gap bytes written while bridging commit gaps.</param>
/// <param name="MaxCoalescedGapBytes">The largest unchanged gap bridged during the commit.</param>
/// <param name="RejectedGapCount">The number of positive byte gaps that stopped a file-backed write group.</param>
/// <param name="RejectedGapBytes">The total size of positive byte gaps that stopped file-backed write groups.</param>
/// <param name="MaxRejectedGapBytes">The largest positive byte gap that stopped a file-backed write group.</param>
/// <param name="OverlapBreakCount">The number of decreasing-offset or overlapping staged segments that stopped file-backed write grouping.</param>
/// <param name="FileCommitSliceBuildTicks">The Stopwatch ticks spent deriving final non-overlapping file commit slices.</param>
/// <param name="FileCommitCoveredRangeMergeTicks">The Stopwatch ticks spent merging final-slice covered ranges.</param>
/// <param name="FileCommitGroupShapeTicks">The Stopwatch ticks spent finding file commit write groups.</param>
/// <param name="FileCommitBufferBuildTicks">The Stopwatch ticks spent building vectored write buffers for grouped file commits.</param>
/// <param name="FileCommitGapReadTicks">The Stopwatch ticks spent reading unchanged gap bytes bridged into grouped file commits.</param>
/// <param name="FileCommitBackingWriteTicks">The Stopwatch ticks spent issuing file-backed write calls.</param>
internal readonly record struct DataKernelCommitTelemetry(
    LibraDexDiagnosticsLevel Level,
    long StagedExtentCount,
    long StagedSegmentCount,
    long WriteCallCount,
    long BackingWriteCallCount,
    long SetLengthCallCount,
    long FlushCallCount,
    long BytesWritten,
    long ElapsedTicks,
    long CoalescedAdjacentSegmentCount,
    long CoalescedGapCount,
    long CoalescedGapBytes,
    long MaxCoalescedGapBytes,
    long RejectedGapCount,
    long RejectedGapBytes,
    long MaxRejectedGapBytes,
    long OverlapBreakCount,
    long FileCommitSliceBuildTicks,
    long FileCommitCoveredRangeMergeTicks,
    long FileCommitGroupShapeTicks,
    long FileCommitBufferBuildTicks,
    long FileCommitGapReadTicks,
    long FileCommitBackingWriteTicks);
