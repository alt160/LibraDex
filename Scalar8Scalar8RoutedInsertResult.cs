namespace LibraDex;

/// <summary>
/// Describes the structural path taken by an internal routed `Scalar8Scalar8` insert.<br/>
/// This is intentionally storage-facing and reports how the index changed, not the eventual public API outcome wording.<br/>
/// </summary>
internal enum Scalar8Scalar8RoutedInsertKind
{
    /// <summary>
    /// The tuple already existed or the shelf reported a structural no-op.<br/>
    /// No commit telemetry is expected for this outcome.<br/>
    /// </summary>
    NoOp = 0,

    /// <summary>
    /// The tuple fit inside the routed shelf and only that shelf extent was rewritten.<br/>
    /// </summary>
    NoSplit = 1,

    /// <summary>
    /// The tuple fit inside a shelf reached through one child router and only that shelf extent was rewritten.<br/>
    /// </summary>
    TwoLevelNoSplit = 5,

    /// <summary>
    /// The tuple fit inside a shelf reached by the classified route walker and only that shelf extent was rewritten.<br/>
    /// </summary>
    WalkedNoSplit = 6,

    /// <summary>
    /// The tuple required a walked same-prefix split that transformed the full shelf into the next-depth router and appended two shelves.<br/>
    /// </summary>
    WalkedShelfTransformSplit = 7,

    /// <summary>
    /// The tuple required a walked parent-router route refinement that rewrote the original shelf, appended one shelf, and rewrote the parent router.<br/>
    /// </summary>
    WalkedParentRouteSplit = 8,

    /// <summary>
    /// The tuple required a root-prefix-visible split that rewrote the old shelf, appended one shelf, and rewrote the root router.<br/>
    /// </summary>
    RootPrefixSplit = 2,

    /// <summary>
    /// The tuple required a same-root-prefix split that transformed the old shelf offset into a child router and appended two shelves.<br/>
    /// </summary>
    ShelfTransformSplit = 3,

    /// <summary>
    /// The insert could not proceed because the selected uniqueness policy rejected the key.<br/>
    /// </summary>
    KeyConflict = 4
}

/// <summary>
/// Captures the storage-facing result of an internal routed `Scalar8Scalar8` insert decision.<br/>
/// Offsets are populated according to the selected structural path so the harness can validate route stability and file-write shape.<br/>
/// </summary>
/// <param name="Kind">The structural path selected by the insert decision.</param>
/// <param name="InsertResult">The shelf-level insert result that drove the final outcome.</param>
/// <param name="PrimaryOffset">The primary offset affected by the operation, usually the routed shelf or transformed child router.</param>
/// <param name="LeftShelfOffset">The left shelf offset after a split, or zero when not applicable.</param>
/// <param name="RightShelfOffset">The right shelf offset after a split, or zero when not applicable.</param>
/// <param name="Commit">The commit telemetry for physical mutations, or default when no commit occurred.</param>
internal readonly record struct Scalar8Scalar8RoutedInsertResult(
    Scalar8Scalar8RoutedInsertKind Kind,
    Scalar8Scalar8InsertResult InsertResult,
    long PrimaryOffset,
    long LeftShelfOffset,
    long RightShelfOffset,
    DataKernelCommitTelemetry Commit);
