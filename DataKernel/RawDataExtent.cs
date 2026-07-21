namespace LibraDex;

/// <summary>
/// Identifies a raw byte extent staged or read through the DataKernel.<br/>
/// This is intentionally structural and does not yet carry shelf, router, or index meaning.<br/>
/// </summary>
/// <param name="Offset">The direct file offset where the extent starts.</param>
/// <param name="Length">The extent length in bytes.</param>
internal readonly record struct RawDataExtent(long Offset, int Length);
