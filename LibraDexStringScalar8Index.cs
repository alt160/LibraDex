using System.Globalization;
using System.Text;

namespace LibraDex;

/// <summary>
/// Provides the first physical string-key facade over routed varlen-key/scalar-identity indexes.<br/>
/// The exact index stores UTF-8 encoded strings, while optional folded and sort-key projections store maintained alternate keys for case-insensitive condition branches.<br/>
/// This type is intentionally narrow: it proves maintained projection storage and condition projection resolution before widening string profiles to reversed projections, persisted projection metadata, or variable-width identity families.<br/>
/// </summary>
public sealed class LibraDexStringScalar8Index : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveMutator, IIdentityPrimitiveTupleExecutor, IIdentityExactTupleMutator, ILibraDexStringComparisonPolicyProvider, IDisposable
{
    private readonly VarKeyScalar8Index exact;
    private readonly LibraDexStringScalar8ProjectionIndex? exactReversed;
    private readonly LibraDexStringScalar8ProjectionIndex? folded;
    private readonly LibraDexStringScalar8SortKeyProjectionIndex? sortKey;
    private readonly LibraDexStringScalar8ProjectionIndex? foldedReversed;
    private readonly CultureInfo foldedCulture;
    private readonly CultureInfo sortKeyCulture;
    private readonly LibraDexStringComparisonPolicy? stringComparisonPolicy;
    private bool disposed;

    internal LibraDexStringScalar8Index(
        string group,
        string name,
        VarKeyScalar8Index exact,
        VarKeyScalar8Index? exactReversed,
        VarKeyScalar8Index? folded,
        VarKeyScalar8Index? sortKey,
        VarKeyScalar8Index? foldedReversed,
        string? foldedCulture,
        string? sortKeyCulture,
        LibraDexStringComparisonPolicy? stringComparisonPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Group = group;
        Name = name;
        this.exact = exact ?? throw new ArgumentNullException(nameof(exact));
        this.foldedCulture = ResolveCulture(foldedCulture);
        this.sortKeyCulture = ResolveCulture(sortKeyCulture);
        this.stringComparisonPolicy = stringComparisonPolicy;
        this.exactReversed = exactReversed is null ? null : new LibraDexStringScalar8ProjectionIndex(group, $"{name}#exact-rev", exactReversed, static value => value);
        this.folded = folded is null ? null : new LibraDexStringScalar8ProjectionIndex(group, $"{name}#folded", folded, value => Fold(value, this.foldedCulture));
        this.sortKey = sortKey is null ? null : new LibraDexStringScalar8SortKeyProjectionIndex(group, $"{name}#sortkey", sortKey);
        this.foldedReversed = foldedReversed is null ? null : new LibraDexStringScalar8ProjectionIndex(group, $"{name}#folded-rev", foldedReversed, static value => value);
    }

    /// <summary>
    /// Gets the logical string index name inside the identity group.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the identity group shared by the exact and projection indexes.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the developer-facing key type accepted by this facade.<br/>
    /// </summary>
    public Type KeyType => typeof(string);

    /// <summary>
    /// Gets the scalar identity type stored by this first physical string facade.<br/>
    /// </summary>
    public Type IdentityType => typeof(ulong);

    /// <summary>
    /// Gets the duplicate-key contract for this proof facade.<br/>
    /// </summary>
    public IndexKeys KeyContract => IndexKeys.NonUnique;

    /// <summary>
    /// Gets the logical key family exposed to condition classification.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.String;

    /// <summary>
    /// Gets the logical identity family exposed to condition classification.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

    /// <summary>
    /// Gets the logical shape descriptor used by condition classification.<br/>
    /// The descriptor declares folded and sort-key projection availability only when this facade owns the corresponding projection indexes.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec LogicalShape => CreateLogicalShape();

    /// <summary>
    /// Gets the runtime string comparison policy attached to this logical string index.<br/>
    /// The policy is consulted only by managed residual comparison and prepared membership fallback routes; exact encoded-key and maintained projection routes remain primary.<br/>
    /// </summary>
    public LibraDexStringComparisonPolicy? StringComparisonPolicy => stringComparisonPolicy;

    /// <summary>
    /// Inserts one exact string key and scalar identity, also maintaining the folded-text and sort-key projections when present.<br/>
    /// The exact and projection keys are written in the same call so condition projection retrieval does not require callers to manually populate companion indexes.<br/>
    /// </summary>
    /// <param name="key">The developer-facing string key.</param>
    /// <param name="identity">The scalar identity to associate with the key.</param>
    /// <returns>The exact-index insert outcome projected to the generic insert result shape.</returns>
    public LibraDexGenericInsertResult Insert(string key, ulong identity)
    {
        ThrowIfDisposed();
        byte[] exactKey = Encode(key);
        VarKeyScalar8InsertOutcome exactResult = exact.Insert(exactKey, identity);
        if (folded is not null)
        {
            _ = folded.InsertProjected(key, identity);
        }

        if (exactReversed is not null)
        {
            _ = exactReversed.InsertProjected(Reverse(key), identity);
        }

        if (sortKey is not null)
        {
            _ = sortKey.InsertProjected(CreateSortKey(key, sortKeyCulture), identity);
        }

        if (foldedReversed is not null)
        {
            _ = foldedReversed.InsertProjected(Reverse(Fold(key, foldedCulture)), identity);
        }

        return new LibraDexGenericInsertResult(
            exactResult.Inserted,
            exactResult.CreatedInitialShelfRoute,
            default,
            exactResult.Commit);
    }

    /// <summary>
    /// Adds one exact string key and scalar identity, maintaining any owned projections the same way as <see cref="Insert(string, ulong)"/>.<br/>
    /// This is the preferred public spelling for ordinary logical string index population.<br/>
    /// </summary>
    /// <param name="key">The developer-facing string key.</param>
    /// <param name="identity">The scalar identity to associate with the key.</param>
    /// <returns>The exact-index insert outcome projected to the generic insert result shape.</returns>
    public LibraDexGenericInsertResult Add(string key, ulong identity)
    {
        return Insert(key, identity);
    }

