using System.Diagnostics;

namespace LibraDex;

/// <summary>
/// Accumulates temporary, opt-in `FS32-8` and `FS32-16` sorted-batch attribution on the current benchmark thread.<br/>
/// Disabled execution retains a null thread-local reference, while enabled execution performs direct primitive increments and emits one summary after the measured workload.<br/>
/// </summary>
internal sealed class Fixed32BatchAttributionDiagnostics
{
    [ThreadStatic]
    private static Fixed32BatchAttributionDiagnostics? current;

    internal long BatchCount;
    internal long InsertAttempts;
    internal long InPlaceInserts;
    internal long NonInsertOutcomes;
    internal long FastRouteResolutions;
    internal long RootRouteWalks;
    internal long ActiveShelfHits;
    internal long ShelfCacheHits;
    internal long ShelfReads;
    internal long SortedTailSplits;
    internal long ParentRouteSplits;
    internal long ShelfTransformSplits;
    internal long SplitNoOps;
    internal long SortedRejectedInvalidOrNotFull;
    internal long SortedRejectedNotAppend;
    internal long SortedRejectedNoPrefixBoundary;
    internal long SortedParentDepthImpossibleFastRejects;
    internal long SortedScannedSlots;
    internal long SortedCopiedItems;
    internal long FlushShelfCount;
    internal long FlushBytes;
    internal long DeferredWriteRequests;
    internal long CommittedBytes;
    internal long RouteTicks;
    internal long InsertTicks;
    internal long SplitTicks;
    internal long SortedSplitTicks;
    internal long ParentSplitTicks;
    internal long TransformSplitTicks;
    internal long TransformMergeTicks;
    internal long TransformPlanTicks;
    internal long TransformShelfBuildTicks;
    internal long TransformShelfCopyTicks;
    internal long TransformRouterBuildTicks;
    internal long TransformCommitTicks;
    internal long ShelfReadTicks;
    internal long ContinuationTicks;
    internal long FlushTicks;
    internal long PublishTicks;
    internal long BatchTicks;
    internal long BatchAllocatedBytes;

    /// <summary>
    /// Gets the attribution accumulator enabled for the current thread, or <see langword="null"/> when diagnostics are disabled.<br/>
    /// </summary>
    internal static Fixed32BatchAttributionDiagnostics? Current => current;

    /// <summary>
    /// Enables a fresh attribution accumulator for the current thread.<br/>
    /// The single allocation occurs before benchmark timing and no diagnostic collections, delegates, or per-entry objects are created afterward.<br/>
    /// </summary>
    internal static void Reset()
    {
        current = new Fixed32BatchAttributionDiagnostics();
    }

    /// <summary>
    /// Disables attribution on the current thread and releases its accumulator for collection.<br/>
    /// </summary>
    internal static void Disable()
    {
        current = null;
    }

    /// <summary>
    /// Records one completed `FS32-8` structural split classification without allocating an event or dispatch delegate.<br/>
    /// </summary>
    /// <param name="kind">The shape-specific structural result returned by the split path.<br/></param>
    /// <param name="elapsedTicks">The already-captured elapsed split ticks, avoiding another timestamp read.<br/></param>
    internal void Record(Fixed32Scalar8RoutedInsertKind kind, long elapsedTicks)
    {
        SplitTicks += elapsedTicks;
        if (kind == Fixed32Scalar8RoutedInsertKind.WalkedSortedTailSplit)
        {
            SortedTailSplits++;
            SortedSplitTicks += elapsedTicks;
        }
        else if (kind == Fixed32Scalar8RoutedInsertKind.WalkedParentRouteSplit)
        {
            ParentRouteSplits++;
            ParentSplitTicks += elapsedTicks;
        }
        else if (kind == Fixed32Scalar8RoutedInsertKind.WalkedShelfTransformSplit)
        {
            ShelfTransformSplits++;
            TransformSplitTicks += elapsedTicks;
        }
        else
            SplitNoOps++;
    }

