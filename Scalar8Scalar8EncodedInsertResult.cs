namespace LibraDex;

/// <summary>
/// Reports the public result of inserting one encoded tuple through a `Scalar8Scalar8Index` wrapper.<br/>
/// First-in-prefix inserts may create an empty routed shelf before inserting the tuple; both commit shapes are reported so convenience does not hide structural I/O.<br/>
/// </summary>
/// <param name="Outcome">The operation-facing insert outcome.</param>
/// <param name="CreatedInitialShelfRoute">Whether the wrapper created the first shelf route for the tuple's root prefix before inserting.</param>
/// <param name="RouteCreateCommit">Commit telemetry for initial shelf-route creation, or default when no route was created.</param>
/// <param name="InsertCommit">Commit telemetry for the insert mutation, or default when the tuple already existed or was rejected.</param>
public readonly record struct Scalar8Scalar8EncodedInsertResult(
    Scalar8Scalar8EncodedInsertOutcome Outcome,
    bool CreatedInitialShelfRoute,
    DataKernelCommitTelemetry RouteCreateCommit,
    DataKernelCommitTelemetry InsertCommit)
{
    /// <summary>
    /// Gets the internal routed mutation kind selected by the storage path.<br/>
    /// This is internal harness-facing attribution data so public callers are not forced to reason about shelf/router implementation details.<br/>
    /// </summary>
    internal Scalar8Scalar8RoutedInsertKind StructuralKind { get; init; }

    /// <summary>
    /// Gets the primary storage offset affected by the routed mutation.<br/>
    /// For no-split inserts this is the rewritten shelf offset; for transform splits this is the new child-router offset.<br/>
    /// </summary>
    internal long PrimaryOffset { get; init; }

    /// <summary>
    /// Gets the left shelf offset after a routed split, or the rewritten shelf offset for no-split inserts.<br/>
    /// The value is zero when the insert did not mutate a shelf.<br/>
    /// </summary>
    internal long LeftShelfOffset { get; init; }

    /// <summary>
    /// Gets the right shelf offset after a routed split.<br/>
    /// The value is zero for no-split inserts and for rejected or no-op inserts.<br/>
    /// </summary>
    internal long RightShelfOffset { get; init; }
}
