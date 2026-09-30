using LibraDex;
using LibraDex.Layouts;

internal static partial class RawHarness
{
    /// <summary>
    /// Checks the exact physical write budget of the isolated depth-three walked-transform fixtures.<br/>
    /// Setup has already created the allocation classes and segments; this operation appends two shelves and one router, and rewrites the source as one router.<br/>
    /// The 32-KiB shelves claim one shared allocation header and the appended router claims another; 40-KiB shelves bypass the power-of-two allocator and claim only the router header.<br/>
    /// Allocation claims publish before payload, so their writes must not be removed or merged across publication phases to satisfy an obsolete payload-only budget.<br/>
    /// This exact fixture budget is not a general bound for segment rollover, shared-parent refinement, gap coalescing, or other transform topologies.<br/>
    /// </summary>
    /// <param name="commit">Physical commit telemetry including allocator metadata.<br/></param>
    /// <param name="shelfExtentSize">The fixture's replacement shelf extent size.<br/></param>
    /// <param name="allocationHeaderPages">Distinct existing allocation-segment headers claimed by this fixture.<br/></param>
    private static void ValidateAllocatedWalkedTransformCommit(DataKernelCommitTelemetry commit, int shelfExtentSize, int allocationHeaderPages)
    {
        long expectedBytes = (2L * shelfExtentSize) + (2L * RouterLayout.Size) +
            ((long)allocationHeaderPages * FileAllocationSegmentLayout.HeaderSize);
        long expectedWrites = 2 + allocationHeaderPages;
        if (commit.BytesWritten != expectedBytes || commit.WriteCallCount != expectedWrites ||
            commit.BackingWriteCallCount != expectedWrites || commit.SetLengthCallCount != 0 ||
            commit.FlushCallCount != 0 || commit.CoalescedGapBytes != 0 || commit.StagedExtentCount != 4)
        {
            throw new InvalidDataException($"Allocated walked transform write budget drifted: bytes={commit.BytesWritten}/{expectedBytes}, writes={commit.WriteCallCount}/{expectedWrites}, backingWrites={commit.BackingWriteCallCount}, SetLength={commit.SetLengthCallCount}, flush={commit.FlushCallCount}, gapBytes={commit.CoalescedGapBytes}, extents={commit.StagedExtentCount}/4.");
        }
    }
}