    /// <summary>
    /// Inserts one runtime key and runtime identity after strict type validation.<br/>
    /// This lets the adopted condition and catalog surfaces treat the string facade like other non-generic indexes.<br/>
    /// </summary>
    /// <param name="key">The runtime key, which must be a string.</param>
    /// <param name="identity">The runtime identity, which must be a UInt64.</param>
    /// <returns>The insert result.</returns>
    public LibraDexGenericInsertResult Insert(object key, object identity)
    {
        return Insert(
            RequireStringKey(key, nameof(key)),
            RequireScalar8Identity(identity, nameof(identity)));
    }

    /// <summary>
    /// Deletes one exact runtime string key and UInt64 identity tuple after strict type validation.<br/>
    /// Projection rows are removed with exact tuple deletes so folded-text, sort-key, and reversed projections cannot retain stale entries for the deleted logical row.<br/>
    /// </summary>
    /// <param name="key">The runtime string key to delete.</param>
    /// <param name="identity">The runtime UInt64 identity to delete.</param>
    /// <returns><see langword="true"/> when the exact tuple was removed.</returns>
    public bool Delete(object key, object identity)
    {
        return DeleteExactTuple(RequireStringKey(key, nameof(key)), RequireScalar8Identity(identity, nameof(identity)));
    }

