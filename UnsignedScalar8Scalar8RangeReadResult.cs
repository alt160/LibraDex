namespace LibraDex;

/// <summary>
/// Reports the public result of reading unsigned scalar identities from an `UnsignedScalar8Scalar8Index` wrapper.<br/>
/// The caller owns the destination span; this result reports the number of identities copied plus the read scratch/coalescing shape.<br/>
/// </summary>
/// <param name="IdentityCount">The number of unsigned scalar identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the convenience read rented scratch from the shared array pool.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the range scratch path.</param>
public readonly record struct UnsignedScalar8Scalar8RangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);
