namespace LibraDex;

/// <summary>
/// Captures a bounded write-side observation for a routed `VS16` shelf mutation.<br/>
/// The hint is approximate and is not authoritative; cold transform code must still validate against the actual shelf key population before choosing a router shape.<br/>
/// The observation is produced from already-adjacent sorted keys so ordinary writes do not scan the shelf or allocate new analysis structures.<br/>
/// </summary>
/// <param name="Sampled">Whether the insert had at least one adjacent key to compare.</param>
/// <param name="ComparedNeighborCount">The number of adjacent keys compared against the incoming key.</param>
/// <param name="StartDepth">The key byte depth where the bounded comparison started.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute key byte depth observed as common with an adjacent key.</param>
/// <param name="MaxCommonPrefixBytes">The number of common bytes observed at or after <paramref name="StartDepth"/>.</param>
internal readonly record struct VarKeyScalar16MutationHint(
    bool Sampled,
    int ComparedNeighborCount,
    int StartDepth,
    int MaxCommonPrefixDepth,
    int MaxCommonPrefixBytes);

/// <summary>
/// Aggregates session-local write-side observations for one routed `VS16` parent route.<br/>
/// The aggregate is intentionally small and advisory so it can steer cold transform policy without becoming a persisted routing contract.<br/>
/// </summary>
/// <param name="SampleCount">The number of sampled mutations recorded for the route.</param>
/// <param name="MaxCommonPrefixBytes">The largest bounded common-prefix byte count observed for the route.</param>
/// <param name="MaxCommonPrefixDepth">The deepest absolute common-prefix depth observed for the route.</param>
internal readonly record struct VarKeyScalar16RouteMutationHint(
    int SampleCount,
    int MaxCommonPrefixBytes,
    int MaxCommonPrefixDepth);

/// <summary>
/// Identifies one parent router route for session-local `VS16` mutation hint aggregation.<br/>
/// The route index is stored instead of only the prefix byte so compressed and multi-byte routers can participate without losing their selected route slot.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The file offset of the parent router that selected the shelf.</param>
/// <param name="RouteIndex">The selected route slot index in that parent router.</param>
internal readonly record struct VarKeyScalar16RouteHintKey(
    long ParentRouterOffset,
    int RouteIndex);
