namespace LibraDex;

/// <summary>
/// Carries caller-owned scratch buffers for an `SS8-8` routed range read.<br/>
/// The one-shelf buffer is required for the conservative path; the range buffer is optional and enables adjacent shelf coalescing when policy allows it.<br/>
/// </summary>
internal readonly ref struct Scalar8Scalar8RangeReadScratch
{
    /// <summary>
    /// Initializes range-read scratch from caller-owned buffers.<br/>
    /// Passing an empty range scratch span keeps the read on the conservative one-shelf path even when options allow coalescing.<br/>
    /// </summary>
    /// <param name="shelf">The caller-owned buffer for one profiled shelf extent.</param>
    /// <param name="range">The caller-owned buffer for one or more profiled shelf extents.</param>
    public Scalar8Scalar8RangeReadScratch(Span<byte> shelf, Span<byte> range)
    {
        Shelf = shelf;
        Range = range;
    }

    /// <summary>
    /// Gets the required one-shelf scratch buffer.<br/>
    /// This buffer is used by the conservative range path and as fallback when range coalescing is disabled or not sufficiently provisioned.<br/>
    /// </summary>
    public Span<byte> Shelf { get; }

    /// <summary>
    /// Gets the optional multi-shelf range scratch buffer.<br/>
    /// This buffer is used only when range coalescing is enabled and it can hold at least one profiled shelf extent.<br/>
    /// </summary>
    public Span<byte> Range { get; }
}
