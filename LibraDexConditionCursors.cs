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
    {
        return Next();
    }

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
    private readonly int skip;
    private readonly int? take;
    private LibraDexObjectTuple current;
    private bool hasCurrent;
    private bool disposed;
    private int skipped;
    private int returned;

    internal LibraDexIndexCursor(
        IEnumerable<LibraDexObjectTuple> tuples,
        IIndex? targetIndex,
        IIdentityExactTupleMutator? exactMutator,
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
    {
        return Next();
    }

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
    {
        return new LibraDexCursorEntry<TKey, TIdentity>(GetKey(), GetIdentity());
    }

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
    {
        return SetCurrentKey(newKey);
    }

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
    internal static IEnumerable<LibraDexObjectTuple> IterateTargetIndexTuples(
        IIdentityCriterion criterion,
        IIndex targetIndex,
        int skip,
        int? take)
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
                takeLimit))
            {
                yield return tuple;
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
            takeLimit: null))
        {
            if (ContainsIdentity(matchedIdentities, tuple.Identity))
            {
                yield return tuple;
            }
        }
    }

    private static IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(
        IIndex targetIndex,
        LibraDexCriteriaKind criteriaKind,
        IReadOnlyList<object?> values,
        int? takeLimit)
    {
        LibraDexIdentityPrimitiveRequest request = new(criteriaKind, values, takeLimit);
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
}
