namespace LibraDex;

/// <summary>
/// Reports the public result of reading encoded identities from a `Scalar8Scalar8Index` wrapper.<br/>
/// The result keeps identity count and scratch/coalescing shape visible while the caller continues to own the output identity span.<br/>
/// </summary>
/// <param name="IdentityCount">The number of encoded identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the convenience read rented scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the range scratch path.</param>
public readonly record struct Scalar8Scalar8EncodedRangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);
