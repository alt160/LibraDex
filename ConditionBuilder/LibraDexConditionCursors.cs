namespace LibraDex;

/// <summary>
/// Represents one key/identity entry returned by a condition-filtered LibraDex index cursor.<br/>
/// The key side is the indexed value stored in the target index; the identity side is the catalog identity associated with that key.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type exposed by the target index.</typeparam>
/// <typeparam name="TIdentity">The public identity type exposed by the target index.</typeparam>
/// <param name="Key">The indexed key value for the current entry.</param>
/// <param name="Identity">The identity value for the current entry.</param>
public readonly record struct LibraDexCursorEntry<TKey, TIdentity>(TKey? Key, TIdentity Identity);

/// <summary>
/// Provides a forward-only cursor over identity values produced by a completed LibraDex condition.<br/>
/// Conditions remain filter descriptors; this cursor is the materialization boundary used when callers only need identities.<br/>
/// </summary>
/// <typeparam name="TIdentity">The public identity type returned by the cursor.</typeparam>
public sealed class LibraDexIdentityCursor<TIdentity> : IDisposable
{
    private readonly IEnumerator<TIdentity> enumerator;
    private TIdentity? current;
    private bool hasCurrent;
    private bool disposed;

    internal LibraDexIdentityCursor(IEnumerable<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        enumerator = identities.GetEnumerator();
        Ordinal = -1;
    }

    /// <summary>
    /// Gets the zero-based ordinal of the current returned identity.<br/>
    /// The value is `-1` until <see cref="Next"/> returns <see langword="true"/> for the first time.<br/>
    /// </summary>
    public long Ordinal { get; private set; }

    /// <summary>
    /// Advances the cursor to the next identity.<br/>
    /// This method returns <see langword="false"/> when the condition stream is exhausted.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when an identity is available through <see cref="GetIdentity"/>.</returns>
    public bool Next()
    {
        ThrowIfDisposed();
        if (!enumerator.MoveNext())
        {
            hasCurrent = false;
            current = default;
            return false;
        }

        current = enumerator.Current;
        hasCurrent = true;
        Ordinal++;
        return true;
    }

    /// <summary>
    /// Advances the cursor to the next identity.<br/>
    /// This alias keeps the cursor compatible with standard .NET cursor naming while <see cref="Next"/> remains the low-friction LibraDex spelling.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when an identity is available through <see cref="GetIdentity"/>.</returns>
    public bool MoveNext()
        => Next();

    /// <summary>
    /// Gets the current identity value.<br/>
    /// Call <see cref="Next"/> first; calling this method before a row is active or after exhaustion throws.<br/>
    /// </summary>
    /// <returns>The current identity value.</returns>
    public TIdentity GetIdentity()
    {
        ThrowIfDisposed();
        if (!hasCurrent)
        {
            throw new InvalidOperationException("The cursor is not positioned on an identity.");
        }

        return current!;
    }

    /// <summary>
    /// Gets the current identity value.<br/>
    /// This property is equivalent to <see cref="GetIdentity"/> and exists for callers that prefer property-style cursor access.<br/>
    /// </summary>
    public TIdentity CurrentIdentity => GetIdentity();

    /// <summary>
    /// Releases the underlying enumerator.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        enumerator.Dispose();
        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexIdentityCursor<TIdentity>));
        }
    }
}

/// <summary>
/// Provides a forward-only cursor over key/identity entries from one target LibraDex index after a completed condition has filtered the identity stream.<br/>
/// The cursor returns the target index's stored key values directly, so callers can enumerate indexed values without separate covering-index ceremony.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type exposed by the target index.</typeparam>
/// <typeparam name="TIdentity">The public identity type exposed by the target index.</typeparam>
public sealed class LibraDexIndexCursor<TKey, TIdentity> : IDisposable
{
    private readonly IEnumerator<LibraDexObjectTuple> enumerator;
    private readonly IIndex? targetIndex;
    private readonly IIdentityExactTupleMutator? exactMutator;
    private readonly IIdentityPrimitiveMutator? primitiveMutator;
    private readonly LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest;
    private readonly int skip;
    private readonly int? take;
    private LibraDexObjectTuple current;
    private bool hasCurrent;
    private bool disposed;
    private bool exhausted;
    private int skipped;
    private int returned;

