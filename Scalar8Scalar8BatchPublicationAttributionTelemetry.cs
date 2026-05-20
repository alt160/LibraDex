namespace LibraDex;

/// <summary>
/// Carries internal dirty-shelf publication attribution for one `SS8-8` batch run.<br/>
/// The counters estimate how much of each cached dirty shelf changed before the current full-shelf rewrite is staged.<br/>
/// </summary>
internal struct Scalar8Scalar8BatchPublicationAttributionTelemetry
{
    internal long DirtyShelfCount;
    internal long FullShelfBytes;
    internal long RawChangedBytes;
    internal long RawChangedRangeCount;
    internal long DeltaCandidateBytes;
    internal long DeltaCandidateRangeCount;
    internal long HeaderDeltaBytes;
    internal long SlotDeltaBytes;
    internal long ItemDeltaBytes;
    internal long MaxDeltaCandidateBytesPerShelf;
    internal long FullShelfBetterOrEqualCount;
}
