namespace LibraDex;

/// <summary>
/// Represents a contiguous staged byte reservation owned by the <see cref="DataKernel"/>.<br/>
/// The reservation is already part of the pending append stream when it is returned.<br/>
/// Callers should fill <see cref="Span"/> immediately and then use <see cref="Extent"/> as the future committed location.<br/>
/// </summary>
public readonly ref struct RawDataReservation
{
    internal RawDataReservation(RawDataExtent extent, Span<byte> span)
    {
        Extent = extent;
        Span = span;
    }

    /// <summary>
    /// Gets the raw extent assigned to the reservation.<br/>
    /// The extent identifies where the reserved bytes will live after the next successful commit.<br/>
    /// </summary>
    public RawDataExtent Extent { get; }

    /// <summary>
    /// Gets the contiguous staged bytes that the caller should fill.<br/>
    /// The span is a direct view over DataKernel-owned staged memory and avoids a second append copy.<br/>
    /// </summary>
    public Span<byte> Span { get; }
}
