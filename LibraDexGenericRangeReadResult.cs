namespace LibraDex;

/// <summary>
/// Reports the public result of reading identities from a generic LibraDex index range.<br/>
/// The caller owns the destination span; this result reports the number of identities copied plus the scratch/coalescing shape used by the underlying routed read.<br/>
/// </summary>
/// <param name="IdentityCount">The number of identities copied into the caller-owned destination span.</param>
/// <param name="UsedPooledScratch">Whether the underlying convenience read rented pooled scratch for routed shelf range traversal.</param>
/// <param name="UsedEncodedIdentityScratch">Whether the generic wrapper rented encoded identity scratch before decoding into the caller-owned destination span.</param>
/// <param name="CoalescingEnabled">Whether adjacent shelf coalescing was enabled for the read.</param>
/// <param name="RangeScratchShelfCapacity">The number of profiled shelf extents represented by the routed range scratch path.</param>
public readonly record struct LibraDexGenericRangeReadResult(
    int IdentityCount,
    bool UsedPooledScratch,
    bool UsedEncodedIdentityScratch,
    bool CoalescingEnabled,
    int RangeScratchShelfCapacity);
