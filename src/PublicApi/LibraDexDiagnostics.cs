namespace LibraDex;

/// <summary>
/// Selects how much operation diagnostic data LibraDex should collect for public result objects and stats.<br/>
/// `Counters` is the intended low-overhead level for development and production spot checks; `Detailed` is reserved for future opt-in diagnostics that may collect more expensive fields.<br/>
/// </summary>
public enum LibraDexDiagnosticsLevel
{
    /// <summary>
    /// Disables optional diagnostic counters where the active storage path can avoid collecting them.<br/>
    /// Semantic result fields such as inserted counts still remain available.<br/>
    /// </summary>
    Off = 0,

    /// <summary>
    /// Collects fixed numeric counters such as bytes written and issued read/write call counts.<br/>
    /// This level should remain allocation-free and suitable for normal troubleshooting.<br/>
    /// </summary>
    Counters = 1,

    /// <summary>
    /// Enables the most detailed diagnostics supported by the current build.<br/>
    /// This level is reserved for future fields and should be treated as an explicit troubleshooting mode.<br/>
    /// </summary>
    Detailed = 2
}

/// <summary>
/// Reports fixed-field diagnostics for one LibraDex storage operation.<br/>
/// Diagnostics describe what LibraDex did to complete an operation; semantic outcomes such as inserted, deleted, or returned counts remain on the surrounding result object.<br/>
/// </summary>
/// <param name="Level">The diagnostics level that produced this value.</param>
/// <param name="StagedExtentCount">The number of logical append extents represented by the operation.</param>
/// <param name="StagedSegmentCount">The number of staged write buffers committed.</param>
/// <param name="WriteCallCount">The number of positional write calls issued by LibraDex.</param>
/// <param name="BackingWriteCallCount">The number of logical backing-store write calls issued by LibraDex.</param>
/// <param name="SetLengthCallCount">The number of file length changes issued by LibraDex.</param>
/// <param name="FlushCallCount">The number of flush calls issued by LibraDex.</param>
/// <param name="BytesWritten">The number of staged bytes written.</param>
/// <param name="ElapsedTicks">The Stopwatch ticks spent inside the operation.</param>
/// <param name="CoalescedAdjacentSegmentCount">The number of staged segments joined to a previous segment with no byte gap.</param>
/// <param name="CoalescedGapCount">The number of unchanged byte gaps bridged to reduce file-backed write calls.</param>
/// <param name="CoalescedGapBytes">The number of unchanged gap bytes written while bridging commit gaps.</param>
/// <param name="MaxCoalescedGapBytes">The largest unchanged gap bridged during the operation.</param>
/// <param name="RejectedGapCount">The number of positive byte gaps that stopped a file-backed write group.</param>
/// <param name="RejectedGapBytes">The total size of positive byte gaps that stopped file-backed write groups.</param>
/// <param name="MaxRejectedGapBytes">The largest positive byte gap that stopped file-backed write grouping.</param>
/// <param name="OverlapBreakCount">The number of decreasing-offset or overlapping staged segments that stopped file-backed write grouping.</param>
/// <param name="FileCommitSliceBuildTicks">The Stopwatch ticks spent deriving final non-overlapping file commit slices.</param>
/// <param name="FileCommitCoveredRangeMergeTicks">The Stopwatch ticks spent merging final-slice covered ranges.</param>
/// <param name="FileCommitGroupShapeTicks">The Stopwatch ticks spent finding file commit write groups.</param>
/// <param name="FileCommitBufferBuildTicks">The Stopwatch ticks spent building vectored write buffers for grouped file commits.</param>
/// <param name="FileCommitGapReadTicks">The Stopwatch ticks spent reading unchanged gap bytes bridged into grouped file commits.</param>
/// <param name="FileCommitBackingWriteTicks">The Stopwatch ticks spent issuing file-backed write calls.</param>
public readonly record struct LibraDexOperationDiagnostics(
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
    long FileCommitBackingWriteTicks)
{
    internal static LibraDexOperationDiagnostics FromDataKernel(DataKernelCommitTelemetry telemetry)
    {
        return new LibraDexOperationDiagnostics(
            telemetry.Level,
            telemetry.StagedExtentCount,
            telemetry.StagedSegmentCount,
            telemetry.WriteCallCount,
            telemetry.BackingWriteCallCount,
            telemetry.SetLengthCallCount,
            telemetry.FlushCallCount,
            telemetry.BytesWritten,
            telemetry.ElapsedTicks,
            telemetry.CoalescedAdjacentSegmentCount,
            telemetry.CoalescedGapCount,
            telemetry.CoalescedGapBytes,
            telemetry.MaxCoalescedGapBytes,
            telemetry.RejectedGapCount,
            telemetry.RejectedGapBytes,
            telemetry.MaxRejectedGapBytes,
            telemetry.OverlapBreakCount,
            telemetry.FileCommitSliceBuildTicks,
            telemetry.FileCommitCoveredRangeMergeTicks,
            telemetry.FileCommitGroupShapeTicks,
            telemetry.FileCommitBufferBuildTicks,
            telemetry.FileCommitGapReadTicks,
            telemetry.FileCommitBackingWriteTicks);
    }
}

/// <summary>
/// Reports the current and cumulative state of one file session's concurrent-write admission queue.<br/>
/// All values are fixed counters; reading this snapshot does not enumerate pending requests or allocate a diagnostic collection.<br/>
/// </summary>
public readonly record struct LibraDexWriteAdmissionDiagnostics(
    int ActiveWriters,
    int QueuedWriters,
    int MaximumQueuedWriters,
    long TotalQueuedWriters,
    long TotalGrantedWriters,
    long CanceledWriters,
    long TimedOutWriters,
    long RejectedWriters,
    long TotalQueueWaitTicks,
    long MaximumQueueWaitTicks)
{
    /// <summary>
    /// Gets the number of fixed-scalar ownership conflicts that waited for an owner-release notification.<br/>
    /// </summary>
    public long ShelfWaitCount { get; init; }

    /// <summary>
    /// Gets the total Stopwatch ticks spent waiting for fixed-scalar shelf ownership release.<br/>
    /// </summary>
    public long ShelfWaitTicks { get; init; }
}