    internal LibraDexIndexCursor(
        IEnumerable<LibraDexObjectTuple> tuples,
        IIndex? targetIndex,
        IIdentityExactTupleMutator? exactMutator,
        IIdentityPrimitiveMutator? primitiveMutator,
        LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest,
        int skip,
        int? take)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");
        }

        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be zero or greater.");
        }

        enumerator = tuples.GetEnumerator();
        this.targetIndex = targetIndex;
        this.exactMutator = exactMutator;
        this.primitiveMutator = primitiveMutator;
        this.primitiveDeleteRequest = primitiveDeleteRequest;
        this.skip = skip;
        this.take = take;
        Ordinal = -1;
    }

    /// <summary>
    /// Gets the zero-based ordinal of the current returned entry after skip/take have been applied.<br/>
    /// The value is `-1` until <see cref="Next"/> returns <see langword="true"/> for the first time.<br/>
    /// </summary>
    public long Ordinal { get; private set; }

    /// <summary>
    /// Advances the cursor to the next target-index entry that survived condition filtering.<br/>
    /// Skip and take are applied while advancing, so callers can discard rows without constructing entry objects for skipped rows.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when an entry is available through <see cref="GetKey"/>, <see cref="GetIdentity"/>, or <see cref="GetEntry"/>.</returns>
    public bool Next()
    {
        ThrowIfDisposed();
        if (exhausted)
        {
            hasCurrent = false;
            current = default;
            return false;
        }

        if (take is not null && returned >= take.Value)
        {
            hasCurrent = false;
            current = default;
            return false;
        }

        while (enumerator.MoveNext())
        {
            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            current = enumerator.Current;
            hasCurrent = true;
            returned++;
            Ordinal++;
            return true;
        }

        hasCurrent = false;
        current = default;
        return false;
    }

    /// <summary>
    /// Advances the cursor to the next target-index entry that survived condition filtering.<br/>
    /// This alias keeps the cursor compatible with standard .NET cursor naming while <see cref="Next"/> remains the low-friction LibraDex spelling.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when an entry is available through <see cref="GetEntry"/>.</returns>
    public bool MoveNext()
        => Next();

    /// <summary>
    /// Gets the current target-index key value.<br/>
    /// Call <see cref="Next"/> first; calling this method before a row is active or after exhaustion throws.<br/>
    /// </summary>
    /// <returns>The current indexed key value.</returns>
    public TKey? GetKey()
    {
        ThrowIfDisposed();
        EnsureCurrent();
        return CastKey(current.Key);
    }

    /// <summary>
    /// Gets the current target-index identity value.<br/>
    /// Call <see cref="Next"/> first; calling this method before a row is active or after exhaustion throws.<br/>
    /// </summary>
    /// <returns>The current identity value.</returns>
    public TIdentity GetIdentity()
    {
        ThrowIfDisposed();
        EnsureCurrent();
        if (current.Identity is TIdentity identity)
        {
            return identity;
        }

        throw new InvalidCastException($"Identity at ordinal {Ordinal} is {current.Identity.GetType().FullName}, not {typeof(TIdentity).FullName}.");
    }

    /// <summary>
    /// Gets the current target-index entry as a key/identity pair.<br/>
    /// This is a convenience wrapper over <see cref="GetKey"/> and <see cref="GetIdentity"/> for callers that want a single value object.<br/>
    /// </summary>
    /// <returns>The current key/identity entry.</returns>
    public LibraDexCursorEntry<TKey, TIdentity> GetEntry()
        => new LibraDexCursorEntry<TKey, TIdentity>(GetKey(), GetIdentity());

    /// <summary>
    /// Deletes the current target-index key/identity entry while preserving traversal over the original cursor stream.<br/>
    /// Call this only after <see cref="Next"/> returns <see langword="true"/>; current key, identity, and entry access are invalidated after a successful delete until the next successful move.<br/>
    /// The following <see cref="Next"/> continues from the cursor's underlying stream rather than re-resolving the condition, so mutation does not revisit the deleted tuple.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the current physical tuple was deleted.</returns>
    public bool DeleteCurrent()
    {
        ThrowIfDisposed();
        EnsureCurrent();
        IIdentityExactTupleMutator mutator = exactMutator
            ?? throw new NotSupportedException("This LibraDex index cursor was not opened with cursor-local delete support.");

        bool deleted = mutator.DeleteExactTuple(current.Key, current.Identity);
        if (deleted)
        {
            hasCurrent = false;
            current = default;
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the current target-index key/identity entry while preserving traversal over the original cursor stream.<br/>
    /// This alias mirrors existing range-cursor mutation syntax for callers that prefer command-style cursor operations.<br/>
    /// </summary>
    public void Delete()
    {
        _ = DeleteCurrent();
    }

    /// <summary>
    /// Deletes the current target-index entry when positioned, then deletes every later entry still reachable from this forward-only cursor.<br/>
    /// Entries already advanced past by earlier cursor movement are not revisited; this method preserves the cursor's original stream semantics rather than re-running the condition.<br/>
    /// Current key, identity, and entry access are invalidated when the current entry is deleted, and the cursor is exhausted when the method returns.<br/>
    /// </summary>
    /// <returns>The number of physical tuples deleted.</returns>
    public long DeleteRemaining()
    {
        ThrowIfDisposed();
        if (TryDeleteRemainingFast(out long fastDeleted))
        {
            return fastDeleted;
        }

        long deleted = 0;
        if (hasCurrent && DeleteCurrent())
        {
            deleted++;
        }

        while (Next())
        {
            if (DeleteCurrent())
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes every target-index entry still reachable from this forward-only cursor.<br/>
    /// On a fresh cursor this deletes every entry matched by the cursor stream; after prior movement, entries already advanced past are intentionally out of scope.<br/>
    /// </summary>
    /// <returns>The number of physical tuples deleted.</returns>
    public long DeleteAll()
        => DeleteRemaining();

    /// <summary>
    /// Attempts to delete the whole still-reachable cursor stream by replaying the direct same-index primitive through the index mutation path.<br/>
    /// The fast path is limited to fresh cursors or cursors positioned on their first returned entry so forward-only semantics cannot delete entries that were already advanced past.<br/>
    /// </summary>
    /// <param name="deleted">Receives the number of physical tuples deleted when the direct primitive mutation path is used.<br/></param>
    /// <returns><see langword="true"/> when the cursor was handled by the primitive mutation path; otherwise <see langword="false"/> so the caller can use exact tuple fallback deletion.<br/></returns>
    private bool TryDeleteRemainingFast(out long deleted)
    {
        deleted = 0;
        if (primitiveMutator is null ||
            primitiveDeleteRequest is null ||
            skip != 0 ||
            take is not null)
        {
            return false;
        }

        if (returned != 0 && !(hasCurrent && returned == 1))
        {
            return false;
        }

        LibraDexIdentityMutationResult result = primitiveMutator.DeleteIdentityPrimitive(primitiveDeleteRequest.Value);
        deleted = result.ChangedCount;
        hasCurrent = false;
        current = default;
        exhausted = true;
        return true;
    }

    /// <summary>
    /// Re-keys the current target-index identity from its current key to <paramref name="newKey"/> while preserving traversal over the original cursor stream.<br/>
    /// The replacement tuple is verified before the old exact tuple is removed by the backing index, so a failed replacement does not lose the original entry.<br/>
    /// Current key, identity, and entry access are invalidated after a successful re-key until the next successful move.<br/>
    /// </summary>
    /// <param name="newKey">The replacement key for the current cursor identity.</param>
    /// <returns><see langword="true"/> when the current physical tuple was re-keyed.</returns>
    public bool SetCurrentKey(TKey? newKey)
    {
        ThrowIfDisposed();
        EnsureCurrent();
        IIndex index = targetIndex
            ?? throw new NotSupportedException("This LibraDex index cursor was not opened with cursor-local re-key support.");

        bool rekeyed = index.Rekey(current.Identity, current.Key, newKey);
        if (rekeyed)
        {
            hasCurrent = false;
            current = default;
        }

        return rekeyed;
    }

    /// <summary>
    /// Re-keys the current target-index identity from its current key to <paramref name="newKey"/> while preserving traversal over the original cursor stream.<br/>
    /// This alias mirrors existing range-cursor mutation syntax for callers that prefer the shorter positioned key-update verb.<br/>
    /// </summary>
    /// <param name="newKey">The replacement key for the current cursor identity.</param>
    /// <returns><see langword="true"/> when the current physical tuple was re-keyed.</returns>
    public bool SetKey(TKey? newKey)
        => SetCurrentKey(newKey);

    /// <summary>
    /// Gets the current target-index key value.<br/>
    /// This property is equivalent to <see cref="GetKey"/> and exists for callers that prefer property-style cursor access.<br/>
    /// </summary>
    public TKey? CurrentKey => GetKey();

    /// <summary>
    /// Gets the current target-index identity value.<br/>
    /// This property is equivalent to <see cref="GetIdentity"/> and exists for callers that prefer property-style cursor access.<br/>
    /// </summary>
    public TIdentity CurrentIdentity => GetIdentity();

    /// <summary>
    /// Gets the current target-index entry.<br/>
    /// This property is equivalent to <see cref="GetEntry"/> and exists for callers that prefer property-style cursor access.<br/>
    /// </summary>
    public LibraDexCursorEntry<TKey, TIdentity> CurrentEntry => GetEntry();

    /// <summary>
    /// Releases the underlying tuple enumerator.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        enumerator.Dispose();
        disposed = true;
    }

    private static TKey? CastKey(object? value)
    {
        if (value is null)
        {
            if (default(TKey) is null)
            {
                return default;
            }

            throw new InvalidCastException($"Null key at the current cursor position cannot be returned as non-nullable {typeof(TKey).FullName}.");
        }

        if (value is TKey key)
        {
            return key;
        }

        throw new InvalidCastException($"Key at the current cursor position is {value.GetType().FullName}, not {typeof(TKey).FullName}.");
    }

    private void EnsureCurrent()
    {
        if (!hasCurrent)
        {
            throw new InvalidOperationException("The cursor is not positioned on an index entry.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexIndexCursor<TKey, TIdentity>));
        }
    }
}

internal static class LibraDexConditionCursorExecutor
{
    /// <summary>
    /// Creates a whole-cursor primitive delete plan when a cursor condition is a direct, unpaged, same-index leaf.<br/>
    /// The returned mutator and request let positioned cursor delete APIs reuse the index's existing route/shelf primitive mutation code instead of deleting one materialized tuple at a time.<br/>
    /// </summary>
    /// <param name="criterion">The materialized condition criterion that defines the cursor stream.<br/></param>
    /// <param name="targetIndex">The index whose key/identity tuples the cursor exposes.<br/></param>
    /// <param name="skip">The cursor skip count requested by the caller.<br/></param>
    /// <param name="take">The optional cursor take limit requested by the caller.<br/></param>
    /// <param name="primitiveMutator">Receives the same-index primitive mutator when the plan is eligible.<br/></param>
    /// <param name="primitiveRequest">Receives the primitive request to replay through the mutation path when the plan is eligible.<br/></param>
    /// <returns><see langword="true"/> when the criterion can be deleted as a direct primitive; otherwise <see langword="false"/>.<br/></returns>
    internal static bool TryCreateDirectPrimitiveDeletePlan(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        int skip,
        int? take,
        out IIdentityPrimitiveMutator? primitiveMutator,
        out LibraDexIdentityPrimitiveRequest? primitiveRequest)
    {
        primitiveMutator = null;
        primitiveRequest = null;
        if (skip != 0 ||
            take is not null ||
            criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            !ReferenceEquals(criterion.Index, targetIndex) ||
            criterion.CriteriaKind is null ||
            targetIndex is not IIdentityPrimitiveMutator mutator)
        {
            return false;
        }

        primitiveMutator = mutator;
        primitiveRequest = new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values);
        return true;
    }

    internal static IEnumerable<LibraDexObjectTuple> IterateTargetIndexTuples(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        int skip,
        int? take,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(targetIndex);
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf &&
            ReferenceEquals(criterion.Index, targetIndex) &&
            criterion.CriteriaKind is not null)
        {
            int? takeLimit = AddForTakeLimit(skip, take);
            foreach (LibraDexObjectTuple tuple in IterateTuplePrimitive(
                targetIndex,
                criterion.CriteriaKind.Value,
                criterion.Values,
                takeLimit,
                direction))
            {
                yield return tuple;
            }

            yield break;
        }

        if (TryCreateComposedTargetTuplePlan(
            criterion,
            targetIndex,
            out IIdentityCriterion? targetLeaf,
            out IIdentityCriterion? filterCriterion,
            out bool includeMatches))
        {
            if (filterCriterion.NodeKind == LibraDexIdentityCriterionNodeKind.External &&
                filterCriterion.ExternalIdentityFilter is not null)
            {
                if (!includeMatches)
                {
                    throw new NotSupportedException("External identity filters are supported for target cursor intersections, not exclusions.");
                }

                foreach (LibraDexObjectTuple tuple in IterateExternalFilteredTargetTuples(targetIndex, targetLeaf, filterCriterion, direction))
                {
                    yield return tuple;
                }

                yield break;
            }

            IReadOnlyList<object> filterIdentities = filterCriterion.IDsWith(
                IdentityResultOrdering.PlanNatural,
                IdentityDeduplication.Distinct,
                skip: 0,
                take: null,
                bookmark: null).ToList();
            if (filterIdentities.Count == 0)
            {
                if (includeMatches)
                {
                    yield break;
                }
            }

            HashSet<object>? filterIdentitySet = CanUseDefaultIdentityHashSet(filterIdentities)
                ? new HashSet<object>(filterIdentities)
                : null;
            foreach (LibraDexObjectTuple tuple in IterateTuplePrimitive(
                targetIndex,
                targetLeaf.CriteriaKind!.Value,
                targetLeaf.Values,
                takeLimit: null,
                direction))
            {
                bool matches = ContainsIdentity(filterIdentities, filterIdentitySet, tuple.Identity);
                if (matches == includeMatches)
                {
                    yield return tuple;
                }
            }

            yield break;
        }

        IReadOnlyList<object> matchedIdentities = criterion.IDsWith(
            IdentityResultOrdering.PlanNatural,
            IdentityDeduplication.Distinct,
            skip: 0,
            take: null,
            bookmark: null).ToList();
        if (matchedIdentities.Count == 0)
        {
            yield break;
        }

        foreach (LibraDexObjectTuple tuple in IterateTuplePrimitive(
            targetIndex,
            LibraDexCriteriaKind.All,
            Array.Empty<object?>(),
            takeLimit: null,
            direction))
        {
            if (ContainsIdentity(matchedIdentities, tuple.Identity))
            {
                yield return tuple;
            }
        }
    }

    /// <summary>
    /// Streams target-index tuples through an external identity predicate without materializing an intermediate identity set.<br/>
    /// The external ordinal is counted over the target primitive candidate stream before filtering, matching identity projection semantics.<br/>
    /// </summary>
    /// <param name="targetIndex">The target index whose tuples are exposed by the cursor.<br/></param>
    /// <param name="targetLeaf">The direct target-index primitive leaf to stream.<br/></param>
    /// <param name="externalCriterion">The external identity-filter criterion.<br/></param>
    /// <returns>Target-index tuples accepted by the external identity filter.</returns>
    private static IEnumerable<LibraDexObjectTuple> IterateExternalFilteredTargetTuples(
        IIndex targetIndex,
        IIdentityCriterion targetLeaf,
        IIdentityCriterion externalCriterion,
        QueryDirection direction)
    {
        Func<LibraDexExternalIdentityContext, bool> filter = externalCriterion.ExternalIdentityFilter
            ?? throw new InvalidOperationException("External identity criterion is missing its filter delegate.");
        long ordinal = 0;
        foreach (LibraDexObjectTuple tuple in IterateTuplePrimitive(
            targetIndex,
            targetLeaf.CriteriaKind!.Value,
            targetLeaf.Values,
            takeLimit: null,
            direction))
        {
            LibraDexExternalIdentityContext context = new(tuple.Identity, ordinal, ordinal == 0);
            ordinal++;
            if (filter(context))
            {
                yield return tuple;
            }
        }
    }

    /// <summary>
    /// Selects a composed target-index tuple plan for simple intersection and exclusion nodes.<br/>
    /// `And` can stream either direct target-index leaf and test identities from the opposite side; `Except` can stream its left direct target-index leaf and exclude identities from the right side.<br/>
    /// </summary>
    /// <param name="criterion">The composed criterion to inspect.<br/></param>
    /// <param name="targetIndex">The index whose tuples the cursor exposes.<br/></param>
    /// <param name="targetLeaf">Receives the direct target-index leaf that can supply tuple order.<br/></param>
    /// <param name="filterCriterion">Receives the opposite criterion whose identities are used as a membership filter.<br/></param>
    /// <param name="includeMatches">Receives <see langword="true"/> for intersection and <see langword="false"/> for exclusion.<br/></param>
    /// <returns><see langword="true"/> when the composed criterion has a safe target-leaf tuple stream.<br/></returns>
    private static bool TryCreateComposedTargetTuplePlan(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IIdentityCriterion? targetLeaf,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IIdentityCriterion? filterCriterion,
        out bool includeMatches)
    {
        targetLeaf = null;
        filterCriterion = null;
        includeMatches = true;
        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.And)
        {
            IIdentityCriterion left = RequireLeft(criterion);
            IIdentityCriterion right = RequireRight(criterion);
            if (IsDirectTargetLeaf(left, targetIndex))
            {
                targetLeaf = left;
                filterCriterion = right;
                includeMatches = true;
                return true;
            }

            if (IsDirectTargetLeaf(right, targetIndex))
            {
                targetLeaf = right;
                filterCriterion = left;
                includeMatches = true;
                return true;
            }
        }

        if (criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Except)
        {
            IIdentityCriterion left = RequireLeft(criterion);
            if (IsDirectTargetLeaf(left, targetIndex))
            {
                targetLeaf = left;
                filterCriterion = RequireRight(criterion);
                includeMatches = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tests whether a criterion is one executable, order-preserving primitive leaf for the cursor's target index.<br/>
    /// Reference equality is intentional because condition materialization binds fluent index handles to the concrete physical index used for tuple streaming.<br/>
    /// </summary>
    /// <param name="criterion">The criterion to inspect.<br/></param>
    /// <param name="targetIndex">The cursor target index.<br/></param>
    /// <returns><see langword="true"/> when the criterion can stream target-index tuples directly.<br/></returns>
    private static bool IsDirectTargetLeaf(IIdentityCriterion criterion, IIndex targetIndex)
    {
        return criterion.NodeKind == LibraDexIdentityCriterionNodeKind.Leaf &&
            ReferenceEquals(criterion.Index, targetIndex) &&
            criterion.CriteriaKind is LibraDexCriteriaKind.All or
                LibraDexCriteriaKind.Find or
                LibraDexCriteriaKind.Between or
                LibraDexCriteriaKind.Before or
                LibraDexCriteriaKind.AtOrBefore or
                LibraDexCriteriaKind.After or
                LibraDexCriteriaKind.AtOrAfter;
    }

    /// <summary>
    /// Gets the required left child from a composed criterion.<br/>
    /// This keeps cursor planning failures explicit if a malformed criterion tree reaches execution.<br/>
    /// </summary>
    /// <param name="criterion">The composed criterion node.<br/></param>
    /// <returns>The non-null left child criterion.<br/></returns>
    private static IIdentityCriterion RequireLeft(IIdentityCriterion criterion)
        => criterion.Left ?? throw new InvalidOperationException("Identity criterion node is missing its left child.");

    /// <summary>
    /// Gets the required right child from a composed criterion.<br/>
    /// This keeps cursor planning failures explicit if a malformed criterion tree reaches execution.<br/>
    /// </summary>
    /// <param name="criterion">The composed criterion node.<br/></param>
    /// <returns>The non-null right child criterion.<br/></returns>
    private static IIdentityCriterion RequireRight(IIdentityCriterion criterion)
        => criterion.Right ?? throw new InvalidOperationException("Identity criterion node is missing its right child.");

    private static IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(
        IIndex targetIndex,
        LibraDexCriteriaKind criteriaKind,
        IReadOnlyList<object?> values,
        int? takeLimit,
        QueryDirection direction)
    {
        LibraDexIdentityPrimitiveRequest request = new(criteriaKind, values, takeLimit, direction);
        if (targetIndex is IIdentityPrimitiveTupleStreamer tupleStreamer)
        {
            return tupleStreamer.IterateTuplePrimitive(request);
        }

        if (targetIndex is not IIdentityPrimitiveTupleExecutor tupleExecutor)
        {
            throw new NotSupportedException("The target index does not expose the internal tuple stream required for condition-filtered index cursors.");
        }

        return tupleExecutor.ExecuteTuplePrimitive(request);
    }

    private static int? AddForTakeLimit(int skip, int? take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");
        }

        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be zero or greater.");
        }

        if (take is null)
        {
            return null;
        }

        return checked(skip + take.Value);
    }

    private static bool ContainsIdentity(IReadOnlyList<object> identities, object candidate)
    {
        for (int i = 0; i < identities.Count; i++)
        {
            if (LibraDexObjectTuple.ValueEquals(identities[i], candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tests a candidate identity against either a default-equality hash set or the structural fallback list.<br/>
    /// The hash set is used only when identity values do not require LibraDex's custom byte-array or composite equality semantics.<br/>
    /// </summary>
    /// <param name="identities">The materialized identity list used by the structural fallback.<br/></param>
    /// <param name="identitySet">The optional default-equality identity set.<br/></param>
    /// <param name="candidate">The candidate tuple identity.<br/></param>
    /// <returns><see langword="true"/> when the candidate identity is present.<br/></returns>
    private static bool ContainsIdentity(IReadOnlyList<object> identities, HashSet<object>? identitySet, object candidate)
    {
        return identitySet is not null
            ? identitySet.Contains(candidate)
            : ContainsIdentity(identities, candidate);
    }

    /// <summary>
    /// Determines whether identity membership can use default object hashing without changing LibraDex structural equality semantics.<br/>
    /// Byte-array and composite identities keep the linear fallback because their logical equality can differ from default object hashing.<br/>
    /// </summary>
    /// <param name="identities">The candidate identity list to inspect.<br/></param>
    /// <returns><see langword="true"/> when default hash-set membership is safe for every identity value.<br/></returns>
    private static bool CanUseDefaultIdentityHashSet(IReadOnlyList<object> identities)
    {
        for (int i = 0; i < identities.Count; i++)
        {
            if (identities[i] is byte[] or LibraDexCompositeKey)
            {
                return false;
            }
        }

        return true;
    }
}
