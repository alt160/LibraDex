namespace LibraDex;

/// <summary>
/// Defines the raw byte movement policy used by <see cref="DataKernel"/>.<br/>
/// These values are knobs/levers and should stay explicit rather than being hard-coded inside hot paths.<br/>
/// </summary>
/// <param name="AppendBufferSize">The preferred staged append buffer size, in bytes.</param>
/// <param name="ReservedPrefixBytes">The number of bytes reserved at the beginning of the file.</param>
/// <param name="FlushToDiskOnCommit">Whether commits should issue a durable flush after writes.</param>
/// <param name="MaxCommitGapCoalesceBytes">The largest unchanged byte gap that file-backed commit may bridge to reduce positional write calls.</param>
internal readonly record struct DataKernelOptions(
    int AppendBufferSize,
    long ReservedPrefixBytes,
    bool FlushToDiskOnCommit,
    int MaxCommitGapCoalesceBytes)
{
    internal const int DefaultAppendBufferSize = 64 * 1024;

    /// <summary>
    /// Gets the default raw DataKernel policy for early development.<br/>
    /// The reserved prefix leaves file offset zero unavailable for future route targets and superblock work.<br/>
    /// </summary>
    public static DataKernelOptions Default { get; } = new(
        AppendBufferSize: DefaultAppendBufferSize,
        ReservedPrefixBytes: 4096,
        FlushToDiskOnCommit: false,
        MaxCommitGapCoalesceBytes: 512);

    /// <summary>
    /// Validates the configured DataKernel policy before file I/O begins.<br/>
    /// This keeps policy errors out of lower-level read/write paths.<br/>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a policy value is outside the supported range.</exception>
    public void Validate()
    {
        if (AppendBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AppendBufferSize), AppendBufferSize, "Append buffer size must be greater than zero.");
        }

        if (ReservedPrefixBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ReservedPrefixBytes), ReservedPrefixBytes, "Reserved prefix bytes cannot be negative.");
        }

        if (MaxCommitGapCoalesceBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCommitGapCoalesceBytes), MaxCommitGapCoalesceBytes, "Commit gap coalescing bytes cannot be negative.");
        }

    }
}
