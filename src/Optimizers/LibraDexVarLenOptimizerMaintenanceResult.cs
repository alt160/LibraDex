namespace LibraDex;

/// <summary>
/// Describes one bounded variable-length route optimizer maintenance publish attempt.<br/>
/// This result intentionally separates queued candidate evidence from refreshed route state so stale queue records are visible without being treated as failures.<br/>
/// The replacement subtree bytes are staged by the caller; this result covers the session-owned refresh and version-checked route publication boundary.<br/>
/// </summary>
/// <param name="ConsideredCount">The number of queued optimizer candidates considered by the maintenance pass.</param>
/// <param name="AttemptedCount">The number of optimizer candidates attempted by the bounded maintenance pass.</param>
/// <param name="PublishedCount">The number of replacement targets successfully published.</param>
/// <param name="CoveredCandidateCount">The number of queued candidates skipped because an earlier publish at the same boundary already covered their root-prefix route.</param>
/// <param name="StaleCandidateTargetCount">The number of candidates whose queued target no longer matched the refreshed route target.</param>
/// <param name="RejectedCount">The number of publish attempts rejected by the route version check.</param>
/// <param name="BuiltKeyCount">The number of keys used by the caller to build the replacement subtree.</param>
/// <param name="CandidateTargetOffset">The target offset captured when the candidate was queued.</param>
/// <param name="RefreshedTargetOffset">The target offset read immediately before publication.</param>
/// <param name="ReplacementTargetOffset">The replacement target offset staged by the caller.</param>
/// <param name="Elapsed">The elapsed time spent inside the session-owned refresh and publish boundary.</param>
internal readonly record struct VarLenOptimizerMaintenanceResult(
    int ConsideredCount,
    int AttemptedCount,
    int PublishedCount,
    int CoveredCandidateCount,
    int StaleCandidateTargetCount,
    int RejectedCount,
    int BuiltKeyCount,
    long CandidateTargetOffset,
    long RefreshedTargetOffset,
    long ReplacementTargetOffset,
    TimeSpan Elapsed);
