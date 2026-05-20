namespace LibraDex;

/// <summary>
/// Carries internal commit-path attribution for one `SS8-8` batch.<br/>
/// The counters are diagnostic-only and split batch publication work above the DataKernel commit boundary.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchCommitAttributionTelemetry
{
    internal long DirtyShelfFlushTicks;
    internal long PublicationAttributionTicks;
    internal long DeltaSelectionTicks;
    internal long DeltaStagingTicks;
    internal long FullShelfStagingTicks;
    internal long SplitFallbackFullShelfStagingTicks;
    internal long DataKernelCommitTicks;
    internal long DeltaShelfCount;
    internal long DeltaUngroupedSpanCount;
    internal long DeltaUngroupedBytes;
    internal long DeltaGrouped512SpanCount;
    internal long DeltaGrouped512Bytes;
    internal long DeltaGrouped1024SpanCount;
    internal long DeltaGrouped1024Bytes;
    internal long DeltaGrouped4096SpanCount;
    internal long DeltaGrouped4096Bytes;
    internal long DeltaGrouped8192SpanCount;
    internal long DeltaGrouped8192Bytes;
    internal long DeltaPositiveGapCount;
    internal long DeltaPositiveGapBytes;
    internal long DeltaMaxPositiveGapBytes;
}