    /// <summary>
    /// Records one completed `FS32-16` structural split classification without allocating an event or dispatch delegate.<br/>
    /// </summary>
    /// <param name="kind">The shape-specific structural result returned by the split path.<br/></param>
    /// <param name="elapsedTicks">The already-captured elapsed split ticks, avoiding another timestamp read.<br/></param>
    internal void Record(Fixed32Scalar16RoutedInsertKind kind, long elapsedTicks)
    {
        SplitTicks += elapsedTicks;
        if (kind == Fixed32Scalar16RoutedInsertKind.WalkedSortedTailSplit)
        {
            SortedTailSplits++;
            SortedSplitTicks += elapsedTicks;
        }
        else if (kind == Fixed32Scalar16RoutedInsertKind.WalkedParentRouteSplit)
        {
            ParentRouteSplits++;
            ParentSplitTicks += elapsedTicks;
        }
        else if (kind == Fixed32Scalar16RoutedInsertKind.WalkedShelfTransformSplit)
        {
            ShelfTransformSplits++;
            TransformSplitTicks += elapsedTicks;
        }
        else
            SplitNoOps++;
    }

    /// <summary>
    /// Creates one compact attribution line after workload timing has stopped.<br/>
    /// Tick values are converted with the runtime stopwatch frequency and all counters remain raw so derived hit rates can be independently checked.<br/>
    /// </summary>
    /// <returns>The stable `fs32-attribution` note consumed by ShapeBench output.<br/></returns>
    internal static string CreateSummary()
    {
        Fixed32BatchAttributionDiagnostics? x = current;
        if (x is null)
            return "fs32-attribution=disabled";

        double tickMs = 1000D / Stopwatch.Frequency;
        return
            $"fs32-attribution batches={x.BatchCount} attempts={x.InsertAttempts} in-place={x.InPlaceInserts} non-insert={x.NonInsertOutcomes} " +
            $"route-fast={x.FastRouteResolutions} route-walk={x.RootRouteWalks} active-shelf-hit={x.ActiveShelfHits} shelf-cache-hit={x.ShelfCacheHits} shelf-read={x.ShelfReads} " +
            $"split-sorted={x.SortedTailSplits} split-parent={x.ParentRouteSplits} split-transform={x.ShelfTransformSplits} split-noop={x.SplitNoOps} " +
            $"sorted-reject-invalid={x.SortedRejectedInvalidOrNotFull} sorted-reject-append={x.SortedRejectedNotAppend} sorted-reject-prefix={x.SortedRejectedNoPrefixBoundary} " +
            $"sorted-parent-impossible-fast-reject={x.SortedParentDepthImpossibleFastRejects} " +
            $"sorted-scan-slots={x.SortedScannedSlots} sorted-copy-items={x.SortedCopiedItems} flush-shelves={x.FlushShelfCount} flush-bytes={x.FlushBytes} " +
            $"deferred-writes={x.DeferredWriteRequests} committed-bytes={x.CommittedBytes} allocated-bytes={x.BatchAllocatedBytes} " +
            $"route-ms={x.RouteTicks * tickMs:F3} insert-ms={x.InsertTicks * tickMs:F3} shelf-read-ms={x.ShelfReadTicks * tickMs:F3} split-ms={x.SplitTicks * tickMs:F3} " +
            $"split-sorted-ms={x.SortedSplitTicks * tickMs:F3} split-parent-ms={x.ParentSplitTicks * tickMs:F3} split-transform-ms={x.TransformSplitTicks * tickMs:F3} continuation-ms={x.ContinuationTicks * tickMs:F3} " +
            $"transform-merge-ms={x.TransformMergeTicks * tickMs:F3} transform-plan-ms={x.TransformPlanTicks * tickMs:F3} transform-shelf-ms={x.TransformShelfBuildTicks * tickMs:F3} " +
            $"transform-copy-ms={x.TransformShelfCopyTicks * tickMs:F3} transform-router-ms={x.TransformRouterBuildTicks * tickMs:F3} transform-commit-ms={x.TransformCommitTicks * tickMs:F3} " +
            $"flush-ms={x.FlushTicks * tickMs:F3} publish-ms={x.PublishTicks * tickMs:F3} batch-ms={x.BatchTicks * tickMs:F3}";
    }
}
