namespace LibraDex;

/// <summary>
/// Reports the public result of inserting one unsigned scalar key and unsigned scalar identity through an `UnsignedScalar8Scalar8Index` wrapper.<br/>
/// The result mirrors the encoded insert telemetry while keeping the developer-facing operation typed as unsigned scalar values.<br/>
/// </summary>
/// <param name="Outcome">The typed operation-facing insert outcome.</param>
/// <param name="CreatedInitialShelfRoute">Whether the wrapper created the first shelf route for the tuple's root prefix before inserting.</param>
/// <param name="RouteCreateCommit">Commit telemetry for initial shelf-route creation, or default when no route was created.</param>
/// <param name="InsertCommit">Commit telemetry for the insert mutation, or default when the tuple already existed or was rejected.</param>
public readonly record struct UnsignedScalar8Scalar8InsertResult(
    UnsignedScalar8Scalar8InsertOutcome Outcome,
    bool CreatedInitialShelfRoute,
    DataKernelCommitTelemetry RouteCreateCommit,
    DataKernelCommitTelemetry InsertCommit);