    /// <summary>
    /// Re-keys one runtime UInt64 identity when the caller knows the old string key.<br/>
    /// The replacement tuple is inserted before the old exact tuple is deleted, preserving the old row if replacement insertion cannot be verified.<br/>
    /// </summary>
    /// <param name="identity">The runtime UInt64 identity to move.</param>
    /// <param name="oldKey">The current runtime string key.</param>
    /// <param name="newKey">The replacement runtime string key.</param>
    /// <returns><see langword="true"/> when the old tuple existed and was removed after the replacement was available.</returns>
    public bool Rekey(object identity, object oldKey, object newKey)
    {
        ulong typedIdentity = RequireScalar8Identity(identity, nameof(identity));
        string typedOldKey = RequireStringKey(oldKey, nameof(oldKey));
        string typedNewKey = RequireStringKey(newKey, nameof(newKey));
        if (string.Equals(typedOldKey, typedNewKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ContainsExactTuple(typedOldKey, typedIdentity))
        {
            return false;
        }

        if (!ContainsExactTuple(typedNewKey, typedIdentity))
        {
            LibraDexGenericInsertResult insert = Insert(typedNewKey, typedIdentity);
            if (!insert.Inserted && !ContainsExactTuple(typedNewKey, typedIdentity))
            {
                throw new InvalidOperationException("String rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        return DeleteExactTuple(typedOldKey, typedIdentity);
    }

    /// <summary>
    /// Re-keys every visible string tuple for one runtime UInt64 identity when the caller does not know the old key.<br/>
    /// This is intentionally a scan over the exact string index until a maintained reverse identity lookup exists, matching the catalog `IIndex` contract without adding a separate retrieval facade.<br/>
    /// </summary>
    /// <param name="identity">The runtime UInt64 identity to move.</param>
    /// <param name="newKey">The replacement runtime string key.</param>
    /// <returns>The number of old tuples removed after replacement tuples were available.</returns>
    public long Rekey(object identity, object newKey)
    {
        ulong typedIdentity = RequireScalar8Identity(identity, nameof(identity));
        string typedNewKey = RequireStringKey(newKey, nameof(newKey));
        List<string> oldKeys = new();
        List<StringScalar8Tuple> tuples = MaterializeExactTuples(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
        for (int i = 0; i < tuples.Count; i++)
        {
            if (tuples[i].Identity == typedIdentity)
            {
                oldKeys.Add(tuples[i].Key);
            }
        }

        long changed = 0;
        for (int i = 0; i < oldKeys.Count; i++)
        {
            if (Rekey(typedIdentity, oldKeys[i], typedNewKey))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Prepares a runtime string membership set for condition-builder `InSet` calls.<br/>
    /// </summary>
    /// <param name="keys">The runtime string keys to validate and capture.</param>
    /// <returns>A strict prepared object set for this facade.</returns>
    public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys is ISet<object> objectSet)
        {
            foreach (object key in objectSet)
            {
                _ = key as string ?? throw new ArgumentException("String indexes require string membership keys.", nameof(keys));
            }

            IEqualityComparer<object>? comparer = objectSet is HashSet<object> hashSet ? hashSet.Comparer : null;
            return new LibraDexPreparedObjectSet(typeof(string), values: null, objectSet, comparer);
        }

        return new LibraDexPreparedObjectSet(typeof(string), keys.Select(key => key as string ?? throw new ArgumentException("String indexes require string membership keys.", nameof(keys))).Cast<object>().ToArray());
    }

    /// <summary>
    /// Resolves one condition index name against this logical string index.<br/>
    /// This helper keeps condition execution low-friction for callers that own this facade and do not want to build resolver dictionaries by hand.<br/>
    /// </summary>
    /// <param name="indexName">The condition index name.</param>
    /// <returns>This logical index when the name matches.</returns>
    public IIndex ResolveIndex(string indexName)
    {
        return string.Equals(indexName, Name, StringComparison.Ordinal)
            ? this
            : throw new KeyNotFoundException($"String index '{Name}' cannot resolve condition index '{indexName}'.");
    }

    /// <summary>
    /// Resolves a projection-backed condition leaf to a maintained string projection index when available.<br/>
    /// Culture-sensitive projections are returned only when the condition culture matches the culture used to maintain the projection.<br/>
    /// Unsupported projection kinds return null so the adopted condition bridge can report that the requested projection is still missing.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf requesting a projection.</param>
    /// <param name="classification">The classification that identified the projection kind.</param>
    /// <returns>The folded projection index for folded-text leaves, or null.</returns>
    public IIndex? ResolveProjection(LibraDexConditionLeafDescriptor descriptor, LibraDexConditionLeafClassification classification)
    {
        CultureInfo requestedCulture = ResolveCulture(descriptor.Culture);
        return classification.ProjectionKind switch
        {
            LibraDexIndexProjectionKind.Exact when descriptor.Operator == LibraDexConditionOperatorKind.EndsWith => exactReversed,
            LibraDexIndexProjectionKind.FoldedText when descriptor.Operator == LibraDexConditionOperatorKind.EndsWith && CulturesMatch(requestedCulture, foldedCulture) => foldedReversed,
            LibraDexIndexProjectionKind.FoldedText when CulturesMatch(requestedCulture, foldedCulture) => folded,
            LibraDexIndexProjectionKind.SortKey when CulturesMatch(requestedCulture, sortKeyCulture) => sortKey,
            _ => null
        };
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitiveCore(request, exact, static value => value).ToArray();
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitiveCore(request, exact, static value => value);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitiveCore(request, exact, static value => value).LongCount();
    }

    LibraDexIdentityMutationResult IIdentityPrimitiveMutator.DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return DeleteIdentityPrimitive(request);
    }

    IReadOnlyList<LibraDexObjectTuple> IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteTuplePrimitive(request);
    }

    bool IIdentityExactTupleMutator.ContainsExactTuple(object key, object identity)
    {
        return ContainsExactTuple(RequireStringKey(key, nameof(key)), RequireScalar8Identity(identity, nameof(identity)));
    }

    bool IIdentityExactTupleMutator.DeleteExactTuple(object key, object identity)
    {
        return DeleteExactTuple(RequireStringKey(key, nameof(key)), RequireScalar8Identity(identity, nameof(identity)));
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        return IterateAll(exact).ToArray();
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateAll(exact);
    }

    /// <summary>
    /// Disposes the exact and folded projection indexes owned by this facade.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        exact.Dispose();
        exactReversed?.Dispose();
        folded?.Dispose();
        sortKey?.Dispose();
        foldedReversed?.Dispose();
        disposed = true;
    }

    /// <summary>
    /// Deletes logical string rows matched by one condition primitive while maintaining every owned projection tuple.<br/>
    /// The method first captures exact string key and identity pairs from the exact index, then deletes the exact tuple and matching projection tuples inside one `VS8` durability batch.<br/>
    /// Projection deletes are exact tuple deletes, not projection-key range deletes, so folded or sort-key collisions do not remove unrelated logical rows.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>A tuple-oriented mutation result.</returns>
    private LibraDexIdentityMutationResult DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        List<StringScalar8Tuple> tuples = MaterializeExactTuples(request);
        if (tuples.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        long changed = 0;
        using VarKeyScalar8Batch batch = exact.BeginBatch();
        for (int i = 0; i < tuples.Count; i++)
        {
            StringScalar8Tuple tuple = tuples[i];
            byte[] exactKey = Encode(tuple.Key);
            if (!batch.DeleteExactTuple(exactKey, tuple.Identity))
            {
                continue;
            }

            changed++;
            DeleteProjectionTuplesInCurrentScope(tuple.Key, tuple.Identity);
        }

        _ = batch.Commit();
        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            tuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
    }

    /// <summary>
    /// Materializes exact string key and identity tuples matched by one normalized primitive request.<br/>
    /// Criteria-scoped `SetKey` uses this contract to capture the old exact key before inserting the replacement and deleting only the matched physical tuple.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>The matching exact string tuples as object-key/object-identity pairs.</returns>
    private IReadOnlyList<LibraDexObjectTuple> ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        List<StringScalar8Tuple> tuples = MaterializeExactTuples(request);
        LibraDexObjectTuple[] result = new LibraDexObjectTuple[tuples.Count];
        for (int i = 0; i < tuples.Count; i++)
        {
            result[i] = new LibraDexObjectTuple(tuples[i].Key, tuples[i].Identity);
        }

        return result;
    }

    /// <summary>
    /// Tests whether one exact string key and scalar identity tuple is visible in the exact projection.<br/>
    /// Replacement mutation uses this to distinguish an already-present replacement from an insert conflict before deleting the old tuple.<br/>
    /// </summary>
    /// <param name="key">The exact string key to test.</param>
    /// <param name="identity">The scalar identity to test.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    private bool ContainsExactTuple(string key, ulong identity)
    {
        ThrowIfDisposed();
        byte[] encodedKey = Encode(key);
        using VarKeyScalar8RangeReader reader = exact.OpenRangeReader(encodedKey, encodedKey);
        while (reader.MoveNext())
        {
            if (reader.CurrentEncodedIdentity == identity)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes one exact string key and scalar identity tuple while maintaining every owned projection tuple.<br/>
    /// The exact and projection rows are removed inside one `VS8` durability batch so condition `SetKey` cannot leave stale folded, sort-key, or reversed rows for the old key.<br/>
    /// </summary>
    /// <param name="key">The exact string key to delete.</param>
    /// <param name="identity">The scalar identity to delete.</param>
    /// <returns><see langword="true"/> when the exact tuple was removed.</returns>
    private bool DeleteExactTuple(string key, ulong identity)
    {
        ThrowIfDisposed();
        using VarKeyScalar8Batch batch = exact.BeginBatch();
        if (!batch.DeleteExactTuple(Encode(key), identity))
        {
            return false;
        }

        DeleteProjectionTuplesInCurrentScope(key, identity);
        _ = batch.Commit();
        return true;
    }

    /// <summary>
    /// Deletes maintained projection tuples for one exact string key inside the caller's active durability scope.<br/>
    /// Projection deletes are exact tuple deletes so folded-text and sort-key collisions preserve neighboring logical rows that share the projected key bytes.<br/>
    /// </summary>
    /// <param name="key">The original exact string key.</param>
    /// <param name="identity">The scalar identity paired with the key.</param>
    private void DeleteProjectionTuplesInCurrentScope(string key, ulong identity)
    {
        exactReversed?.DeleteProjectedExactTuple(Reverse(key), identity);
        folded?.DeleteProjectedExactTuple(key, identity);
        sortKey?.DeleteProjectedExactTuple(CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.DeleteProjectedExactTuple(Reverse(Fold(key, foldedCulture)), identity);
    }

    /// <summary>
    /// Captures exact string key and identity tuples matched by a normalized primitive request.<br/>
    /// Mutation uses tuple capture rather than identity-only projection because maintained projection rows must be removed with the original exact key and identity pair.<br/>
    /// The capture still routes through bounded `VS8` ranges for ordered primitives and candidate-range string predicates before applying residual text comparison when needed.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>The exact string tuples matched by the request.</returns>
    private List<StringScalar8Tuple> MaterializeExactTuples(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(), keyFilter: null, textFilter: null, request.TakeLimit),
            LibraDexCriteriaKind.Find => MaterializeExactTuplesInRange(
                Encode(RequireString(request.Values, 0)),
                Encode(RequireString(request.Values, 0)),
                keyFilter: null,
                textFilter: null,
                request.TakeLimit),
            LibraDexCriteriaKind.Between => MaterializeExactTuplesInRange(
                Encode(RequireString(request.Values, 0)),
                Encode(RequireString(request.Values, 1)),
                keyFilter: null,
                textFilter: null,
                request.TakeLimit),
            LibraDexCriteriaKind.Before => MaterializeExactTuplesBefore(Encode(RequireString(request.Values, 0)), inclusive: false, request.TakeLimit),
            LibraDexCriteriaKind.AtOrBefore => MaterializeExactTuplesBefore(Encode(RequireString(request.Values, 0)), inclusive: true, request.TakeLimit),
            LibraDexCriteriaKind.After => MaterializeExactTuplesAfter(Encode(RequireString(request.Values, 0)), inclusive: false, request.TakeLimit),
            LibraDexCriteriaKind.AtOrAfter => MaterializeExactTuplesAfter(Encode(RequireString(request.Values, 0)), inclusive: true, request.TakeLimit),
            LibraDexCriteriaKind.InSet => MaterializeExactMembershipTuples(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.StringPattern => MaterializeExactStringPatternTuples(RequireStringPatternPredicate(request.Values), request.TakeLimit),
            _ => throw new NotSupportedException($"{request.CriteriaKind} string identity deletion is not connected to the string scalar facade yet.")
        };
    }

    /// <summary>
    /// Captures exact string key and identity tuples from one encoded-key range.<br/>
    /// Optional key and text filters preserve exclusive varlen bounds and scan-backed string conditions while keeping route pruning in the `VS8` reader.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower encoded key.</param>
    /// <param name="upper">The inclusive upper encoded key.</param>
    /// <param name="keyFilter">Optional encoded-key filter.</param>
    /// <param name="textFilter">Optional decoded-string filter.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the range and filters.</returns>
    private List<StringScalar8Tuple> MaterializeExactTuplesInRange(
        byte[] lower,
        byte[] upper,
        Func<byte[], bool>? keyFilter,
        Func<string, bool>? textFilter,
        int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        using VarKeyScalar8RangeReader reader = exact.OpenRangeReader(lower, upper);
        while (reader.MoveNext())
        {
            byte[] keyBytes = reader.MaterializeCurrentKey();
            if (keyFilter is not null && !keyFilter(keyBytes))
            {
                continue;
            }

            string key = Decode(keyBytes);
            if (textFilter is not null && !textFilter(key))
            {
                continue;
            }

            tuples.Add(new StringScalar8Tuple(key, reader.CurrentEncodedIdentity));
            if (takeLimit is int limit && tuples.Count >= limit)
            {
                break;
            }
        }

        return tuples;
    }

    /// <summary>
    /// Captures tuples whose encoded key sorts before a boundary key.<br/>
    /// Exclusive boundaries are implemented by filtering equal encoded keys from the same routed range so no string successor/predecessor helper is required.<br/>
    /// </summary>
    /// <param name="boundary">The encoded boundary key.</param>
    /// <param name="inclusive">True to include keys equal to the boundary.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the boundary request.</returns>
    private List<StringScalar8Tuple> MaterializeExactTuplesBefore(byte[] boundary, bool inclusive, int? takeLimit)
    {
        return MaterializeExactTuplesInRange(
            FullLowerBound(),
            boundary,
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) < 0,
            textFilter: null,
            takeLimit);
    }

    /// <summary>
    /// Captures tuples whose encoded key sorts after a boundary key.<br/>
    /// Exclusive boundaries are implemented by filtering equal encoded keys from the same routed range so no string successor/predecessor helper is required.<br/>
    /// </summary>
    /// <param name="boundary">The encoded boundary key.</param>
    /// <param name="inclusive">True to include keys equal to the boundary.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the boundary request.</returns>
    private List<StringScalar8Tuple> MaterializeExactTuplesAfter(byte[] boundary, bool inclusive, int? takeLimit)
    {
        return MaterializeExactTuplesInRange(
            boundary,
            FullUpperBound(),
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) > 0,
            textFilter: null,
            takeLimit);
    }

    /// <summary>
    /// Captures exact tuples for a string membership primitive.<br/>
    /// Membership remains repeated exact key routing so hash-set preparation controls operand handling without forcing an index-wide scan.<br/>
    /// </summary>
    /// <param name="values">The primitive values containing one enumerable of string keys.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the membership request.</returns>
    private List<StringScalar8Tuple> MaterializeExactMembershipTuples(IReadOnlyList<object?> values, int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        foreach (object? value in values)
        {
            if (value is not IEnumerable<object> objects)
            {
                throw new InvalidOperationException("String membership primitive requires an object enumerable operand.");
            }

            foreach (object item in objects)
            {
                string key = item as string ?? throw new InvalidOperationException("String membership primitive requires string values.");
                tuples.AddRange(MaterializeExactTuplesInRange(Encode(key), Encode(key), keyFilter: null, textFilter: null, RemainingTake(takeLimit, tuples.Count)));
                if (takeLimit is int limit && tuples.Count >= limit)
                {
                    return tuples;
                }
            }
        }

        return tuples;
    }

    /// <summary>
    /// Captures exact tuples for a scan-backed or candidate-range string predicate.<br/>
    /// Candidate ranges are honored first so no-case prefix variants and similar coarse ranges avoid scanning the entire exact string index.<br/>
    /// </summary>
    /// <param name="predicate">The compiled condition predicate.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the predicate.</returns>
    private List<StringScalar8Tuple> MaterializeExactStringPatternTuples(LibraDexStringPatternPredicate predicate, int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        IReadOnlyList<(string Lower, string Upper)> candidateRanges = predicate.CreateCandidateRanges();
        if (candidateRanges.Count == 0)
        {
            return MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(), keyFilter: null, predicate.Matches, takeLimit);
        }

        foreach ((string lower, string upper) in candidateRanges)
        {
            tuples.AddRange(MaterializeExactTuplesInRange(Encode(lower), Encode(upper), keyFilter: null, predicate.Matches, RemainingTake(takeLimit, tuples.Count)));
            if (takeLimit is int limit && tuples.Count >= limit)
            {
                break;
            }
        }

        return tuples;
    }

