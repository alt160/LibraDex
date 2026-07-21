using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Owns pending mutation state for one LibraDex writer before that state is published through the session.<br/>
/// The first slice keeps existing single-owner batch behavior, but moves shape-specific dirty state behind an explicit writer context so later concurrency work can validate and publish independent writer-local changes.<br/>
/// </summary>
internal sealed class LibraDexWriteContext
{
    private Dictionary<long, byte[]>? scalar8Scalar8MutableBatchShelfBytes;
    private HashSet<long>? scalar8Scalar8DirtyShelfOffsets;
    private Dictionary<long, Scalar8Scalar8RouteClaim>? scalar8Scalar8RouteClaims;
    private Dictionary<long, object>? scalar8Scalar8ShelfDomainLocks;
    private Dictionary<long, byte[]>? scalar16Scalar8MutableBatchShelfBytes;
    private Dictionary<long, Scalar16Scalar8RouteClaim>? scalar16Scalar8RouteClaims;
    private Dictionary<long, byte[]>? scalar8Scalar16MutableBatchShelfBytes;
    private Dictionary<long, Scalar8Scalar16RouteClaim>? scalar8Scalar16RouteClaims;
    private Dictionary<long, byte[]>? scalar16Scalar16MutableBatchShelfBytes;
    private Dictionary<long, Scalar16Scalar16RouteClaim>? scalar16Scalar16RouteClaims;
    private Dictionary<long, byte[]>? fixed32Scalar8MutableBatchShelfBytes;
    private Dictionary<long, Fixed32Scalar8RouteClaim>? fixed32Scalar8RouteClaims;
    private Dictionary<long, byte[]>? fixed32Scalar16MutableBatchShelfBytes;
    private Dictionary<long, Fixed32Scalar16RouteClaim>? fixed32Scalar16RouteClaims;
    private Dictionary<long, byte[]>? fixedNScalar8MutableBatchShelfBytes;
    private Dictionary<long, FixedNScalar8RouteClaim>? fixedNScalar8RouteClaims;
    private Dictionary<long, byte[]>? fixedNScalar16MutableBatchShelfBytes;
    private Dictionary<long, FixedNScalar16RouteClaim>? fixedNScalar16RouteClaims;
    private Dictionary<long, byte[]>? terminalIdentity8MutableShelfBytes;
    private Dictionary<long, VarKeyScalar8MutableShelf>? varKeyScalar8MutableBatchShelves;
    private Dictionary<long, VarKeyScalar8RouteClaim>? varKeyScalar8RouteClaims;
    private Dictionary<long, VarKeyScalar16MutableShelf>? varKeyScalar16MutableBatchShelves;
    private Dictionary<long, VarKeyScalar16RouteClaim>? varKeyScalar16RouteClaims;
    private Dictionary<long, VarKeyVarIdentityMutableShelf>? varKeyVarIdentityMutableBatchShelves;
    private Dictionary<long, VarKeyVarIdentityRouteClaim>? varKeyVarIdentityRouteClaims;
    private Dictionary<long, byte[]>? varKeyScalar8RawMutableShelfBytes;
    private Dictionary<long, Scalar8VarIdentityMutableShelfView>? scalar8VarIdentityMutableBatchShelves;
    private Dictionary<long, Scalar8VarIdentityRouteClaim>? scalar8VarIdentityRouteClaims;
    private Dictionary<long, Scalar16VarIdentityMutableShelfView>? scalar16VarIdentityMutableBatchShelves;
    private Dictionary<long, Scalar16VarIdentityRouteClaim>? scalar16VarIdentityRouteClaims;

    internal LibraDexWriteContext(long operationToken)
    {
        OperationToken = operationToken;
    }

    /// <summary>
    /// Gets the diagnostic operation token associated with the active write window.<br/>
    /// A zero value means detailed ownership diagnostics were disabled for this context.<br/>
    /// </summary>
    internal long OperationToken { get; }

