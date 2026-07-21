using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LibraDex;

/// <summary>
/// Provides the first physical string-key facade over routed varlen-key/scalar-identity indexes.<br/>
/// The exact index stores UTF-8 encoded strings, while optional folded and sort-key projections store maintained alternate keys for case-insensitive condition branches.<br/>
/// This type is intentionally narrow: it proves maintained projection storage and condition projection resolution before widening string profiles to reversed projections, persisted projection metadata, or variable-width identity families.<br/>
/// </summary>
public sealed class LibraDexStringScalar8Index : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveMutator, IIdentityPrimitiveTupleExecutor, IIdentityPrimitiveTupleStreamer, IIdentityExactTupleMutator, ILibraDexStringComparisonPolicyProvider, IDisposable
{
    private readonly Catalog? catalog;
    private readonly VarKeyScalar8Index exact;
    private readonly LibraDexStringScalar8ProjectionIndex? exactReversed;
    private readonly LibraDexStringScalar8ProjectionIndex? folded;
    private readonly LibraDexStringScalar8SortKeyProjectionIndex? sortKey;
    private readonly LibraDexStringScalar8ProjectionIndex? foldedReversed;
    private readonly CultureInfo foldedCulture;
    private readonly CultureInfo sortKeyCulture;
    private readonly LibraDexStringComparisonPolicy? stringComparisonPolicy;
    [ThreadStatic]
    private static LibraDexStringScalar8ThreadInsertDiagnostics? threadInsertDiagnostics;
    private bool disposed;

