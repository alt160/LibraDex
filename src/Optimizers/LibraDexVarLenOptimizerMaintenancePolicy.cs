namespace LibraDex;

/// <summary>
/// Identifies where variable-length route optimizer maintenance is allowed to run.<br/>
/// The value is internal for now because the first production task is separating scheduler/accounting behavior from the proven maintenance primitive before exposing a developer-facing API.<br/>
/// </summary>
internal enum VarLenOptimizerMaintenanceMode
{
    Disabled = 0,
    AutomaticBetweenHotActions = 1,
    OnDemand = 2
}

/// <summary>
/// Identifies the current execution boundary where optimizer maintenance is being considered.<br/>
/// Hot write code should only record cheap route-pressure facts; maintenance work belongs at explicit on-demand or between-hot-action boundaries.<br/>
/// </summary>
internal enum VarLenOptimizerMaintenanceBoundary
{
    HotWrite = 0,
    BetweenHotActions = 1,
    OnDemand = 2
}

/// <summary>
/// Describes the internal maintenance policy for one variable-length optimizer state.<br/>
/// The policy keeps trigger thresholds and scheduling mode together so callers do not hard-code depth, shelf fullness, hit count, or maintenance cadence in write loops.<br/>
/// It does not run maintenance by itself; it answers whether a queued candidate may be drained at a given boundary.<br/>
/// </summary>
/// <param name="Mode">The maintenance scheduling mode.</param>
/// <param name="DepthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
/// <param name="ShelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
/// <param name="HitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
/// <param name="MaxCandidatesPerBoundary">The maximum candidates that may be drained at one maintenance boundary.</param>
internal readonly record struct VarLenOptimizerMaintenancePolicy(
    VarLenOptimizerMaintenanceMode Mode,
    int DepthThreshold,
    int ShelfItemThreshold,
    int HitThreshold,
    int MaxCandidatesPerBoundary)
{
    /// <summary>
    /// Creates an automatic maintenance policy for bounded between-hot-action optimizer work.<br/>
    /// The returned policy allows maintenance only after a candidate has just been queued and the caller is at an idle or batch boundary.<br/>
    /// </summary>
    /// <param name="depthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
    /// <param name="shelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
    /// <param name="hitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
    /// <returns>An automatic bounded maintenance policy.</returns>
    public static VarLenOptimizerMaintenancePolicy Automatic(
        int depthThreshold,
        int shelfItemThreshold,
        int hitThreshold)
    {
        return new VarLenOptimizerMaintenancePolicy(
            VarLenOptimizerMaintenanceMode.AutomaticBetweenHotActions,
            depthThreshold,
            shelfItemThreshold,
            hitThreshold,
            MaxCandidatesPerBoundary: 1);
    }

    /// <summary>
    /// Creates an on-demand maintenance policy for explicit developer-requested optimizer work.<br/>
    /// The returned policy allows maintenance only when the caller has completed normal writes and intentionally asks for optimization.<br/>
    /// </summary>
    /// <param name="depthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
    /// <param name="shelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
    /// <param name="hitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
    /// <returns>An explicit on-demand maintenance policy.</returns>
    public static VarLenOptimizerMaintenancePolicy OnDemand(
        int depthThreshold,
        int shelfItemThreshold,
        int hitThreshold)
    {
        return OnDemand(depthThreshold, shelfItemThreshold, hitThreshold, maxCandidatesPerBoundary: 8);
    }

    /// <summary>
    /// Creates an on-demand maintenance policy for explicit developer-requested optimizer work with a caller-selected candidate cap.<br/>
    /// The returned policy allows maintenance only when the caller has completed normal writes and intentionally asks for optimization.<br/>
    /// The cap bounds cold maintenance work while still letting an explicit optimization pass drain more than the automatic one-candidate boundary.<br/>
    /// </summary>
    /// <param name="depthThreshold">The minimum routed depth required before an observation can count toward a candidate.</param>
    /// <param name="shelfItemThreshold">The minimum landing shelf item count required before an observation can count toward a candidate.</param>
    /// <param name="hitThreshold">The number of qualifying observations needed before the prefix is enqueued.</param>
    /// <param name="maxCandidatesPerBoundary">The maximum candidates that may be considered at one explicit on-demand boundary.</param>
    /// <returns>An explicit on-demand maintenance policy.</returns>
    public static VarLenOptimizerMaintenancePolicy OnDemand(
        int depthThreshold,
        int shelfItemThreshold,
        int hitThreshold,
        int maxCandidatesPerBoundary)
    {
        return new VarLenOptimizerMaintenancePolicy(
            VarLenOptimizerMaintenanceMode.OnDemand,
            depthThreshold,
            shelfItemThreshold,
            hitThreshold,
            Math.Max(maxCandidatesPerBoundary, 0));
    }

    /// <summary>
    /// Returns whether a queued candidate may be drained at the supplied scheduler boundary.<br/>
    /// Hot-write boundaries always return <see langword="false"/> so route optimization cannot silently enter the write hot path.<br/>
    /// The policy cap is intentionally separate from scheduling mode so automatic work can stay very small while explicit on-demand work can drain a bounded queue slice.<br/>
    /// </summary>
    /// <param name="boundary">The boundary where maintenance is being considered.</param>
    /// <param name="candidateQueued">Whether the current operation has an enqueued candidate available.</param>
    /// <param name="alreadyPublishedAtBoundary">The number of candidates already published at this boundary.</param>
    /// <returns><see langword="true"/> when the caller may drain one optimizer candidate.</returns>
    public bool ShouldDrain(
        VarLenOptimizerMaintenanceBoundary boundary,
        bool candidateQueued,
        int alreadyPublishedAtBoundary)
    {
        if (!candidateQueued ||
            MaxCandidatesPerBoundary <= 0 ||
            alreadyPublishedAtBoundary >= MaxCandidatesPerBoundary)
        {
            return false;
        }

        return Mode switch
        {
            VarLenOptimizerMaintenanceMode.AutomaticBetweenHotActions => boundary == VarLenOptimizerMaintenanceBoundary.BetweenHotActions,
            VarLenOptimizerMaintenanceMode.OnDemand => boundary == VarLenOptimizerMaintenanceBoundary.OnDemand,
            _ => false
        };
    }
}