    private static int? RemainingTake(int? takeLimit, int currentCount)
    {
        return takeLimit.HasValue ? Math.Max(0, takeLimit.Value - currentCount) : null;
    }

    /// <summary>
    /// Builds the logical shape descriptor for the projections physically owned by this facade.<br/>
    /// The descriptor is recreated on demand so the public shape remains derived from the actual maintained projection fields instead of a parallel mutable flag set.<br/>
    /// </summary>
    /// <returns>A logical string shape descriptor for condition classification.</returns>
    private LibraDexIndexShapeSpec CreateLogicalShape()
    {
        StringKeys stringKeys = StringKeys.Exact;
        List<LibraDexIndexProjectionSpec> projections = new()
        {
            new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending)
        };

        if (folded is not null)
        {
            stringKeys |= StringKeys.Folded;
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending));
        }

        if (sortKey is not null)
        {
            stringKeys |= StringKeys.SortKey;
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.SortKey, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending));
        }

        LibraDexProjectionDirectionSet directions = LibraDexProjectionDirectionSet.Forward;
        if (exactReversed is not null)
        {
            directions |= LibraDexProjectionDirectionSet.Reversed;
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Reversed, LibraDexIndexSortOrder.Ascending));
        }

        if (foldedReversed is not null)
        {
            directions |= LibraDexProjectionDirectionSet.Reversed;
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Reversed, LibraDexIndexSortOrder.Ascending));
        }

        return new LibraDexIndexShapeSpec(
            Group,
            Name,
            typeof(string),
            typeof(ulong),
            CatalogIndexKeyFamily.String,
            CatalogIndexIdentityFamily.Scalar,
            IndexKeys.NonUnique,
            stringKeys,
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            LibraDexIndexSortOrder.Ascending,
            projections.ToArray(),
            Array.Empty<LibraDexCompositeKeyPartSpec>());
    }

    private static IEnumerable<object> IterateIdentityPrimitiveCore(
        LibraDexIdentityPrimitiveRequest request,
        VarKeyScalar8Index index,
        Func<string, string> transform)
    {
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                return IterateAll(index);
            case LibraDexCriteriaKind.Find:
                string key = transform(RequireString(request.Values, 0));
                return IterateRange(index, key, key, null);
            case LibraDexCriteriaKind.Between:
                return IterateRange(index, transform(RequireString(request.Values, 0)), transform(RequireString(request.Values, 1)), request.TakeLimit);
            case LibraDexCriteriaKind.Before:
                return IterateBefore(index, Encode(transform(RequireString(request.Values, 0))), inclusive: false, request.TakeLimit);
            case LibraDexCriteriaKind.AtOrBefore:
                return IterateBefore(index, Encode(transform(RequireString(request.Values, 0))), inclusive: true, request.TakeLimit);
            case LibraDexCriteriaKind.After:
                return IterateAfter(index, Encode(transform(RequireString(request.Values, 0))), inclusive: false, request.TakeLimit);
            case LibraDexCriteriaKind.AtOrAfter:
                return IterateAfter(index, Encode(transform(RequireString(request.Values, 0))), inclusive: true, request.TakeLimit);
            case LibraDexCriteriaKind.InSet:
                return IterateMembership(index, request.Values, transform, request.TakeLimit);
            case LibraDexCriteriaKind.StringPattern:
                return IterateStringPattern(index, RequireStringPatternPredicate(request.Values), request.TakeLimit);
            default:
                throw new NotSupportedException($"{request.CriteriaKind} is not connected to the string scalar facade yet.");
        }
    }

    private static IEnumerable<object> IterateAll(VarKeyScalar8Index index)
    {
        return IterateRange(index, FullLowerBound(), FullUpperBound(), null);
    }

    private static IEnumerable<object> IterateRange(VarKeyScalar8Index index, string lower, string upper, int? takeLimit)
    {
        return IterateRange(index, Encode(lower), Encode(upper), takeLimit);
    }

    private static IEnumerable<object> IterateRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, int? takeLimit)
    {
        return IterateRange(index, lower, upper, takeLimit, keyFilter: null);
    }

    /// <summary>
    /// Streams identities from a routed variable-key/scalar-identity range and optionally filters the current encoded key.<br/>
    /// The key filter is used only for exclusive variable-length boundary cases where a cheap exact next/previous key is not yet part of the primitive surface.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index.</param>
    /// <param name="lower">The inclusive lower encoded key bound.</param>
    /// <param name="upper">The inclusive upper encoded key bound.</param>
    /// <param name="takeLimit">Optional identity limit.</param>
    /// <param name="keyFilter">Optional encoded-key filter applied after range navigation.</param>
    /// <returns>The matching scalar identities as runtime objects.</returns>
    private static IEnumerable<object> IterateRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, int? takeLimit, Func<byte[], bool>? keyFilter)
    {
        using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lower, upper);
        int yielded = 0;
        while (reader.MoveNext())
        {
            if (keyFilter is not null && !keyFilter(reader.MaterializeCurrentKey()))
            {
                continue;
            }

            yield return reader.CurrentEncodedIdentity;
            yielded++;
            if (takeLimit is int limit && yielded >= limit)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Streams identities whose encoded key sorts before a boundary key.<br/>
    /// Inclusive boundaries route directly to the upper range bound, while exclusive boundaries filter out equal encoded keys from that same narrow range.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index.</param>
    /// <param name="boundary">The encoded boundary key.</param>
    /// <param name="inclusive">True to include keys equal to the boundary.</param>
    /// <param name="takeLimit">Optional identity limit.</param>
    /// <returns>The matching scalar identities as runtime objects.</returns>
    private static IEnumerable<object> IterateBefore(VarKeyScalar8Index index, byte[] boundary, bool inclusive, int? takeLimit)
    {
        return IterateRange(
            index,
            FullLowerBound(),
            boundary,
            takeLimit,
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) < 0);
    }

    /// <summary>
    /// Streams identities whose encoded key sorts after a boundary key.<br/>
    /// Inclusive boundaries route directly from the lower range bound, while exclusive boundaries filter out equal encoded keys from that same narrow range.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index.</param>
    /// <param name="boundary">The encoded boundary key.</param>
    /// <param name="inclusive">True to include keys equal to the boundary.</param>
    /// <param name="takeLimit">Optional identity limit.</param>
    /// <returns>The matching scalar identities as runtime objects.</returns>
    private static IEnumerable<object> IterateAfter(VarKeyScalar8Index index, byte[] boundary, bool inclusive, int? takeLimit)
    {
        return IterateRange(
            index,
            boundary,
            FullUpperBound(),
            takeLimit,
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) > 0);
    }

    private static IEnumerable<object> IterateMembership(
        VarKeyScalar8Index index,
        IReadOnlyList<object?> values,
        Func<string, string> transform,
        int? takeLimit)
    {
        int yielded = 0;
        foreach (object? value in values)
        {
            if (value is not IEnumerable<object> objects)
            {
                throw new InvalidOperationException("String membership primitive requires an object enumerable operand.");
            }

            foreach (object item in objects)
            {
                string key = transform(item as string ?? throw new InvalidOperationException("String membership primitive requires string values."));
                foreach (object identity in IterateRange(index, key, key, null))
                {
                    yield return identity;
                    yielded++;
                    if (takeLimit is int limit && yielded >= limit)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Executes a condition-derived string predicate over exact stored string keys.<br/>
    /// The predicate may provide bounded candidate ranges, such as no-case prefix variants, and this method applies the residual .NET text comparison before yielding identities.<br/>
    /// </summary>
    /// <param name="index">The exact string index.</param>
    /// <param name="predicate">The compiled string predicate.</param>
    /// <param name="takeLimit">Optional identity limit.</param>
    /// <returns>The matching scalar identities as runtime objects.</returns>
    private static IEnumerable<object> IterateStringPattern(VarKeyScalar8Index index, LibraDexStringPatternPredicate predicate, int? takeLimit)
    {
        IReadOnlyList<(string Lower, string Upper)> candidateRanges = predicate.CreateCandidateRanges();
        int yielded = 0;
        if (candidateRanges.Count == 0)
        {
            foreach (object identity in IterateStringPatternRange(index, FullLowerBound(), FullUpperBound(), predicate, null))
            {
                yield return identity;
                yielded++;
                if (takeLimit is int limit && yielded >= limit)
                {
                    yield break;
                }
            }

            yield break;
        }

        foreach ((string lower, string upper) in candidateRanges)
        {
            foreach (object identity in IterateStringPatternRange(index, Encode(lower), Encode(upper), predicate, takeLimit.HasValue ? takeLimit.Value - yielded : null))
            {
                yield return identity;
                yielded++;
                if (takeLimit is int limit && yielded >= limit)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Scans one encoded-key range and applies a decoded string residual predicate.<br/>
    /// This keeps scan-backed string conditions on compact index key bytes instead of source records, while still preserving correctness for culture-aware comparisons.<br/>
    /// </summary>
    /// <param name="index">The exact string index.</param>
    /// <param name="lower">The inclusive lower encoded key bound.</param>
    /// <param name="upper">The inclusive upper encoded key bound.</param>
    /// <param name="predicate">The residual string predicate.</param>
    /// <param name="takeLimit">Optional identity limit for this range.</param>
    /// <returns>The matching scalar identities as runtime objects.</returns>
    private static IEnumerable<object> IterateStringPatternRange(
        VarKeyScalar8Index index,
        byte[] lower,
        byte[] upper,
        LibraDexStringPatternPredicate predicate,
        int? takeLimit)
    {
        using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lower, upper);
        int yielded = 0;
        while (reader.MoveNext())
        {
            string candidate = Decode(reader.CurrentKey);
            if (!predicate.Matches(candidate))
            {
                continue;
            }

            yield return reader.CurrentEncodedIdentity;
            yielded++;
            if (takeLimit is int limit && yielded >= limit)
            {
                yield break;
            }
        }
    }

    private static string RequireString(IReadOnlyList<object?> values, int ordinal)
    {
        return values.Count > ordinal && values[ordinal] is string text
            ? text
            : throw new InvalidOperationException("String primitive requests require string operands.");
    }

    private static LibraDexStringPatternPredicate RequireStringPatternPredicate(IReadOnlyList<object?> values)
    {
        return values.Count > 0 && values[0] is LibraDexStringPatternPredicate predicate
            ? predicate
            : throw new InvalidOperationException("String pattern primitive requests require a string predicate operand.");
    }

    private static string RequireStringKey(object value, string paramName)
    {
        return value as string ?? throw new ArgumentException("String indexes require string keys.", paramName);
    }

    private static ulong RequireScalar8Identity(object value, string paramName)
    {
        return value is ulong typed ? typed : throw new ArgumentException("StringScalar8 indexes require UInt64 identities.", paramName);
    }

    private const byte StringNullMarker = 0x00;
    private const byte StringEmptyMarker = 0x01;
    private const byte StringValueMarker = 0x02;

    /// <summary>
    /// Encodes one developer-facing string into LibraDex's ordered string-key byte contract.<br/>
    /// A sentinel byte keeps null, empty, and non-empty text distinct while preserving byte-ordered routing for normal UTF-8 string payloads.<br/>
    /// </summary>
    /// <param name="value">The non-null string value to encode.</param>
    /// <returns>The encoded string key bytes.</returns>
    private static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return new[] { StringEmptyMarker };
        }

        byte[] payload = Encoding.UTF8.GetBytes(value);
        byte[] encoded = new byte[payload.Length + 1];
        encoded[0] = StringValueMarker;
        payload.CopyTo(encoded, 1);
        return encoded;
    }

    /// <summary>
    /// Decodes one LibraDex ordered string-key value back into the developer-facing string value.<br/>
    /// The null marker is reserved for a future explicit null-key policy and is intentionally rejected by the current non-null string facade.<br/>
    /// </summary>
    /// <param name="encoded">The encoded string key bytes.</param>
    /// <returns>The decoded non-null string value.</returns>
    private static string Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length == 1 && encoded[0] == StringEmptyMarker)
        {
            return string.Empty;
        }

        if (encoded.Length > 1 && encoded[0] == StringValueMarker)
        {
            return Encoding.UTF8.GetString(encoded[1..]);
        }

        if (encoded.Length > 0 && encoded[0] == StringNullMarker)
        {
            throw new InvalidOperationException("String null-key marker is reserved but not enabled for this string index.");
        }

        throw new InvalidDataException("String index key did not use a recognized LibraDex string sentinel marker.");
    }

    /// <summary>
    /// Returns the current coarse lower bound used for full-range variable-key scans.<br/>
    /// This preserves the existing proof-slice behavior until empty-key and true full-byte-range semantics are settled for string facades.<br/>
    /// </summary>
    /// <returns>The lower encoded key bound.</returns>
    private static byte[] FullLowerBound()
    {
        return new byte[] { 0x00 };
    }

    /// <summary>
    /// Returns the current coarse upper bound used for full-range variable-key scans.<br/>
    /// This preserves the existing proof-slice behavior until empty-key and true full-byte-range semantics are settled for string facades.<br/>
    /// </summary>
    /// <returns>The upper encoded key bound.</returns>
    private static byte[] FullUpperBound()
    {
        return new byte[] { 0xFF };
    }

    /// <summary>
    /// Folds one string using the projection culture chosen when the string index was created.<br/>
    /// This mirrors the condition bridge's invariant-or-named culture behavior so projection lookup operands and maintained keys stay byte-compatible.<br/>
    /// </summary>
    /// <param name="value">The original developer-facing string value.</param>
    /// <param name="culture">The projection culture.</param>
    /// <returns>The culture-folded string value.</returns>
    private static string Fold(string value, CultureInfo culture)
    {
        return value.ToLower(culture);
    }

    /// <summary>
    /// Reverses a folded string by UTF-16 code unit positions for maintained suffix projection keys.<br/>
    /// LibraDex stores the reversed folded value at insert time so suffix conditions can become ordinary prefix-like ordered extents over the reversed projection.<br/>
    /// </summary>
    /// <param name="value">The folded string value to reverse.</param>
    /// <returns>The reversed folded string.</returns>
    private static string Reverse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[source.Length - 1 - i];
            }
        });
    }

    /// <summary>
    /// Creates a culture-sensitive sort-key projection value for one string.<br/>
    /// The returned bytes are stored directly in the maintained sort-key `VS8` projection and later compared through ordinary byte-range primitives.<br/>
    /// </summary>
    /// <param name="value">The original developer-facing string value.</param>
    /// <param name="culture">The projection culture.</param>
    /// <returns>The sort-key bytes for ignore-case comparison in the supplied culture.</returns>
    private static byte[] CreateSortKey(string value, CultureInfo culture)
    {
        return culture.CompareInfo.GetSortKey(value, CompareOptions.IgnoreCase).KeyData;
    }

    /// <summary>
    /// Resolves an optional culture name into the culture used for maintained string projection keys.<br/>
    /// Null or empty culture names intentionally mean invariant culture, matching the adopted condition bridge convention.<br/>
    /// </summary>
    /// <param name="culture">The optional culture name.</param>
    /// <returns>The resolved culture.</returns>
    private static CultureInfo ResolveCulture(string? culture)
    {
        return string.IsNullOrEmpty(culture)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(culture);
    }

    /// <summary>
    /// Compares two resolved culture instances by name for projection compatibility checks.<br/>
    /// Projection lookup must not use a maintained sort-key or folded index with a different culture because that can produce incorrect byte ordering or equality.<br/>
    /// </summary>
    /// <param name="left">The condition-requested culture.</param>
    /// <param name="right">The projection-maintained culture.</param>
    /// <returns>True when both cultures represent the same projection convention.</returns>
    private static bool CulturesMatch(CultureInfo left, CultureInfo right)
    {
        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexStringScalar8Index));
        }
    }

    private readonly record struct StringScalar8Tuple(string Key, ulong Identity);

    private sealed class LibraDexStringScalar8ProjectionIndex : IIndex, IIdentityPrimitiveExecutor, IDisposable
    {
        private readonly VarKeyScalar8Index index;
        private readonly Func<string, string> transform;

        internal LibraDexStringScalar8ProjectionIndex(string group, string name, VarKeyScalar8Index index, Func<string, string> transform)
        {
            Group = group;
            Name = name;
            this.index = index;
            this.transform = transform;
        }

        public string Name { get; }

        public string Group { get; }

        public Type KeyType => typeof(string);

        public Type IdentityType => typeof(ulong);

        public IndexKeys KeyContract => IndexKeys.NonUnique;

        public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.String;

        public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

        public LibraDexIndexShapeSpec? LogicalShape => null;

        public LibraDexGenericInsertResult Insert(object key, object identity)
        {
            return InsertProjected(
                key as string ?? throw new ArgumentException("String projection indexes require string keys.", nameof(key)),
                identity is ulong typed ? typed : throw new ArgumentException("String projection indexes require UInt64 identities.", nameof(identity)));
        }

        public LibraDexGenericInsertResult InsertProjected(string key, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.Insert(Encode(transform(key)), identity);
            return new LibraDexGenericInsertResult(result.Inserted, result.CreatedInitialShelfRoute, default, result.Commit);
        }

        /// <summary>
        /// Deletes one maintained projection tuple using the projection's configured string transform.<br/>
        /// This is intentionally exact-tuple deletion so folded projection collisions do not remove identities belonging to another exact string key.<br/>
        /// The owning logical string facade coordinates the surrounding durability batch before invoking this helper.<br/>
        /// </summary>
        /// <param name="key">The projection source key value expected by this projection wrapper.</param>
        /// <param name="identity">The encoded scalar identity to delete.</param>
        /// <returns><see langword="true"/> when a live projection tuple was deleted.</returns>
        internal bool DeleteProjectedExactTuple(string key, ulong identity)
        {
            return index.DeleteExactTupleInCurrentScope(Encode(transform(key)), identity);
        }

        public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            return new LibraDexPreparedObjectSet(typeof(string), keys.Select(key => key as string ?? throw new ArgumentException("String projection indexes require string keys.", nameof(keys))).Cast<object>().ToArray());
        }

        public void Dispose()
        {
            index.Dispose();
        }

        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateIdentityPrimitiveCore(request, index, transform).ToArray();
        }

        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateIdentityPrimitiveCore(request, index, transform);
        }

        long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateIdentityPrimitiveCore(request, index, transform).LongCount();
        }

        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
        {
            return IterateAll(index).ToArray();
        }

        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
        {
            return IterateAll(index);
        }
    }

    private sealed class LibraDexStringScalar8SortKeyProjectionIndex : IIndex, IIdentityPrimitiveExecutor, IDisposable
    {
        private readonly VarKeyScalar8Index index;

        internal LibraDexStringScalar8SortKeyProjectionIndex(string group, string name, VarKeyScalar8Index index)
        {
            Group = group;
            Name = name;
            this.index = index;
        }

        public string Name { get; }

        public string Group { get; }

        public Type KeyType => typeof(byte[]);

        public Type IdentityType => typeof(ulong);

        public IndexKeys KeyContract => IndexKeys.NonUnique;

        public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Blob;

        public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

        public LibraDexIndexShapeSpec? LogicalShape => null;

        public LibraDexGenericInsertResult Insert(object key, object identity)
        {
            return InsertProjected(
                key as byte[] ?? throw new ArgumentException("Sort-key projection indexes require byte[] keys.", nameof(key)),
                identity is ulong typed ? typed : throw new ArgumentException("Sort-key projection indexes require UInt64 identities.", nameof(identity)));
        }

        public LibraDexGenericInsertResult InsertProjected(byte[] key, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.Insert(key, identity);
            return new LibraDexGenericInsertResult(result.Inserted, result.CreatedInitialShelfRoute, default, result.Commit);
        }

        /// <summary>
        /// Deletes one maintained sort-key projection tuple using the already materialized sort-key bytes.<br/>
        /// This is intentionally exact-tuple deletion so sort-key collisions do not remove identities belonging to another exact string key.<br/>
        /// The owning logical string facade coordinates the surrounding durability batch before invoking this helper.<br/>
        /// </summary>
        /// <param name="key">The maintained sort-key bytes.</param>
        /// <param name="identity">The encoded scalar identity to delete.</param>
        /// <returns><see langword="true"/> when a live projection tuple was deleted.</returns>
        internal bool DeleteProjectedExactTuple(byte[] key, ulong identity)
        {
            return index.DeleteExactTupleInCurrentScope(key, identity);
        }

        public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            return new LibraDexPreparedObjectSet(typeof(byte[]), keys.Select(key => key as byte[] ?? throw new ArgumentException("Sort-key projection indexes require byte[] keys.", nameof(keys))).Cast<object>().ToArray());
        }

        public void Dispose()
        {
            index.Dispose();
        }

        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateByteIdentityPrimitiveCore(request, index).ToArray();
        }

        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateByteIdentityPrimitiveCore(request, index);
        }

        long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateByteIdentityPrimitiveCore(request, index).LongCount();
        }

        IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
        {
            return IterateAll(index).ToArray();
        }

        IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
        {
            return IterateAll(index);
        }

        /// <summary>
        /// Executes condition-derived sort-key byte primitives over the maintained sort-key projection index.<br/>
        /// The condition materializer has already converted developer-facing string operands into culture-compatible sort-key byte arrays, so this executor only routes byte ranges and membership sets.<br/>
        /// </summary>
        /// <param name="request">The identity primitive request produced by the condition bridge.</param>
        /// <param name="index">The maintained sort-key projection index.</param>
        /// <returns>The matching scalar identities as runtime objects.</returns>
        private static IEnumerable<object> IterateByteIdentityPrimitiveCore(LibraDexIdentityPrimitiveRequest request, VarKeyScalar8Index index)
        {
            switch (request.CriteriaKind)
            {
                case LibraDexCriteriaKind.All:
                    return IterateAll(index);
                case LibraDexCriteriaKind.Find:
                    byte[] key = RequireBytes(request.Values, 0);
                    return IterateRange(index, key, key, null);
                case LibraDexCriteriaKind.Between:
                    return IterateRange(index, RequireBytes(request.Values, 0), RequireBytes(request.Values, 1), request.TakeLimit);
                case LibraDexCriteriaKind.Before:
                    return IterateBefore(index, RequireBytes(request.Values, 0), inclusive: false, request.TakeLimit);
                case LibraDexCriteriaKind.AtOrBefore:
                    return IterateBefore(index, RequireBytes(request.Values, 0), inclusive: true, request.TakeLimit);
                case LibraDexCriteriaKind.After:
                    return IterateAfter(index, RequireBytes(request.Values, 0), inclusive: false, request.TakeLimit);
                case LibraDexCriteriaKind.AtOrAfter:
                    return IterateAfter(index, RequireBytes(request.Values, 0), inclusive: true, request.TakeLimit);
                case LibraDexCriteriaKind.InSet:
                    return IterateByteMembership(index, request.Values, request.TakeLimit);
                default:
                    throw new NotSupportedException($"{request.CriteriaKind} is not connected to the sort-key string projection facade yet.");
            }
        }

        /// <summary>
        /// Executes sort-key membership as repeated exact byte-key lookups against the maintained projection index.<br/>
        /// This keeps the first physical sort-key proof on the same ordinary byte-range primitive as exact lookup and ordered comparison.<br/>
        /// </summary>
        /// <param name="index">The maintained sort-key projection index.</param>
        /// <param name="values">The primitive request values containing one enumerable of byte-array sort keys.</param>
        /// <param name="takeLimit">Optional identity limit.</param>
        /// <returns>The matching scalar identities as runtime objects.</returns>
        private static IEnumerable<object> IterateByteMembership(VarKeyScalar8Index index, IReadOnlyList<object?> values, int? takeLimit)
        {
            int yielded = 0;
            foreach (object? value in values)
            {
                if (value is not IEnumerable<object> objects)
                {
                    throw new InvalidOperationException("Sort-key membership primitive requires an object enumerable operand.");
                }

                foreach (object item in objects)
                {
                    byte[] key = item as byte[] ?? throw new InvalidOperationException("Sort-key membership primitive requires byte[] values.");
                    foreach (object identity in IterateRange(index, key, key, null))
                    {
                        yield return identity;
                        yielded++;
                        if (takeLimit is int limit && yielded >= limit)
                        {
                            yield break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Reads one required byte-array operand from a sort-key primitive request.<br/>
        /// Projection materialization should fail before execution if a generated descriptor supplies a non-byte-array sort-key operand.<br/>
        /// </summary>
        /// <param name="values">The primitive request values.</param>
        /// <param name="ordinal">The required operand ordinal.</param>
        /// <returns>The byte-array sort-key operand.</returns>
        private static byte[] RequireBytes(IReadOnlyList<object?> values, int ordinal)
        {
            return values.Count > ordinal && values[ordinal] is byte[] bytes
                ? bytes
                : throw new InvalidOperationException("Sort-key primitive requests require byte[] operands.");
        }
    }
}