    internal LibraDexStringScalar8Index(
        Catalog? catalog,
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
        this.catalog = catalog;
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

    public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

    /// <summary>
    /// Gets the logical key family exposed to condition classification.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.String;

    /// <summary>
    /// Gets the logical identity family exposed to condition classification.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

#if LIBRADEX_PREFIX_COUNT_TELEMETRY
    internal VarKeyScalar8PrefixCountTelemetry LastExactPrefixCountTelemetry => exact.LastPrefixCountTelemetry;
#endif

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
    /// Inserts one exact string key and scalar identity, also maintaining the folded-text and sort-key projections when present for non-empty text.<br/>
    /// Null and empty string keys are written to metadata-backed identity routes so they remain compact and fast to enumerate before ordinary non-empty string values.<br/>
    /// Non-empty exact and projection keys are written in the same call so condition projection retrieval does not require callers to manually populate companion indexes.<br/>
    /// </summary>
    /// <param name="key">The developer-facing string key, or null for the null-key sentinel.</param>
    /// <param name="identity">The scalar identity to associate with the key.</param>
    /// <returns>The exact-index insert outcome projected to the generic insert result shape.</returns>
    public LibraDexGenericInsertResult Insert(string? key, ulong identity)
    {
        ThrowIfDisposed();
        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.Insert(this, key, identity);
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return InsertStringKeyStateIdentity(keyState, identity);
        }

        byte[] exactKey = Encode(key);
        VarKeyScalar8InsertOutcome exactResult = exact.InsertEncoded(exactKey, identity);
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
            LibraDexOperationDiagnostics.FromDataKernel(exactResult.Commit));
    }

    /// <summary>
    /// Starts a concurrent batch for this string/scalar8 index and its maintained folded, sort-key, and reversed projection indexes.<br/>
    /// The batch stages exact and projection `VS8` shelf-local writes in one shared writer context and publishes once, falling back only when a logical operation requires topology work.<br/>
    /// This is not a transaction; it is a physical concurrency and publication-cadence surface for bulk string index maintenance.<br/>
    /// </summary>
    /// <returns>A string concurrent batch facade for this index.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when this index has been disposed.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when a session durability batch or identity-group batch is active.<br/></exception>
    public LibraDexStringScalar8ConcurrentBatch BeginConcurrentBatch()
    {
        ThrowIfDisposed();
        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out _) == true)
        {
            throw new InvalidOperationException("The string concurrent batch cannot start while an identity-group batch is active.");
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();
        return new LibraDexStringScalar8ConcurrentBatch(this);
    }

    /// <summary>
    /// Prepares one string key and every maintained projection owned by this facade into reusable encoded bytes.<br/>
    /// Bulk loaders use this for low-cardinality string fields where the same logical key is inserted many times; the routed index still receives the same encoded bytes, but folding, reversing, sort-key creation, and sentinel wrapping happen once per distinct source value.<br/>
    /// The prepared key is tied to this facade's projection configuration and should not be reused with a differently configured string index.<br/>
    /// </summary>
    /// <param name="key">The developer-facing string key, or null for the null-key sentinel.</param>
    /// <returns>The prepared exact and projection key bytes for this facade.</returns>
    internal LibraDexStringScalar8PreparedKey PrepareKey(string? key)
    {
        ThrowIfDisposed();
        if (TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return new LibraDexStringScalar8PreparedKey(
                key,
                keyState,
                null,
                null,
                null,
                null,
                null);
        }

        byte[] exactKey = Encode(key);
        byte[]? foldedKey = folded is null ? null : Encode(Fold(key, foldedCulture));
        byte[]? exactReversedKey = exactReversed is null ? null : Encode(Reverse(key));
        byte[]? sortKeyBytes = sortKey is null ? null : CreateSortKey(key, sortKeyCulture);
        byte[]? foldedReversedKey = foldedReversed is null ? null : Encode(Reverse(Fold(key, foldedCulture)));
        return new LibraDexStringScalar8PreparedKey(
            key,
            null,
            exactKey,
            foldedKey,
            exactReversedKey,
            sortKeyBytes,
            foldedReversedKey);
    }

    /// <summary>
    /// Inserts one prepared string key and scalar identity, preserving the same exact/projection maintenance as <see cref="Insert(string?, ulong)"/>.<br/>
    /// The prepared form avoids per-row projection allocation for repeated keys while keeping normal group-batch routing and telemetry semantics.<br/>
    /// </summary>
    /// <param name="prepared">The prepared key returned by <see cref="PrepareKey(string?)"/> for this facade.</param>
    /// <param name="identity">The scalar identity to associate with the key.</param>
    /// <returns>The exact-index insert outcome projected to the generic insert result shape.</returns>
    internal LibraDexGenericInsertResult InsertPrepared(LibraDexStringScalar8PreparedKey prepared, ulong identity)
    {
        ThrowIfDisposed();
        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.InsertPrepared(this, prepared, identity);
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (prepared.KeyState is NullKey keyState)
        {
            return InsertStringKeyStateIdentity(keyState, identity);
        }

        VarKeyScalar8InsertOutcome exactResult = exact.InsertEncoded(prepared.ExactEncodedKey, identity);
        if (folded is not null)
        {
            _ = folded.InsertEncoded(RequirePreparedProjection(prepared.FoldedEncodedKey, "folded"), identity);
        }

        if (exactReversed is not null)
        {
            _ = exactReversed.InsertEncoded(RequirePreparedProjection(prepared.ExactReversedEncodedKey, "exact-reversed"), identity);
        }

        if (sortKey is not null)
        {
            _ = sortKey.InsertProjected(RequirePreparedProjection(prepared.SortKeyBytes, "sort-key"), identity);
        }

        if (foldedReversed is not null)
        {
            _ = foldedReversed.InsertEncoded(RequirePreparedProjection(prepared.FoldedReversedEncodedKey, "folded-reversed"), identity);
        }

        return new LibraDexGenericInsertResult(
            exactResult.Inserted,
            exactResult.CreatedInitialShelfRoute,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(exactResult.Commit));
    }

    internal LibraDexGenericInsertResult InsertInCurrentScope(string? key, ulong identity)
    {
        return InsertInCurrentScope(key, identity, scratch: null);
    }

    internal LibraDexGenericInsertResult InsertInCurrentScope(string? key, ulong identity, LibraDexStringScalar8InsertScratch? scratch)
    {
        ThrowIfDisposed();
        if (TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return InsertStringKeyStateIdentity(keyState, identity);
        }

        ReadOnlySpan<byte> exactKey = scratch is null
            ? Encode(key)
            : scratch.EncodeString(key);
        VarKeyScalar8InsertOutcome exactResult = exact.InsertEncodedInCurrentScope(exactKey, identity);
        RecordThreadInsertOutcome(Name, "exact", exactResult);
        if (folded is not null)
        {
            if (scratch is not null && IsInvariantCulture(foldedCulture))
            {
                _ = folded.InsertEncodedInCurrentScope(scratch.EncodeInvariantFoldedString(key), identity);
            }
            else
            {
                _ = folded.InsertProjectedInCurrentScope(key, identity, scratch);
            }
        }

        if (exactReversed is not null)
        {
            _ = exactReversed.InsertProjectedInCurrentScope(Reverse(key), identity, scratch);
        }

        if (sortKey is not null)
        {
            _ = sortKey.InsertProjectedInCurrentScope(CreateSortKey(key, sortKeyCulture), identity, scratch);
        }

        if (foldedReversed is not null)
        {
            _ = foldedReversed.InsertProjectedInCurrentScope(Reverse(Fold(key, foldedCulture)), identity, scratch);
        }

        return new LibraDexGenericInsertResult(
            exactResult.Inserted,
            exactResult.CreatedInitialShelfRoute,
            default,
            default);
    }

    internal LibraDexGenericInsertResult InsertPreparedInCurrentScope(LibraDexStringScalar8PreparedKey prepared, ulong identity, LibraDexStringScalar8InsertScratch? scratch)
    {
        ThrowIfDisposed();
        if (prepared.KeyState is NullKey keyState)
        {
            return InsertStringKeyStateIdentity(keyState, identity);
        }

        VarKeyScalar8InsertOutcome exactResult = exact.InsertEncodedInCurrentScope(prepared.ExactEncodedKey, identity);
        RecordThreadInsertOutcome(Name, "exact", exactResult);
        if (folded is not null)
        {
            _ = folded.InsertEncodedInCurrentScope(RequirePreparedProjection(prepared.FoldedEncodedKey, "folded"), identity);
        }

        if (exactReversed is not null)
        {
            _ = exactReversed.InsertEncodedInCurrentScope(RequirePreparedProjection(prepared.ExactReversedEncodedKey, "exact-reversed"), identity);
        }

        if (sortKey is not null)
        {
            _ = sortKey.InsertProjectedInCurrentScope(RequirePreparedProjection(prepared.SortKeyBytes, "sort-key"), identity, scratch);
        }

        if (foldedReversed is not null)
        {
            _ = foldedReversed.InsertEncodedInCurrentScope(RequirePreparedProjection(prepared.FoldedReversedEncodedKey, "folded-reversed"), identity);
        }

        return new LibraDexGenericInsertResult(
            exactResult.Inserted,
            exactResult.CreatedInitialShelfRoute,
            default,
            default);
    }

    public static void ResetThreadInsertDiagnostics()
    {
        threadInsertDiagnostics = new LibraDexStringScalar8ThreadInsertDiagnostics();
        LibraDexFileSession.ResetVarKeyScalar8PhaseAllocationDiagnostics();
    }

    public static string CreateThreadInsertDiagnosticsSummary()
    {
        return string.Concat(
            threadInsertDiagnostics?.CreateSummary() ?? "none",
            " || phases: ",
            LibraDexFileSession.CreateVarKeyScalar8PhaseAllocationDiagnosticsSummary());
    }

    private static void RecordThreadInsertOutcome(string indexName, string projectionName, VarKeyScalar8InsertOutcome outcome)
    {
        threadInsertDiagnostics?.Record(indexName, projectionName, outcome);
    }

    private static byte[] RequirePreparedProjection(byte[]? bytes, string projectionName)
        => bytes ?? throw new InvalidOperationException($"Prepared string key did not include the configured '{projectionName}' projection.");

    /// <summary>
    /// Adds one exact string key and scalar identity, maintaining any owned projections the same way as <see cref="Insert(string?, ulong)"/>.<br/>
    /// This is the preferred public spelling for ordinary logical string index population.<br/>
    /// </summary>
    /// <param name="key">The developer-facing string key, or null for the null-key sentinel.</param>
    /// <param name="identity">The scalar identity to associate with the key.</param>
    /// <returns>The exact-index insert outcome projected to the generic insert result shape.</returns>
    public LibraDexGenericInsertResult Add(string? key, ulong identity)
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
    public LibraDexGenericInsertResult Insert(object? key, object identity)
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
    public bool Delete(object? key, object identity)
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
    public bool Rekey(object identity, object? oldKey, object? newKey)
    {
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        ulong typedIdentity = RequireScalar8Identity(identity, nameof(identity));
        string? typedOldKey = RequireStringKey(oldKey, nameof(oldKey));
        string? typedNewKey = RequireStringKey(newKey, nameof(newKey));
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
    public long Rekey(object identity, object? newKey)
    {
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        ulong typedIdentity = RequireScalar8Identity(identity, nameof(identity));
        string? typedNewKey = RequireStringKey(newKey, nameof(newKey));
        List<string?> oldKeys = new();
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
            foreach (object? key in objectSet)
            {
                if (key is not null && key is not string)
                {
                    throw new ArgumentException("String indexes require string membership keys.", nameof(keys));
                }
            }

            IEqualityComparer<object>? comparer = objectSet is HashSet<object> hashSet ? hashSet.Comparer : null;
            return new LibraDexPreparedObjectSet(typeof(string), values: null, objectSet, comparer);
        }

        return new LibraDexPreparedObjectSet(typeof(string), keys.Select(key =>
        {
            if (key is null)
            {
                return null;
            }

            return key as string ?? throw new ArgumentException("String indexes require string membership keys.", nameof(keys));
        }).Cast<object>().ToArray());
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

    /// <summary>
    /// Streams maintained folded-text projection tuples for grouping and proof paths that explicitly request folded string buckets.<br/>
    /// The returned keys are folded projection values, not exact developer-facing strings, so callers can distinguish normalized grouping from exact grouping at the API boundary.<br/>
    /// </summary>
    /// <returns>A forward-only stream of folded string key and scalar identity tuples.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when this string index was not created with a folded-text projection.<br/></exception>
    internal IEnumerable<LibraDexObjectTuple> IterateFoldedTextTuplePrimitive()
    {
        ThrowIfDisposed();
        if (folded is null)
        {
            throw new InvalidOperationException($"String index '{Name}' does not maintain a folded-text projection.");
        }

        return folded.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    /// <summary>
    /// Streams exact string grouping tuples in their maintained binary key form.<br/>
    /// Null and empty routes are represented by the same sentinel bytes used by ordinary exact string storage, so grouping does not decode every key to a managed string.<br/>
    /// </summary>
    /// <returns>A forward-only stream of encoded exact string keys and scalar identities.<br/></returns>
    internal IEnumerable<LibraDexObjectTuple> IterateExactEncodedGroupingTuplePrimitive()
    {
        ThrowIfDisposed();
        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Null })))
        {
            yield return new LibraDexObjectTuple(EncodedNullKey, tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Empty })))
        {
            yield return new LibraDexObjectTuple(EncodedEmptyKey, tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in IterateEncodedStringGroupingTuples(exact))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams one maintained string store directly through its encoded range reader.<br/>
    /// Keys are copied only when emitted so byte-native grouping never constructs managed strings.<br/>
    /// </summary>
    /// <param name="index">The exact or Folded physical string store.<br/></param>
    /// <returns>A forward-only stream of encoded string keys and scalar identities.<br/></returns>
    private static IEnumerable<LibraDexObjectTuple> IterateEncodedStringGroupingTuples(VarKeyScalar8Index index)
    {
        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(
            FullLowerBound(),
            FullUpperBound(index.Handle.MaxKeyLength));
        while (reader.MoveNext())
            yield return new LibraDexObjectTuple(reader.MaterializeCurrentKey(), reader.CurrentEncodedIdentity);
    }

    /// <summary>
    /// Streams folded string grouping tuples in their maintained binary key form.<br/>
    /// Null and empty exact routes retain their own sentinels; ordinary values come directly from the Folded subindex without managed string conversion.<br/>
    /// </summary>
    /// <returns>A forward-only stream of encoded folded string keys and scalar identities.<br/></returns>
    internal IEnumerable<LibraDexObjectTuple> IterateFoldedEncodedGroupingTuplePrimitive()
    {
        ThrowIfDisposed();
        if (folded is null)
            throw new InvalidOperationException($"String index '{Name}' does not maintain a folded-text projection.");

        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Null })))
        {
            yield return new LibraDexObjectTuple(EncodedNullKey, tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Empty })))
        {
            yield return new LibraDexObjectTuple(EncodedEmptyKey, tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in folded.IterateEncodedTuplePrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Streams maintained sort-key projection tuples for grouping and proof paths that explicitly request binary sort-key buckets.<br/>
    /// The returned keys are byte-array sort keys, not exact developer-facing strings, because sort-key bytes are the projection's natural grouping domain.<br/>
    /// </summary>
    /// <returns>A forward-only stream of binary sort-key and scalar identity tuples.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when this string index was not created with a sort-key projection.<br/></exception>
    internal IEnumerable<LibraDexObjectTuple> IterateSortKeyTuplePrimitive()
    {
        ThrowIfDisposed();
        if (sortKey is null)
        {
            throw new InvalidOperationException($"String index '{Name}' does not maintain a sort-key projection.");
        }

        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Null })))
        {
            yield return new LibraDexObjectTuple(CreateSortKey(null, sortKeyCulture), tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in IterateExactTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.Empty })))
        {
            yield return new LibraDexObjectTuple(CreateSortKey(string.Empty, sortKeyCulture), tuple.Identity);
        }

        foreach (LibraDexObjectTuple tuple in sortKey.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
        {
            yield return tuple;
        }
    }

    /// <summary>
    /// Opens a key/identity cursor for a completed condition rooted at this logical string index.<br/>
    /// The returned keys are exact string keys from this facade, even when the condition itself uses maintained folded, sort-key, or suffix projections to find matching identities.<br/>
    /// Descending direction streams exact string tuples from highest key to lowest key using the existing materialized tuple bridge.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching string-index entries to skip.</param>
    /// <param name="take">The optional maximum number of string-index entries to return.</param>
    /// <param name="direction">The requested exact-string key traversal direction.</param>
    /// <returns>A cursor over exact string key and scalar identity entries.</returns>
    public LibraDexIndexCursor<string, ulong> GetCursor(
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(condition);
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(ResolveIndex, ResolveProjection);
        _ = LibraDexConditionCursorExecutor.TryCreateDirectPrimitiveDeletePlan(
            criterion,
            this,
            skip,
            take,
            out IIdentityPrimitiveMutator? primitiveMutator,
            out LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest);
        return new LibraDexIndexCursor<string, ulong>(
            LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, this, skip, take, direction),
            this,
            this,
            primitiveMutator,
            primitiveDeleteRequest,
            skip,
            take);
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
        return CountIdentityPrimitiveCore(request, exact, static value => value);
    }

    /// <summary>
    /// Executes count as a string/scalar8 aggregate over the exact string primitive path.<br/>
    /// Exact range-compatible conditions use key-state metadata and routed `VS8` counts; string-pattern branches are classified as key scans because they may apply residual text predicates.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.<br/></param>
    /// <returns>The aggregate count result and physical plan classification.<br/></returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to string scalar aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to string scalar aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitiveCore(request.PrimitiveRequest, exact, static value => value),
            ClassifyStringAggregatePlan(request.PrimitiveRequest));
    }

    LibraDexIdentityMutationResult IIdentityPrimitiveMutator.DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return DeleteIdentityPrimitive(request);
    }

    IReadOnlyList<LibraDexObjectTuple> IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteTuplePrimitive(request);
    }

    IEnumerable<LibraDexObjectTuple> IIdentityPrimitiveTupleStreamer.IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateExactTuplePrimitive(request);
    }

    bool IIdentityExactTupleMutator.ContainsExactTuple(object? key, object identity)
    {
        return ContainsExactTuple(RequireStringKey(key, nameof(key)), RequireScalar8Identity(identity, nameof(identity)));
    }

    bool IIdentityExactTupleMutator.DeleteExactTuple(object? key, object identity)
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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (!HasMaintainedProjections() &&
            TryDeleteExactOnlyIdentityPrimitiveByRange(request, out LibraDexIdentityMutationResult directDelete))
        {
            return directDelete;
        }

        List<StringScalar8Tuple> tuples = MaterializeExactTuples(request);
        if (tuples.Count == 0)
        {
            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                MatchedCount: 0,
                ChangedCount: 0,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: 0, RowsReturned: 0));
        }

        if (!HasMaintainedProjections())
        {
            return DeleteExactOnlyIdentityPrimitive(tuples);
        }

        return DeleteIdentityPrimitiveWithImmediateProjections(tuples);
    }

    /// <summary>
    /// Attempts direct exact-only string criteria deletion through the raw `VS8` encoded range path.<br/>
    /// The direct bridge is limited to all matching rows for exact or byte-bounded range predicates; limited, projection, residual, and key-state-heavy requests stay on tuple materialization.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.<br/></param>
    /// <param name="result">Receives the direct mutation result when the request is handled.<br/></param>
    /// <returns><see langword="true"/> when the request was fully handled by direct range deletion.<br/></returns>
    private bool TryDeleteExactOnlyIdentityPrimitiveByRange(
        LibraDexIdentityPrimitiveRequest request,
        out LibraDexIdentityMutationResult result)
    {
        result = default;
        if (request.TakeLimit is not null)
        {
            return false;
        }

        if (request.Direction is not QueryDirection.Ascending and not QueryDirection.Descending)
        {
            return false;
        }

        byte[] lower;
        byte[] upper;
        int matchedCount;
        if (request.CriteriaKind == LibraDexCriteriaKind.All)
        {
            long keyStateChanged = DeleteStringKeyStateRoute(NullKey.NullOrEmpty);
            lower = FullLowerBound();
            upper = FullUpperBound(exact.Handle.MaxKeyLength);
            matchedCount = CountEncodedRange(lower, upper);
            long allChanged = matchedCount == 0
                ? keyStateChanged
                : keyStateChanged + exact.DeleteEncodedRange(lower, upper);
            result = new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                keyStateChanged + matchedCount,
                allChanged,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: keyStateChanged + matchedCount, RowsReturned: allChanged));
            return true;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.KeyState)
        {
            long keyStateChanged = DeleteStringKeyStateRoute(RequireNullKeyState(request.Values));
            result = new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                keyStateChanged,
                keyStateChanged,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: keyStateChanged, RowsReturned: keyStateChanged));
            return true;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.InSet)
        {
            result = DeleteExactOnlyMembershipPrimitiveByRange(request.Values);
            return true;
        }

        if (request.CriteriaKind == LibraDexCriteriaKind.Find)
        {
            string? key = RequireString(request.Values, 0);
            if (TryClassifyStringKeyState(key, out NullKey keyState))
            {
                long keyStateChanged = DeleteStringKeyStateRoute(keyState);
                result = new LibraDexIdentityMutationResult(
                    LibraDexCriteriaMutationKind.Delete,
                    keyStateChanged,
                    keyStateChanged,
                    new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: keyStateChanged, RowsReturned: keyStateChanged));
                return true;
            }

            lower = Encode(key);
            upper = lower;
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.Between)
        {
            string? lowerText = RequireString(request.Values, 0);
            string? upperText = RequireString(request.Values, 1);
            if (TryClassifyStringKeyState(lowerText, out _) ||
                TryClassifyStringKeyState(upperText, out _))
            {
                return false;
            }

            lower = Encode(lowerText);
            upper = Encode(upperText);
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.Prefix)
        {
            string prefixText = RequireString(request.Values, 0) ?? throw new InvalidOperationException("String prefix deletion requires a non-null prefix operand.");
            if (prefixText.Length == 0)
            {
                return false;
            }

            lower = Encode(prefixText);
            upper = Encode(prefixText + '\uffff');
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.AtOrBefore)
        {
            string? boundaryText = RequireString(request.Values, 0);
            if (TryClassifyStringKeyState(boundaryText, out _))
            {
                return false;
            }

            lower = FullLowerBound();
            upper = Encode(boundaryText);
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.AtOrAfter)
        {
            string? boundaryText = RequireString(request.Values, 0);
            if (TryClassifyStringKeyState(boundaryText, out _))
            {
                return false;
            }

            lower = Encode(boundaryText);
            upper = FullUpperBound(exact.Handle.MaxKeyLength);
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.Before)
        {
            string? boundaryText = RequireString(request.Values, 0);
            if (TryClassifyStringKeyState(boundaryText, out _) ||
                !TryCreateExclusiveUpperBoundBefore(Encode(boundaryText), exact.Handle.MaxKeyLength, out upper))
            {
                return false;
            }

            lower = FullLowerBound();
            matchedCount = CountEncodedRange(lower, upper);
        }
        else if (request.CriteriaKind == LibraDexCriteriaKind.After)
        {
            string? boundaryText = RequireString(request.Values, 0);
            if (TryClassifyStringKeyState(boundaryText, out _) ||
                !TryCreateExclusiveLowerBoundAfter(Encode(boundaryText), exact.Handle.MaxKeyLength, out lower))
            {
                return false;
            }

            upper = FullUpperBound(exact.Handle.MaxKeyLength);
            matchedCount = CountEncodedRange(lower, upper);
        }
        else
        {
            return false;
        }

        long changed = matchedCount == 0
            ? 0
            : exact.DeleteEncodedRange(lower, upper);
        result = new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            matchedCount,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: matchedCount, RowsReturned: changed));
        return true;
    }

    /// <summary>
    /// Deletes all identities in one string key-state route and returns the number removed.<br/>
    /// The helper is used only by exact-only direct criteria deletion, where no maintained projection rows need a coupled batch boundary.<br/>
    /// </summary>
    /// <param name="keyState">The key-state route selector.<br/></param>
    /// <returns>The number of identities removed from the selected key-state route.<br/></returns>
    private long DeleteStringKeyStateRoute(NullKey keyState)
    {
        long changed = 0;
        if (keyState is NullKey.Null or NullKey.NullOrEmpty)
        {
            ulong[] identities = ReadStringKeyStateIdentities(NullKey.Null, takeLimit: null).ToArray();
            for (int i = 0; i < identities.Length; i++)
            {
                if (exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(NullKey.Null), identities[i]))
                {
                    changed++;
                }
            }
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            ulong[] identities = ReadStringKeyStateIdentities(NullKey.Empty, takeLimit: null).ToArray();
            for (int i = 0; i < identities.Length; i++)
            {
                if (exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(NullKey.Empty), identities[i]))
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Counts exact string tuples in one encoded `VS8` key range without materializing key strings.<br/>
    /// Direct criteria delete uses this to preserve matched-count diagnostics before mutating the same physical range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower encoded key.<br/></param>
    /// <param name="upper">The inclusive upper encoded key.<br/></param>
    /// <returns>The number of tuples currently visible in the encoded range.<br/></returns>
    private int CountEncodedRange(byte[] lower, byte[] upper)
    {
        if (lower.AsSpan().SequenceCompareTo(upper) > 0)
        {
            return 0;
        }

        using VarKeyScalar8RangeReader reader = exact.OpenEncodedRangeReader(lower, upper);
        return reader.Count;
    }

    /// <summary>
    /// Deletes exact-only string membership operands through direct key-state and encoded `VS8` range deletes.<br/>
    /// Match counting is performed before mutation and intentionally counts duplicate operands more than once, matching the previous tuple-materialization diagnostics while deleting each unique physical key route once.<br/>
    /// </summary>
    /// <param name="values">The primitive membership operands.<br/></param>
    /// <returns>The tuple-oriented mutation result for the membership delete.<br/></returns>
    private LibraDexIdentityMutationResult DeleteExactOnlyMembershipPrimitiveByRange(IReadOnlyList<object?> values)
    {
        long matched = 0;
        bool deleteNull = false;
        bool deleteEmpty = false;
        List<byte[]> ordinaryKeys = new();
        HashSet<string?> seen = new(StringComparer.Ordinal);
        foreach (object? value in values)
        {
            if (value is not IEnumerable<object> objects)
            {
                throw new InvalidOperationException("String membership primitive requires an object enumerable operand.");
            }

            foreach (object? item in objects)
            {
                string? key = item as string;
                if (item is not null && key is null)
                {
                    throw new InvalidOperationException("String membership primitive requires string values.");
                }

                if (TryClassifyStringKeyState(key, out NullKey keyState))
                {
                    long keyStateCount = CountStringKeyStateRoute(keyState);
                    matched += keyStateCount;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (keyState == NullKey.Null)
                    {
                        deleteNull = true;
                    }
                    else
                    {
                        deleteEmpty = true;
                    }

                    continue;
                }

                byte[] encodedKey = Encode(key);
                matched += CountEncodedRange(encodedKey, encodedKey);
                if (seen.Add(key))
                {
                    ordinaryKeys.Add(encodedKey);
                }
            }
        }

        long changed = 0;
        if (deleteNull)
        {
            changed += DeleteStringKeyStateRoute(NullKey.Null);
        }

        if (deleteEmpty)
        {
            changed += DeleteStringKeyStateRoute(NullKey.Empty);
        }

        for (int i = 0; i < ordinaryKeys.Count; i++)
        {
            byte[] encodedKey = ordinaryKeys[i];
            changed += exact.DeleteEncodedRange(encodedKey, encodedKey);
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            matched,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: matched, RowsReturned: changed));
    }

    /// <summary>
    /// Counts identities in one string key-state route without mutating it.<br/>
    /// Direct membership delete uses this to preserve matched-count diagnostics before deleting each unique key-state route once.<br/>
    /// </summary>
    /// <param name="keyState">The key-state route selector.<br/></param>
    /// <returns>The number of identities currently visible in the selected key-state route.<br/></returns>
    private long CountStringKeyStateRoute(NullKey keyState)
    {
        long count = 0;
        if (keyState is NullKey.Null or NullKey.NullOrEmpty)
        {
            count += exact.Session.CountScalar8KeyStateIdentities(exact.SlotIndex, KeyStateRoute.Null);
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            count += exact.Session.CountScalar8KeyStateIdentities(exact.SlotIndex, KeyStateRoute.Empty);
        }

        return count;
    }

    /// <summary>
    /// Creates an inclusive upper encoded-key bound for deleting keys strictly before one encoded boundary.<br/>
    /// If the boundary ends in zero, the nearest lower bound is the shorter prefix; otherwise the last byte is decremented and padded with `0xFF` so every lower extension remains covered.<br/>
    /// </summary>
    /// <param name="boundary">The encoded string boundary key.<br/></param>
    /// <param name="maxKeyLength">The routed var-key profile's maximum encoded key length.<br/></param>
    /// <param name="upper">Receives the inclusive upper bound when one exists.<br/></param>
    /// <returns><see langword="true"/> when a non-empty lower range can be represented.<br/></returns>
    private static bool TryCreateExclusiveUpperBoundBefore(byte[] boundary, int maxKeyLength, out byte[] upper)
    {
        upper = Array.Empty<byte>();
        if (boundary.Length == 0)
        {
            return false;
        }

        if (boundary[^1] == 0)
        {
            if (boundary.Length == 1)
            {
                return false;
            }

            upper = boundary[..^1];
            return true;
        }

        int upperLength = Math.Max(boundary.Length, maxKeyLength);
        upper = GC.AllocateUninitializedArray<byte>(upperLength);
        boundary.CopyTo(upper.AsSpan(0, boundary.Length));
        upper[boundary.Length - 1]--;
        if (upper.Length > boundary.Length)
        {
            upper.AsSpan(boundary.Length).Fill(0xFF);
        }

        return true;
    }

    /// <summary>
    /// Creates an inclusive lower encoded-key bound for deleting keys strictly after one encoded boundary.<br/>
    /// Appending `0x00` covers all longer keys with the boundary as a prefix; max-length boundaries fall back to incrementing the nearest byte that has a successor.<br/>
    /// </summary>
    /// <param name="boundary">The encoded string boundary key.<br/></param>
    /// <param name="maxKeyLength">The routed var-key profile's maximum encoded key length.<br/></param>
    /// <param name="lower">Receives the inclusive lower bound when one exists.<br/></param>
    /// <returns><see langword="true"/> when a higher range can be represented.<br/></returns>
    private static bool TryCreateExclusiveLowerBoundAfter(byte[] boundary, int maxKeyLength, out byte[] lower)
    {
        lower = Array.Empty<byte>();
        if (boundary.Length == 0)
        {
            return false;
        }

        if (boundary.Length < maxKeyLength)
        {
            lower = GC.AllocateUninitializedArray<byte>(boundary.Length + 1);
            boundary.CopyTo(lower.AsSpan(0, boundary.Length));
            lower[^1] = 0;
            return true;
        }

        for (int i = boundary.Length - 1; i > 0; i--)
        {
            if (boundary[i] == 0xFF)
            {
                continue;
            }

            lower = GC.AllocateUninitializedArray<byte>(i + 1);
            boundary.AsSpan(0, i + 1).CopyTo(lower);
            lower[i]++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes materialized exact-only string tuples with one `VS8` writer context when all non-key-state rows are ordinary warmed shelves.<br/>
    /// Same-shelf ownership conflicts are retried internally; unsupported terminal, duplicate-run, or topology shapes fall back to the existing durability-batch path.<br/>
    /// </summary>
    /// <param name="tuples">The already materialized exact string tuples to delete.</param>
    /// <returns>The tuple-oriented mutation result for the delete operation.</returns>
    private LibraDexIdentityMutationResult DeleteExactOnlyIdentityPrimitive(List<StringScalar8Tuple> tuples)
    {
        while (true)
        {
            long changed = 0;
            LibraDexWriteContext writeContext = exact.Session.BeginVarKeyScalar8WriteContext();
            try
            {
                for (int i = 0; i < tuples.Count; i++)
                {
                    StringScalar8Tuple tuple = tuples[i];
                    if (TryClassifyStringKeyState(tuple.Key, out _))
                    {
                        continue;
                    }

                    byte[] exactKey = Encode(tuple.Key);
                    if (exact.Session.DeleteVarKeyScalar8ExactTupleForWriteContext(
                        writeContext,
                        exact.Handle.RootRouterOffset,
                        exact.Handle.MaxKeyLength,
                        exactKey,
                        tuple.Identity))
                    {
                        changed++;
                    }
                }

                if (changed == 0)
                {
                    exact.Session.AbortVarKeyScalar8WriteContext(writeContext);
                }
                else
                {
                    _ = exact.Session.PublishVarKeyScalar8WriteContext(writeContext);
                }

                for (int i = 0; i < tuples.Count; i++)
                {
                    StringScalar8Tuple tuple = tuples[i];
                    if (TryClassifyStringKeyState(tuple.Key, out NullKey keyState) &&
                        exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), tuple.Identity))
                    {
                        changed++;
                    }
                }

                return new LibraDexIdentityMutationResult(
                    LibraDexCriteriaMutationKind.Delete,
                    tuples.Count,
                    changed,
                    new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
            }
            catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException ex)
            {
                exact.Session.AbortVarKeyScalar8WriteContext(writeContext);
                exact.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
            {
                exact.Session.AbortVarKeyScalar8WriteContext(writeContext);
                exact.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                exact.Session.AbortVarKeyScalar8WriteContext(writeContext);
                return DeleteIdentityPrimitiveWithBatch(tuples);
            }
        }
    }

    /// <summary>
    /// Deletes materialized projection-owned string tuples without opening a shared `VS8` durability batch.<br/>
    /// Exact and projection rows are removed with immediate encoded exact-delete paths, letting each routed index use writer-context staging where its topology allows it.<br/>
    /// </summary>
    /// <param name="tuples">The already materialized exact string tuples to delete.</param>
    /// <returns>The tuple-oriented mutation result for the delete operation.</returns>
    private LibraDexIdentityMutationResult DeleteIdentityPrimitiveWithImmediateProjections(List<StringScalar8Tuple> tuples)
    {
        long changed = 0;
        for (int i = 0; i < tuples.Count; i++)
        {
            StringScalar8Tuple tuple = tuples[i];
            if (TryClassifyStringKeyState(tuple.Key, out NullKey keyState))
            {
                if (exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), tuple.Identity))
                {
                    changed++;
                }

                continue;
            }

            byte[] exactKey = Encode(tuple.Key);
            if (!exact.DeleteEncodedExactTuple(exactKey, tuple.Identity))
            {
                continue;
            }

            changed++;
            DeleteProjectionTuplesImmediate(tuple.Key, tuple.Identity);
        }

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            tuples.Count,
            changed,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: changed));
    }

    /// <summary>
    /// Deletes materialized string tuples through the existing coupled durability-batch path.<br/>
    /// This remains required for projection-maintained indexes because exact and projection tuple removals need one shared batch boundary.<br/>
    /// </summary>
    /// <param name="tuples">The already materialized exact string tuples to delete.</param>
    /// <returns>The tuple-oriented mutation result for the delete operation.</returns>
    private LibraDexIdentityMutationResult DeleteIdentityPrimitiveWithBatch(List<StringScalar8Tuple> tuples)
    {
        long changed = 0;
        using VarKeyScalar8Batch batch = exact.BeginBatch();
        for (int i = 0; i < tuples.Count; i++)
        {
            StringScalar8Tuple tuple = tuples[i];
            if (TryClassifyStringKeyState(tuple.Key, out NullKey keyState))
            {
                if (exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), tuple.Identity))
                {
                    changed++;
                }

                continue;
            }

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
    /// Gets whether this logical string index owns maintained projection indexes that must be mutated with the exact index.<br/>
    /// Exact-only string indexes can use narrower writer-context paths because there are no coupled projection rows to keep in the same batch boundary.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when at least one maintained projection is present.</returns>
    private bool HasMaintainedProjections()
    {
        return exactReversed is not null ||
            folded is not null ||
            sortKey is not null ||
            foldedReversed is not null;
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
        return IterateExactTuplePrimitive(request).ToArray();
    }

    /// <summary>
    /// Streams exact string key and scalar identity tuples matched by one normalized primitive request.<br/>
    /// This is the non-mutating tuple projection path used by condition cursors and diagnostic tuple reads; mutation paths can still materialize through <see cref="ExecuteTuplePrimitive"/> when they require stable captured tuples.<br/>
    /// The stream preserves the same exact-key semantics as previous tuple execution while avoiding the extra intermediate `StringScalar8Tuple` list for read-only callers.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>A forward-only stream of exact string key and scalar identity tuples.</returns>
    private IEnumerable<LibraDexObjectTuple> IterateExactTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        List<StringScalar8Tuple> tuples = MaterializeExactTuples(request);
        for (int i = 0; i < tuples.Count; i++)
        {
            yield return new LibraDexObjectTuple(tuples[i].Key, tuples[i].Identity);
        }
    }

    /// <summary>
    /// Reads key/identity tuples directly from one maintained string projection selected by a materialized condition leaf.<br/>
    /// Logical string conditions may resolve to folded-text, sort-key, or reversed folded projection indexes; this helper lets index-set tuple projection inspect that actual projection instead of correlating identities back through the exact string key index.<br/>
    /// The returned key is the projection key, not the exact developer-facing string key, which is intentional for diagnostics and workbench paths that need to measure the selected physical route.<br/>
    /// </summary>
    /// <param name="criterion">The materialized single-leaf criterion whose index may name one of this facade's maintained projections.</param>
    /// <param name="skip">The number of projection tuples to skip after the projection reader starts.</param>
    /// <param name="take">The optional maximum number of projection tuples to return.</param>
    /// <param name="direction">The requested projection tuple traversal direction.</param>
    /// <param name="rows">Receives materialized runtime tuples when a projection fast path applies.</param>
    /// <returns><see langword="true"/> when a maintained projection handled the tuple read.</returns>
    internal bool TryReadProjectionTuples(
        IIdentityCriterion criterion,
        int skip,
        int? take,
        QueryDirection direction,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<LibraDexRuntimeTuple>? rows)
    {
        rows = null;
        ThrowIfDisposed();
        if (criterion.NodeKind != LibraDexIdentityCriterionNodeKind.Leaf ||
            criterion.Index is not IIndex criterionIndex ||
            criterion.CriteriaKind is null ||
            !string.Equals(criterionIndex.Group, Group, StringComparison.Ordinal))
        {
            return false;
        }

        IEnumerable<LibraDexObjectTuple>? tuples = null;
        if (folded is not null && string.Equals(criterionIndex.Name, folded.Name, StringComparison.Ordinal))
        {
            tuples = folded.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, AddForTakeLimit(skip, take), direction));
        }
        else if (sortKey is not null && string.Equals(criterionIndex.Name, sortKey.Name, StringComparison.Ordinal))
        {
            tuples = sortKey.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, AddForTakeLimit(skip, take), direction));
        }
        else if (foldedReversed is not null && string.Equals(criterionIndex.Name, foldedReversed.Name, StringComparison.Ordinal))
        {
            tuples = foldedReversed.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, AddForTakeLimit(skip, take), direction));
        }
        else if (exactReversed is not null && string.Equals(criterionIndex.Name, exactReversed.Name, StringComparison.Ordinal))
        {
            tuples = exactReversed.IterateTuplePrimitive(new LibraDexIdentityPrimitiveRequest(criterion.CriteriaKind.Value, criterion.Values, AddForTakeLimit(skip, take), direction));
        }

        if (tuples is null)
        {
            return false;
        }

        rows = MaterializeRuntimeTuples(tuples, skip, take);
        return true;
    }

    /// <summary>
    /// Materializes runtime tuples from a projected tuple stream while applying final skip/take bounds.<br/>
    /// The primitive request already receives `skip + take` as an upstream limit when possible, so this method only performs the final offset trim and public tuple wrapper projection.<br/>
    /// </summary>
    /// <param name="tuples">The projection tuple stream.</param>
    /// <param name="skip">The number of tuples to skip.</param>
    /// <param name="take">The optional maximum number of tuples to return.</param>
    /// <returns>The materialized runtime tuple array.</returns>
    private static IReadOnlyList<LibraDexRuntimeTuple> MaterializeRuntimeTuples(IEnumerable<LibraDexObjectTuple> tuples, int skip, int? take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");
        }

        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be zero or greater.");
        }

        if (take == 0)
        {
            return Array.Empty<LibraDexRuntimeTuple>();
        }

        List<LibraDexRuntimeTuple> rows = new(take ?? 0);
        int skipped = 0;
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            rows.Add(new LibraDexRuntimeTuple(tuple.Key, tuple.Identity));
            if (take is int limit && rows.Count >= limit)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Adds skip and take into a primitive reader limit so direct projection tuple reads can stop before reading rows that the caller will discard.<br/>
    /// A null take means no upper limit; otherwise the primitive receives `skip + take` and the final skip is applied while materializing runtime tuples.<br/>
    /// </summary>
    /// <param name="skip">The requested skip count.</param>
    /// <param name="take">The requested take count, or null for all remaining rows.</param>
    /// <returns>The upstream primitive take limit, or null when no upper bound exists.</returns>
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

        return take is null ? null : checked(skip + take.Value);
    }

    /// <summary>
    /// Tests whether one exact string key and scalar identity tuple is visible in the exact projection.<br/>
    /// Replacement mutation uses this to distinguish an already-present replacement from an insert conflict before deleting the old tuple.<br/>
    /// </summary>
    /// <param name="key">The exact string key to test.</param>
    /// <param name="identity">The scalar identity to test.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    private bool ContainsExactTuple(string? key, ulong identity)
    {
        ThrowIfDisposed();
        if (TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return exact.Session.ContainsScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), identity);
        }

        byte[] encodedKey = Encode(key);
        using VarKeyScalar8RangeReader reader = exact.OpenEncodedRangeReader(encodedKey, encodedKey);
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
    /// Deletes one exact string key and scalar identity tuple while maintaining every owned projection tuple for non-empty text.<br/>
    /// Null and empty string keys are removed from their metadata-backed identity routes, while non-empty exact and projection rows are removed inside one `VS8` durability batch.<br/>
    /// </summary>
    /// <param name="key">The exact string key to delete, or null for the null-key sentinel.</param>
    /// <param name="identity">The scalar identity to delete.</param>
    /// <returns><see langword="true"/> when the exact tuple was removed.</returns>
    private bool DeleteExactTuple(string? key, ulong identity)
    {
        ThrowIfDisposed();
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), identity);
        }

        byte[] exactKey = Encode(key);
        if (exactReversed is null &&
            folded is null &&
            sortKey is null &&
            foldedReversed is null)
        {
            return exact.DeleteEncodedExactTuple(exactKey, identity);
        }

        if (!exact.DeleteEncodedExactTuple(exactKey, identity))
        {
            return false;
        }

        DeleteProjectionTuplesImmediate(key, identity);
        return true;
    }

    /// <summary>
    /// Deletes maintained projection tuples for one exact string key inside the caller's active durability scope.<br/>
    /// Projection deletes are exact tuple deletes so folded-text and sort-key collisions preserve neighboring logical rows that share the projected key bytes.<br/>
    /// </summary>
    /// <param name="key">The original exact string key, or null for the null-key sentinel.</param>
    /// <param name="identity">The scalar identity paired with the key.</param>
    private void DeleteProjectionTuplesInCurrentScope(string? key, ulong identity)
    {
        exactReversed?.DeleteProjectedExactTuple(Reverse(key), identity);
        folded?.DeleteProjectedExactTuple(key, identity);
        sortKey?.DeleteProjectedExactTuple(CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.DeleteProjectedExactTuple(Reverse(Fold(key, foldedCulture)), identity);
    }

    /// <summary>
    /// Deletes maintained projection tuples for one exact string key through immediate encoded exact-delete paths.<br/>
    /// This is used by no-batch projection-owned mutation so projection routes can use writer-context staging instead of requiring a shared exact-index batch.<br/>
    /// </summary>
    /// <param name="key">The original exact string key, or null for the null-key sentinel.</param>
    /// <param name="identity">The scalar identity paired with the key.</param>
    private void DeleteProjectionTuplesImmediate(string? key, ulong identity)
    {
        exactReversed?.DeleteProjectedExactTupleImmediate(Reverse(key), identity);
        folded?.DeleteProjectedExactTupleImmediate(key, identity);
        sortKey?.DeleteProjectedExactTupleImmediate(CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.DeleteProjectedExactTupleImmediate(Reverse(Fold(key, foldedCulture)), identity);
    }

    /// <summary>
    /// Gets whether the supplied string key is stored through a key-state route rather than the ordinary `VS8` exact index.<br/>
    /// Concurrent batches use this to keep null and empty keys on their compact immediate route while batching ordinary string keys.<br/>
    /// </summary>
    /// <param name="key">The string key to classify.<br/></param>
    /// <returns><see langword="true"/> when the key is null or empty.</returns>
    internal static bool IsKeyStateForConcurrentBatch(string? key)
    {
        return TryClassifyStringKeyState(key, out _);
    }

    /// <summary>
    /// Inserts one key-state string identity through the immediate compact route for a concurrent batch.<br/>
    /// Key-state rows are not `VS8` shelf rows, so they cannot participate in the shared var-key writer context.<br/>
    /// </summary>
    /// <param name="key">The null or empty key.<br/></param>
    /// <param name="identity">The scalar identity to insert.<br/></param>
    /// <returns>The insert result.</returns>
    internal LibraDexGenericInsertResult InsertKeyStateForConcurrentBatch(string? key, ulong identity)
    {
        if (!TryClassifyStringKeyState(key, out NullKey keyState))
        {
            throw new ArgumentException("String concurrent key-state insertion requires a null or empty key.", nameof(key));
        }

        return InsertStringKeyStateIdentity(keyState, identity);
    }

    /// <summary>
    /// Deletes one key-state string identity through the immediate compact route for a concurrent batch.<br/>
    /// Key-state rows are not `VS8` shelf rows, so they cannot participate in the shared var-key writer context.<br/>
    /// </summary>
    /// <param name="key">The null or empty key.<br/></param>
    /// <param name="identity">The scalar identity to delete.<br/></param>
    /// <returns><see langword="true"/> when the identity was removed.</returns>
    internal bool DeleteKeyStateForConcurrentBatch(string? key, ulong identity)
    {
        if (!TryClassifyStringKeyState(key, out NullKey keyState))
        {
            return false;
        }

        return exact.Session.DeleteScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), identity);
    }

    /// <summary>
    /// Tests one exact string tuple for concurrent batch rekey semantics.<br/>
    /// The batch inserts the replacement only after this check passes so a missing old key cannot create a new tuple.<br/>
    /// </summary>
    /// <param name="key">The exact string key to test.<br/></param>
    /// <param name="identity">The scalar identity to test.<br/></param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    internal bool ContainsExactTupleForConcurrentBatch(string? key, ulong identity)
    {
        return ContainsExactTuple(key, identity);
    }

    /// <summary>
    /// Stages one exact string tuple and every maintained projection tuple in a shared `VS8` writer context.<br/>
    /// The caller owns publication; this method only mutates writer-local shelf images when all touched routes remain shelf-local.<br/>
    /// </summary>
    /// <param name="writeContext">The shared `VS8` writer context.<br/></param>
    /// <param name="key">The exact string key to insert.<br/></param>
    /// <param name="identity">The scalar identity to insert.<br/></param>
    /// <returns>The exact-index insert result.</returns>
    internal LibraDexGenericInsertResult InsertForConcurrentBatch(
        LibraDexWriteContext writeContext,
        string? key,
        ulong identity)
    {
        byte[] exactKey = Encode(key);
        VarKeyScalar8InsertOutcome exactResult = exact.InsertEncodedForConcurrentBatch(writeContext, exactKey, identity);
        if (exactResult.Inserted)
        {
            InsertProjectionTuplesForConcurrentBatch(writeContext, key, identity);
        }

        return new LibraDexGenericInsertResult(
            exactResult.Inserted,
            false,
            default,
            default)
        {
            QueuedInsertPath = exactResult.Inserted
                ? Scalar8Scalar8QueuedInsertPath.WriterContext
                : Scalar8Scalar8QueuedInsertPath.None
        };
    }

    /// <summary>
    /// Stages one exact string tuple delete and every maintained projection tuple delete in a shared `VS8` writer context.<br/>
    /// The caller owns publication; this method only mutates writer-local shelf images when all touched routes remain shelf-local.<br/>
    /// </summary>
    /// <param name="writeContext">The shared `VS8` writer context.<br/></param>
    /// <param name="key">The exact string key to delete.<br/></param>
    /// <param name="identity">The scalar identity to delete.<br/></param>
    /// <returns>The exact-index delete result.</returns>
    internal LibraDexGenericDeleteResult DeleteForConcurrentBatch(
        LibraDexWriteContext writeContext,
        string? key,
        ulong identity)
    {
        byte[] exactKey = Encode(key);
        bool deleted = exact.DeleteEncodedExactTupleForConcurrentBatch(writeContext, exactKey, identity);
        if (deleted)
        {
            DeleteProjectionTuplesForConcurrentBatch(writeContext, key, identity);
        }

        return new LibraDexGenericDeleteResult(
            deleted,
            default)
        {
            QueuedInsertPath = deleted
                ? Scalar8Scalar8QueuedInsertPath.WriterContext
                : Scalar8Scalar8QueuedInsertPath.None
        };
    }

    /// <summary>
    /// Inserts maintained string projection tuples through the immediate paths after a concurrent batch had to publish staged exact work early.<br/>
    /// The method is idempotent for already-present projection tuples and repairs only the companion rows for the supplied logical key/identity.<br/>
    /// </summary>
    /// <param name="key">The exact string key.<br/></param>
    /// <param name="identity">The scalar identity.</param>
    internal void InsertProjectionTuplesFallbackForConcurrentBatch(string? key, ulong identity)
    {
        InsertProjectionTuplesImmediate(key, identity);
    }

    /// <summary>
    /// Deletes maintained string projection tuples through the immediate paths after a concurrent batch had to publish staged exact work early.<br/>
    /// The method removes only exact projection tuples for the supplied logical key/identity.<br/>
    /// </summary>
    /// <param name="key">The exact string key.<br/></param>
    /// <param name="identity">The scalar identity.</param>
    internal void DeleteProjectionTuplesFallbackForConcurrentBatch(string? key, ulong identity)
    {
        DeleteProjectionTuplesImmediate(key, identity);
    }

    /// <summary>
    /// Creates a caller-owned `VS8` writer context for string concurrent batch work.<br/>
    /// The context can stage exact, folded, sort-key, and reversed projection shelves together because ownership is physical-shelf based.<br/>
    /// </summary>
    /// <returns>A new `VS8` writer context.</returns>
    internal LibraDexWriteContext BeginConcurrentBatchContext()
    {
        return exact.BeginConcurrentBatchContext();
    }

    /// <summary>
    /// Publishes a caller-owned string concurrent batch writer context.<br/>
    /// Publication is serialized at the DataKernel boundary after exact and projection shelves have been staged outside that boundary.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to publish.<br/></param>
    /// <returns>The publication telemetry.</returns>
    internal DataKernelCommitTelemetry PublishConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        return exact.PublishConcurrentBatchContext(writeContext);
    }

    /// <summary>
    /// Aborts a caller-owned string concurrent batch writer context.<br/>
    /// Staged exact and projection shelf images are discarded without writing to DataKernel.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to abort.<br/></param>
    internal void AbortConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        exact.AbortConcurrentBatchContext(writeContext);
    }

    private void InsertProjectionTuplesImmediate(string? key, ulong identity)
    {
        exactReversed?.InsertProjected(Reverse(key), identity);
        folded?.InsertProjected(key, identity);
        sortKey?.InsertProjected(CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.InsertProjected(Reverse(Fold(key, foldedCulture)), identity);
    }

    private void InsertProjectionTuplesForConcurrentBatch(
        LibraDexWriteContext writeContext,
        string? key,
        ulong identity)
    {
        exactReversed?.InsertProjectedForConcurrentBatch(writeContext, Reverse(key), identity);
        folded?.InsertProjectedForConcurrentBatch(writeContext, key, identity);
        sortKey?.InsertProjectedForConcurrentBatch(writeContext, CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.InsertProjectedForConcurrentBatch(writeContext, Reverse(Fold(key, foldedCulture)), identity);
    }

    private void DeleteProjectionTuplesForConcurrentBatch(
        LibraDexWriteContext writeContext,
        string? key,
        ulong identity)
    {
        exactReversed?.DeleteProjectedExactTupleForConcurrentBatch(writeContext, Reverse(key), identity);
        folded?.DeleteProjectedExactTupleForConcurrentBatch(writeContext, key, identity);
        sortKey?.DeleteProjectedExactTupleForConcurrentBatch(writeContext, CreateSortKey(key, sortKeyCulture), identity);
        foldedReversed?.DeleteProjectedExactTupleForConcurrentBatch(writeContext, Reverse(Fold(key, foldedCulture)), identity);
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
        int? innerTakeLimit = request.Direction == QueryDirection.Descending ? null : request.TakeLimit;
        List<StringScalar8Tuple> tuples = request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => MaterializeExactAllTuples(innerTakeLimit),
            LibraDexCriteriaKind.KeyState => MaterializeExactKeyStateTuples(RequireNullKeyState(request.Values), innerTakeLimit),
            LibraDexCriteriaKind.Find => MaterializeExactTuplesInRange(
                Encode(RequireString(request.Values, 0)),
                Encode(RequireString(request.Values, 0)),
                keyFilter: null,
                textFilter: null,
                innerTakeLimit),
            LibraDexCriteriaKind.Between => MaterializeExactTuplesInRange(
                Encode(RequireString(request.Values, 0)),
                Encode(RequireString(request.Values, 1)),
                keyFilter: null,
                textFilter: null,
                innerTakeLimit),
            LibraDexCriteriaKind.Prefix when (RequireString(request.Values, 0) ?? throw new InvalidOperationException("String prefix materialization requires a non-null prefix operand.")).Length == 0 => MaterializeExactOrderedKeyStateTuples(NullKey.Empty, before: false, inclusive: true, innerTakeLimit),
            LibraDexCriteriaKind.Prefix => MaterializeExactTuplesInRange(
                Encode(RequireString(request.Values, 0) ?? throw new InvalidOperationException("String prefix materialization requires a non-null prefix operand.")),
                Encode((RequireString(request.Values, 0) ?? throw new InvalidOperationException("String prefix materialization requires a non-null prefix operand.")) + '\uffff'),
                keyFilter: null,
                textFilter: null,
                innerTakeLimit),
            LibraDexCriteriaKind.Before when TryClassifyStringKeyState(RequireString(request.Values, 0), out NullKey beforeState) => MaterializeExactOrderedKeyStateTuples(beforeState, before: true, inclusive: false, innerTakeLimit),
            LibraDexCriteriaKind.Before => MaterializeExactTuplesBefore(Encode(RequireString(request.Values, 0)), inclusive: false, innerTakeLimit),
            LibraDexCriteriaKind.AtOrBefore when TryClassifyStringKeyState(RequireString(request.Values, 0), out NullKey atOrBeforeState) => MaterializeExactOrderedKeyStateTuples(atOrBeforeState, before: true, inclusive: true, innerTakeLimit),
            LibraDexCriteriaKind.AtOrBefore => MaterializeExactTuplesBefore(Encode(RequireString(request.Values, 0)), inclusive: true, innerTakeLimit),
            LibraDexCriteriaKind.After when TryClassifyStringKeyState(RequireString(request.Values, 0), out NullKey afterState) => MaterializeExactOrderedKeyStateTuples(afterState, before: false, inclusive: false, innerTakeLimit),
            LibraDexCriteriaKind.After => MaterializeExactTuplesAfter(Encode(RequireString(request.Values, 0)), inclusive: false, innerTakeLimit),
            LibraDexCriteriaKind.AtOrAfter when TryClassifyStringKeyState(RequireString(request.Values, 0), out NullKey atOrAfterState) => MaterializeExactOrderedKeyStateTuples(atOrAfterState, before: false, inclusive: true, innerTakeLimit),
            LibraDexCriteriaKind.AtOrAfter => MaterializeExactTuplesAfter(Encode(RequireString(request.Values, 0)), inclusive: true, innerTakeLimit),
            LibraDexCriteriaKind.InSet => MaterializeExactMembershipTuples(request.Values, innerTakeLimit),
            LibraDexCriteriaKind.StringPattern => MaterializeExactStringPatternTuples(RequireStringPatternPredicate(request.Values), innerTakeLimit),
            _ => throw new NotSupportedException($"{request.CriteriaKind} string identity deletion is not connected to the string scalar facade yet.")
        };

        if (request.Direction == QueryDirection.Descending)
        {
            tuples.Reverse();
            if (request.TakeLimit is int limit && tuples.Count > limit)
            {
                tuples.RemoveRange(limit, tuples.Count - limit);
            }
        }
        else if (request.Direction != QueryDirection.Ascending)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Direction, "Unknown query direction.");
        }

        return tuples;
    }

    /// <summary>
    /// Captures every exact string tuple in key-state order: null route, empty route, then ordinary non-empty string router.<br/>
    /// The route-backed tuples are materialized before normal var-key tuples so condition mutations observe the same index-natural order as identity reads.<br/>
    /// </summary>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the all-scan request.</returns>
    private List<StringScalar8Tuple> MaterializeExactAllTuples(int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        tuples.AddRange(MaterializeExactKeyStateTuples(NullKey.Null, RemainingTake(takeLimit, tuples.Count)));
        if (takeLimit is int limit && tuples.Count >= limit)
        {
            return tuples;
        }

        tuples.AddRange(MaterializeExactKeyStateTuples(NullKey.Empty, RemainingTake(takeLimit, tuples.Count)));
        if (takeLimit is int limitAfterEmpty && tuples.Count >= limitAfterEmpty)
        {
            return tuples;
        }

        tuples.AddRange(MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(exact.Handle.MaxKeyLength), keyFilter: null, textFilter: null, RemainingTake(takeLimit, tuples.Count)));
        return tuples;
    }

    /// <summary>
    /// Captures exact string tuples from metadata-backed null and empty key-state routes.<br/>
    /// `NullKey.NullOrEmpty` captures null first and then empty, matching all-scan route ordering.<br/>
    /// </summary>
    /// <param name="keyState">The key-state route selector.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the key-state request.</returns>
    private List<StringScalar8Tuple> MaterializeExactKeyStateTuples(NullKey keyState, int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        if (keyState is NullKey.Null or NullKey.NullOrEmpty)
        {
            foreach (ulong identity in ReadStringKeyStateIdentities(NullKey.Null, takeLimit))
            {
                tuples.Add(new StringScalar8Tuple(null, identity));
            }
        }

        if (takeLimit is int limit && tuples.Count >= limit)
        {
            return tuples;
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            foreach (ulong identity in ReadStringKeyStateIdentities(NullKey.Empty, RemainingTake(takeLimit, tuples.Count)))
            {
                tuples.Add(new StringScalar8Tuple(string.Empty, identity));
            }
        }

        return tuples;
    }

    private List<StringScalar8Tuple> MaterializeExactOrderedKeyStateTuples(NullKey boundary, bool before, bool inclusive, int? takeLimit)
    {
        if (boundary == NullKey.Null)
        {
            if (before && inclusive)
            {
                return MaterializeExactKeyStateTuples(NullKey.Null, takeLimit);
            }

            return before
                ? new List<StringScalar8Tuple>()
                : inclusive
                    ? MaterializeExactAllTuples(takeLimit)
                    : MaterializeExactOrderedKeyStateTuples(NullKey.Empty, before: false, inclusive: true, takeLimit);
        }

        if (before)
        {
            List<StringScalar8Tuple> tuples = MaterializeExactKeyStateTuples(NullKey.Null, takeLimit);
            if (inclusive)
            {
                tuples.AddRange(MaterializeExactKeyStateTuples(NullKey.Empty, RemainingTake(takeLimit, tuples.Count)));
            }

            return tuples;
        }

        if (inclusive)
        {
            List<StringScalar8Tuple> tuples = MaterializeExactKeyStateTuples(NullKey.Empty, takeLimit);
            tuples.AddRange(MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(exact.Handle.MaxKeyLength), keyFilter: null, textFilter: null, RemainingTake(takeLimit, tuples.Count)));
            return tuples;
        }

        return MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(exact.Handle.MaxKeyLength), keyFilter: null, textFilter: null, takeLimit);
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
        => MaterializeExactTuplesInRange(lower, upper, keyFilter, textFilter, byteMatcher: null, takeLimit);

    /// <summary>
    /// Captures exact string key and identity tuples from one encoded-key range with an optional UTF-8 byte residual predicate.<br/>
    /// The byte matcher runs before string decoding so scan-backed simple string predicates can reject non-matching rows without allocating decoded strings.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower encoded key.</param>
    /// <param name="upper">The inclusive upper encoded key.</param>
    /// <param name="keyFilter">Optional encoded-key filter.</param>
    /// <param name="textFilter">Optional decoded-string fallback filter.</param>
    /// <param name="byteMatcher">Optional byte-native residual matcher for the key payload.</param>
    /// <param name="takeLimit">Optional tuple limit.</param>
    /// <returns>The exact string tuples matched by the range and filters.</returns>
    private List<StringScalar8Tuple> MaterializeExactTuplesInRange(
        byte[] lower,
        byte[] upper,
        Func<byte[], bool>? keyFilter,
        Func<string, bool>? textFilter,
        LibraDexUtf8StringPatternPredicate? byteMatcher,
        int? takeLimit)
    {
        List<StringScalar8Tuple> tuples = new();
        using VarKeyScalar8RangeReader reader = exact.OpenEncodedRangeReader(lower, upper);
        while (reader.MoveNext())
        {
            ReadOnlySpan<byte> currentKey = reader.CurrentKey;
            if (keyFilter is not null)
            {
                byte[] keyBytes = reader.MaterializeCurrentKey();
                if (!keyFilter(keyBytes))
                {
                    continue;
                }
            }

            if (byteMatcher is not null && !TryMatchEncodedStringPayload(currentKey, byteMatcher))
            {
                continue;
            }

            string? key = Decode(currentKey);
            if (byteMatcher is null && textFilter is not null && (key is null || !textFilter(key)))
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
            FullUpperBound(exact.Handle.MaxKeyLength),
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

            foreach (object? item in objects)
            {
                string? key = item as string;
                if (item is not null && key is null)
                {
                    throw new InvalidOperationException("String membership primitive requires string values.");
                }

                if (TryClassifyStringKeyState(key, out NullKey keyState))
                {
                    tuples.AddRange(MaterializeExactKeyStateTuples(keyState, RemainingTake(takeLimit, tuples.Count)));
                }
                else
                {
                    tuples.AddRange(MaterializeExactTuplesInRange(Encode(key), Encode(key), keyFilter: null, textFilter: null, RemainingTake(takeLimit, tuples.Count)));
                }

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
        if (predicate.Matches(string.Empty))
        {
            tuples.AddRange(MaterializeExactKeyStateTuples(NullKey.Empty, takeLimit));
            if (takeLimit is int limit && tuples.Count >= limit)
            {
                return tuples;
            }
        }

        IReadOnlyList<(string Lower, string Upper)> candidateRanges = predicate.CreateCandidateRanges();
        LibraDexUtf8StringPatternPredicate? byteMatcher = predicate.TryCreateUtf8ByteMatcher(IdentityTransform, allowCaseNormalizedBytes: false, out LibraDexUtf8StringPatternPredicate? matcher)
            ? matcher
            : null;
        if (candidateRanges.Count == 0)
        {
            tuples.AddRange(MaterializeExactTuplesInRange(FullLowerBound(), FullUpperBound(exact.Handle.MaxKeyLength), keyFilter: null, textFilter: predicate.Matches, byteMatcher: byteMatcher, takeLimit: RemainingTake(takeLimit, tuples.Count)));
            return tuples;
        }

        foreach ((string lower, string upper) in candidateRanges)
        {
            tuples.AddRange(MaterializeExactTuplesInRange(Encode(lower), Encode(upper), keyFilter: null, textFilter: predicate.Matches, byteMatcher: byteMatcher, takeLimit: RemainingTake(takeLimit, tuples.Count)));
            if (takeLimit is int limit && tuples.Count >= limit)
            {
                break;
            }
        }

        return tuples;
    }

    /// <summary>
    /// Rejects ordinary string-facade mutation while any explicit session durability batch is active.<br/>
    /// Non-empty `VS8` writes already enter their own batch, but null and empty key-state writes can mutate metadata-backed routes directly, so the facade must block unrelated no-batch callers before they touch the shared session overlay.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the owning session already has an active durability batch.<br/></exception>
    private void ThrowIfSessionDurabilityBatchActiveForImmediateMutation()
    {
        if (exact.Session.IsDurabilityBatchActive)
        {
            throw new InvalidOperationException("Immediate LibraDex string mutation cannot run while another session durability batch is active; publish, abort, or disable the active batch before issuing unrelated no-batch writes.");
        }
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
        List<LibraDexIndexProjectionSpec> projections = new()
        {
            new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending)
        };

        if (folded is not null)
        {
            projections.Add(new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.FoldedText, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending));
        }

        if (sortKey is not null)
        {
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
            ResolveStringKeys(),
            GuidKeys.Exact,
            DateKeys.Exact,
            DateTimeKeyEncoding.CalendarSdt,
            directions,
            LibraDexIndexSortOrder.Ascending,
            projections.ToArray(),
            Array.Empty<LibraDexCompositeKeyPartSpec>());
    }

    private StringKeys ResolveStringKeys()
    {
        if (folded is not null)
        {
            return sortKey is not null
                ? StringKeys.ExactFoldedAndSortKey
                : StringKeys.ExactAndFolded;
        }

        return sortKey is not null
            ? StringKeys.ExactAndSortKey
            : StringKeys.Exact;
    }

    private static IEnumerable<object> IterateIdentityPrimitiveCore(
        LibraDexIdentityPrimitiveRequest request,
        VarKeyScalar8Index index,
        Func<string?, string?> transform)
    {
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                return IterateAll(index);
            case LibraDexCriteriaKind.KeyState:
                return IterateKeyState(index, RequireNullKeyState(request.Values), request.TakeLimit);
            case LibraDexCriteriaKind.Find:
                string? key = transform(RequireString(request.Values, 0));
                if (TryClassifyStringKeyState(key, out NullKey keyState))
                {
                    return IterateKeyState(index, keyState, request.TakeLimit);
                }

                return IterateRange(index, key, key, null);
            case LibraDexCriteriaKind.Between:
                return IterateRange(index, transform(RequireString(request.Values, 0)), transform(RequireString(request.Values, 1)), request.TakeLimit);
            case LibraDexCriteriaKind.Prefix:
            {
                string? prefix = transform(RequireString(request.Values, 0));
                return prefix is { Length: 0 }
                    ? IterateOrderedKeyState(index, NullKey.Empty, before: false, inclusive: true, request.TakeLimit)
                    : IterateRange(index, prefix, prefix + '\uffff', request.TakeLimit);
            }
            case LibraDexCriteriaKind.Before:
                return TryCreateOrderedKeyStateIterator(index, transform(RequireString(request.Values, 0)), before: true, inclusive: false, request.TakeLimit, out IEnumerable<object>? beforeKeyState)
                    ? beforeKeyState!
                    : IterateBefore(index, Encode(transform(RequireString(request.Values, 0))), inclusive: false, request.TakeLimit);
            case LibraDexCriteriaKind.AtOrBefore:
                return TryCreateOrderedKeyStateIterator(index, transform(RequireString(request.Values, 0)), before: true, inclusive: true, request.TakeLimit, out IEnumerable<object>? atOrBeforeKeyState)
                    ? atOrBeforeKeyState!
                    : IterateBefore(index, Encode(transform(RequireString(request.Values, 0))), inclusive: true, request.TakeLimit);
            case LibraDexCriteriaKind.After:
                return TryCreateOrderedKeyStateIterator(index, transform(RequireString(request.Values, 0)), before: false, inclusive: false, request.TakeLimit, out IEnumerable<object>? afterKeyState)
                    ? afterKeyState!
                    : IterateAfter(index, Encode(transform(RequireString(request.Values, 0))), inclusive: false, request.TakeLimit);
            case LibraDexCriteriaKind.AtOrAfter:
                return TryCreateOrderedKeyStateIterator(index, transform(RequireString(request.Values, 0)), before: false, inclusive: true, request.TakeLimit, out IEnumerable<object>? atOrAfterKeyState)
                    ? atOrAfterKeyState!
                    : IterateAfter(index, Encode(transform(RequireString(request.Values, 0))), inclusive: true, request.TakeLimit);
            case LibraDexCriteriaKind.InSet:
                return IterateMembership(index, request.Values, transform, request.TakeLimit);
            case LibraDexCriteriaKind.StringPattern:
                return IterateStringPattern(index, RequireStringPatternPredicate(request.Values), request.TakeLimit);
            default:
                throw new NotSupportedException($"{request.CriteriaKind} is not connected to the string scalar facade yet.");
        }
    }

    /// <summary>
    /// Counts identities for one normalized string primitive without yielding boxed scalar identities.<br/>
    /// Exact key, ordered range, and membership branches use key-state route counts or routed `VS8` reader counts; residual string-pattern branches scan only encoded keys needed for predicate correctness.<br/>
    /// </summary>
    /// <param name="request">The primitive request produced by the condition materializer.<br/></param>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="transform">The projection transform to apply to string operands before encoding.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountIdentityPrimitiveCore(
        LibraDexIdentityPrimitiveRequest request,
        VarKeyScalar8Index index,
        Func<string?, string?> transform)
    {
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                return CountAll(index);
            case LibraDexCriteriaKind.KeyState:
                return CountKeyState(index, RequireNullKeyState(request.Values));
            case LibraDexCriteriaKind.Find:
            {
                string? key = transform(RequireString(request.Values, 0));
                return TryClassifyStringKeyState(key, out NullKey keyState)
                    ? CountKeyState(index, keyState)
                    : CountRange(index, key, key);
            }
            case LibraDexCriteriaKind.Between:
                return CountRange(index, transform(RequireString(request.Values, 0)), transform(RequireString(request.Values, 1)));
            case LibraDexCriteriaKind.Prefix:
                return CountPrefix(index, transform(RequireString(request.Values, 0)));
            case LibraDexCriteriaKind.Before:
                return CountBefore(index, transform(RequireString(request.Values, 0)), inclusive: false);
            case LibraDexCriteriaKind.AtOrBefore:
                return CountBefore(index, transform(RequireString(request.Values, 0)), inclusive: true);
            case LibraDexCriteriaKind.After:
                return CountAfter(index, transform(RequireString(request.Values, 0)), inclusive: false);
            case LibraDexCriteriaKind.AtOrAfter:
                return CountAfter(index, transform(RequireString(request.Values, 0)), inclusive: true);
            case LibraDexCriteriaKind.InSet:
                return CountMembership(index, request.Values, transform);
            case LibraDexCriteriaKind.StringPattern:
                return CountStringPattern(index, RequireStringPatternPredicate(request.Values));
            default:
                throw new NotSupportedException($"{request.CriteriaKind} is not connected to the string scalar facade yet.");
        }
    }

    /// <summary>
    /// Classifies the physical plan used by a string aggregate primitive.<br/>
    /// Key-state counts are metadata-backed, ordinary exact/range/membership counts are routed slot counts, and string patterns may scan candidate keys for residual predicate checks.<br/>
    /// </summary>
    /// <param name="request">The primitive request being aggregated.<br/></param>
    /// <returns>The conservative physical plan classification.</returns>
    private static LibraDexPrimitiveAggregatePlanKind ClassifyStringAggregatePlan(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.KeyState => LibraDexPrimitiveAggregatePlanKind.Metadata,
            LibraDexCriteriaKind.Before or
            LibraDexCriteriaKind.After or
            LibraDexCriteriaKind.StringPattern => LibraDexPrimitiveAggregatePlanKind.KeyScan,
            _ => LibraDexPrimitiveAggregatePlanKind.RangeSlots
        };
    }

    /// <summary>
    /// Counts all scalar identities in the string facade's physical key space.<br/>
    /// Null and empty key-state routes are counted from route-root metadata, and ordinary string keys use the routed `VS8` reader count.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <returns>The total physical identity count.</returns>
    private static long CountAll(VarKeyScalar8Index index)
    {
        return CountKeyState(index, NullKey.NullOrEmpty) +
            index.CountOrdinaryIdentities();
    }

    /// <summary>
    /// Counts one or both string key-state routes from existing route-root metadata.<br/>
    /// This path copies no identities and does not maintain any additional counter beyond the route page's existing item count.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index that owns the key-state routes.<br/></param>
    /// <param name="keyState">The key-state route selector.</param>
    /// <returns>The selected key-state identity count.</returns>
    private static long CountKeyState(VarKeyScalar8Index index, NullKey keyState)
    {
        long count = 0;
        if (keyState is NullKey.Null or NullKey.NullOrEmpty)
        {
            count += index.Session.CountScalar8KeyStateIdentities(index.SlotIndex, KeyStateRoute.Null);
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            count += index.Session.CountScalar8KeyStateIdentities(index.SlotIndex, KeyStateRoute.Empty);
        }

        return count;
    }

    /// <summary>
    /// Counts identities in an inclusive logical string-key range.<br/>
    /// The logical bounds are encoded once, then the routed `VS8` reader supplies the shelf-local row count without yielding identities.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="lower">The inclusive lower logical string key.<br/></param>
    /// <param name="upper">The inclusive upper logical string key.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountRange(VarKeyScalar8Index index, string? lower, string? upper)
    {
        return CountEncodedRange(index, Encode(lower), Encode(upper), keyFilter: null);
    }

    /// <summary>
    /// Counts identities whose logical string key starts with <paramref name="prefix"/> while preserving prefix intent for the `VS8` physical shape.<br/>
    /// Empty prefixes match the empty key-state route plus every ordinary string key; non-empty prefixes route through the shape-native encoded-prefix count planner.<br/>
    /// Null is not a valid starts-with operand and is rejected by condition materialization before this helper is reached.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="prefix">The logical string prefix to match.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountPrefix(VarKeyScalar8Index index, string? prefix)
    {
        if (prefix is null)
        {
            throw new InvalidOperationException("String prefix count requires a non-null prefix operand.");
        }

        if (prefix.Length == 0)
        {
            return CountKeyState(index, NullKey.Empty) + index.CountOrdinaryIdentities();
        }

        return index.CountEncodedIdentityPrefix(Encode(prefix));
    }

    /// <summary>
    /// Counts identities whose logical string key sorts before a boundary.<br/>
    /// Null and empty boundaries are answered from key-state route metadata; ordinary boundaries combine key-state metadata with an encoded-key range count.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="boundary">The logical boundary string key.<br/></param>
    /// <param name="inclusive">True to include identities whose key equals <paramref name="boundary"/>.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountBefore(VarKeyScalar8Index index, string? boundary, bool inclusive)
    {
        if (TryClassifyStringKeyState(boundary, out NullKey keyState))
        {
            if (keyState == NullKey.Null)
            {
                return inclusive ? CountKeyState(index, NullKey.Null) : 0;
            }

            long count = CountKeyState(index, NullKey.Null);
            if (inclusive)
            {
                count += CountKeyState(index, NullKey.Empty);
            }

            return count;
        }

        byte[] encodedBoundary = Encode(boundary);
        return CountKeyState(index, NullKey.NullOrEmpty) +
            CountEncodedRange(
            index,
            FullLowerBound(),
            encodedBoundary,
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(encodedBoundary) < 0);
    }

    /// <summary>
    /// Counts identities whose logical string key sorts after a boundary.<br/>
    /// Null and empty boundaries are answered from key-state route metadata plus ordinary routed range counts; ordinary boundaries use encoded-key range counting.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="boundary">The logical boundary string key.<br/></param>
    /// <param name="inclusive">True to include identities whose key equals <paramref name="boundary"/>.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountAfter(VarKeyScalar8Index index, string? boundary, bool inclusive)
    {
        if (TryClassifyStringKeyState(boundary, out NullKey keyState))
        {
            if (keyState == NullKey.Null)
            {
                long count = inclusive ? CountKeyState(index, NullKey.Null) : 0;
                count += CountKeyState(index, NullKey.Empty);
                return count + CountEncodedRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), keyFilter: null);
            }

            long ordinaryCount = CountEncodedRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), keyFilter: null);
            return inclusive ? CountKeyState(index, NullKey.Empty) + ordinaryCount : ordinaryCount;
        }

        byte[] encodedBoundary = Encode(boundary);
        return CountEncodedRange(
            index,
            encodedBoundary,
            FullUpperBound(index.Handle.MaxKeyLength),
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(encodedBoundary) > 0);
    }

    /// <summary>
    /// Counts string membership criteria by summing one physical exact-key count per distinct transformed operand.<br/>
    /// This count path treats membership operands as set membership, so repeated caller values do not count the same physical key-state route or ordinary string key more than once.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="values">The primitive request values containing one or more string-key enumerables.<br/></param>
    /// <param name="transform">The projection transform to apply to each string operand before encoding.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountMembership(
        VarKeyScalar8Index index,
        IReadOnlyList<object?> values,
        Func<string?, string?> transform)
    {
        long count = 0;
        HashSet<NullKey> seenKeyStates = new();
        HashSet<string> seenOrdinaryKeys = new(StringComparer.Ordinal);
        foreach (object? value in values)
        {
            if (value is not IEnumerable<object> objects)
            {
                throw new InvalidOperationException("String membership primitive requires an object enumerable operand.");
            }

            foreach (object? item in objects)
            {
                string? key = item as string;
                if (item is not null && key is null)
                {
                    throw new InvalidOperationException("String membership primitive requires string values.");
                }

                key = transform(key);
                if (TryClassifyStringKeyState(key, out NullKey keyState))
                {
                    if (seenKeyStates.Add(keyState))
                    {
                        count += CountKeyState(index, keyState);
                    }

                    continue;
                }

                if (seenOrdinaryKeys.Add(key!))
                {
                    count += CountRange(index, key, key);
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Counts identities matched by a condition-derived string predicate.<br/>
    /// Candidate ranges prune traversal through the routed `VS8` reader; residual predicate checks inspect encoded keys only when the predicate cannot be represented as a pure range count.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="predicate">The compiled string predicate to count.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountStringPattern(VarKeyScalar8Index index, LibraDexStringPatternPredicate predicate)
    {
        IReadOnlyList<(string Lower, string Upper)> candidateRanges = predicate.CreateCandidateRanges();
        LibraDexUtf8StringPatternPredicate? byteMatcher = predicate.TryCreateUtf8ByteMatcher(IdentityTransform, allowCaseNormalizedBytes: false, out LibraDexUtf8StringPatternPredicate? matcher)
            ? matcher
            : null;
        long count = predicate.Matches(string.Empty) ? CountKeyState(index, NullKey.Empty) : 0;
        if (candidateRanges.Count == 0)
        {
            return count + CountStringPatternRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), predicate, byteMatcher);
        }

        foreach ((string lower, string upper) in candidateRanges)
        {
            count += CountStringPatternRange(index, Encode(lower), Encode(upper), predicate, byteMatcher);
        }

        return count;
    }

    /// <summary>
    /// Counts identities in one encoded-key candidate range after applying a string-pattern residual predicate.<br/>
    /// When a UTF-8 byte matcher is available this avoids string allocation; otherwise only current keys are decoded, never identities.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="lower">The inclusive lower encoded key.<br/></param>
    /// <param name="upper">The inclusive upper encoded key.<br/></param>
    /// <param name="predicate">The compiled string predicate to apply when byte matching is not available.<br/></param>
    /// <param name="byteMatcher">Optional UTF-8 byte predicate for encoded string payloads.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountStringPatternRange(
        VarKeyScalar8Index index,
        byte[] lower,
        byte[] upper,
        LibraDexStringPatternPredicate predicate,
        LibraDexUtf8StringPatternPredicate? byteMatcher)
    {
        return CountEncodedRange(
            index,
            lower,
            upper,
            key =>
            {
                if (byteMatcher is not null)
                {
                    return TryMatchEncodedStringPayload(key, byteMatcher);
                }

                string? candidate = Decode(key);
                return candidate is not null && predicate.Matches(candidate);
            });
    }

    /// <summary>
    /// Counts identities in one encoded `VS8` key range.<br/>
    /// Unfiltered counts return the reader's shelf-local count directly; filtered counts walk only current keys for boundary or predicate residual checks.<br/>
    /// </summary>
    /// <param name="index">The routed varlen-key/scalar-identity index to count through.<br/></param>
    /// <param name="lower">The inclusive lower encoded key.<br/></param>
    /// <param name="upper">The inclusive upper encoded key.<br/></param>
    /// <param name="keyFilter">Optional encoded-key filter for residual checks.<br/></param>
    /// <returns>The number of matching scalar identities.<br/></returns>
    private static long CountEncodedRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, Func<byte[], bool>? keyFilter)
    {
        if (lower.AsSpan().SequenceCompareTo(upper) > 0)
        {
            return 0;
        }

        if (keyFilter is null)
        {
            return index.CountEncodedIdentityRange(lower, upper);
        }

        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(lower, upper);
        long count = 0;
        while (reader.MoveNext())
        {
            if (keyFilter(reader.MaterializeCurrentKey()))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<object> IterateAll(VarKeyScalar8Index index)
    {
        return IterateAll(index, null);
    }

    private static bool TryCreateOrderedKeyStateIterator(
        VarKeyScalar8Index index,
        string? boundary,
        bool before,
        bool inclusive,
        int? takeLimit,
        out IEnumerable<object>? identities)
    {
        if (!TryClassifyStringKeyState(boundary, out NullKey keyState))
        {
            identities = null;
            return false;
        }

        identities = IterateOrderedKeyState(index, keyState, before, inclusive, takeLimit);
        return true;
    }

    private static IEnumerable<object> IterateOrderedKeyState(
        VarKeyScalar8Index index,
        NullKey boundary,
        bool before,
        bool inclusive,
        int? takeLimit)
    {
        if (boundary == NullKey.Null)
        {
            if (before && inclusive)
            {
                foreach (object identity in IterateKeyState(index, NullKey.Null, takeLimit))
                {
                    yield return identity;
                }
            }
            else if (!before)
            {
                foreach (object identity in inclusive ? IterateAll(index, takeLimit) : IterateOrderedKeyState(index, NullKey.Empty, before: false, inclusive: true, takeLimit))
                {
                    yield return identity;
                }
            }

            yield break;
        }

        if (before)
        {
            int returned = 0;
            foreach (object identity in IterateKeyState(index, NullKey.Null, takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }

            if (inclusive)
            {
                int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
                foreach (object identity in IterateKeyState(index, NullKey.Empty, remaining))
                {
                    yield return identity;
                }
            }

            yield break;
        }

        if (inclusive)
        {
            int returned = 0;
            foreach (object identity in IterateKeyState(index, NullKey.Empty, takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }

            int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
            foreach (object identity in IterateRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), remaining))
            {
                yield return identity;
            }

            yield break;
        }

        foreach (object identity in IterateRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), takeLimit))
        {
            yield return identity;
        }
    }

    private static IEnumerable<object> IterateAll(VarKeyScalar8Index index, int? takeLimit)
    {
        int returned = 0;
        foreach (object identity in IterateKeyState(index, NullKey.Null, takeLimit))
        {
            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }

        int? remainingForEmpty = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (object identity in IterateKeyState(index, NullKey.Empty, remainingForEmpty))
        {
            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }

        int? remainingForNormal = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (object identity in IterateRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), remainingForNormal))
        {
            yield return identity;
        }
    }

    private static IEnumerable<object> IterateRange(VarKeyScalar8Index index, string? lower, string? upper, int? takeLimit)
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
        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(lower, upper);
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
            FullUpperBound(index.Handle.MaxKeyLength),
            takeLimit,
            inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) > 0);
    }

    private static IEnumerable<object> IterateMembership(
        VarKeyScalar8Index index,
        IReadOnlyList<object?> values,
        Func<string?, string?> transform,
        int? takeLimit)
    {
        int yielded = 0;
        foreach (object? value in values)
        {
            if (value is not IEnumerable<object> objects)
            {
                throw new InvalidOperationException("String membership primitive requires an object enumerable operand.");
            }

            foreach (object? item in objects)
            {
                string? key = item as string;
                if (item is not null && key is null)
                {
                    throw new InvalidOperationException("String membership primitive requires string values.");
                }

                key = transform(key);
                IEnumerable<object> identities = TryClassifyStringKeyState(key, out NullKey keyState)
                    ? IterateKeyState(index, keyState, takeLimit.HasValue ? takeLimit.Value - yielded : null)
                    : IterateRange(index, key, key, null);
                foreach (object identity in identities)
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
        LibraDexUtf8StringPatternPredicate? byteMatcher = predicate.TryCreateUtf8ByteMatcher(IdentityTransform, allowCaseNormalizedBytes: false, out LibraDexUtf8StringPatternPredicate? matcher)
            ? matcher
            : null;
        int yielded = 0;
        if (predicate.Matches(string.Empty))
        {
            foreach (object identity in IterateKeyState(index, NullKey.Empty, takeLimit))
            {
                yield return identity;
                yielded++;
                if (takeLimit is int limit && yielded >= limit)
                {
                    yield break;
                }
            }
        }

        if (candidateRanges.Count == 0)
        {
            foreach (object identity in IterateStringPatternRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), predicate, byteMatcher, takeLimit.HasValue ? takeLimit.Value - yielded : null))
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
            foreach (object identity in IterateStringPatternRange(index, Encode(lower), Encode(upper), predicate, byteMatcher, takeLimit.HasValue ? takeLimit.Value - yielded : null))
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
        LibraDexUtf8StringPatternPredicate? byteMatcher,
        int? takeLimit)
    {
        using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(lower, upper);
        int yielded = 0;
        while (reader.MoveNext())
        {
            ReadOnlySpan<byte> currentKey = reader.CurrentKey;
            if (byteMatcher is not null)
            {
                if (!TryMatchEncodedStringPayload(currentKey, byteMatcher))
                {
                    continue;
                }
            }
            else
            {
                string? candidate = Decode(currentKey);
                if (candidate is null || !predicate.Matches(candidate))
                {
                    continue;
                }
            }

            yield return reader.CurrentEncodedIdentity;
            yielded++;
            if (takeLimit is int limit && yielded >= limit)
            {
                yield break;
            }
        }
    }

    private static string? RequireString(IReadOnlyList<object?> values, int ordinal)
    {
        if (values.Count <= ordinal)
        {
            throw new InvalidOperationException("String primitive requests require string operands.");
        }

        object? value = values[ordinal];
        return value is null || value is string
            ? (string?)value
            : throw new InvalidOperationException("String primitive requests require string operands.");
    }

    private static LibraDexStringPatternPredicate RequireStringPatternPredicate(IReadOnlyList<object?> values)
    {
        return values.Count > 0 && values[0] is LibraDexStringPatternPredicate predicate
            ? predicate
            : throw new InvalidOperationException("String pattern primitive requests require a string predicate operand.");
    }

    private static NullKey RequireNullKeyState(IReadOnlyList<object?> values)
    {
        return values.Count > 0 && values[0] is NullKey state
            ? state
            : throw new InvalidOperationException("String key-state primitive requests require a NullKey operand.");
    }

    private static string? RequireStringKey(object? value, string paramName)
    {
        return value switch
        {
            null or DBNull => null,
            string text => text,
            _ => throw new ArgumentException("String indexes require string keys.", paramName)
        };
    }

    private static ulong RequireScalar8Identity(object value, string paramName)
    {
        return value is ulong typed ? typed : throw new ArgumentException("StringScalar8 indexes require UInt64 identities.", paramName);
    }

    /// <summary>
    /// Inserts one scalar identity into this string index's null or empty key-state route.<br/>
    /// The route stores encoded identities only, making null and empty string keys compact to count and enumerate before ordinary string values.<br/>
    /// </summary>
    /// <param name="keyState">The concrete string key state to write.</param>
    /// <param name="identity">The scalar identity to associate with the key state.</param>
    /// <returns>The insert result reported through the generic public result contract.</returns>
    private LibraDexGenericInsertResult InsertStringKeyStateIdentity(NullKey keyState, ulong identity)
    {
        if (keyState == NullKey.NullOrEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "NullKey.NullOrEmpty is a predicate state and cannot be inserted as one concrete key.");
        }

        bool inserted = exact.Session.InsertScalar8KeyStateIdentity(exact.SlotIndex, ToKeyStateRoute(keyState), identity);
        return new LibraDexGenericInsertResult(inserted, false, default, default);
    }

    private static IEnumerable<object> IterateKeyState(VarKeyScalar8Index index, NullKey keyState, int? takeLimit)
    {
        if (keyState != NullKey.NullOrEmpty)
        {
            foreach (ulong identity in ReadStringKeyStateIdentities(index, keyState, takeLimit))
            {
                yield return identity;
            }

            yield break;
        }

        int returned = 0;
        foreach (ulong identity in ReadStringKeyStateIdentities(index, NullKey.Null, takeLimit))
        {
            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }

        int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (ulong identity in ReadStringKeyStateIdentities(index, NullKey.Empty, remaining))
        {
            yield return identity;
        }
    }

    private IEnumerable<ulong> ReadStringKeyStateIdentities(NullKey keyState, int? takeLimit)
    {
        return ReadStringKeyStateIdentities(exact, keyState, takeLimit);
    }

    private static IEnumerable<ulong> ReadStringKeyStateIdentities(VarKeyScalar8Index index, NullKey keyState, int? takeLimit)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        ulong[] identities = index.Session.ReadScalar8KeyStateIdentities(index.SlotIndex, ToKeyStateRoute(keyState));
        for (int i = 0; i < identities.Length; i++)
        {
            yield return identities[i];
            if (takeLimit is not null && i + 1 >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private static bool TryClassifyStringKeyState(string? key, out NullKey keyState)
    {
        if (key is null)
        {
            keyState = NullKey.Null;
            return true;
        }

        if (key.Length == 0)
        {
            keyState = NullKey.Empty;
            return true;
        }

        keyState = default;
        return false;
    }

    private static KeyStateRoute ToKeyStateRoute(NullKey keyState)
    {
        return keyState switch
        {
            NullKey.Null => KeyStateRoute.Null,
            NullKey.Empty => KeyStateRoute.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "NullKey.NullOrEmpty is not a single physical route.")
        };
    }

    private const byte StringNullMarker = 0x00;
    private const byte StringEmptyMarker = 0x01;
    private const byte StringValueMarker = 0x02;
    private static readonly byte[] EncodedNullKey = [StringNullMarker];
    private static readonly byte[] EncodedEmptyKey = [StringEmptyMarker];

    /// <summary>
    /// Encodes one developer-facing string into LibraDex's ordered string-key byte contract.<br/>
    /// A sentinel byte keeps null, empty, and non-empty text distinct while preserving byte-ordered routing for normal UTF-8 string payloads.<br/>
    /// </summary>
    /// <param name="value">The string value to encode, or null for the null-key sentinel.</param>
    /// <returns>The encoded string key bytes.</returns>
    private static byte[] Encode(string? value)
    {
        if (value is null)
        {
            return EncodedNullKey;
        }

        if (value.Length == 0)
        {
            return EncodedEmptyKey;
        }

        int payloadLength = Encoding.UTF8.GetByteCount(value);
        byte[] encoded = GC.AllocateUninitializedArray<byte>(payloadLength + 1);
        encoded[0] = StringValueMarker;
        _ = Encoding.UTF8.GetBytes(value, encoded.AsSpan(1));
        return encoded;
    }

    /// <summary>
    /// Encodes one logical string value with the exact maintained string-key contract.<br/>
    /// The returned array is caller-owned and includes the null/empty/value marker used by physical string grouping.<br/>
    /// </summary>
    /// <param name="value">The logical string value, including null or empty.<br/></param>
    /// <returns>The encoded maintained string key.<br/></returns>
    internal static byte[] EncodeGroupingKey(string? value)
        => Encode(value).ToArray();

    /// <summary>
    /// Decodes one exact or folded maintained string grouping key.<br/>
    /// Sort-key bytes are intentionally not accepted because culture sort keys are not reversible text.<br/>
    /// </summary>
    /// <param name="encoded">The encoded exact or folded string key.<br/></param>
    /// <returns>The logical string value represented by the grouping key.<br/></returns>
    internal static string? DecodeEncodedGroupingKey(ReadOnlySpan<byte> encoded)
        => Decode(encoded);

    /// <summary>
    /// Decodes one LibraDex ordered string-key value back into the developer-facing string value.<br/>
    /// The null marker returns null so null-key tuples remain queryable and mutable through the logical string facade.<br/>
    /// </summary>
    /// <param name="encoded">The encoded string key bytes.</param>
    /// <returns>The decoded string value, or null for the null-key sentinel.</returns>
    private static string? Decode(ReadOnlySpan<byte> encoded)
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
            return null;
        }

        throw new InvalidDataException("String index key did not use a recognized LibraDex string sentinel marker.");
    }

    /// <summary>
    /// Tests one encoded LibraDex string key against a byte-native UTF-8 payload matcher.<br/>
    /// Null and empty sentinel keys are not routed through ordinary var-key string scans, but the guard keeps the helper defensive for future callers.<br/>
    /// </summary>
    /// <param name="encoded">The encoded string key bytes, including the leading LibraDex string marker.</param>
    /// <param name="matcher">The byte-native predicate compiled for this query.</param>
    /// <returns><see langword="true"/> when the encoded key has a string payload and the payload matches.</returns>
    private static bool TryMatchEncodedStringPayload(ReadOnlySpan<byte> encoded, LibraDexUtf8StringPatternPredicate matcher)
    {
        return encoded.Length > 1 &&
            encoded[0] == StringValueMarker &&
            matcher.Matches(encoded[1..]);
    }

    /// <summary>
    /// Returns the input string unchanged for exact-index byte matcher preparation.<br/>
    /// The helper avoids capturing lambdas on hot query paths while making the transform contract explicit at call sites.<br/>
    /// </summary>
    /// <param name="value">The operand value.</param>
    /// <returns>The same operand value.</returns>
    private static string? IdentityTransform(string? value)
    {
        return value;
    }

    /// <summary>
    /// Returns the encoded lower bound used for non-empty exact string scans.<br/>
    /// Null and empty strings are stored through dedicated key-state routes, so the variable-key range only needs the non-empty value sentinel.<br/>
    /// </summary>
    /// <returns>The lower encoded key bound.</returns>
    private static byte[] FullLowerBound()
    {
        return new byte[] { StringValueMarker };
    }

    /// <summary>
    /// Returns the encoded upper bound used for non-empty exact string scans.<br/>
    /// The bound is padded to the routed index max key length so deeper router range pruning follows every possible payload byte instead of treating missing upper-bound bytes as zero.<br/>
    /// </summary>
    /// <param name="maxKeyLength">The routed var-key profile's maximum encoded key length.<br/></param>
    /// <returns>The upper encoded key bound.</returns>
    private static byte[] FullUpperBound(int maxKeyLength)
    {
        byte[] upper = GC.AllocateUninitializedArray<byte>(Math.Max(2, maxKeyLength));
        upper[0] = StringValueMarker;
        upper.AsSpan(1).Fill(0xFF);
        return upper;
    }

    /// <summary>
    /// Folds one string using the projection culture chosen when the string index was created.<br/>
    /// This mirrors the condition bridge's invariant-or-named culture behavior so projection lookup operands and maintained keys stay byte-compatible.<br/>
    /// </summary>
    /// <param name="value">The original developer-facing string value, or null for the null-key sentinel.</param>
    /// <param name="culture">The projection culture.</param>
    /// <returns>The culture-folded string value, or null for the null-key sentinel.</returns>
    private static string? Fold(string? value, CultureInfo culture)
    {
        if (value is null)
        {
            return null;
        }

        return value.ToLower(culture);
    }

    /// <summary>
    /// Reverses a folded string by UTF-16 code unit positions for maintained suffix projection keys.<br/>
    /// LibraDex stores the reversed folded value at insert time so suffix conditions can become ordinary prefix-like ordered extents over the reversed projection.<br/>
    /// </summary>
    /// <param name="value">The folded string value to reverse, or null for the null-key sentinel.</param>
    /// <returns>The reversed folded string, or null for the null-key sentinel.</returns>
    private static string? Reverse(string? value)
    {
        if (value is null)
        {
            return null;
        }

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
    /// <param name="value">The original developer-facing string value, or null for the null-key sentinel.</param>
    /// <param name="culture">The projection culture.</param>
    /// <returns>The sort-key bytes for ignore-case comparison in the supplied culture.</returns>
    private static byte[] CreateSortKey(string? value, CultureInfo culture)
    {
        if (value is null)
        {
            return new[] { StringNullMarker };
        }

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
    /// Returns whether a resolved culture is the invariant culture used by the current hot folded-string insert path.<br/>
    /// The invariant fast path can lower characters into caller-owned scratch without allocating a transformed string; named cultures keep the existing culture-aware `ToLower` route.<br/>
    /// </summary>
    /// <param name="culture">The resolved projection culture.</param>
    /// <returns><see langword="true"/> when <paramref name="culture"/> is invariant.</returns>
    private static bool IsInvariantCulture(CultureInfo culture)
    {
        return string.IsNullOrEmpty(culture.Name);
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

    private readonly record struct StringScalar8Tuple(string? Key, ulong Identity);

    private sealed class LibraDexStringScalar8ProjectionIndex : IIndex, IIdentityPrimitiveExecutor, IDisposable
    {
        private readonly VarKeyScalar8Index index;
        private readonly Func<string?, string?> transform;

        internal LibraDexStringScalar8ProjectionIndex(string group, string name, VarKeyScalar8Index index, Func<string?, string?> transform)
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

        public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

        public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.String;

        public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

        public LibraDexIndexShapeSpec? LogicalShape => null;

        public LibraDexGenericInsertResult Insert(object? key, object identity)
        {
            return InsertProjected(
                RequireStringKey(key, nameof(key)),
                identity is ulong typed ? typed : throw new ArgumentException("String projection indexes require UInt64 identities.", nameof(identity)));
        }

        public LibraDexGenericInsertResult InsertProjected(string? key, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.InsertEncoded(Encode(transform(key)), identity);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
        }

        internal LibraDexGenericInsertResult InsertEncoded(ReadOnlySpan<byte> encodedKey, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.InsertEncoded(encodedKey, identity);
            RecordThreadInsertOutcome(Name, "projection", result);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
        }

        internal LibraDexGenericInsertResult InsertProjectedInCurrentScope(string? key, ulong identity)
        {
            return InsertProjectedInCurrentScope(key, identity, scratch: null);
        }

        internal LibraDexGenericInsertResult InsertProjectedInCurrentScope(string? key, ulong identity, LibraDexStringScalar8InsertScratch? scratch)
        {
            string? transformedKey = transform(key);
            ReadOnlySpan<byte> encodedKey = scratch is null
                ? Encode(transformedKey)
                : scratch.EncodeString(transformedKey);
            VarKeyScalar8InsertOutcome result = index.InsertEncodedInCurrentScope(encodedKey, identity);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                default);
        }

        /// <summary>
        /// Inserts one already encoded projection key using the caller's active grouped durability scope.<br/>
        /// Hot string-maintained projections use this when the owning facade can produce projection bytes directly in scratch storage without materializing a transient transformed string.<br/>
        /// </summary>
        /// <param name="encodedKey">The already encoded projection key bytes.</param>
        /// <param name="identity">The encoded scalar identity.</param>
        /// <returns>The projection insert result.</returns>
        internal LibraDexGenericInsertResult InsertEncodedInCurrentScope(ReadOnlySpan<byte> encodedKey, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.InsertEncodedInCurrentScope(encodedKey, identity);
            RecordThreadInsertOutcome(Name, "projection", result);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                default);
        }

        internal LibraDexGenericInsertResult InsertProjectedForConcurrentBatch(
            LibraDexWriteContext writeContext,
            string? key,
            ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.InsertEncodedForConcurrentBatch(writeContext, Encode(transform(key)), identity);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                false,
                default,
                default)
            {
                QueuedInsertPath = result.Inserted
                    ? Scalar8Scalar8QueuedInsertPath.WriterContext
                    : Scalar8Scalar8QueuedInsertPath.None
            };
        }


        /// <summary>
        /// Deletes one maintained projection tuple using the projection's configured string transform.<br/>
        /// This is intentionally exact-tuple deletion so folded projection collisions do not remove identities belonging to another exact string key.<br/>
        /// The owning logical string facade coordinates the surrounding durability batch before invoking this helper.<br/>
        /// </summary>
        /// <param name="key">The projection source key value expected by this projection wrapper.</param>
        /// <param name="identity">The encoded scalar identity to delete.</param>
        /// <returns><see langword="true"/> when a live projection tuple was deleted.</returns>
        internal bool DeleteProjectedExactTuple(string? key, ulong identity)
        {
            return index.DeleteExactTupleInCurrentScope(Encode(transform(key)), identity);
        }

        /// <summary>
        /// Deletes one maintained projection tuple through the projection index's immediate encoded exact-delete path.<br/>
        /// No-batch logical string mutation uses this so projection cleanup can benefit from writer-context staging without opening a shared exact-index batch.<br/>
        /// </summary>
        /// <param name="key">The projection source key value expected by this projection wrapper.</param>
        /// <param name="identity">The encoded scalar identity to delete.</param>
        /// <returns><see langword="true"/> when a live projection tuple was deleted.</returns>
        internal bool DeleteProjectedExactTupleImmediate(string? key, ulong identity)
        {
            return index.DeleteEncodedExactTuple(Encode(transform(key)), identity);
        }

        internal bool DeleteProjectedExactTupleForConcurrentBatch(
            LibraDexWriteContext writeContext,
            string? key,
            ulong identity)
        {
            return index.DeleteEncodedExactTupleForConcurrentBatch(writeContext, Encode(transform(key)), identity);
        }

        public LibraDexPreparedObjectSet PrepareInSet(IEnumerable<object> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            return new LibraDexPreparedObjectSet(typeof(string), keys.Select(key =>
            {
                string? text = key as string;
                if (key is not null && text is null)
                {
                    throw new ArgumentException("String projection indexes require string keys.", nameof(keys));
                }

                return text!;
            }).ToArray());
        }

        public void Dispose()
        {
            index.Dispose();
        }

        /// <summary>
        /// Streams projection key and scalar identity tuples for one normalized primitive request.<br/>
        /// The projection key is returned in the projection's own logical key space, which lets diagnostic tuple readers measure folded or reversed-folded routes directly without correlating back to the exact string facade.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
        /// <returns>A forward-only stream of projection key and scalar identity tuples.</returns>
        internal IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateStringProjectionTuplePrimitive(request, index, transform);
        }

        /// <summary>
        /// Streams maintained projection tuples without decoding the projection's ordered string bytes.<br/>
        /// Grouped aggregate execution uses this path so bucket comparison remains byte-native.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.<br/></param>
        /// <returns>A forward-only stream of encoded projection keys and scalar identities.<br/></returns>
        internal IEnumerable<LibraDexObjectTuple> IterateEncodedTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            if (request.CriteriaKind != LibraDexCriteriaKind.All || request.TakeLimit is not null)
                throw new NotSupportedException("Encoded string projection grouping currently requires an unbounded All tuple stream.");

            return IterateEncodedStringGroupingTuples(index);
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
            return CountIdentityPrimitiveCore(request, index, transform);
        }

        /// <summary>
        /// Executes count as a maintained string projection aggregate.<br/>
        /// Projection operands have already been transformed by the condition materializer, so the aggregate keeps the same folded/reversed string semantics as identity retrieval while avoiding identity streaming.<br/>
        /// </summary>
        /// <param name="request">The aggregate request to execute.<br/></param>
        /// <returns>The aggregate count result and physical plan classification.<br/></returns>
        LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
        {
            if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
            {
                throw new NotSupportedException($"{request.Kind} is not connected to string projection aggregation yet.");
            }

            if (request.Scope != AggregateScope.Tuples)
            {
                throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to string projection aggregation yet.");
            }

            return LibraDexPrimitiveAggregateResult.ForCount(
                CountIdentityPrimitiveCore(request.PrimitiveRequest, index, transform),
                ClassifyStringAggregatePlan(request.PrimitiveRequest));
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
        /// Executes condition-derived text primitives as projection key/identity tuple reads over a maintained string projection index.<br/>
        /// Operand transformation mirrors identity retrieval so folded-text and reversed-folded projection tuple reads use the same route bounds as identity-only execution.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.</param>
        /// <param name="index">The maintained projection index.</param>
        /// <param name="transform">The string transform applied to developer-facing operands before route lookup.</param>
        /// <returns>A forward-only stream of projection key and scalar identity tuples.</returns>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionTuplePrimitive(
            LibraDexIdentityPrimitiveRequest request,
            VarKeyScalar8Index index,
            Func<string?, string?> transform)
        {
            return request.CriteriaKind switch
            {
                LibraDexCriteriaKind.All => IterateStringProjectionTupleRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), keyFilter: null, textFilter: null, request.TakeLimit),
                LibraDexCriteriaKind.Find => IterateStringProjectionFind(index, transform(RequireString(request.Values, 0)), request.TakeLimit),
                LibraDexCriteriaKind.Between => IterateStringProjectionTupleRange(index, Encode(transform(RequireString(request.Values, 0))), Encode(transform(RequireString(request.Values, 1))), keyFilter: null, textFilter: null, request.TakeLimit),
                LibraDexCriteriaKind.Before => IterateStringProjectionBefore(index, transform(RequireString(request.Values, 0)), inclusive: false, request.TakeLimit),
                LibraDexCriteriaKind.AtOrBefore => IterateStringProjectionTupleRange(index, FullLowerBound(), Encode(transform(RequireString(request.Values, 0))), keyFilter: null, textFilter: null, request.TakeLimit),
                LibraDexCriteriaKind.After => IterateStringProjectionAfter(index, transform(RequireString(request.Values, 0)), inclusive: false, request.TakeLimit),
                LibraDexCriteriaKind.AtOrAfter => IterateStringProjectionTupleRange(index, Encode(transform(RequireString(request.Values, 0))), FullUpperBound(index.Handle.MaxKeyLength), keyFilter: null, textFilter: null, request.TakeLimit),
                LibraDexCriteriaKind.StringPattern => IterateStringProjectionPatternTuples(index, RequireStringPatternPredicate(request.Values), transform, request.TakeLimit),
                _ => throw new NotSupportedException($"{request.CriteriaKind} is not connected to string projection tuple streaming yet.")
            };
        }

        /// <summary>
        /// Streams exact matches for one transformed projection key.<br/>
        /// Null and empty keys are not stored in maintained projection indexes, so key-state sentinels produce an empty stream.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionFind(VarKeyScalar8Index index, string? key, int? takeLimit)
        {
            if (TryClassifyStringKeyState(key, out _))
            {
                yield break;
            }

            foreach (LibraDexObjectTuple tuple in IterateStringProjectionTupleRange(index, Encode(key), Encode(key), keyFilter: null, textFilter: null, takeLimit))
            {
                yield return tuple;
            }
        }

        /// <summary>
        /// Streams projection tuples before one already transformed string boundary.<br/>
        /// The encoded boundary is captured once so exclusive filtering does not allocate per candidate row.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionBefore(VarKeyScalar8Index index, string? boundary, bool inclusive, int? takeLimit)
        {
            byte[] encodedBoundary = Encode(boundary);
            Func<byte[], bool>? filter = inclusive ? null : key => key.AsSpan().SequenceCompareTo(encodedBoundary) < 0;
            return IterateStringProjectionTupleRange(index, FullLowerBound(), encodedBoundary, filter, textFilter: null, takeLimit);
        }

        /// <summary>
        /// Streams projection tuples after one already transformed string boundary.<br/>
        /// The encoded boundary is captured once so exclusive filtering does not allocate per candidate row.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionAfter(VarKeyScalar8Index index, string? boundary, bool inclusive, int? takeLimit)
        {
            byte[] encodedBoundary = Encode(boundary);
            Func<byte[], bool>? filter = inclusive ? null : key => key.AsSpan().SequenceCompareTo(encodedBoundary) > 0;
            return IterateStringProjectionTupleRange(index, encodedBoundary, FullUpperBound(index.Handle.MaxKeyLength), filter, textFilter: null, takeLimit);
        }

        /// <summary>
        /// Streams projection key and identity tuples from one encoded projection key range.<br/>
        /// Key filters handle exclusive boundary forms while text filters handle scan-backed string predicates such as contains.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionTupleRange(
            VarKeyScalar8Index index,
            byte[] lower,
            byte[] upper,
            Func<byte[], bool>? keyFilter,
            Func<string, bool>? textFilter,
            int? takeLimit)
            => IterateStringProjectionTupleRange(index, lower, upper, keyFilter, textFilter, byteMatcher: null, takeLimit);

        /// <summary>
        /// Streams projection key and identity tuples from one encoded projection key range with an optional byte residual predicate.<br/>
        /// The byte matcher rejects non-matching candidates before string decoding, leaving string materialization only for rows that are returned to the caller as tuple keys.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionTupleRange(
            VarKeyScalar8Index index,
            byte[] lower,
            byte[] upper,
            Func<byte[], bool>? keyFilter,
            Func<string, bool>? textFilter,
            LibraDexUtf8StringPatternPredicate? byteMatcher,
            int? takeLimit)
        {
            using VarKeyScalar8RangeReader reader = index.OpenEncodedRangeReader(lower, upper);
            int yielded = 0;
            while (reader.MoveNext())
            {
                ReadOnlySpan<byte> currentKey = reader.CurrentKey;
                if (keyFilter is not null)
                {
                    byte[] keyBytes = reader.MaterializeCurrentKey();
                    if (!keyFilter(keyBytes))
                    {
                        continue;
                    }
                }

                if (byteMatcher is not null && !TryMatchEncodedStringPayload(currentKey, byteMatcher))
                {
                    continue;
                }

                string? key = Decode(currentKey);
                if (key is null)
                {
                    continue;
                }

                if (byteMatcher is null && textFilter is not null && !textFilter(key))
                {
                    continue;
                }

                yield return new LibraDexObjectTuple(key, reader.CurrentEncodedIdentity);
                yielded++;
                if (takeLimit is int limit && yielded >= limit)
                {
                    yield break;
                }
            }
        }

        /// <summary>
        /// Streams projection tuples for a scan-backed string-pattern primitive.<br/>
        /// Candidate ranges from the pattern are reused when available so contains and prefix-like predicates keep route pruning before residual text matching.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateStringProjectionPatternTuples(
            VarKeyScalar8Index index,
            LibraDexStringPatternPredicate predicate,
            Func<string?, string?> transform,
            int? takeLimit)
        {
            int yielded = 0;
            IReadOnlyList<(string Lower, string Upper)> candidateRanges = predicate.CreateCandidateRanges();
            LibraDexUtf8StringPatternPredicate? byteMatcher = predicate.TryCreateUtf8ByteMatcher(transform, allowCaseNormalizedBytes: true, out LibraDexUtf8StringPatternPredicate? matcher)
                ? matcher
                : null;
            if (candidateRanges.Count == 0)
            {
                foreach (LibraDexObjectTuple tuple in IterateStringProjectionTupleRange(index, FullLowerBound(), FullUpperBound(index.Handle.MaxKeyLength), keyFilter: null, textFilter: predicate.Matches, byteMatcher: byteMatcher, takeLimit: takeLimit))
                {
                    yield return tuple;
                }

                yield break;
            }

            foreach ((string lower, string upper) in candidateRanges)
            {
                int? remaining = takeLimit.HasValue ? takeLimit.Value - yielded : null;
                foreach (LibraDexObjectTuple tuple in IterateStringProjectionTupleRange(index, Encode(transform(lower)), Encode(transform(upper)), keyFilter: null, textFilter: predicate.Matches, byteMatcher: byteMatcher, takeLimit: remaining))
                {
                    yield return tuple;
                    yielded++;
                    if (takeLimit is int limit && yielded >= limit)
                    {
                        yield break;
                    }
                }
            }
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

        public IdentityKeyMultiplicity IdentityKeyMultiplicity => IdentityKeyMultiplicity.MultipleKeysPerIdentity;

        public CatalogIndexKeyFamily KeyFamily => CatalogIndexKeyFamily.Blob;

        public CatalogIndexIdentityFamily IdentityFamily => CatalogIndexIdentityFamily.Scalar;

        public LibraDexIndexShapeSpec? LogicalShape => null;

        public LibraDexGenericInsertResult Insert(object? key, object identity)
        {
            return InsertProjected(
                key as byte[] ?? throw new ArgumentException("Sort-key projection indexes require byte[] keys.", nameof(key)),
                identity is ulong typed ? typed : throw new ArgumentException("Sort-key projection indexes require UInt64 identities.", nameof(identity)));
        }

        public LibraDexGenericInsertResult InsertProjected(byte[] key, ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.Insert(key, identity);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                LibraDexOperationDiagnostics.FromDataKernel(result.Commit));
        }

        internal LibraDexGenericInsertResult InsertProjectedInCurrentScope(byte[] key, ulong identity)
        {
            return InsertProjectedInCurrentScope(key, identity, scratch: null);
        }

        internal LibraDexGenericInsertResult InsertProjectedInCurrentScope(byte[] key, ulong identity, LibraDexStringScalar8InsertScratch? scratch)
        {
            ReadOnlySpan<byte> encodedKey = scratch is null
                ? LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key))
                : scratch.EncodeVarLenKey(key, index.Handle.MaxKeyLength, nameof(key));
            VarKeyScalar8InsertOutcome result = index.InsertEncodedInCurrentScope(encodedKey, identity);
            RecordThreadInsertOutcome(Name, "projection", result);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                result.CreatedInitialShelfRoute,
                default,
                default);
        }

        internal LibraDexGenericInsertResult InsertProjectedForConcurrentBatch(
            LibraDexWriteContext writeContext,
            byte[] key,
            ulong identity)
        {
            VarKeyScalar8InsertOutcome result = index.InsertEncodedForConcurrentBatch(
                writeContext,
                LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key)),
                identity);
            return new LibraDexGenericInsertResult(
                result.Inserted,
                false,
                default,
                default)
            {
                QueuedInsertPath = result.Inserted
                    ? Scalar8Scalar8QueuedInsertPath.WriterContext
                    : Scalar8Scalar8QueuedInsertPath.None
            };
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
            return index.DeleteExactTupleInCurrentScope(LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key)), identity);
        }

        /// <summary>
        /// Deletes one maintained sort-key projection tuple through the projection index's immediate encoded exact-delete path.<br/>
        /// No-batch logical string mutation uses this so binary sort-key cleanup can benefit from writer-context staging without opening a shared exact-index batch.<br/>
        /// </summary>
        /// <param name="key">The maintained sort-key bytes.</param>
        /// <param name="identity">The encoded scalar identity to delete.</param>
        /// <returns><see langword="true"/> when a live projection tuple was deleted.</returns>
        internal bool DeleteProjectedExactTupleImmediate(byte[] key, ulong identity)
        {
            return index.DeleteEncodedExactTuple(LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key)), identity);
        }

        internal bool DeleteProjectedExactTupleForConcurrentBatch(
            LibraDexWriteContext writeContext,
            byte[] key,
            ulong identity)
        {
            return index.DeleteEncodedExactTupleForConcurrentBatch(
                writeContext,
                LibraDexVarLenKeyCodec.Encode(key, index.Handle.MaxKeyLength, nameof(key)),
                identity);
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

        /// <summary>
        /// Streams maintained sort-key projection key and scalar identity tuples for one normalized primitive request.<br/>
        /// Sort-key tuple output returns byte-array keys because the projection's natural key is a binary culture sort key rather than developer-facing text.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
        /// <returns>A forward-only stream of sort-key byte-array key and scalar identity tuples.</returns>
        internal IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
        {
            return IterateByteTuplePrimitiveCore(request, index);
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
            return CountByteIdentityPrimitiveCore(request, index);
        }

        /// <summary>
        /// Executes count as a maintained sort-key byte aggregate.<br/>
        /// Direct byte ranges use routed `VS8` counts, while exclusive bound predicates are classified as key scans because they need residual byte-key comparison after range pruning.<br/>
        /// </summary>
        /// <param name="request">The aggregate request to execute.<br/></param>
        /// <returns>The aggregate count result and physical plan classification.<br/></returns>
        LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
        {
            if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
            {
                throw new NotSupportedException($"{request.Kind} is not connected to sort-key aggregation yet.");
            }

            if (request.Scope != AggregateScope.Tuples)
            {
                throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to sort-key aggregation yet.");
            }

            return LibraDexPrimitiveAggregateResult.ForCount(
                CountByteIdentityPrimitiveCore(request.PrimitiveRequest, index),
                ClassifyByteAggregatePlan(request.PrimitiveRequest));
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
                    return IterateLogicalByteRange(index, Array.Empty<byte>(), new byte[] { 0xFF }, null);
                case LibraDexCriteriaKind.Find:
                    byte[] key = RequireBytes(request.Values, 0);
                    return IterateLogicalByteRange(index, key, key, null);
                case LibraDexCriteriaKind.Between:
                    return IterateLogicalByteRange(index, RequireBytes(request.Values, 0), RequireBytes(request.Values, 1), request.TakeLimit);
                case LibraDexCriteriaKind.Before:
                    return IterateLogicalByteBefore(index, RequireBytes(request.Values, 0), inclusive: false, request.TakeLimit);
                case LibraDexCriteriaKind.AtOrBefore:
                    return IterateLogicalByteBefore(index, RequireBytes(request.Values, 0), inclusive: true, request.TakeLimit);
                case LibraDexCriteriaKind.After:
                    return IterateLogicalByteAfter(index, RequireBytes(request.Values, 0), inclusive: false, request.TakeLimit);
                case LibraDexCriteriaKind.AtOrAfter:
                    return IterateLogicalByteAfter(index, RequireBytes(request.Values, 0), inclusive: true, request.TakeLimit);
                case LibraDexCriteriaKind.InSet:
                    return IterateByteMembership(index, request.Values, request.TakeLimit);
                default:
                    throw new NotSupportedException($"{request.CriteriaKind} is not connected to the sort-key string projection facade yet.");
            }
        }

        /// <summary>
        /// Executes condition-derived sort-key byte primitives as projection key/identity tuple reads.<br/>
        /// The condition materializer has already converted string operands into byte sort keys, so tuple streaming can use the same byte range bounds as identity retrieval.<br/>
        /// </summary>
        /// <param name="request">The normalized primitive request.</param>
        /// <param name="index">The maintained sort-key projection index.</param>
        /// <returns>A forward-only stream of byte-array key and scalar identity tuples.</returns>
        private static IEnumerable<LibraDexObjectTuple> IterateByteTuplePrimitiveCore(LibraDexIdentityPrimitiveRequest request, VarKeyScalar8Index index)
        {
            return request.CriteriaKind switch
            {
                LibraDexCriteriaKind.All => IterateLogicalByteTupleRange(index, Array.Empty<byte>(), new byte[] { 0xFF }, null, request.TakeLimit),
                LibraDexCriteriaKind.Find => IterateLogicalByteTupleRange(index, RequireBytes(request.Values, 0), RequireBytes(request.Values, 0), null, request.TakeLimit),
                LibraDexCriteriaKind.Between => IterateLogicalByteTupleRange(index, RequireBytes(request.Values, 0), RequireBytes(request.Values, 1), null, request.TakeLimit),
                LibraDexCriteriaKind.Before => IterateLogicalByteTupleRange(index, Array.Empty<byte>(), RequireBytes(request.Values, 0), key => key.AsSpan().SequenceCompareTo(RequireBytes(request.Values, 0)) < 0, request.TakeLimit),
                LibraDexCriteriaKind.AtOrBefore => IterateLogicalByteTupleRange(index, Array.Empty<byte>(), RequireBytes(request.Values, 0), null, request.TakeLimit),
                LibraDexCriteriaKind.After => IterateLogicalByteTupleRange(index, RequireBytes(request.Values, 0), new byte[] { 0xFF }, key => key.AsSpan().SequenceCompareTo(RequireBytes(request.Values, 0)) > 0, request.TakeLimit),
                LibraDexCriteriaKind.AtOrAfter => IterateLogicalByteTupleRange(index, RequireBytes(request.Values, 0), new byte[] { 0xFF }, null, request.TakeLimit),
                LibraDexCriteriaKind.InSet => IterateByteMembershipTuples(index, request.Values, request.TakeLimit),
                _ => throw new NotSupportedException($"{request.CriteriaKind} is not connected to sort-key projection tuple streaming yet.")
            };
        }

        /// <summary>
        /// Streams sort-key projection key and identity tuples from one logical byte-key range.<br/>
        /// Optional key filtering handles exclusive boundary forms after the inclusive range reader has pruned to the candidate route set.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateLogicalByteTupleRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, Func<byte[], bool>? keyFilter, int? takeLimit)
        {
            using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lower, upper);
            int yielded = 0;
            while (reader.MoveNext())
            {
                byte[] key = reader.MaterializeCurrentKey();
                if (keyFilter is not null && !keyFilter(key))
                {
                    continue;
                }

                yield return new LibraDexObjectTuple(key, reader.CurrentEncodedIdentity);
                yielded++;
                if (takeLimit is int limit && yielded >= limit)
                {
                    yield break;
                }
            }
        }

        /// <summary>
        /// Streams sort-key projection tuples for repeated exact byte-key membership requests.<br/>
        /// Key order follows the supplied membership values; duplicate-key behavior remains the physical projection's tuple behavior.<br/>
        /// </summary>
        private static IEnumerable<LibraDexObjectTuple> IterateByteMembershipTuples(VarKeyScalar8Index index, IReadOnlyList<object?> values, int? takeLimit)
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
                    foreach (LibraDexObjectTuple tuple in IterateLogicalByteTupleRange(index, key, key, null, null))
                    {
                        yield return tuple;
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
                    foreach (object identity in IterateLogicalByteRange(index, key, key, null))
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

        private static IEnumerable<object> IterateLogicalByteRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, int? takeLimit)
        {
            return IterateLogicalByteRange(index, lower, upper, takeLimit, keyFilter: null);
        }

        private static IEnumerable<object> IterateLogicalByteRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, int? takeLimit, Func<byte[], bool>? keyFilter)
        {
            using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lower, upper);
            int yielded = 0;
            while (reader.MoveNext())
            {
                byte[] key = reader.MaterializeCurrentKey();
                if (keyFilter is not null && !keyFilter(key))
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

        private static IEnumerable<object> IterateLogicalByteBefore(VarKeyScalar8Index index, byte[] boundary, bool inclusive, int? takeLimit)
        {
            return IterateLogicalByteRange(
                index,
                Array.Empty<byte>(),
                boundary,
                takeLimit,
                inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) < 0);
        }

        private static IEnumerable<object> IterateLogicalByteAfter(VarKeyScalar8Index index, byte[] boundary, bool inclusive, int? takeLimit)
        {
            return IterateLogicalByteRange(
                index,
                boundary,
                new byte[] { 0xFF },
                takeLimit,
                inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) > 0);
        }

        /// <summary>
        /// Counts identities for one normalized sort-key byte primitive without yielding boxed scalar identities.<br/>
        /// Direct byte range branches use routed `VS8` reader counts; exclusive boundary branches scan only keys needed to apply the residual boundary predicate.<br/>
        /// </summary>
        /// <param name="request">The primitive request produced by the condition materializer.<br/></param>
        /// <param name="index">The maintained sort-key projection index.<br/></param>
        /// <returns>The number of matching scalar identities.<br/></returns>
        private static long CountByteIdentityPrimitiveCore(LibraDexIdentityPrimitiveRequest request, VarKeyScalar8Index index)
        {
            switch (request.CriteriaKind)
            {
                case LibraDexCriteriaKind.All:
                    return CountLogicalByteRange(index, Array.Empty<byte>(), new byte[] { 0xFF }, keyFilter: null);
                case LibraDexCriteriaKind.Find:
                {
                    byte[] key = RequireBytes(request.Values, 0);
                    return CountLogicalByteRange(index, key, key, keyFilter: null);
                }
                case LibraDexCriteriaKind.Between:
                    return CountLogicalByteRange(index, RequireBytes(request.Values, 0), RequireBytes(request.Values, 1), keyFilter: null);
                case LibraDexCriteriaKind.Before:
                    return CountLogicalByteBefore(index, RequireBytes(request.Values, 0), inclusive: false);
                case LibraDexCriteriaKind.AtOrBefore:
                    return CountLogicalByteBefore(index, RequireBytes(request.Values, 0), inclusive: true);
                case LibraDexCriteriaKind.After:
                    return CountLogicalByteAfter(index, RequireBytes(request.Values, 0), inclusive: false);
                case LibraDexCriteriaKind.AtOrAfter:
                    return CountLogicalByteAfter(index, RequireBytes(request.Values, 0), inclusive: true);
                case LibraDexCriteriaKind.InSet:
                    return CountByteMembership(index, request.Values);
                default:
                    throw new NotSupportedException($"{request.CriteriaKind} is not connected to the sort-key string projection facade yet.");
            }
        }

        /// <summary>
        /// Classifies the physical plan used by a sort-key byte aggregate primitive.<br/>
        /// Direct byte-key range forms use routed slot counts, while exclusive one-sided bounds need residual key comparisons after route pruning.<br/>
        /// </summary>
        /// <param name="request">The primitive request being aggregated.<br/></param>
        /// <returns>The conservative physical plan classification.</returns>
        private static LibraDexPrimitiveAggregatePlanKind ClassifyByteAggregatePlan(LibraDexIdentityPrimitiveRequest request)
        {
            return request.CriteriaKind is LibraDexCriteriaKind.Before or LibraDexCriteriaKind.After
                ? LibraDexPrimitiveAggregatePlanKind.KeyScan
                : LibraDexPrimitiveAggregatePlanKind.RangeSlots;
        }

        /// <summary>
        /// Counts sort-key membership as one exact byte-key lookup per distinct logical byte key against the maintained projection index.<br/>
        /// Structural byte-array equality is required here because condition operands often arrive as fresh arrays even when they represent the same persisted sort key.<br/>
        /// </summary>
        /// <param name="index">The maintained sort-key projection index.<br/></param>
        /// <param name="values">The primitive request values containing one enumerable of byte-array sort keys.<br/></param>
        /// <returns>The number of matching scalar identities.</returns>
        private static long CountByteMembership(VarKeyScalar8Index index, IReadOnlyList<object?> values)
        {
            long count = 0;
            HashSet<byte[]> seenKeys = new(LibraDexKeyEquality<byte[]>.Comparer);
            foreach (object? value in values)
            {
                if (value is not IEnumerable<object> objects)
                {
                    throw new InvalidOperationException("Sort-key membership primitive requires an object enumerable operand.");
                }

                foreach (object item in objects)
                {
                    byte[] key = item as byte[] ?? throw new InvalidOperationException("Sort-key membership primitive requires byte[] values.");
                    if (seenKeys.Add(key))
                    {
                        count += CountLogicalByteRange(index, key, key, keyFilter: null);
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Counts identities in one logical byte-key range for a maintained sort-key projection.<br/>
        /// Unfiltered ranges use the reader's shelf-local count; filtered ranges scan current keys only for boundary correctness.<br/>
        /// </summary>
        /// <param name="index">The maintained sort-key projection index.<br/></param>
        /// <param name="lower">The inclusive lower logical byte key.<br/></param>
        /// <param name="upper">The inclusive upper logical byte key.<br/></param>
        /// <param name="keyFilter">Optional key filter for exclusive boundary forms.<br/></param>
        /// <returns>The number of matching scalar identities.</returns>
        private static long CountLogicalByteRange(VarKeyScalar8Index index, byte[] lower, byte[] upper, Func<byte[], bool>? keyFilter)
        {
            if (lower.AsSpan().SequenceCompareTo(upper) > 0)
            {
                return 0;
            }

            using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lower, upper);
            if (keyFilter is null)
            {
                return reader.Count;
            }

            long count = 0;
            while (reader.MoveNext())
            {
                if (keyFilter(reader.MaterializeCurrentKey()))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Counts identities whose sort-key byte key sorts before a boundary.<br/>
        /// Inclusive boundaries route directly to the upper key; exclusive boundaries apply a residual key comparison after routed pruning.<br/>
        /// </summary>
        /// <param name="index">The maintained sort-key projection index.<br/></param>
        /// <param name="boundary">The encoded boundary key.<br/></param>
        /// <param name="inclusive">True to include keys equal to the boundary.<br/></param>
        /// <returns>The number of matching scalar identities.</returns>
        private static long CountLogicalByteBefore(VarKeyScalar8Index index, byte[] boundary, bool inclusive)
        {
            return CountLogicalByteRange(
                index,
                Array.Empty<byte>(),
                boundary,
                inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) < 0);
        }

        /// <summary>
        /// Counts identities whose sort-key byte key sorts after a boundary.<br/>
        /// Inclusive boundaries route directly from the lower key; exclusive boundaries apply a residual key comparison after routed pruning.<br/>
        /// </summary>
        /// <param name="index">The maintained sort-key projection index.<br/></param>
        /// <param name="boundary">The encoded boundary key.<br/></param>
        /// <param name="inclusive">True to include keys equal to the boundary.<br/></param>
        /// <returns>The number of matching scalar identities.</returns>
        private static long CountLogicalByteAfter(VarKeyScalar8Index index, byte[] boundary, bool inclusive)
        {
            return CountLogicalByteRange(
                index,
                boundary,
                new byte[] { 0xFF },
                inclusive ? null : key => key.AsSpan().SequenceCompareTo(boundary) > 0);
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

    /// <summary>
    /// Holds precomputed encoded bytes for one logical string key and the projections configured on a string/scalar8 facade.<br/>
    /// Instances are immutable and may be reused across repeated inserts of the same logical key into the same facade configuration.<br/>
    /// Null and empty keys keep only their key-state marker because those routes bypass ordinary var-key shelf insertion.<br/>
    /// </summary>
    internal sealed class LibraDexStringScalar8PreparedKey
    {
        internal LibraDexStringScalar8PreparedKey(
            string? source,
            NullKey? keyState,
            byte[]? exactEncodedKey,
            byte[]? foldedEncodedKey,
            byte[]? exactReversedEncodedKey,
            byte[]? sortKeyBytes,
            byte[]? foldedReversedEncodedKey)
        {
            Source = source;
            KeyState = keyState;
            ExactEncodedKey = exactEncodedKey ?? Array.Empty<byte>();
            FoldedEncodedKey = foldedEncodedKey;
            ExactReversedEncodedKey = exactReversedEncodedKey;
            SortKeyBytes = sortKeyBytes;
            FoldedReversedEncodedKey = foldedReversedEncodedKey;
        }

        internal string? Source { get; }

        internal NullKey? KeyState { get; }

        internal byte[] ExactEncodedKey { get; }

        internal byte[]? FoldedEncodedKey { get; }

        internal byte[]? ExactReversedEncodedKey { get; }

        internal byte[]? SortKeyBytes { get; }

        internal byte[]? FoldedReversedEncodedKey { get; }
    }

    /// <summary>
    /// Reuses transient encoded-key bytes for grouped string inserts.<br/>
    /// The scratch belongs to the caller's batch/session scope, not to an individual insert, so hot string indexing can encode into the same rented buffer repeatedly instead of allocating one `byte[]` per key projection.<br/>
    /// Returned spans are valid only until the next call on this scratch instance.<br/>
    /// </summary>
    internal sealed class LibraDexStringScalar8InsertScratch
    {
        private const int InitialCapacity = 512;

        private byte[] bytes = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        private char[] chars = ArrayPool<char>.Shared.Rent(InitialCapacity);

        /// <summary>
        /// Encodes one logical string key into the reusable scratch byte buffer.<br/>
        /// Null and empty keys return stable sentinel spans because they do not need caller-owned mutable storage.<br/>
        /// Non-empty keys are encoded as LibraDex string keys: one string-value marker followed by UTF-8 payload bytes.<br/>
        /// </summary>
        /// <param name="value">The logical string key value.</param>
        /// <returns>A span containing the encoded key bytes until this scratch is reused.</returns>
        internal ReadOnlySpan<byte> EncodeString(string? value)
        {
            if (value is null)
            {
                return EncodedNullKey;
            }

            if (value.Length == 0)
            {
                return EncodedEmptyKey;
            }

            int payloadLength = Encoding.UTF8.GetByteCount(value);
            Span<byte> destination = RentSpan(payloadLength + 1);
            destination[0] = StringValueMarker;
            _ = Encoding.UTF8.GetBytes(value, destination[1..]);
            return destination;
        }

        /// <summary>
        /// Encodes one logical string key as an invariant folded string key without allocating a folded string.<br/>
        /// Characters are lowered into reusable char scratch, then UTF-8 encoded into reusable byte scratch with the ordinary string-value marker.<br/>
        /// </summary>
        /// <param name="value">The logical string key value.</param>
        /// <returns>A span containing the encoded invariant-folded key bytes until this scratch is reused.</returns>
        internal ReadOnlySpan<byte> EncodeInvariantFoldedString(string? value)
        {
            if (value is null)
            {
                return EncodedNullKey;
            }

            if (value.Length == 0)
            {
                return EncodedEmptyKey;
            }

            Span<char> folded = RentCharSpan(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                folded[i] = char.ToLowerInvariant(value[i]);
            }

            int payloadLength = Encoding.UTF8.GetByteCount(folded);
            Span<byte> destination = RentSpan(payloadLength + 1);
            destination[0] = StringValueMarker;
            _ = Encoding.UTF8.GetBytes(folded, destination[1..]);
            return destination;
        }

        /// <summary>
        /// Encodes one raw variable-length key payload into the reusable scratch byte buffer.<br/>
        /// This is used by maintained byte-key projections, such as sort-key projections, to avoid allocating the wrapper key bytes around an already materialized payload.<br/>
        /// </summary>
        /// <param name="key">The raw logical key payload bytes.</param>
        /// <param name="maxPhysicalLength">The maximum encoded key length supported by the target index.</param>
        /// <param name="paramName">The parameter name to report on length validation failures.</param>
        /// <returns>A span containing the encoded key bytes until this scratch is reused.</returns>
        internal ReadOnlySpan<byte> EncodeVarLenKey(ReadOnlySpan<byte> key, int maxPhysicalLength, string paramName)
        {
            if (key.Length > LibraDexVarLenKeyCodec.GetMaxLogicalLength(maxPhysicalLength))
            {
                throw new ArgumentOutOfRangeException(paramName, key.Length, $"Variable-length key payload length must be from 0 to {LibraDexVarLenKeyCodec.GetMaxLogicalLength(maxPhysicalLength)} bytes.");
            }

            if (key.Length == 0)
            {
                Span<byte> empty = RentSpan(1);
                empty[0] = LibraDexVarLenKeyCodec.EmptyMarker;
                return empty;
            }

            Span<byte> destination = RentSpan(key.Length + 1);
            destination[0] = LibraDexVarLenKeyCodec.ValueMarker;
            key.CopyTo(destination[1..]);
            return destination;
        }

        /// <summary>
        /// Ensures the scratch has enough byte capacity for the next encoded key.<br/>
        /// The old rented array is returned before replacing it, keeping one live scratch buffer per active string batch manager.<br/>
        /// </summary>
        /// <param name="length">The required encoded byte length.</param>
        /// <returns>A writable span over the first <paramref name="length"/> bytes of the scratch buffer.</returns>
        private Span<byte> RentSpan(int length)
        {
            if (length > bytes.Length)
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: false);
                bytes = ArrayPool<byte>.Shared.Rent(Math.Max(length, bytes.Length * 2));
            }

            return bytes.AsSpan(0, length);
        }

        /// <summary>
        /// Ensures the scratch has enough character capacity for the next transformed string key.<br/>
        /// The returned span is reused across projection inserts and is valid only until the next character scratch request.<br/>
        /// </summary>
        /// <param name="length">The required character length.</param>
        /// <returns>A writable span over the first <paramref name="length"/> characters of the scratch buffer.</returns>
        private Span<char> RentCharSpan(int length)
        {
            if (length > chars.Length)
            {
                ArrayPool<char>.Shared.Return(chars, clearArray: false);
                chars = ArrayPool<char>.Shared.Rent(Math.Max(length, chars.Length * 2));
            }

            return chars.AsSpan(0, length);
        }
    }

    private sealed class LibraDexStringScalar8ThreadInsertDiagnostics
    {
        private readonly Dictionary<string, LibraDexStringScalar8InsertShapeCounter> counters = new(StringComparer.Ordinal);

        internal void Record(string indexName, string projectionName, VarKeyScalar8InsertOutcome outcome)
        {
            string key = string.Concat(indexName, "#", projectionName);
            ref LibraDexStringScalar8InsertShapeCounter? counter = ref CollectionsMarshal.GetValueRefOrAddDefault(counters, key, out bool exists);
            if (!exists || counter is null)
            {
                counter = new LibraDexStringScalar8InsertShapeCounter(key);
            }

            counter.Record(outcome);
        }

        internal string CreateSummary()
        {
            if (counters.Count == 0)
            {
                return "none";
            }

            StringBuilder builder = new();
            foreach (LibraDexStringScalar8InsertShapeCounter counter in counters.Values.OrderByDescending(static item => item.Attempted))
            {
                if (builder.Length != 0)
                {
                    builder.Append(" | ");
                }

                builder
                    .Append(counter.Name)
                    .Append(": attempts=")
                    .Append(counter.Attempted.ToString("N0", CultureInfo.InvariantCulture))
                    .Append("; split=")
                    .Append(counter.Split.ToString("N0", CultureInfo.InvariantCulture))
                    .Append("; grow=")
                    .Append(counter.Grow.ToString("N0", CultureInfo.InvariantCulture))
                    .Append("; initial=")
                    .Append(counter.Initial.ToString("N0", CultureInfo.InvariantCulture))
                    .Append("; maxExtent=")
                    .Append(counter.MaxTargetExtent.ToString("N0", CultureInfo.InvariantCulture))
                    .Append("; maxDepth=")
                    .Append(counter.MaxRouterDepth.ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    private sealed class LibraDexStringScalar8InsertShapeCounter
    {
        internal LibraDexStringScalar8InsertShapeCounter(string name)
        {
            Name = name;
        }

        internal string Name { get; }

        internal long Attempted { get; private set; }

        internal long Split { get; private set; }

        internal long Grow { get; private set; }

        internal long Initial { get; private set; }

        internal int MaxTargetExtent { get; private set; }

        internal ushort MaxRouterDepth { get; private set; }

        internal void Record(VarKeyScalar8InsertOutcome outcome)
        {
            Attempted++;
            if (outcome.SplitShelf)
            {
                Split++;
            }

            if (outcome.GrewShelf)
            {
                Grow++;
            }

            if (outcome.CreatedInitialShelfRoute)
            {
                Initial++;
            }

            MaxTargetExtent = Math.Max(MaxTargetExtent, outcome.TargetShelfExtentSize);
            MaxRouterDepth = Math.Max(MaxRouterDepth, outcome.TargetRouterDepth);
        }
    }
}

/// <summary>
/// Provides a writer-context-backed concurrent batch for string/scalar8 indexes and their maintained `VS8` projections.<br/>
/// Exact, folded, sort-key, exact-reversed, and folded-reversed projection rows share one writer context, so independent physical shelves can stage concurrently and publish once per batch.<br/>
/// </summary>
public sealed class LibraDexStringScalar8ConcurrentBatch : IDisposable
{
    private readonly LibraDexStringScalar8Index index;
    private LibraDexWriteContext? writer;
    private long attemptedInsertCount;
    private long insertedCount;
    private long attemptedDeleteCount;
    private long deletedCount;
    private long attemptedRekeyCount;
    private long changedRekeyCount;
    private long publishedContextCount;
    private DataKernelCommitTelemetry lastPublishTelemetry;
    private bool completed;

    internal LibraDexStringScalar8ConcurrentBatch(LibraDexStringScalar8Index index)
    {
        this.index = index;
    }

    /// <summary>
    /// Inserts one string key and scalar identity through the concurrent batch.<br/>
    /// Ordinary string keys stage exact and projection rows in the shared `VS8` writer context; null and empty keys use the compact immediate key-state route.<br/>
    /// </summary>
    /// <param name="key">The string key to insert, or null for the null-key sentinel.<br/></param>
    /// <param name="identity">The scalar identity to associate with the key.<br/></param>
    /// <returns>The exact-index insert result.</returns>
    public LibraDexGenericInsertResult Insert(string? key, ulong identity)
    {
        ThrowIfCompleted();
        attemptedInsertCount++;
        LibraDexGenericInsertResult result = InsertCore(key, identity);
        if (result.Inserted)
        {
            insertedCount++;
        }

        return result;
    }

    /// <summary>
    /// Deletes one exact string key and scalar identity through the concurrent batch.<br/>
    /// Ordinary string keys stage exact and projection deletes in the shared `VS8` writer context; null and empty keys use the compact immediate key-state route.<br/>
    /// </summary>
    /// <param name="key">The exact string key to delete, or null for the null-key sentinel.<br/></param>
    /// <param name="identity">The scalar identity paired with the key.<br/></param>
    /// <returns>The exact-index delete result.</returns>
    public LibraDexGenericDeleteResult Delete(string? key, ulong identity)
    {
        ThrowIfCompleted();
        attemptedDeleteCount++;
        LibraDexGenericDeleteResult result = DeleteCore(key, identity);
        if (result.Deleted)
        {
            deletedCount++;
        }

        return result;
    }

    /// <summary>
    /// Moves one scalar identity from an old string key to a new string key through the concurrent batch.<br/>
    /// Rekey is modeled as replacement insert followed by old tuple delete, and the replacement is attempted only after the old exact tuple is known to exist.<br/>
    /// </summary>
    /// <param name="identity">The scalar identity to move.<br/></param>
    /// <param name="oldKey">The old string key.<br/></param>
    /// <param name="newKey">The replacement string key.<br/></param>
    /// <returns>The generic rekey result with replacement and removal legs.</returns>
    public LibraDexGenericRekeyResult Rekey(
        ulong identity,
        string? oldKey,
        string? newKey)
    {
        ThrowIfCompleted();
        attemptedRekeyCount++;
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            return new LibraDexGenericRekeyResult(false, default, default);
        }

        if (!index.ContainsExactTupleForConcurrentBatch(oldKey, identity))
        {
            return new LibraDexGenericRekeyResult(false, default, default);
        }

        LibraDexGenericInsertResult replacement = InsertCore(newKey, identity);
        LibraDexGenericDeleteResult removal = DeleteCore(oldKey, identity);
        if (replacement.Inserted)
        {
            insertedCount++;
        }

        if (removal.Deleted)
        {
            deletedCount++;
            changedRekeyCount++;
        }

        return new LibraDexGenericRekeyResult(removal.Deleted, replacement, removal);
    }

    /// <summary>
    /// Publishes any staged exact and projection shelf work and closes this concurrent batch.<br/>
    /// Topology fallback operations may already have published; this method publishes the final shelf-local context and reports aggregate counters.<br/>
    /// </summary>
    /// <returns>The concurrent batch publication result.</returns>
    public LibraDexConcurrentBatchPublishResult Publish()
    {
        ThrowIfCompleted();
        PublishCurrentWriter();
        completed = true;
        return new LibraDexConcurrentBatchPublishResult(
            attemptedInsertCount,
            insertedCount,
            attemptedDeleteCount,
            deletedCount,
            attemptedRekeyCount,
            changedRekeyCount,
            publishedContextCount,
            LibraDexOperationDiagnostics.FromDataKernel(lastPublishTelemetry));
    }

    /// <summary>
    /// Aborts any unpublished staged shelf-local work and closes this concurrent batch.<br/>
    /// Immediate key-state and topology fallback operations that already published cannot be rolled back by this abort.<br/>
    /// </summary>
    public void Abort()
    {
        ThrowIfCompleted();
        AbortCurrentWriter();
        completed = true;
    }

    /// <summary>
    /// Aborts unpublished staged work when the batch is disposed without an explicit publish or abort.<br/>
    /// </summary>
    public void Dispose()
    {
        if (!completed)
        {
            Abort();
        }
    }

    private LibraDexGenericInsertResult InsertCore(string? key, ulong identity)
    {
        if (LibraDexStringScalar8Index.IsKeyStateForConcurrentBatch(key))
        {
            PublishCurrentWriter();
            return index.InsertKeyStateForConcurrentBatch(key, identity);
        }

        SpinWait wait = default;
        while (true)
        {
            LibraDexWriteContext current = GetOrCreateWriter();
            try
            {
                return index.InsertForConcurrentBatch(current, key, identity);
            }
            catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException)
            {
                PublishCurrentWriter();
                wait.SpinOnce();
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException)
            {
                PublishCurrentWriter();
                wait.SpinOnce();
            }
            catch (InvalidOperationException)
            {
                PublishCurrentWriter();
                LibraDexGenericInsertResult fallback = InsertImmediateFallback(key, identity);
                bool exactPresent = index.ContainsExactTupleForConcurrentBatch(key, identity);
                if (!fallback.Inserted && exactPresent)
                {
                    index.InsertProjectionTuplesFallbackForConcurrentBatch(key, identity);
                    return new LibraDexGenericInsertResult(
                        true,
                        false,
                        default,
                        default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.SerializedFallback
                    };
                }

                if (fallback.Inserted)
                {
                    return fallback;
                }

                return fallback;
            }
        }
    }

    private LibraDexGenericDeleteResult DeleteCore(string? key, ulong identity)
    {
        if (LibraDexStringScalar8Index.IsKeyStateForConcurrentBatch(key))
        {
            PublishCurrentWriter();
            bool keyStateDeleted = index.DeleteKeyStateForConcurrentBatch(key, identity);
            return new LibraDexGenericDeleteResult(keyStateDeleted, default);
        }

        SpinWait wait = default;
        while (true)
        {
            LibraDexWriteContext current = GetOrCreateWriter();
            try
            {
                return index.DeleteForConcurrentBatch(current, key, identity);
            }
            catch (LibraDexWriteContextVarKeyScalar8ShelfOwnershipException)
            {
                PublishCurrentWriter();
                wait.SpinOnce();
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException)
            {
                PublishCurrentWriter();
                wait.SpinOnce();
            }
            catch (InvalidOperationException)
            {
                PublishCurrentWriter();
                bool fallbackDeleted = DeleteImmediateFallback(key, identity);
                bool exactAbsent = !index.ContainsExactTupleForConcurrentBatch(key, identity);
                if (fallbackDeleted ||
                    exactAbsent)
                {
                    index.DeleteProjectionTuplesFallbackForConcurrentBatch(key, identity);
                }

                bool deleted = fallbackDeleted || exactAbsent;
                return new LibraDexGenericDeleteResult(
                    deleted,
                    default)
                {
                    QueuedInsertPath = deleted
                        ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                        : Scalar8Scalar8QueuedInsertPath.None
                };
            }
        }
    }

    private LibraDexGenericInsertResult InsertImmediateFallback(string? key, ulong identity)
    {
        SpinWait wait = default;
        while (true)
        {
            try
            {
                return index.Insert(key, identity);
            }
            catch (InvalidOperationException exception) when (IsImmediateDurabilityBatchConflict(exception))
            {
                wait.SpinOnce();
            }
        }
    }

    private bool DeleteImmediateFallback(string? key, ulong identity)
    {
        SpinWait wait = default;
        while (true)
        {
            try
            {
                return index.Delete(key, identity);
            }
            catch (InvalidOperationException exception) when (IsImmediateDurabilityBatchConflict(exception))
            {
                wait.SpinOnce();
            }
        }
    }

    private static bool IsImmediateDurabilityBatchConflict(InvalidOperationException exception)
    {
        return exception.Message.StartsWith(
            "Immediate LibraDex string mutation cannot run while another session durability batch is active",
            StringComparison.Ordinal);
    }

    private LibraDexWriteContext GetOrCreateWriter()
    {
        writer ??= index.BeginConcurrentBatchContext();
        return writer;
    }

    private void PublishCurrentWriter()
    {
        LibraDexWriteContext? current = writer;
        if (current is null)
        {
            return;
        }

        lastPublishTelemetry = index.PublishConcurrentBatchContext(current);
        publishedContextCount++;
        writer = null;
    }

    private void AbortCurrentWriter()
    {
        LibraDexWriteContext? current = writer;
        if (current is null)
        {
            return;
        }

        index.AbortConcurrentBatchContext(current);
        writer = null;
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The LibraDex string concurrent batch has already completed.");
        }
    }
}