    /// <summary>
    /// Gets writer-local dirty `SS8-8` shelf images staged by the active batch.<br/>
    /// The dictionary remains private to the writer context until the session publish path validates and flushes it.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Scalar8Scalar8MutableBatchShelfBytes => scalar8Scalar8MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets the `SS8-8` ordinary shelf offsets whose writer-local images contain accepted mutations.<br/>
    /// A shelf may exist in <see cref="Scalar8Scalar8MutableBatchShelfBytes"/> only because an operation read and claimed it; publication must use this dirty set so unchanged read images are never mistaken for useful staged work.<br/>
    /// </summary>
    internal HashSet<long> Scalar8Scalar8DirtyShelfOffsets => scalar8Scalar8DirtyShelfOffsets ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SS8-8` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a shelf-local writer cannot publish an old shelf image after another writer transformed or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Scalar8Scalar8RouteClaim> Scalar8Scalar8RouteClaims => scalar8Scalar8RouteClaims ??= [];

    /// <summary>
    /// Gets primitive shelf-domain locks held by this writer context.<br/>
    /// These locks are acquired only for shelves whose bytes are staged in the context and are released when the context publishes or aborts.<br/>
    /// </summary>
    internal Dictionary<long, object> Scalar8Scalar8ShelfDomainLocks => scalar8Scalar8ShelfDomainLocks ??= [];

    /// <summary>
    /// Gets whether this context contains any accepted `SS8-8` ordinary or terminal shelf mutation.<br/>
    /// This is the authoritative empty-context signal used by conflict handling; caller operation counters are not sufficient because admission exceptions can occur between byte mutation and operation completion.<br/>
    /// </summary>
    internal bool HasScalar8Scalar8Mutations =>
        (scalar8Scalar8DirtyShelfOffsets?.Count ?? 0) != 0 ||
        (terminalIdentity8MutableShelfBytes?.Count ?? 0) != 0;

    /// <summary>
    /// Gets or sets the per-root topology read gate held for this `SS8-8` writer context.<br/>
    /// The gate remains held until publish or abort so broad topology fallback cannot rewrite a shelf after this context copied its committed bytes but before those staged bytes are published.<br/>
    /// </summary>
    internal ReaderWriterLockSlim? Scalar8Scalar8TopologyReadSync { get; set; }

    /// <summary>
    /// Gets writer-local dirty `SS16-8` shelf images staged by the active batch.<br/>
    /// These widened-key shelves are published through the same serialized session seam as `SS8-8`, but their staging and ownership maps stay shape-specific.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Scalar16Scalar8MutableBatchShelfBytes => scalar16Scalar8MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SS16-8` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a widened-key shelf-local writer cannot publish an old shelf image after another writer transformed, split, or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Scalar16Scalar8RouteClaim> Scalar16Scalar8RouteClaims => scalar16Scalar8RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `SS8-16` shelf images staged by the active batch.<br/>
    /// These wide-identity shelves are published through the same serialized session seam as the other fixed-scalar writer-context shapes.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Scalar8Scalar16MutableBatchShelfBytes => scalar8Scalar16MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SS8-16` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a wide-identity shelf-local writer cannot publish an old shelf image after another writer transformed, split, or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Scalar8Scalar16RouteClaim> Scalar8Scalar16RouteClaims => scalar8Scalar16RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `SS16-16` shelf images staged by the active batch.<br/>
    /// These wide-key/wide-identity shelves are published through the same serialized session seam as the other fixed-scalar writer-context shapes.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Scalar16Scalar16MutableBatchShelfBytes => scalar16Scalar16MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SS16-16` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a wide-key/wide-identity shelf-local writer cannot publish an old shelf image after another writer transformed, split, or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Scalar16Scalar16RouteClaim> Scalar16Scalar16RouteClaims => scalar16Scalar16RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `FS32-8` shelf images staged by the active batch.<br/>
    /// These 32-byte-key shelves use the same publication seam as fixed-scalar writer-context shapes, but keep ownership state shape-specific.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Fixed32Scalar8MutableBatchShelfBytes => fixed32Scalar8MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `FS32-8` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a fixed-32-key shelf-local writer cannot publish an old shelf image after another writer transformed, split, or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Fixed32Scalar8RouteClaim> Fixed32Scalar8RouteClaims => fixed32Scalar8RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `FS32-16` shelf images staged by the active batch.<br/>
    /// These 32-byte-key/wide-identity shelves use the same publication seam as fixed-scalar writer-context shapes, but keep ownership state shape-specific.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> Fixed32Scalar16MutableBatchShelfBytes => fixed32Scalar16MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `FS32-16` shelf staged by this writer.<br/>
    /// Publication revalidates these claims so a fixed-32-key/wide-identity shelf-local writer cannot publish an old shelf image after another writer transformed, split, or relinked the route that selected it.<br/>
    /// </summary>
    internal Dictionary<long, Fixed32Scalar16RouteClaim> Fixed32Scalar16RouteClaims => fixed32Scalar16RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `FSN-8` shelf images staged by the active writer.<br/>
    /// Programmable fixed-key shelves use the same publish seam as fixed32 shelves, but preserve independent ownership buckets because shelf extents vary by profile.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> FixedNScalar8MutableBatchShelfBytes => fixedNScalar8MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed physical-location evidence for each `FSN-8` shelf staged by this writer.<br/>
    /// Direct roots assert that the root is still a shelf; routed claims assert that the parent router slot still selects the same shelf offset.<br/>
    /// </summary>
    internal Dictionary<long, FixedNScalar8RouteClaim> FixedNScalar8RouteClaims => fixedNScalar8RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `FSN-16` shelf images staged by the active writer.<br/>
    /// These programmable fixed-key shelves keep wide identity bytes private until publication so ordinary leaf mutations can overlap across different shelves.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> FixedNScalar16MutableBatchShelfBytes => fixedNScalar16MutableBatchShelfBytes ??= [];

