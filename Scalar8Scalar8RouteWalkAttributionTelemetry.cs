namespace LibraDex;

/// <summary>
/// Carries internal timing counters for diagnostic `SS8-8` route walking.<br/>
/// The counters split direct-router and persisted-router work so routing experiments can be judged against concrete low-level operations.<br/>
/// </summary>
internal struct Scalar8Scalar8RouteWalkAttributionTelemetry
{
    internal long DirectViewLookupTicks;
    internal long DirectPrefixTicks;
    internal long DirectSlotLoadTicks;
    internal long DirectTargetClassificationTicks;
    internal long DirectRouterTargetCount;
    internal long DirectShelfTargetCount;
    internal long NonDirectRouterReadTicks;
    internal long NonDirectPrefixTicks;
    internal long NonDirectRouteLookupTicks;
    internal long NonDirectTargetClassificationTicks;
    internal long NonDirectRouterTargetCount;
    internal long NonDirectShelfTargetCount;
}
