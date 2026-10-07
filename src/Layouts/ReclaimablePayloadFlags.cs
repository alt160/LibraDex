namespace LibraDex.Layouts;

/// <summary>
/// Encodes exact orphaned variable-record bytes into the persisted shelf flags word.<br/>
/// Bit zero remains reserved for shape-specific flags such as the VS8 duplicate-run marker; bits one through thirty-one store the reclaimable byte count without changing shelf size or adding delete-time I/O.<br/>
/// </summary>
internal static class ReclaimablePayloadFlags
{
    private const int ByteCountShift = 1;
    private const uint PreservedFlagMask = 1U;
    private const int MaximumByteCount = int.MaxValue;

    /// <summary>
    /// Decodes the exact orphaned variable-record byte count from one shelf flags value.<br/>
    /// </summary>
    /// <param name="flags">The persisted shelf flags word.<br/></param>
    /// <returns>The non-negative reclaimable payload byte count.<br/></returns>
    internal static int Read(uint flags)
        => checked((int)(flags >> ByteCountShift));

    /// <summary>
    /// Replaces the encoded orphaned byte count while preserving the shape-specific low flag bit.<br/>
    /// </summary>
    /// <param name="flags">The current persisted shelf flags word.<br/></param>
    /// <param name="byteCount">The exact non-negative orphaned record byte count.<br/></param>
    /// <returns>The updated persisted flags word.<br/></returns>
    internal static uint Write(uint flags, int byteCount)
    {
        if (byteCount < 0 || byteCount > MaximumByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Reclaimable payload bytes must be non-negative and fit the persisted 31-bit shelf counter.");

        return (flags & PreservedFlagMask) | (checked((uint)byteCount) << ByteCountShift);
    }
}
