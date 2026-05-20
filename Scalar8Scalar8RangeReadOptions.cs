namespace LibraDex;

/// <summary>
/// Describes storage-facing policy for an `SS8-8` routed range read.<br/>
/// This is an internal API-shape checkpoint: callers choose route-cache behavior and whether multi-shelf coalescing is allowed instead of hiding those costs behind convenience defaults.<br/>
/// </summary>
/// <param name="RouteReadPolicy">The router/classifier read policy used while walking route targets.</param>
/// <param name="EnableCoalescing">Whether the reader may use caller-owned range scratch to coalesce adjacent shelf extents.</param>
/// <param name="MinimumRootPrefixSpanForTraversalCoalescing">The minimum inclusive root-prefix width before generalized traversal coalescing is considered.</param>
/// <param name="CoalescedShelfScratchCount">The preferred number of shelf extents to reserve when a pooled convenience wrapper owns range scratch.</param>
internal readonly record struct Scalar8Scalar8RangeReadOptions(
    Scalar8Scalar8RouteReadPolicy RouteReadPolicy,
    bool EnableCoalescing,
    int MinimumRootPrefixSpanForTraversalCoalescing,
    int CoalescedShelfScratchCount)
{
    /// <summary>
    /// Gets the conservative range-read policy.<br/>
    /// This mode uses the arena-aware route policy but does not opt into range coalescing, making it suitable for small or mixed route spans where coalescing may add reads or byte movement.<br/>
    /// </summary>
    public static Scalar8Scalar8RangeReadOptions ConservativeCached { get; } = new(
        Scalar8Scalar8RouteReadPolicy.PreferArenaCache,
        EnableCoalescing: false,
        MinimumRootPrefixSpanForTraversalCoalescing: 4,
        CoalescedShelfScratchCount: 1);

    /// <summary>
    /// Gets the wide-span range-read policy.<br/>
    /// This mode uses caller-owned range scratch for adjacent shelf coalescing and keeps generalized traversal coalescing behind the root-prefix-width gate.<br/>
    /// </summary>
    public static Scalar8Scalar8RangeReadOptions CoalescingCached { get; } = new(
        Scalar8Scalar8RouteReadPolicy.PreferArenaCache,
        EnableCoalescing: true,
        MinimumRootPrefixSpanForTraversalCoalescing: 4,
        CoalescedShelfScratchCount: 8);

    /// <summary>
    /// Validates the policy values before they are used by a routed range read.<br/>
    /// The gate must be positive because a zero-width or negative threshold would make generalized coalescing effectively unconditional and hide a performance-sensitive branch.<br/>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a numeric policy value is outside the supported range.</exception>
    public void Validate()
    {
        if (MinimumRootPrefixSpanForTraversalCoalescing <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumRootPrefixSpanForTraversalCoalescing),
                MinimumRootPrefixSpanForTraversalCoalescing,
                "The traversal coalescing root-prefix span gate must be positive.");
        }

        if (CoalescedShelfScratchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CoalescedShelfScratchCount),
                CoalescedShelfScratchCount,
                "The pooled coalescing shelf scratch count must be positive.");
        }
    }
}
