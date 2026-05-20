namespace LibraDex;

/// <summary>
/// Reports the outcome of an encoded `SS8-8` identity range read.<br/>
/// This is the first outer range result contract: it keeps the returned identity count explicit and records whether the call used pooled scratch so perf reports can distinguish convenience from reusable-scratch hot paths.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned output span.</param>
/// <param name="UsedPooledScratch">Whether the call rented temporary byte scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether the selected options allowed adjacent shelf coalescing for this read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents the supplied or pooled range scratch could hold.</param>
internal readonly record struct Scalar8Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);