    /// <summary>
    /// Gets committed physical-location evidence for each `FSN-16` shelf staged by this writer.<br/>
    /// Direct roots assert that the root is still a shelf; routed claims assert that the parent router slot still selects the same shelf offset.<br/>
    /// </summary>
    internal Dictionary<long, FixedNScalar16RouteClaim> FixedNScalar16RouteClaims => fixedNScalar16RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty terminal identity shelf images staged by the active batch.<br/>
    /// These shelves belong to exhausted-key duplicate routes and can be published independently when the append stays inside an existing terminal shelf.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> TerminalIdentity8MutableShelfBytes => terminalIdentity8MutableShelfBytes ??= [];

    /// <summary>
    /// Gets writer-local dirty `VS8` mutable shelves staged by the active writer.<br/>
    /// These shelves remain private until the session publication path validates, normalizes, and flushes them through DataKernel.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyScalar8MutableShelf> VarKeyScalar8MutableBatchShelves => varKeyScalar8MutableBatchShelves ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `VS8` shelf or terminal root staged by this writer.<br/>
    /// Publication revalidates these claims so a shelf-local var-key writer cannot publish an old shelf image after another writer transformed, grew, or relinked the parent route.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyScalar8RouteClaim> VarKeyScalar8RouteClaims => varKeyScalar8RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `VS16` mutable shelves staged by the active writer.<br/>
    /// These shelves follow the same warmed ordinary-shelf publication model as `VS8`, with shape-specific ownership state and 16-byte identity slot handling.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyScalar16MutableShelf> VarKeyScalar16MutableBatchShelves => varKeyScalar16MutableBatchShelves ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `VS16` shelf staged by this writer.<br/>
    /// Publication revalidates the exact parent-router slot so widened-identity var-key writers cannot publish stale shelf bytes after another writer changes the route.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyScalar16RouteClaim> VarKeyScalar16RouteClaims => varKeyScalar16RouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `VV` mutable shelves staged by the active writer.<br/>
    /// These shelves follow the same warmed ordinary-shelf publication model as `VS8`, with variable key and variable identity bytes both staying shelf-local until publish.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyVarIdentityMutableShelf> VarKeyVarIdentityMutableBatchShelves => varKeyVarIdentityMutableBatchShelves ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `VV` shelf staged by this writer.<br/>
    /// Publication revalidates the exact parent-router slot and physical shelf shape before writer-local variable-key/variable-identity bytes are flushed.<br/>
    /// </summary>
    internal Dictionary<long, VarKeyVarIdentityRouteClaim> VarKeyVarIdentityRouteClaims => varKeyVarIdentityRouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty raw `VS8` shelf images that cannot use the ordinary mutable sidecar.<br/>
    /// Duplicate-run shelves use this bucket because their compact identity-list layout is distinct from ordinary var-key tuple slots.<br/>
    /// </summary>
    internal Dictionary<long, byte[]> VarKeyScalar8RawMutableShelfBytes => varKeyScalar8RawMutableShelfBytes ??= [];

    /// <summary>
    /// Gets writer-local dirty `SV8` mutable shelves staged by the active writer.<br/>
    /// The first variable-identity slice supports warmed ordinary shelves while terminal duplicate routes and topology-changing cases stay on the existing durability path.<br/>
    /// </summary>
    internal Dictionary<long, Scalar8VarIdentityMutableShelfView> Scalar8VarIdentityMutableBatchShelves => scalar8VarIdentityMutableBatchShelves ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SV8` shelf staged by this writer.<br/>
    /// Publication revalidates the exact parent-router slot and physical shelf shape before writer-local variable-identity bytes are flushed.<br/>
    /// </summary>
    internal Dictionary<long, Scalar8VarIdentityRouteClaim> Scalar8VarIdentityRouteClaims => scalar8VarIdentityRouteClaims ??= [];

    /// <summary>
    /// Gets writer-local dirty `SV16` mutable shelves staged by the active writer.<br/>
    /// This mirrors the `SV8` variable-identity slice for warmed ordinary shelves with 16-byte scalar keys.<br/>
    /// </summary>
    internal Dictionary<long, Scalar16VarIdentityMutableShelfView> Scalar16VarIdentityMutableBatchShelves => scalar16VarIdentityMutableBatchShelves ??= [];

    /// <summary>
    /// Gets committed-route evidence for each `SV16` shelf staged by this writer.<br/>
    /// Publication revalidates the exact parent-router slot and physical shelf shape before writer-local wide-key variable-identity bytes are flushed.<br/>
    /// </summary>
    internal Dictionary<long, Scalar16VarIdentityRouteClaim> Scalar16VarIdentityRouteClaims => scalar16VarIdentityRouteClaims ??= [];
}

/// <summary>
/// Captures the committed router relationship that selected an `SS8-8` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar8Scalar8RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router relationship that selected an `SS16-8` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar16Scalar8RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router relationship that selected an `SS8-16` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar8Scalar16RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router relationship that selected an `SS16-16` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar16Scalar16RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router relationship that selected an `FS32-8` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Fixed32Scalar8RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router relationship that selected an `FS32-16` shelf for a writer-local mutation.<br/>
/// The claim is evidence, not a reader lock: readers keep using committed/cache-visible state, while the writer revalidates this exact relationship immediately before publishing its private shelf image.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Fixed32Scalar16RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed physical relationship that selected an `FSN-8` shelf for writer-local mutation.<br/>
/// A zero parent router offset means the handle root itself was a direct shelf; otherwise the claim revalidates the exact parent router prefix that selected the shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The parent router offset, or zero when the claimed target is the direct root shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router, or zero for a direct root shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
/// <param name="Profile">The fixed-N profile required to validate the target as a physical `FSN-8` shelf.<br/></param>
internal readonly record struct FixedNScalar8RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset,
    FixedNScalar8Profile Profile);

/// <summary>
/// Captures the committed physical relationship that selected an `FSN-16` shelf for writer-local mutation.<br/>
/// A zero parent router offset means the handle root itself was a direct shelf; otherwise the claim revalidates the exact parent router prefix that selected the shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The parent router offset, or zero when the claimed target is the direct root shelf.<br/></param>
/// <param name="RoutePrefixByte">The prefix byte inside the parent router, or zero for a direct root shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
/// <param name="Profile">The fixed-N profile required to validate the target as a physical `FSN-16` shelf.<br/></param>
internal readonly record struct FixedNScalar16RouteClaim(
    long ParentRouterOffset,
    byte RoutePrefixByte,
    long ExpectedTargetOffset,
    FixedNScalar16Profile Profile);

/// <summary>
/// Captures the committed router slot that selected a `VS8` shelf or terminal root for writer-local mutation.<br/>
/// Route index is used instead of prefix-only so compressed multi-byte route spans revalidate the exact parent-router slot that selected the physical target.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the target.<br/></param>
/// <param name="RouteIndex">The route slot inside the parent router that selected the target.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf or terminal root offset expected at publication time.<br/></param>
internal readonly record struct VarKeyScalar8RouteClaim(
    long ParentRouterOffset,
    int RouteIndex,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router slot that selected a `VS16` shelf for writer-local mutation.<br/>
/// Route index is used instead of prefix-only so compressed multi-byte route spans revalidate the exact parent-router slot that selected the physical shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RouteIndex">The route slot inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct VarKeyScalar16RouteClaim(
    long ParentRouterOffset,
    int RouteIndex,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router slot that selected a `VV` shelf for writer-local mutation.<br/>
/// Route index is used instead of prefix-only so compressed multi-byte route spans revalidate the exact parent-router slot that selected the physical shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RouteIndex">The route slot inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct VarKeyVarIdentityRouteClaim(
    long ParentRouterOffset,
    int RouteIndex,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router slot that selected an `SV8` shelf for writer-local mutation.<br/>
/// Route index is used instead of prefix-only so compressed route spans revalidate the exact parent-router slot that selected the physical shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RouteIndex">The route slot inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar8VarIdentityRouteClaim(
    long ParentRouterOffset,
    int RouteIndex,
    long ExpectedTargetOffset);

/// <summary>
/// Captures the committed router slot that selected an `SV16` shelf for writer-local mutation.<br/>
/// Route index is used instead of prefix-only so compressed route spans revalidate the exact parent-router slot that selected the physical shelf.<br/>
/// </summary>
/// <param name="ParentRouterOffset">The router offset that selected the shelf.<br/></param>
/// <param name="RouteIndex">The route slot inside the parent router that selected the shelf.<br/></param>
/// <param name="ExpectedTargetOffset">The shelf offset expected at publication time.<br/></param>
internal readonly record struct Scalar16VarIdentityRouteClaim(
    long ParentRouterOffset,
    int RouteIndex,
    long ExpectedTargetOffset);

/// <summary>
/// Identifies a retryable writer-context admission failure for a shelf already owned by another writer context.<br/>
/// Queued writer facades catch this specific exception so same-shelf caller overlap waits and retries instead of falling through to a serialized fallback that could read stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextShelfOwnershipException(long shelfOffset)
        : base(
            "The SS8-8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `SS16-8` shelf already owned by another writer context.<br/>
/// Queued writer facades catch this specific exception so same-shelf caller overlap waits and retries instead of falling through to a serialized fallback that could read stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextScalar16Scalar8ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextScalar16Scalar8ShelfOwnershipException(long shelfOffset)
        : base(
            "The SS16-8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `SS8-16` shelf already owned by another writer context.<br/>
/// Queued writer facades catch this specific exception so same-shelf caller overlap waits and retries instead of falling through to a serialized fallback that could read stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextScalar8Scalar16ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextScalar8Scalar16ShelfOwnershipException(long shelfOffset)
        : base(
            "The SS8-16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `SS16-16` shelf already owned by another writer context.<br/>
/// Queued writer facades catch this specific exception so same-shelf caller overlap waits and retries instead of falling through to a serialized fallback that could read stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextScalar16Scalar16ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextScalar16Scalar16ShelfOwnershipException(long shelfOffset)
        : base(
            "The SS16-16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `FS32-8` shelf already owned by another writer context.<br/>
/// Queued writer and direct mutation paths catch this specific exception so same-shelf caller overlap waits and retries instead of reading stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextFixed32Scalar8ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextFixed32Scalar8ShelfOwnershipException(long shelfOffset)
        : base(
            "The FS32-8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `FS32-16` shelf already owned by another writer context.<br/>
/// Queued writer and direct mutation paths catch this specific exception so same-shelf caller overlap waits and retries instead of reading stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextFixed32Scalar16ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextFixed32Scalar16ShelfOwnershipException(long shelfOffset)
        : base(
            "The FS32-16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `FSN-8` shelf already owned by another writer context.<br/>
/// Direct fixed-N callers catch this so same-shelf overlap waits while different fixed-key shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextFixedNScalar8ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextFixedNScalar8ShelfOwnershipException(long shelfOffset)
        : base(
            "The FSN-8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different FSN-8 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `FSN-16` shelf already owned by another writer context.<br/>
/// Direct fixed-N callers catch this so same-shelf overlap waits while different fixed-key shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextFixedNScalar16ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextFixedNScalar16ShelfOwnershipException(long shelfOffset)
        : base(
            "The FSN-16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different FSN-16 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for a terminal identity shelf already owned by another writer context.<br/>
/// Queued writer facades catch this specific exception so same-terminal-shelf caller overlap waits and retries instead of falling through to a serialized fallback that could read stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextTerminalIdentityShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextTerminalIdentityShelfOwnershipException(long shelfOffset)
        : base(
            "The terminal identity shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different terminal shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the terminal identity shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for a `VS8` shelf already owned by another writer context.<br/>
/// The first var-key concurrency slice uses this to let same-shelf callers wait and retry while different shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextVarKeyScalar8ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextVarKeyScalar8ShelfOwnershipException(long shelfOffset)
        : base(
            "The VS8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different VS8 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the `VS8` shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for a `VS16` shelf already owned by another writer context.<br/>
/// The first `VS16` parity slice uses this to let same-shelf callers wait and retry while different shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextVarKeyScalar16ShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextVarKeyScalar16ShelfOwnershipException(long shelfOffset)
        : base(
            "The VS16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different VS16 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the `VS16` shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `SV8` shelf already owned by another writer context.<br/>
/// Variable-identity one-shot callers catch this so same-shelf overlap waits and retries while different shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextScalar8VarIdentityShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextScalar8VarIdentityShelfOwnershipException(long shelfOffset)
        : base(
            "The SV8 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different SV8 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the `SV8` shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for an `SV16` shelf already owned by another writer context.<br/>
/// Variable-identity one-shot callers catch this so same-shelf overlap waits and retries while different shelves stage independently.<br/>
/// </summary>
internal sealed class LibraDexWriteContextScalar16VarIdentityShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextScalar16VarIdentityShelfOwnershipException(long shelfOffset)
        : base(
            "The SV16 shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different SV16 shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the `SV16` shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}

/// <summary>
/// Identifies a retryable writer-context admission failure for a `VV` shelf already owned by another writer context.<br/>
/// Ordinary `VV` shelf-local writers catch this specific exception so same-shelf caller overlap waits and retries instead of reading stale shelf bytes.<br/>
/// </summary>
internal sealed class LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException : InvalidOperationException
{
    internal LibraDexWriteContextVarKeyVarIdentityShelfOwnershipException(long shelfOffset)
        : base(
            "The VV shelf is already owned by another LibraDex writer context. " +
            "Concurrent writer contexts must target different shelves or retry after the owning context publishes or aborts.")
    {
        ShelfOffset = shelfOffset;
    }

    /// <summary>
    /// Gets the shelf offset that was already owned by another writer context.<br/>
    /// </summary>
    internal long ShelfOffset { get; }
}
