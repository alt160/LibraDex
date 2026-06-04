using System.Buffers;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Provides the first generic public wrapper over routed fixed-scalar LibraDex indexes.<br/>
/// The wrapper owns a concrete physical shape selected from `TKey`, `TIdentity`, and any required `byte[]` scalar-width options while keeping the call site in familiar .NET generic form.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexIndex<TKey, TIdentity> : IIndex, IIdentityPrimitiveExecutor, IIdentityPrimitiveMutator, IIdentityPrimitiveTupleExecutor, IIdentityExactTupleMutator, IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly LibraDexGenericScalarShape shape;
    private readonly bool ownsSession;
    private readonly IndexKeys keyContract;
    private readonly Catalog? catalog;
    private readonly LibraDexIndex<TKey, TIdentity>? exactReversedProjection;
    private readonly DateTimeKeyEncoding dateTimeKeyEncoding;
    private bool disposed;

    internal LibraDexIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        LibraDexGenericScalarShape shape,
        bool ownsSession,
        IndexKeys keyContract = IndexKeys.NonUnique,
        Catalog? catalog = null,
        string group = "",
        CatalogIndexKeyFamily keyFamily = CatalogIndexKeyFamily.Unknown,
        CatalogIndexIdentityFamily identityFamily = CatalogIndexIdentityFamily.Unknown,
        LibraDexIndexShapeSpec? logicalShape = null,
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = null,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt)
    {
        this.session = session;
        this.shape = shape;
        this.ownsSession = ownsSession;
        this.keyContract = keyContract;
        this.catalog = catalog;
        Group = group;
        KeyFamily = keyFamily;
        IdentityFamily = identityFamily;
        LogicalShape = logicalShape;
        this.exactReversedProjection = exactReversedProjection;
        this.dateTimeKeyEncoding = logicalShape?.DateTimeKeyEncoding ?? dateTimeKeyEncoding;
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        BatchManager = new IndexBatchManager<TKey, TIdentity>(this);
        Stats = new LibraDexIndexStats<TKey, TIdentity>(this);
        Maintenance = new LibraDexIndexMaintenance<TKey, TIdentity>(this);
    }

    /// <summary>
    /// Gets the fixed index-directory slot used to resolve this runtime index.<br/>
    /// Slot identity remains the primary create/open selector until richer name lookup is deliberately widened.<br/>
    /// </summary>
    public int SlotIndex { get; }

    /// <summary>
    /// Gets the index name stored in the fixed index-directory slot.<br/>
    /// Names are metadata labels in this slice; slot identity remains the primary create/open selector.<br/>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the identity group name recorded for this index, or an empty string for older ungrouped scaffold entries.<br/>
    /// The value is metadata for public composition and discovery; physical routing remains anchored by the directory slot and root-router offset.<br/>
    /// </summary>
    public string Group { get; }

    /// <summary>
    /// Gets the CLR key type accepted by this index.<br/>
    /// This supports strict non-generic programmatic callers without forcing every layer to carry `TKey` explicitly.<br/>
    /// </summary>
    public Type KeyType => typeof(TKey);

    /// <summary>
    /// Gets the CLR identity type associated with this index.<br/>
    /// </summary>
    public Type IdentityType => typeof(TIdentity);

    /// <summary>
    /// Gets the logical key family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexKeyFamily KeyFamily { get; }

    /// <summary>
    /// Gets the logical identity family recorded for this index.<br/>
    /// </summary>
    public CatalogIndexIdentityFamily IdentityFamily { get; }

    /// <summary>
    /// Gets the logical shape descriptor used to create this runtime index, when available.<br/>
    /// Shape descriptors are the planner-facing counterpart to Abraxas selector/index metadata; catalog metadata version 2 can rehydrate them after reopen.<br/>
    /// </summary>
    public LibraDexIndexShapeSpec? LogicalShape { get; }

    /// <summary>
    /// Gets the DateTime-like key encoding contract used by this index.<br/>
    /// Date conditions are builder-agnostic and execution consults this value to choose calendar-SDT direct fields or precision-SDT derived component behavior.<br/>
    /// </summary>
    public DateTimeKeyEncoding DateTimeKeyEncoding => dateTimeKeyEncoding;

    /// <summary>
    /// Encodes one public scalar key with this index's DateTime-like binary contract.<br/>
    /// Non-date key types ignore the DateTime encoding selector through the generic codec's normal type dispatch.<br/>
    /// </summary>
    /// <param name="key">The public key value to encode.</param>
    /// <returns>The encoded scalar key lane.</returns>
    internal ulong EncodeKey8(TKey key)
    {
        return LibraDexGenericScalarCodec<TKey>.Encode8(key, dateTimeKeyEncoding);
    }

    /// <summary>
    /// Decodes one scalar key lane with this index's DateTime-like binary contract.<br/>
    /// Precision-SDT indexes recover sub-millisecond ticks while calendar-SDT indexes recover the calendar-optimized quantum.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded scalar key lane.</param>
    /// <returns>The decoded public key value.</returns>
    internal TKey DecodeKey8(ulong encodedKey)
    {
        return LibraDexGenericScalarCodec<TKey>.Decode8(encodedKey, dateTimeKeyEncoding);
    }

    /// <summary>
    /// Computes the previous representable public key under this index's DateTime-like binary contract.<br/>
    /// DateTime-like keys use the selected SDT quantum while other scalar domains keep their existing codec rules.<br/>
    /// </summary>
    /// <param name="key">The current public key.</param>
    /// <param name="previousKey">The previous public key when one exists.</param>
    /// <returns><see langword="true"/> when a previous key exists.</returns>
    private bool TryGetPreviousKeyValue(TKey key, out TKey previousKey)
    {
        return LibraDexGenericScalarCodec<TKey>.TryGetPreviousValue(key, dateTimeKeyEncoding, out previousKey);
    }

    /// <summary>
    /// Computes the next representable public key under this index's DateTime-like binary contract.<br/>
    /// DateTime-like keys use the selected SDT quantum while other scalar domains keep their existing codec rules.<br/>
    /// </summary>
    /// <param name="key">The current public key.</param>
    /// <param name="nextKey">The next public key when one exists.</param>
    /// <returns><see langword="true"/> when a next key exists.</returns>
    private bool TryGetNextKeyValue(TKey key, out TKey nextKey)
    {
        return LibraDexGenericScalarCodec<TKey>.TryGetNextValue(key, dateTimeKeyEncoding, out nextKey);
    }

    /// <summary>
    /// Gets the fixed encoded key byte width when this generic handle stores raw fixed-width byte-array keys.<br/>
    /// Binary suffix projection planning uses this to turn a suffix into exact reversed-prefix range bounds without exposing shape slots to callers.<br/>
    /// </summary>
    public int? FixedKeyByteWidth => typeof(TKey) == typeof(byte[])
        ? shape switch
        {
            LibraDexGenericScalarShape.SS88 or LibraDexGenericScalarShape.SS816 => 8,
            LibraDexGenericScalarShape.SS168 or LibraDexGenericScalarShape.SS1616 => 16,
            LibraDexGenericScalarShape.FS328 or LibraDexGenericScalarShape.FS3216 => 32,
            _ => null
        }
        : null;

    /// <summary>
    /// Gets the root-router file offset for this typed index.<br/>
    /// Exposing this as diagnostics keeps multi-index validation visible without exposing mutation internals.<br/>
    /// </summary>
    public long RootRouterOffset { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the index-wide key uniqueness contract selected when the index was opened or created through the public facade.<br/>
    /// `Unique` rejects duplicate keys at insert time; `NonUnique` allows multiple identities to share the same key.<br/>
    /// </summary>
    public IndexKeys KeyContract => keyContract;

    /// <summary>
    /// Gets the index-global batch manager used to control durability cadence without replacing the index mutation surface.<br/>
    /// When enabled, normal insert calls stage writes until `Commit` or `CommitAndDisable` publishes them; the manager is not a SQL transaction and does not provide rollback semantics.<br/>
    /// </summary>
    public IndexBatchManager<TKey, TIdentity> BatchManager { get; }

    /// <summary>
    /// Gets the index-global batch controller used to control durability cadence without replacing the index mutation surface.<br/>
    /// This is the preferred short public spelling for handwritten code; `BatchManager` remains an alias for compatibility while the public scaffold settles.<br/>
    /// </summary>
    public IndexBatchManager<TKey, TIdentity> Batch => BatchManager;

    /// <summary>
    /// Gets passive stats, marker/delta helpers, and explicit layout snapshots for this index.<br/>
    /// Expensive physical layout calculations should remain explicit snapshot calls rather than hidden work on ordinary properties.<br/>
    /// </summary>
    public LibraDexIndexStats<TKey, TIdentity> Stats { get; }

    /// <summary>
    /// Gets explicit index-level maintenance operations.<br/>
    /// Route optimization, validation, repacking, and cache work should be requested deliberately rather than hidden inside ordinary reads or writes.<br/>
    /// </summary>
    public LibraDexIndexMaintenance<TKey, TIdentity> Maintenance { get; }

    /// <summary>
    /// Gets a typed condition root for this opened index.<br/>
    /// The index supplies its identity group, index name, and key type so handwritten code can build reusable conditions without repeating `ForGroup(...).Index(...).AsType` ceremony.<br/>
    /// </summary>
    public LibraDexIndexWhere<TKey, TIdentity> Where => new(this);

    internal LibraDexFileSession Session => session;

    internal LibraDexGenericScalarShape Shape => shape;

    internal Catalog? Catalog => catalog;

    /// <summary>
    /// Inserts the reversed-key companion tuple into the maintained exact reversed projection when this index owns one.<br/>
    /// The caller supplies the active durability batch so the primary and projection writes publish at the same commit boundary.<br/>
    /// </summary>
    /// <param name="key">The original forward key supplied to the logical index.</param>
    /// <param name="identity">The identity associated with the key.</param>
    /// <param name="durabilityBatch">The active durability batch shared with the primary write.</param>
    internal void InsertExactReversedProjection(TKey key, TIdentity identity, LibraDexFileSessionDurabilityBatch durabilityBatch)
    {
        if (exactReversedProjection is null)
        {
            return;
        }

        TKey reversedKey = CreateExactReversedProjectionKey(key);
        var sharedBatch = new LibraDexBatch<TKey, TIdentity>(exactReversedProjection, durabilityBatch, ownsDurabilityBatch: false);
        _ = sharedBatch.Insert(reversedKey, identity);
    }

    /// <summary>
    /// Starts a typed generic durability batch for bulk mutation.<br/>
    /// Batch inserts use developer-facing generic values and defer lower-level commit requests until the returned batch commits or aborts.<br/>
    /// Write intent is an optional optimization hint and `default` preserves the normal shape behavior.<br/>
    /// </summary>
    /// <param name="writeIntent">Optional coarse write-pattern hints for the batch.</param>
    /// <returns>A typed batch object that accepts generic inserts and controls the durability publication boundary.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another batch is already active on the owning session.</exception>
    public LibraDexBatch<TKey, TIdentity> BeginBatch(LibraDexWriteIntent writeIntent = default)
    {
        ThrowIfDisposed();
        if (BatchManager.IsEnabled)
        {
            throw new InvalidOperationException("The index BatchManager is already enabled; use BatchManager.Commit or BatchManager.CommitAndDisable to control the active batch.");
        }

        return new LibraDexBatch<TKey, TIdentity>(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Inserts one typed key and typed identity into this index.<br/>
    /// Values are encoded according to the physical shape selected at create/open time, then routed through the matching fixed-scalar storage path.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.</param>
    /// <param name="identity">The typed identity value associated with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        ThrowIfDisposed();
        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return Insert(keyState, identity);
        }

        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.Insert(this, key, identity);
        }

        if (BatchManager.IsEnabled)
        {
            return BatchManager.Insert(key, identity);
        }

        using LibraDexBatch<TKey, TIdentity> batch = BeginBatch();
        LibraDexGenericInsertResult result = batch.Insert(key, identity);
        _ = batch.Commit();
        return result;
    }

    /// <summary>
    /// Adds one typed key and typed identity to this index.<br/>
    /// This is the preferred public spelling for ordinary index population; it delegates to <see cref="Insert(TKey, TIdentity)"/> so batching, duplicate-key behavior, telemetry, and routing remain identical.<br/>
    /// </summary>
    /// <param name="key">The typed key value to add.</param>
    /// <param name="identity">The typed identity value associated with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    public LibraDexGenericInsertResult Add(TKey key, TIdentity identity)
    {
        return Insert(key, identity);
    }

    /// <summary>
    /// Inserts one identity onto this scalar index's metadata-backed null-key route.<br/>
    /// This is the typed public write spelling for scalar key families where null is a key state rather than a CLR scalar value.<br/>
    /// Use <see cref="ScalarNull.Null"/> as the only accepted state; <see cref="ScalarNull.NonNull"/> is a predicate state and should be written with a concrete scalar key through <see cref="Insert(TKey, TIdentity)"/>.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to write; only <see cref="ScalarNull.Null"/> is accepted.</param>
    /// <param name="identity">The typed identity value associated with the null key state.</param>
    /// <returns>The insert result plus commit telemetry when available.</returns>
    public LibraDexGenericInsertResult Insert(ScalarNull keyState, TIdentity identity)
    {
        ThrowIfDisposed();
        if (keyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Only ScalarNull.Null is a concrete scalar key state for insertion.");
        }

        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out _) == true)
        {
            throw new NotSupportedException("Scalar null route inserts are not connected to identity-group batch publication yet.");
        }

        if (BatchManager.IsEnabled)
        {
            throw new NotSupportedException("Scalar null route inserts are not connected to index batch publication yet.");
        }

        return InsertScalarNullIdentity(identity);
    }

    /// <summary>
    /// Adds one identity onto this scalar index's metadata-backed null-key route.<br/>
    /// This is the short spelling counterpart to <see cref="Insert(ScalarNull, TIdentity)"/> and preserves the same route semantics.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to write; only <see cref="ScalarNull.Null"/> is accepted.</param>
    /// <param name="identity">The typed identity value associated with the null key state.</param>
    /// <returns>The insert result plus commit telemetry when available.</returns>
    public LibraDexGenericInsertResult Add(ScalarNull keyState, TIdentity identity)
    {
        return Insert(keyState, identity);
    }

    /// <summary>
    /// Inserts one identity onto this binary index's metadata-backed null or empty key-state route.<br/>
    /// This is the typed public write spelling for `byte[]` key families where null and empty are key states rather than ordinary fixed-width byte values.<br/>
    /// Use <see cref="NullKey.Null"/> or <see cref="NullKey.Empty"/> as concrete write states; <see cref="NullKey.NullOrEmpty"/> is a predicate state and is not a single writable key.<br/>
    /// </summary>
    /// <param name="keyState">The binary key state to write; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <param name="identity">The typed identity value associated with the key state.</param>
    /// <returns>The insert result plus commit telemetry when available.</returns>
    public LibraDexGenericInsertResult Insert(NullKey keyState, TIdentity identity)
    {
        ThrowIfDisposed();
        EnsureNullKeyRouteSupported();
        if (keyState == NullKey.NullOrEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "NullKey.NullOrEmpty is a predicate state and cannot be inserted as one concrete key.");
        }

        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out _) == true)
        {
            throw new NotSupportedException("NullKey route inserts are not connected to identity-group batch publication yet.");
        }

        if (BatchManager.IsEnabled)
        {
            throw new NotSupportedException("NullKey route inserts are not connected to index batch publication yet.");
        }

        return InsertNullKeyIdentity(keyState, identity);
    }

    /// <summary>
    /// Adds one identity onto this binary index's metadata-backed null or empty key-state route.<br/>
    /// This is the short spelling counterpart to <see cref="Insert(NullKey, TIdentity)"/> and preserves the same route semantics.<br/>
    /// </summary>
    /// <param name="keyState">The binary key state to write; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <param name="identity">The typed identity value associated with the key state.</param>
    /// <returns>The insert result plus commit telemetry when available.</returns>
    public LibraDexGenericInsertResult Add(NullKey keyState, TIdentity identity)
    {
        return Insert(keyState, identity);
    }

    /// <summary>
    /// Materializes identities for a completed condition rooted at this index.<br/>
    /// This convenience is intentionally single-index scoped: composed cross-index conditions should be executed from the owning identity group so every referenced index can be resolved by name.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="ordering">The requested identity ordering contract.</param>
    /// <param name="deduplication">The requested duplicate identity policy.</param>
    /// <param name="skip">The number of matching identities to skip.</param>
    /// <param name="take">The optional maximum number of identities to return.</param>
    /// <param name="bookmark">The optional continuation bookmark.</param>
    /// <returns>A typed list of matching identities.</returns>
    public IReadOnlyList<TIdentity> GetIdentities(
        LibraDexConditionEndCondition condition,
        IdentityResultOrdering ordering = IdentityResultOrdering.PlanNatural,
        IdentityDeduplication deduplication = IdentityDeduplication.Distinct,
        int skip = 0,
        int? take = null,
        LibraDexBookmark? bookmark = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            ResolveOwnIndex,
            ResolveOwnProjectionIndex);
        return criterion.IDsWith(ordering, deduplication, skip, take, bookmark).ToList<TIdentity>();
    }

    /// <summary>
    /// Inserts one non-generic key/identity tuple after validating the runtime values against this index's persisted CLR type contract.<br/>
    /// The method then delegates to the typed insert path so batching, telemetry, duplicate-key behavior, and physical routing remain identical to `Insert(TKey, TIdentity)`.<br/>
    /// </summary>
    /// <param name="key">The runtime key value to insert.</param>
    /// <param name="identity">The runtime identity value to associate with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    LibraDexGenericInsertResult IIndex.Insert(object? key, object identity)
    {
        if (key is null || key == DBNull.Value)
        {
            if (SupportsNullKeyRoute())
            {
                return Insert(NullKey.Null, RequireObjectIdentity(identity, nameof(identity)));
            }

            return Insert(ScalarNull.Null, RequireObjectIdentity(identity, nameof(identity)));
        }

        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return Insert(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        return Insert(RequireObjectKey(key, nameof(key)), RequireObjectIdentity(identity, nameof(identity)));
    }

    bool IIndex.Delete(object? key, object identity)
    {
        ThrowIfDisposed();
        if (key is null || key == DBNull.Value)
        {
            if (SupportsNullKeyRoute())
            {
                return DeleteNullKeyIdentity(NullKey.Null, RequireObjectIdentity(identity, nameof(identity)));
            }

            return DeleteScalarNullIdentity(RequireObjectIdentity(identity, nameof(identity)));
        }

        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return DeleteNullKeyIdentity(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        return DeleteExactTuple(RequireObjectKey(key, nameof(key)), RequireObjectIdentity(identity, nameof(identity)));
    }

    bool IIndex.Rekey(object identity, object? oldKey, object? newKey)
    {
        ThrowIfDisposed();
        TIdentity typedIdentity = RequireObjectIdentity(identity, nameof(identity));
        if (oldKey is null || oldKey == DBNull.Value)
        {
            if (newKey is null || newKey == DBNull.Value)
            {
                return false;
            }

            return Rekey(typedIdentity, ScalarNull.Null, RequireObjectKey(newKey, nameof(newKey)));
        }

        if (newKey is null || newKey == DBNull.Value)
        {
            return Rekey(typedIdentity, RequireObjectKey(oldKey, nameof(oldKey)), ScalarNull.Null);
        }

        TKey typedOldKey = RequireObjectKey(oldKey, nameof(oldKey));
        TKey typedNewKey = RequireObjectKey(newKey, nameof(newKey));
        if (TupleComponentEquals(typedOldKey, typedNewKey))
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
                throw new InvalidOperationException("Rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        bool deleted = DeleteExactTuple(typedOldKey, typedIdentity);
        if (deleted)
        {
            RecordRekey();
        }

        return deleted;
    }

    long IIndex.Rekey(object identity, object? newKey)
    {
        ThrowIfDisposed();
        TIdentity typedIdentity = RequireObjectIdentity(identity, nameof(identity));
        if (newKey is null || newKey == DBNull.Value)
        {
            long changedToNull = 0;
            List<TKey> oldNonNullKeys = new();
            using (LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader())
            {
                while (reader.TryReadNext(out TKey key, out TIdentity currentIdentity))
                {
                    if (TupleComponentEquals(currentIdentity, typedIdentity))
                    {
                        oldNonNullKeys.Add(key);
                    }
                }
            }

            for (int i = 0; i < oldNonNullKeys.Count; i++)
            {
                if (Rekey(typedIdentity, oldNonNullKeys[i], ScalarNull.Null))
                {
                    changedToNull++;
                }
            }

            return changedToNull;
        }

        TKey typedNewKey = RequireObjectKey(newKey, nameof(newKey));
        List<TKey> oldKeys = new();
        using (LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader())
        {
            while (reader.TryReadNext(out TKey key, out TIdentity currentIdentity))
            {
                if (TupleComponentEquals(currentIdentity, typedIdentity))
                {
                    oldKeys.Add(key);
                }
            }
        }

        long changed = 0;
        for (int i = 0; i < oldKeys.Count; i++)
        {
            if (((IIndex)this).Rekey(typedIdentity!, oldKeys[i]!, typedNewKey!))
            {
                changed++;
            }
        }

        if (ContainsScalarNullIdentity(typedIdentity) &&
            Rekey(typedIdentity, ScalarNull.Null, typedNewKey))
        {
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Prepares a strict non-generic membership set after validating all runtime keys against this index's key type.<br/>
    /// </summary>
    /// <param name="keys">The runtime key values to prepare.</param>
    /// <returns>A prepared non-generic key set.</returns>
    LibraDexPreparedObjectSet IIndex.PrepareInSet(IEnumerable<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys is ISet<object> objectSet)
        {
            foreach (object key in objectSet)
            {
                _ = RequireObjectKey(key, nameof(keys));
            }

            IEqualityComparer<object>? comparer = objectSet is HashSet<object> hashSet ? hashSet.Comparer : null;
            return new LibraDexPreparedObjectSet(typeof(TKey), values: null, objectSet, comparer);
        }

        object[] values = keys.Select(key => (object)RequireObjectKey(key, nameof(keys))!).ToArray();
        return new LibraDexPreparedObjectSet(typeof(TKey), values);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteIdentityPrimitive(request);
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitive(request);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return CountIdentityPrimitive(request);
    }

    LibraDexIdentityMutationResult IIdentityPrimitiveMutator.DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return DeleteIdentityPrimitive(request);
    }

    IReadOnlyList<LibraDexObjectTuple> IIdentityPrimitiveTupleExecutor.ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteTuplePrimitive(request);
    }

    bool IIdentityExactTupleMutator.ContainsExactTuple(object? key, object identity)
    {
        if (key is null || key == DBNull.Value)
        {
            if (SupportsNullKeyRoute())
            {
                return ContainsNullKeyIdentity(NullKey.Null, RequireObjectIdentity(identity, nameof(identity)));
            }

            return ContainsScalarNullIdentity(RequireObjectIdentity(identity, nameof(identity)));
        }

        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return ContainsNullKeyIdentity(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        return ContainsExactTuple(RequireObjectKey(key, nameof(key)), RequireObjectIdentity(identity, nameof(identity)));
    }

    bool IIdentityExactTupleMutator.DeleteExactTuple(object? key, object identity)
    {
        if (key is null || key == DBNull.Value)
        {
            if (SupportsNullKeyRoute())
            {
                return DeleteNullKeyIdentity(NullKey.Null, RequireObjectIdentity(identity, nameof(identity)));
            }

            return DeleteScalarNullIdentity(RequireObjectIdentity(identity, nameof(identity)));
        }

        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return DeleteNullKeyIdentity(keyState, RequireObjectIdentity(identity, nameof(identity)));
        }

        return DeleteExactTuple(RequireObjectKey(key, nameof(key)), RequireObjectIdentity(identity, nameof(identity)));
    }

    private IReadOnlyList<object> ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => MaterializeAllIdentityObjects(request.TakeLimit),
            LibraDexCriteriaKind.Find => MaterializeIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.Between => MaterializeIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.Before => MaterializeIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.AtOrBefore => MaterializeIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.After => MaterializeIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.AtOrAfter => MaterializeIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit),
            LibraDexCriteriaKind.In => MaterializeMembershipIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.InSet => MaterializeMembershipIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.MultiRange => MaterializeMultiRangeIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.StructuredComponent => MaterializeStructuredComponentIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.GuidPattern => MaterializeGuidPatternIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.BinaryPattern => MaterializeBinaryPatternIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.BinaryTypedSlice => MaterializeBinaryTypedSliceIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.Bitmask => MaterializeBitmaskIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.ScalarNull => MaterializeScalarNullIdentityObjects(request.Values, request.TakeLimit),
            LibraDexCriteriaKind.KeyState => MaterializeNullKeyIdentityObjects(request.Values, request.TakeLimit),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity execution is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Streams identities for one normalized condition-builder primitive through the generic range-reader path.<br/>
    /// This is the internal non-materializing equivalent of <see cref="ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest)"/> and is used by plan-natural condition iteration.<br/>
    /// Membership requests preserve supplied key order while honoring the request take limit across the whole key fan-out.<br/>
    /// </summary>
    /// <param name="request">The primitive request to execute.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                foreach (object identity in IterateAllIdentityObjects(request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Find:
                foreach (object identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Between:
                foreach (object identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Before:
                foreach (object identity in IterateIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrBefore:
                foreach (object identity in IterateIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.After:
                foreach (object identity in IterateIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrAfter:
                foreach (object identity in IterateIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (object identity in IterateMembershipIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.MultiRange:
                foreach (object identity in IterateMultiRangeIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.StructuredComponent:
                foreach (object identity in IterateStructuredComponentIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.GuidPattern:
                foreach (object identity in IterateGuidPatternIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.BinaryPattern:
                foreach (object identity in IterateBinaryPatternIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.BinaryTypedSlice:
                foreach (object identity in IterateBinaryTypedSliceIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Bitmask:
                foreach (object identity in IterateBitmaskIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.ScalarNull:
                foreach (object identity in IterateScalarNullIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.KeyState:
                foreach (object identity in IterateNullKeyIdentityObjects(request.Values, request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            default:
                throw new NotSupportedException($"{request.CriteriaKind} identity iteration is not connected to physical readers yet.");
        }
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteAllIdentities()
    {
        ThrowIfDisposed();
        return MaterializeAllIdentityObjects();
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityUniverse()
    {
        return IterateIdentityUniverse();
    }

    /// <summary>
    /// Streams the identity universe visible to this index handle.<br/>
    /// For grouped catalog indexes, the universe is the de-duplicated union of `All` identities across indexes that declare the same identity group in catalog metadata.<br/>
    /// For ungrouped or standalone handles, the method falls back to this index's own `All` primitive because no broader identity source is currently known.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateIdentityUniverse()
    {
        ThrowIfDisposed();
        if (catalog is null || string.IsNullOrWhiteSpace(Group))
        {
            foreach (object identity in IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
            {
                yield return identity;
            }

            yield break;
        }

        HashSet<object> seen = new();
        CatalogIndexInfo[] indexes = catalog.Indexes[Group].List();
        for (int i = 0; i < indexes.Length; i++)
        {
            IIndex openedIndex = indexes[i].SlotIndex == SlotIndex
                ? this
                : catalog.OpenIndex(indexes[i]);
            if (openedIndex is not IIdentityPrimitiveExecutor primitiveExecutor)
            {
                throw new NotSupportedException("A grouped index does not expose the internal identity primitive executor required for identity-universe enumeration.");
            }

            using IDisposable? openedDisposable = ReferenceEquals(openedIndex, this)
                ? null
                : openedIndex as IDisposable;
            foreach (object identity in primitiveExecutor.IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
            {
                if (seen.Add(identity))
                {
                    yield return identity;
                }
            }
        }
    }

    private long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => CountAllIdentityObjects(),
            LibraDexCriteriaKind.Find => CountIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.Between => CountIdentityObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)))),
            LibraDexCriteriaKind.Before => CountIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrBefore => CountIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.After => CountIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrAfter => CountIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.In => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.InSet => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.MultiRange => CountMultiRangeIdentityObjects(request.Values),
            LibraDexCriteriaKind.StructuredComponent => CountStructuredComponentIdentityObjects(request.Values),
            LibraDexCriteriaKind.GuidPattern => CountGuidPatternIdentityObjects(request.Values),
            LibraDexCriteriaKind.BinaryPattern => CountBinaryPatternIdentityObjects(request.Values),
            LibraDexCriteriaKind.BinaryTypedSlice => CountBinaryTypedSliceIdentityObjects(request.Values),
            LibraDexCriteriaKind.Bitmask => CountBitmaskIdentityObjects(request.Values),
            LibraDexCriteriaKind.ScalarNull => CountScalarNullIdentityObjects(request.Values),
            LibraDexCriteriaKind.KeyState => CountNullKeyIdentityObjects(request.Values),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity count is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Deletes tuples matched by one normalized condition primitive when the selected generic physical shape has a connected shelf-local delete path.<br/>
    /// This keeps condition-driven mutation on the same primitive spine as retrieval while exposing missing physical delete implementations as explicit shape gaps.<br/>
    /// Connected fixed scalar shapes compact and rewrite only affected shelves for the requested key range.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>A tuple-oriented mutation result.</returns>
    private LibraDexIdentityMutationResult DeleteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        long deleted = request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => DeleteAllIdentityPrimitive(),
            LibraDexCriteriaKind.Find => DeleteIdentityPrimitiveRange(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))),
            LibraDexCriteriaKind.Between => DeleteIdentityPrimitiveRange(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))),
            LibraDexCriteriaKind.Before => DeleteBeforeIdentityPrimitive(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))),
            LibraDexCriteriaKind.AtOrBefore => DeleteIdentityPrimitiveRange(GetLowerFullKeyBound(), RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))),
            LibraDexCriteriaKind.After => DeleteAfterIdentityPrimitive(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))),
            LibraDexCriteriaKind.AtOrAfter => DeleteIdentityPrimitiveRange(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), GetUpperFullKeyBound()),
            LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet => DeleteMembershipIdentityPrimitive(request.Values),
            LibraDexCriteriaKind.MultiRange => DeleteMultiRangeIdentityPrimitive(request.Values),
            LibraDexCriteriaKind.ScalarNull => DeleteScalarNullIdentityPrimitive(request.Values),
            LibraDexCriteriaKind.KeyState => DeleteNullKeyIdentityPrimitive(request.Values),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity deletion is not connected to physical mutation yet.")
        };

        return new LibraDexIdentityMutationResult(
            LibraDexCriteriaMutationKind.Delete,
            deleted,
            deleted,
            new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: deleted, RowsReturned: deleted));
    }

    /// <summary>
    /// Materializes key/identity tuples matched by one normalized condition primitive.<br/>
    /// Criteria-scoped re-key uses this tuple-oriented path because the replacement must delete each old physical tuple exactly, not just the key range that discovered it.<br/>
    /// Membership and multirange requests fan out through the same range readers used by identity projection so primitive routing remains aligned with retrieval.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>The matching key/identity tuples as object values.</returns>
    private IReadOnlyList<LibraDexObjectTuple> ExecuteTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => MaterializeTupleObjects(OpenAllRangeReader()),
            LibraDexCriteriaKind.Find => MaterializeTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.Between => MaterializeTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)))),
            LibraDexCriteriaKind.Before => MaterializeTupleObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrBefore => MaterializeTupleObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.After => MaterializeTupleObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.AtOrAfter => MaterializeTupleObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)))),
            LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet => MaterializeMembershipTupleObjects(request.Values),
            LibraDexCriteriaKind.MultiRange => MaterializeMultiRangeTupleObjects(request.Values),
            LibraDexCriteriaKind.Bitmask => MaterializeBitmaskTupleObjects(request.Values),
            LibraDexCriteriaKind.ScalarNull => MaterializeScalarNullTupleObjects(request.Values),
            LibraDexCriteriaKind.KeyState => MaterializeNullKeyTupleObjects(request.Values),
            _ => throw new NotSupportedException($"{request.CriteriaKind} tuple execution is not connected to physical readers yet.")
        };
    }

    private TKey GetLowerFullKeyBound()
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        return lowerKey;
    }

    private TKey GetUpperFullKeyBound()
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        return upperKey;
    }

    private long DeleteBeforeIdentityPrimitive(TKey key)
    {
        return TryGetPreviousKey(key, out TKey upperKey)
            ? DeleteIdentityPrimitiveRange(GetLowerFullKeyBound(), upperKey)
            : 0;
    }

    /// <summary>
    /// Deletes every identity visible through this index's all-scan contract.<br/>
    /// Scalar null-route identities are removed before ordinary value-route tuples so mutation semantics match null-first enumeration.<br/>
    /// </summary>
    /// <returns>The number of deleted identities or tuples.</returns>
    private long DeleteAllIdentityPrimitive()
    {
        long deleted = 0;
        if (SupportsScalarNullKeyRoute())
        {
            deleted += DeleteScalarNullRouteIdentities();
        }
        else if (SupportsNullKeyRoute())
        {
            deleted += DeleteNullKeyRouteIdentities(NullKey.Null);
            deleted += DeleteNullKeyRouteIdentities(NullKey.Empty);
        }

        deleted += DeleteIdentityPrimitiveRange(GetLowerFullKeyBound(), GetUpperFullKeyBound());
        return deleted;
    }

    private long DeleteAfterIdentityPrimitive(TKey key)
    {
        return TryGetNextKey(key, out TKey lowerKey)
            ? DeleteIdentityPrimitiveRange(lowerKey, GetUpperFullKeyBound())
            : 0;
    }

    private long DeleteMembershipIdentityPrimitive(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return DeleteMembershipIdentityPrimitive(values.Select(RequireNonNullMembershipValue));
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity deletion requires an enumerable key value or prepared set.")
        };

        return DeleteMembershipIdentityPrimitive(keys);
    }

    private long DeleteMembershipIdentityPrimitive(IEnumerable<object> keys)
    {
        long deleted = 0;
        foreach (object keyValue in keys)
        {
            TKey key = RequireObjectKey(keyValue, nameof(keys));
            deleted += DeleteIdentityPrimitiveRange(key, key);
        }

        return deleted;
    }

    private long DeleteMultiRangeIdentityPrimitive(IReadOnlyList<object?> values)
    {
        if (values.Count != 1 || values[0] is not IReadOnlyList<LibraDexIdentityKeyRange> ranges)
        {
            throw new InvalidOperationException("Multi-range identity deletion requires one range-list operand.");
        }

        long deleted = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            deleted += DeleteIdentityPrimitiveRange(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)));
        }

        return deleted;
    }

    /// <summary>
    /// Deletes identities selected by a scalar-null condition primitive.<br/>
    /// `ScalarNull.Null` clears the null route, while `ScalarNull.NonNull` deletes ordinary value-route tuples only.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <returns>The number of deleted identities or tuples.</returns>
    private long DeleteScalarNullIdentityPrimitive(IReadOnlyList<object?> values)
    {
        ScalarNull state = RequireScalarNullState(values);
        return state == ScalarNull.Null
            ? DeleteScalarNullRouteIdentities()
            : DeleteIdentityPrimitiveRange(GetLowerFullKeyBound(), GetUpperFullKeyBound());
    }

    private long DeleteIdentityPrimitiveRange(TKey lowerKey, TKey upperKey)
    {
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => DeleteScalar8Scalar8Range(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS168 => DeleteScalar16Scalar8Range(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS816 => DeleteScalar8Scalar16Range(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS1616 => DeleteScalar16Scalar16Range(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS328 => DeleteFixed32Scalar8Range(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS3216 => DeleteFixed32Scalar16Range(lowerKey, upperKey),
            _ => throw new NotSupportedException($"{shape} identity deletion is not connected to physical shelf mutation yet.")
        };
    }

    /// <summary>
    /// Deletes `SS8-8` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// The router walk is shared with the range reader plan so the delete touches only shelves whose key intervals intersect the condition primitive.<br/>
    /// Surviving payload cells are compacted before the shelf rewrite is staged, keeping the active payload prefix safe for later inserts.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteScalar8Scalar8Range(TKey lowerKey, TKey upperKey)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        if (upperEncodedKey < lowerEncodedKey)
        {
            return 0;
        }

        using Scalar8Scalar8RangePlan plan = session.BuildScalar8Scalar8RangePlan(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, lowerEncodedKey, upperEncodedKey);
        long deleted = 0;
        for (int i = 0; i < plan.ShelfCount; i++)
        {
            byte[] shelfBytes = session.IsDurabilityBatchActive
                ? session.ReadScalar8Scalar8ShelfBytesForBatch(plan.ShelfOffsets[i], plan.Profile)
                : plan.Shelves[i];
            Scalar8Scalar8ReadOnly readOnly = new(shelfBytes, plan.Profile);
            int startSlot = session.IsDurabilityBatchActive
                ? readOnly.LowerBoundKey(lowerEncodedKey)
                : plan.StartSlots[i];
            int endSlot = session.IsDurabilityBatchActive ? startSlot : plan.EndSlots[i];
            if (session.IsDurabilityBatchActive)
            {
                while (endSlot < readOnly.ItemCount && readOnly.ReadKeyAt(endSlot) <= upperEncodedKey)
                {
                    endSlot++;
                }
            }

            Scalar8Scalar8 shelf = new(shelfBytes, plan.Profile);
            int removed = session.IsDurabilityBatchActive
                ? shelf.MarkSlotRangeDeleted(startSlot, endSlot - startSlot)
                : shelf.RemoveSlotRange(startSlot, endSlot - startSlot);
            if (removed == 0)
            {
                continue;
            }

            _ = session.StageScalar8Scalar8ShelfRewriteForBatch(plan.ShelfOffsets[i], plan.Profile, shelfBytes);
            deleted += removed;
        }

        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Deletes `SS16-8` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// The reader owns route traversal and retained shelf buffers; deletion mutates only the planned shelf intervals and stages validated rewrites through the session.<br/>
    /// This mirrors the `SS8-8` delete bridge while preserving the two-lane key comparison rules inside the existing range reader.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteScalar16Scalar8Range(TKey lowerKey, TKey upperKey)
    {
        using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(lowerKey, upperKey);
        long deleted = reader.DeleteMatchedRanges();
        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Deletes `FS32-8` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// The fixed 32-byte key shape keeps its range comparison and router traversal in the existing range reader; this method only invokes the reader's shelf-local delete executor and records mutation counters.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteFixed32Scalar8Range(TKey lowerKey, TKey upperKey)
    {
        using Fixed32Scalar8RangeReader reader = OpenFixed32Scalar8RangeReader(lowerKey, upperKey);
        long deleted = reader.DeleteMatchedRanges();
        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Deletes `SS8-16` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// Widened identities do not change the delete predicate because the condition primitive selects by key; duplicate tuple order remains encoded in the shelf slot table.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteScalar8Scalar16Range(TKey lowerKey, TKey upperKey)
    {
        using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(lowerKey, upperKey);
        long deleted = reader.DeleteMatchedRanges();
        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Deletes `SS16-16` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// The existing two-lane-key range reader owns route traversal and key comparison, while the delete bridge only applies shelf-local slot removal and stages rewritten shelves.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteScalar16Scalar16Range(TKey lowerKey, TKey upperKey)
    {
        using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(lowerKey, upperKey);
        long deleted = reader.DeleteMatchedRanges();
        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Deletes `FS32-16` tuples in an inclusive key range by removing matching sorted slots from affected shelves.<br/>
    /// The fixed-key widened-identity shape uses the same routed range reader as retrieval, so delete remains aligned with condition materialization and avoids a separate mutation selector.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.</param>
    /// <param name="upperKey">The inclusive upper public key.</param>
    /// <returns>The number of active tuples removed.</returns>
    private long DeleteFixed32Scalar16Range(TKey lowerKey, TKey upperKey)
    {
        using Fixed32Scalar16RangeReader reader = OpenFixed32Scalar16RangeReader(lowerKey, upperKey);
        long deleted = reader.DeleteMatchedRanges();
        for (long i = 0; i < deleted; i++)
        {
            RecordDelete();
        }

        return deleted;
    }

    private void RecordDelete()
    {
        Stats.RecordDelete();
        catalog?.Stats.RecordDelete();
    }

    private void RecordRekey()
    {
        Stats.RecordRekey();
        catalog?.Stats.RecordRekey();
    }

    /// <summary>
    /// Tests one exact typed key/identity tuple through an equality range over the key.<br/>
    /// This avoids relying on insert result ambiguity, where an uninserted replacement can mean either "already present" or "blocked by uniqueness".<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to test.</param>
    /// <param name="identity">The identity side of the tuple to test.</param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    private bool ContainsExactTuple(TKey key, TIdentity identity)
    {
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(key, key);
        while (reader.TryReadNext(out _, out TIdentity currentIdentity))
        {
            if (TupleComponentEquals(currentIdentity, identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes one exact typed key/identity tuple without removing neighboring identities that share the same key.<br/>
    /// The operation is the tuple-level primitive used by direct delete and by criteria-scoped re-key after replacement tuples have been created.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    /// <returns><see langword="true"/> when one tuple was removed.</returns>
    private bool DeleteExactTuple(TKey key, TIdentity identity)
    {
        bool deleted = shape switch
        {
            LibraDexGenericScalarShape.SS88 => DeleteExactScalar8Scalar8Tuple(key, identity),
            LibraDexGenericScalarShape.SS168 => DeleteExactScalar16Scalar8Tuple(key, identity),
            LibraDexGenericScalarShape.SS816 => DeleteExactScalar8Scalar16Tuple(key, identity),
            LibraDexGenericScalarShape.SS1616 => DeleteExactScalar16Scalar16Tuple(key, identity),
            LibraDexGenericScalarShape.FS328 => DeleteExactFixed32Scalar8Tuple(key, identity),
            LibraDexGenericScalarShape.FS3216 => DeleteExactFixed32Scalar16Tuple(key, identity),
            _ => throw new NotSupportedException($"{shape} exact tuple deletion is not connected to physical shelf mutation yet.")
        };
        if (deleted)
        {
            DeleteExactReversedProjection(key, identity);
            RecordDelete();
        }

        return deleted;
    }

    /// <summary>
    /// Removes the reversed-key companion tuple from the maintained exact reversed projection when this index owns one.<br/>
    /// Projection deletion is tied to exact tuple deletion so rekey and direct delete keep suffix routing consistent with the primary index.<br/>
    /// </summary>
    /// <param name="key">The original forward key removed from the logical index.</param>
    /// <param name="identity">The identity associated with the removed tuple.</param>
    private void DeleteExactReversedProjection(TKey key, TIdentity identity)
    {
        if (exactReversedProjection is null)
        {
            return;
        }

        _ = exactReversedProjection.DeleteExactTuple(CreateExactReversedProjectionKey(key), identity);
    }

    /// <summary>
    /// Produces the stored key value for an exact reversed binary projection.<br/>
    /// The input key is cloned before reversal so caller-owned byte arrays and primary index values are not mutated by projection maintenance.<br/>
    /// </summary>
    /// <param name="key">The original forward byte-array key.</param>
    /// <returns>The reversed key value encoded as the same generic key type.</returns>
    private static TKey CreateExactReversedProjectionKey(TKey key)
    {
        if (key is not byte[] bytes)
        {
            throw new NotSupportedException("Exact reversed projections are currently connected only for fixed-width byte[] keys.");
        }

        byte[] reversed = (byte[])bytes.Clone();
        Array.Reverse(reversed);
        return (TKey)(object)reversed;
    }

    private bool DeleteExactScalar8Scalar8Tuple(TKey key, TIdentity identity)
    {
        ulong encodedKey = EncodeKey8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Scalar8Scalar8RangePlan plan = session.BuildScalar8Scalar8RangePlan(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, encodedKey, encodedKey);
        for (int i = 0; i < plan.ShelfCount; i++)
        {
            Scalar8Scalar8ReadOnly readOnly = new(plan.Shelves[i], plan.Profile);
            for (int slot = plan.StartSlots[i]; slot < plan.EndSlots[i]; slot++)
            {
                if (readOnly.ReadIdentityAt(slot) != encodedIdentity)
                {
                    continue;
                }

                Scalar8Scalar8 shelf = new(plan.Shelves[i], plan.Profile);
                _ = shelf.RemoveSlotRange(slot, 1);
                _ = session.StageScalar8Scalar8ShelfRewriteForBatch(plan.ShelfOffsets[i], plan.Profile, plan.Shelves[i]);
                return true;
            }
        }

        return false;
    }

    private bool DeleteExactScalar16Scalar8Tuple(TKey key, TIdentity identity)
    {
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
    }

    private bool DeleteExactScalar8Scalar16Tuple(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    private bool DeleteExactScalar16Scalar16Tuple(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    private bool DeleteExactFixed32Scalar8Tuple(TKey key, TIdentity identity)
    {
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Fixed32Scalar8RangeReader reader = OpenFixed32Scalar8RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
    }

    private bool DeleteExactFixed32Scalar16Tuple(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Fixed32Scalar16RangeReader reader = OpenFixed32Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    private static bool TupleComponentEquals<TValue>(TValue left, TValue right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        return EqualityComparer<TValue>.Default.Equals(left, right);
    }

    /// <summary>
    /// Re-keys one known identity when the caller also knows the old key.<br/>
    /// This is the fastest direct mutation shape because LibraDex can target the old key/identity tuple without discovering the old key through a scan or reverse identity map.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKey">The current key associated with the identity.</param>
    /// <param name="newKey">The replacement key to associate with the identity.</param>
    public void Rekey(TIdentity identity, TKey oldKey, TKey newKey)
    {
        ThrowIfDisposed();
        if (TupleComponentEquals(oldKey, newKey))
        {
            return;
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            LibraDexGenericInsertResult insert = Insert(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        _ = DeleteExactTuple(oldKey, identity);
        RecordRekey();
    }

    /// <summary>
    /// Re-keys one identity from the scalar null route to an ordinary scalar key.<br/>
    /// The replacement value tuple is created before the null-route identity is removed so a failed replacement does not lose the original null-key association.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKeyState">The old scalar key state; only <see cref="ScalarNull.Null"/> is accepted.</param>
    /// <param name="newKey">The replacement ordinary scalar key.</param>
    /// <returns><see langword="true"/> when the null-route identity existed and was removed after the replacement tuple was available.</returns>
    public bool Rekey(TIdentity identity, ScalarNull oldKeyState, TKey newKey)
    {
        ThrowIfDisposed();
        if (oldKeyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(oldKeyState), oldKeyState, "Only ScalarNull.Null is a concrete scalar key state for rekey.");
        }

        if (!ContainsScalarNullIdentity(identity))
        {
            return false;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            LibraDexGenericInsertResult insert = Insert(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Scalar-null rekey could not create the replacement tuple; the null-route identity was left unchanged.");
            }
        }

        bool deleted = DeleteScalarNullIdentity(identity);
        if (deleted)
        {
            RecordRekey();
        }

        return deleted;
    }

    /// <summary>
    /// Re-keys one identity from an ordinary scalar key to the scalar null route.<br/>
    /// The null-route identity is created before the old ordinary tuple is removed so a failed replacement does not lose the original key association.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKey">The old ordinary scalar key.</param>
    /// <param name="newKeyState">The replacement scalar key state; only <see cref="ScalarNull.Null"/> is accepted.</param>
    /// <returns><see langword="true"/> when the old ordinary tuple existed and was removed after the null-route identity was available.</returns>
    public bool Rekey(TIdentity identity, TKey oldKey, ScalarNull newKeyState)
    {
        ThrowIfDisposed();
        if (newKeyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(newKeyState), newKeyState, "Only ScalarNull.Null is a concrete scalar key state for rekey.");
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return false;
        }

        if (!ContainsScalarNullIdentity(identity))
        {
            LibraDexGenericInsertResult insert = Insert(ScalarNull.Null, identity);
            if (!insert.Inserted && !ContainsScalarNullIdentity(identity))
            {
                throw new InvalidOperationException("Scalar-null rekey could not create the null-route identity; the original tuple was left unchanged.");
            }
        }

        bool deleted = DeleteExactTuple(oldKey, identity);
        if (deleted)
        {
            RecordRekey();
        }

        return deleted;
    }

    /// <summary>
    /// Re-keys the exact tuple captured by a live range cursor.<br/>
    /// The range reader owns traversal state only; this method performs the durable mutation through the owning index so retained shelf snapshots are not rewritten after a replacement insert.<br/>
    /// A no-op is returned when the old tuple is already gone or when the requested key is equal to the current key.<br/>
    /// </summary>
    /// <param name="oldKey">The key captured from the cursor's current row before mutation.</param>
    /// <param name="identity">The identity captured from the cursor's current row before mutation.</param>
    /// <param name="newKey">The replacement key to associate with the captured identity.</param>
    /// <returns>The insert result for the replacement tuple, or a no-op result when no replacement insert was required.</returns>
    private LibraDexGenericInsertResult RekeyForCursor(TKey oldKey, TIdentity identity, TKey newKey)
    {
        ThrowIfDisposed();
        if (TupleComponentEquals(oldKey, newKey))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        LibraDexGenericInsertResult insert = ContainsExactTuple(newKey, identity)
            ? new LibraDexGenericInsertResult(false, false, default, default)
            : Insert(newKey, identity);
        if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
        {
            throw new InvalidOperationException("Cursor-local SetKey could not create the replacement tuple; the original tuple was left unchanged.");
        }

        if (DeleteExactTuple(oldKey, identity))
        {
            RecordRekey();
        }

        return insert;
    }

    /// <summary>
    /// Re-keys one identity when the caller does not know the old key.<br/>
    /// The current implementation scans this index's visible tuples to discover old keys, then applies the exact old-key rekey primitive to each matching tuple.<br/>
    /// A maintained reverse identity lookup can optimize this later without changing the public call shape.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="newKey">The replacement key to associate with the identity.</param>
    public void Rekey(TIdentity identity, TKey newKey)
    {
        ThrowIfDisposed();
        List<TKey> oldKeys = new();
        using (LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader())
        {
            while (reader.TryReadNext(out TKey key, out TIdentity currentIdentity))
            {
                if (TupleComponentEquals(currentIdentity, identity))
                {
                    oldKeys.Add(key);
                }
            }
        }

        for (int i = 0; i < oldKeys.Count; i++)
        {
            Rekey(identity, oldKeys[i], newKey);
        }

        if (ContainsScalarNullIdentity(identity))
        {
            _ = Rekey(identity, ScalarNull.Null, newKey);
        }
    }

    /// <summary>
    /// Deletes one known key/identity tuple.<br/>
    /// This direct helper is for callers that already know both sides of the tuple; criteria-based deletion should normally use a retrieval cursor and call `Delete` on matching rows.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to delete.</param>
    /// <param name="identity">The identity side of the tuple to delete.</param>
    public void Delete(TKey key, TIdentity identity)
    {
        ThrowIfDisposed();
        _ = DeleteExactTuple(key, identity);
    }

    /// <summary>
    /// Deletes one identity from this scalar index's metadata-backed null-key route.<br/>
    /// This is the typed exact-delete counterpart to <see cref="Insert(ScalarNull, TIdentity)"/> and targets the identity-keyed route directly.<br/>
    /// </summary>
    /// <param name="keyState">The scalar key state to delete; only <see cref="ScalarNull.Null"/> is accepted.</param>
    /// <param name="identity">The typed identity value to remove from the null route.</param>
    /// <returns><see langword="true"/> when the identity was removed.</returns>
    public bool Delete(ScalarNull keyState, TIdentity identity)
    {
        ThrowIfDisposed();
        if (keyState != ScalarNull.Null)
        {
            throw new ArgumentOutOfRangeException(nameof(keyState), keyState, "Only ScalarNull.Null is a concrete scalar key state for deletion.");
        }

        return DeleteScalarNullIdentity(identity);
    }

    /// <summary>
    /// Deletes one identity from this binary index's metadata-backed null or empty key-state route.<br/>
    /// This is the typed exact-delete counterpart to <see cref="Insert(NullKey, TIdentity)"/> and targets the identity-keyed route directly.<br/>
    /// </summary>
    /// <param name="keyState">The binary key state to delete; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <param name="identity">The typed identity value to remove from the key-state route.</param>
    /// <returns><see langword="true"/> when the identity was removed.</returns>
    public bool Delete(NullKey keyState, TIdentity identity)
    {
        ThrowIfDisposed();
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        return DeleteNullKeyIdentity(keyState, identity);
    }

    /// <summary>
    /// Closes the index by delegating to `Dispose`.<br/>
    /// This method exists for callers who prefer explicit verb-style lifetime control over `using` while preserving the same cleanup path.<br/>
    /// </summary>
    public void Close()
    {
        Dispose();
    }

    /// <summary>
    /// Reads identities for an inclusive key range into a caller-owned destination span.<br/>
    /// The selected physical shape determines the encoded key lanes and identity lanes used for the routed range read, while the public destination receives decoded CLR identities.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <param name="identities">The caller-owned destination span that receives decoded identities.</param>
    /// <param name="enableCoalescing">Whether the read may coalesce adjacent shelf extents when the route shape justifies it.</param>
    /// <param name="coalescedShelfScratchCount">The pooled scratch capacity, measured in profiled shelf extents, when coalescing is enabled.</param>
    /// <returns>The generic range-read result describing copied identity count and scratch/coalescing shape.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the selected physical shape does not have generic public read support in this slice.</exception>
    public LibraDexGenericRangeReadResult ReadRange(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities,
        bool enableCoalescing = false,
        int coalescedShelfScratchCount = 8)
    {
        ThrowIfDisposed();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => ReadScalar8Scalar8Range(lowerKey, upperKey, identities, enableCoalescing, coalescedShelfScratchCount),
            LibraDexGenericScalarShape.SS168 => ReadScalar16Scalar8Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.SS816 => ReadScalar8Scalar16Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.SS1616 => ReadScalar16Scalar16Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.FS328 => ReadFixed32Scalar8Range(lowerKey, upperKey, identities),
            LibraDexGenericScalarShape.FS3216 => ReadFixed32Scalar16Range(lowerKey, upperKey, identities),
            _ => throw new NotSupportedException($"Generic LibraDex read does not support resolved shape {shape}.")
        };
    }

    /// <summary>
    /// Opens a forward-only generic range reader for an inclusive key range.<br/>
    /// The returned cursor follows the same physical shape selected for this index and allows callers to stream matching rows without allocating a destination array for the whole result set.<br/>
    /// Current key and identity values are decoded only when requested from the cursor, so identity-only analysis can avoid key materialization after positioning.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <returns>A cursor positioned before the first matching key/identity row.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the selected physical shape does not have generic cursor support in this slice.</exception>
    public LibraDexRangeReader<TKey, TIdentity> OpenRangeReader(TKey lowerKey, TKey upperKey)
    {
        ThrowIfDisposed();
        object reader = shape switch
        {
            LibraDexGenericScalarShape.SS88 => OpenScalar8Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS168 => OpenScalar16Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS816 => OpenScalar8Scalar16RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS1616 => OpenScalar16Scalar16RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS328 => OpenFixed32Scalar8RangeReader(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS3216 => OpenFixed32Scalar16RangeReader(lowerKey, upperKey),
            _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
        };

        return new LibraDexRangeReader<TKey, TIdentity>(
            reader,
            shape,
            recordDelete: RecordDelete,
            rekeyTuple: RekeyForCursor);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAllRangeReader()
    {
        GetFullKeyBounds(out TKey lowerKey, out TKey upperKey);
        return OpenRangeReader(lowerKey, upperKey);
    }

    /// <summary>
    /// Streams every decoded key/identity tuple in this index through the lowest available all-range reader.<br/>
    /// This method is the condition/grouping bridge for whole-index tuple walks after the old direct retrieval facade has been removed.<br/>
    /// Callers that need filtered identity results should still enter through condition primitives so new physical needs are exposed as primitive requests instead of hidden facade reuse.<br/>
    /// </summary>
    /// <returns>A forward-only tuple sequence ordered by the index's physical key/identity traversal.</returns>
    internal IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateAllTuples()
    {
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
        }
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenBeforeRangeReader(TKey key)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        if (!TryGetPreviousKey(key, out TKey upperKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader();
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrBeforeRangeReader(TKey key)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        return OpenRangeReader(lowerKey, key);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAfterRangeReader(TKey key)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        if (!TryGetNextKey(key, out TKey lowerKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader();
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrAfterRangeReader(TKey key)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        return OpenRangeReader(key, upperKey);
    }

    private void GetFullKeyBounds(out TKey lowerKey, out TKey upperKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
                if (LibraDexGenericScalarCodec<TKey>.TryGetMinimumValue(out lowerKey) &&
                    LibraDexGenericScalarCodec<TKey>.TryGetMaximumValue(out upperKey))
                {
                    return;
                }

                lowerKey = DecodeKey8(0);
                upperKey = DecodeKey8(ulong.MaxValue);
                return;
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
                if (LibraDexGenericScalarCodec<TKey>.TryGetMinimumValue(out lowerKey) &&
                    LibraDexGenericScalarCodec<TKey>.TryGetMaximumValue(out upperKey))
                {
                    return;
                }

                lowerKey = LibraDexGenericScalarCodec<TKey>.Decode16(0, 0);
                upperKey = LibraDexGenericScalarCodec<TKey>.Decode16(ulong.MaxValue, ulong.MaxValue);
                return;
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                lowerKey = CreateFixed32Key(0, 0, 0, 0);
                upperKey = CreateFixed32Key(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
                return;
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private bool TryGetPreviousKey(TKey key, out TKey previousKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
            {
                if (LibraDexGenericScalarCodec<TKey>.HasExplicitScalarDomain)
                {
                    return TryGetPreviousKeyValue(key, out previousKey);
                }

                ulong encoded = EncodeKey8(key);
                if (encoded == 0)
                {
                    previousKey = default!;
                    return false;
                }

                previousKey = DecodeKey8(encoded - 1);
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
            {
                if (LibraDexGenericScalarCodec<TKey>.HasExplicitScalarDomain)
                {
                    return TryGetPreviousKeyValue(key, out previousKey);
                }

                LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong high, out ulong low);
                if (high == 0 && low == 0)
                {
                    previousKey = default!;
                    return false;
                }

                if (low == 0)
                {
                    high--;
                    low = ulong.MaxValue;
                }
                else
                {
                    low--;
                }

                previousKey = LibraDexGenericScalarCodec<TKey>.Decode16(high, low);
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                return TryAdjustFixed32Key(key, decrement: true, out previousKey);
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private bool TryGetNextKey(TKey key, out TKey nextKey)
    {
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS816:
            {
                if (LibraDexGenericScalarCodec<TKey>.HasExplicitScalarDomain)
                {
                    return TryGetNextKeyValue(key, out nextKey);
                }

                ulong encoded = EncodeKey8(key);
                if (encoded == ulong.MaxValue)
                {
                    nextKey = default!;
                    return false;
                }

                nextKey = DecodeKey8(encoded + 1);
                return true;
            }
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.SS1616:
            {
                if (LibraDexGenericScalarCodec<TKey>.HasExplicitScalarDomain)
                {
                    return TryGetNextKeyValue(key, out nextKey);
                }

                LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong high, out ulong low);
                if (high == ulong.MaxValue && low == ulong.MaxValue)
                {
                    nextKey = default!;
                    return false;
                }

                if (low == ulong.MaxValue)
                {
                    high++;
                    low = 0;
                }
                else
                {
                    low++;
                }

                nextKey = LibraDexGenericScalarCodec<TKey>.Decode16(high, low);
                return true;
            }
            case LibraDexGenericScalarShape.FS328:
            case LibraDexGenericScalarShape.FS3216:
                return TryAdjustFixed32Key(key, decrement: false, out nextKey);
            default:
                throw new NotSupportedException($"Generic LibraDex criteria readers do not support resolved shape {shape}.");
        }
    }

    private static TKey CreateFixed32Key(ulong part0, ulong part1, ulong part2, ulong part3)
    {
        byte[] bytes = new byte[32];
        WriteBigEndianUInt64(bytes.AsSpan(0, 8), part0);
        WriteBigEndianUInt64(bytes.AsSpan(8, 8), part1);
        WriteBigEndianUInt64(bytes.AsSpan(16, 8), part2);
        WriteBigEndianUInt64(bytes.AsSpan(24, 8), part3);
        return (TKey)(object)bytes;
    }

    private static bool TryAdjustFixed32Key(TKey key, bool decrement, out TKey adjustedKey)
    {
        if (key is not byte[] source || source.Length != 32)
        {
            throw new ArgumentException("Generic LibraDex fixed 32-byte criteria require byte[] keys that are exactly 32 bytes.", nameof(key));
        }

        byte[] adjusted = new byte[32];
        source.CopyTo(adjusted, 0);
        if (decrement)
        {
            for (int i = adjusted.Length - 1; i >= 0; i--)
            {
                if (adjusted[i] != 0)
                {
                    adjusted[i]--;
                    adjustedKey = (TKey)(object)adjusted;
                    return true;
                }

                adjusted[i] = byte.MaxValue;
            }
        }
        else
        {
            for (int i = adjusted.Length - 1; i >= 0; i--)
            {
                if (adjusted[i] != byte.MaxValue)
                {
                    adjusted[i]++;
                    adjustedKey = (TKey)(object)adjusted;
                    return true;
                }

                adjusted[i] = 0;
            }
        }

        adjustedKey = default!;
        return false;
    }

    private static void WriteBigEndianUInt64(Span<byte> destination, ulong value)
    {
        for (int i = 7; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }

    /// <summary>
    /// Disposes the owning session when this generic index was created by a facade factory.<br/>
    /// Disposing a file-backed index releases the file handle; disposing a memory-backed index releases committed pooled memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (BatchManager.IsEnabled)
        {
            _ = BatchManager.CommitAndDisable();
        }

        if (ownsSession)
        {
            session.Dispose();
        }

        disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(LibraDexIndex<TKey, TIdentity>));
        }
    }

    private IIndex ResolveOwnIndex(string indexName)
    {
        if (!string.Equals(indexName, Name, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException($"Index '{indexName}' cannot be resolved from single-index handle '{Name}'. Execute composed cross-index conditions from the owning identity group instead.");
        }

        return this;
    }

    /// <summary>
    /// Resolves this handle's hidden projection for single-index condition execution.<br/>
    /// The current connected projection route is binary `EndsWith`, which maps to the maintained reversed exact-byte projection when present.<br/>
    /// </summary>
    /// <param name="descriptor">The condition leaf requesting a projection route.</param>
    /// <param name="classification">The planner classification that identified the projection need.</param>
    /// <returns>The hidden projection index, or <see langword="null"/> when this handle has no matching projection.</returns>
    private IIndex? ResolveOwnProjectionIndex(
        LibraDexConditionLeafDescriptor descriptor,
        LibraDexConditionLeafClassification classification)
    {
        if (classification.ProjectionKind == LibraDexIndexProjectionKind.Exact &&
            descriptor.ValueKind == LibraDexConditionValueKind.Binary &&
            descriptor.Operator == LibraDexConditionOperatorKind.EndsWith &&
            string.Equals(descriptor.IndexName, Name, StringComparison.Ordinal))
        {
            return exactReversedProjection;
        }

        return null;
    }

    private static TKey RequireObjectKey(object value, string parameterName)
    {
        if (value is TKey key)
        {
            return key;
        }

        string actualType = value?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"Expected a key value assignable to {typeof(TKey).FullName}; received {actualType}.", parameterName);
    }

    private static TIdentity RequireObjectIdentity(object value, string parameterName)
    {
        if (value is TIdentity identity)
        {
            return identity;
        }

        string actualType = value?.GetType().FullName ?? "<null>";
        throw new ArgumentException($"Expected an identity value assignable to {typeof(TIdentity).FullName}; received {actualType}.", parameterName);
    }

    private static object RequireCriterionValue(IReadOnlyList<object?> values, int index)
    {
        if (index >= values.Count || values[index] is null)
        {
            throw new InvalidOperationException("The identity criterion is missing a required runtime value.");
        }

        return values[index]!;
    }

    private IReadOnlyList<object> MaterializeIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
    {
        return IterateIdentityObjects(reader, takeLimit).ToList();
    }

    /// <summary>
    /// Materializes decoded key/identity tuples from a generic range reader and disposes the reader after consumption.<br/>
    /// The object wrapper is intentionally internal and preserves exact old keys for tuple-level mutation without adding a public projection type.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <returns>The decoded tuples as object key/identity pairs.</returns>
    private static IReadOnlyList<LibraDexObjectTuple> MaterializeTupleObjects(LibraDexRangeReader<TKey, TIdentity> reader)
    {
        List<LibraDexObjectTuple> tuples = new();
        using (reader)
        {
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                tuples.Add(new LibraDexObjectTuple(key!, identity!));
            }
        }

        return tuples;
    }

    /// <summary>
    /// Streams decoded identities from a concrete generic range reader and disposes the reader when enumeration completes or stops early.<br/>
    /// The limit is applied while reading so callers such as `Exists` can avoid copying more identities than they need.<br/>
    /// This helper is the shared low-level identity-only path for materialized and non-materialized primitive execution.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private static IEnumerable<object> IterateIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            reader.Dispose();
            yield break;
        }

        using (reader)
        {
            int returned = 0;
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    private static long CountIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader)
    {
        using (reader)
        {
            return reader.Count;
        }
    }

    /// <summary>
    /// Materializes every identity visible through this index in LibraDex index-natural order.<br/>
    /// Scalar null-route identities are emitted before ordinary value-route identities so the public all-scan order matches the key-state route contract.<br/>
    /// </summary>
    /// <param name="takeLimit">The optional maximum number of identities to materialize.</param>
    /// <returns>The decoded identities in plan-natural order.</returns>
    private IReadOnlyList<object> MaterializeAllIdentityObjects(int? takeLimit = null)
    {
        return IterateAllIdentityObjects(takeLimit).ToList();
    }

    /// <summary>
    /// Streams every identity visible through this index in LibraDex index-natural order.<br/>
    /// The stream reads the scalar null route first, then falls through to the ordinary value router while preserving the caller's optional take limit across both route classes.<br/>
    /// </summary>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<object> IterateAllIdentityObjects(int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        int returned = 0;
        if (SupportsScalarNullKeyRoute())
        {
            foreach (object identity in IterateScalarNullRouteIdentityObjects(takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
        else if (SupportsNullKeyRoute())
        {
            foreach (object identity in IterateNullKeyRouteIdentityObjects(NullKey.Null, takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }

            int? remainingForEmpty = takeLimit is null ? null : takeLimit.Value - returned;
            foreach (object identity in IterateNullKeyRouteIdentityObjects(NullKey.Empty, remainingForEmpty))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }

        int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (object identity in IterateIdentityObjects(OpenAllRangeReader(), remaining))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Counts every identity visible through this index, including the scalar null route before ordinary value routes.<br/>
    /// This keeps `All().Count(...)` consistent with `All().ToList(...)` without forcing an identity materialization pass.<br/>
    /// </summary>
    /// <returns>The total identity count.</returns>
    private long CountAllIdentityObjects()
    {
        long count = CountIdentityObjects(OpenAllRangeReader());
        if (SupportsScalarNullKeyRoute())
        {
            count += CountScalarNullRouteIdentityObjects();
        }
        else if (SupportsNullKeyRoute())
        {
            count += CountNullKeyRouteIdentityObjects(NullKey.Null);
            count += CountNullKeyRouteIdentityObjects(NullKey.Empty);
        }

        return count;
    }

    /// <summary>
    /// Materializes identities for a scalar-null presence primitive.<br/>
    /// `ScalarNull.Null` reads the compact metadata-backed null route, while `ScalarNull.NonNull` reads the ordinary value-router range and therefore excludes scalar nulls.<br/>
    /// The take limit is applied during route enumeration so callers such as paged condition terminals do not decode more null-route identities than requested.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to materialize.</param>
    /// <returns>The decoded identities selected by the scalar-null state.</returns>
    private IReadOnlyList<object> MaterializeScalarNullIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateScalarNullIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities for a scalar-null presence primitive without first materializing the whole route.<br/>
    /// Null-route identities are stored as encoded identities only, so this method decodes scalar-8 and scalar-16 identity lanes directly from the compact route shelf.<br/>
    /// Non-null scalar state intentionally falls through to the normal all-range reader instead of synthesizing a complement against the null route.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<object> IterateScalarNullIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            foreach (object identity in IterateIdentityObjects(OpenAllRangeReader(), takeLimit))
            {
                yield return identity;
            }

            yield break;
        }

        foreach (object identity in IterateScalarNullRouteIdentityObjects(takeLimit))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Counts identities for a scalar-null presence primitive.<br/>
    /// `ScalarNull.NonNull` delegates to the normal all-range count, while `ScalarNull.Null` counts the compact null-route identity shelf for the selected identity width.<br/>
    /// This keeps null-only counts on the fast identity-only route rather than scanning ordinary key shelves.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <returns>The selected identity count.</returns>
    private long CountScalarNullIdentityObjects(IReadOnlyList<object?> values)
    {
        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            return CountIdentityObjects(OpenAllRangeReader());
        }

        return CountScalarNullRouteIdentityObjects();
    }

    /// <summary>
    /// Reads the scalar-null state operand from a normalized condition primitive.<br/>
    /// The condition builder validates this before creating the primitive, but the executor keeps its own guard so manually created internal criteria fail clearly.<br/>
    /// </summary>
    /// <param name="values">The primitive operand values.</param>
    /// <returns>The requested scalar-null state.</returns>
    private static ScalarNull RequireScalarNullState(IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values[0] is not ScalarNull state)
        {
            throw new InvalidOperationException("Scalar null criteria require a ScalarNull operand.");
        }

        return state;
    }

    /// <summary>
    /// Inserts one identity into this index's scalar null route.<br/>
    /// The route is keyed by encoded identity, so duplicate identity writes are no-ops and unique-key indexes reject a second different identity on the null key state.<br/>
    /// </summary>
    /// <param name="identity">The public identity value to associate with the scalar null key state.</param>
    /// <returns>The insert result reported through the generic public result contract.</returns>
    private LibraDexGenericInsertResult InsertScalarNullIdentity(TIdentity identity)
    {
        EnsureScalarNullKeyRouteSupported();
        bool inserted = shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => InsertScalar8NullRouteIdentity(identity),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => InsertScalar16NullRouteIdentity(identity),
            _ => throw new NotSupportedException($"Scalar null route inserts do not support resolved shape {shape}.")
        };

        LibraDexGenericInsertResult result = new(inserted, false, default, default);
        Stats.RecordInsert(result);
        Catalog?.Stats.RecordInsert(result);
        return result;
    }

    private bool InsertScalar8NullRouteIdentity(TIdentity identity)
    {
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        if (keyContract == IndexKeys.Unique)
        {
            ulong[] existing = session.ReadScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
            if (existing.Length != 0 && !session.ContainsScalar8KeyStateIdentity(SlotIndex, KeyStateRoute.Null, encodedIdentity))
            {
                return false;
            }
        }

        return session.InsertScalar8KeyStateIdentity(SlotIndex, KeyStateRoute.Null, encodedIdentity);
    }

    private bool InsertScalar16NullRouteIdentity(TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        if (keyContract == IndexKeys.Unique)
        {
            (ulong[] highs, _) = session.ReadScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
            if (highs.Length != 0 && !session.ContainsScalar16KeyStateIdentity(SlotIndex, KeyStateRoute.Null, identityHigh, identityLow))
            {
                return false;
            }
        }

        return session.InsertScalar16KeyStateIdentity(SlotIndex, KeyStateRoute.Null, identityHigh, identityLow);
    }

    /// <summary>
    /// Deletes one identity from this index's scalar null route.<br/>
    /// The route is keyed by encoded identity, so exact null-key deletion avoids scanning ordinary value shelves.<br/>
    /// </summary>
    /// <param name="identity">The public identity value to remove from the scalar null key state.</param>
    /// <returns><see langword="true"/> when the identity was removed.</returns>
    private bool DeleteScalarNullIdentity(TIdentity identity)
    {
        EnsureScalarNullKeyRouteSupported();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.DeleteScalar8KeyStateIdentity(
                SlotIndex,
                KeyStateRoute.Null,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => DeleteScalar16NullRouteIdentity(identity),
            _ => throw new NotSupportedException($"Scalar null route deletes do not support resolved shape {shape}.")
        };
    }

    private bool DeleteScalar16NullRouteIdentity(TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        return session.DeleteScalar16KeyStateIdentity(SlotIndex, KeyStateRoute.Null, identityHigh, identityLow);
    }

    /// <summary>
    /// Tests whether one identity is currently associated with this index's scalar null route.<br/>
    /// The identity-keyed route answers exact membership without scanning ordinary value shelves.<br/>
    /// </summary>
    /// <param name="identity">The public identity value to test.</param>
    /// <returns><see langword="true"/> when the identity is present on the scalar null route.</returns>
    private bool ContainsScalarNullIdentity(TIdentity identity)
    {
        EnsureScalarNullKeyRouteSupported();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.ContainsScalar8KeyStateIdentity(
                SlotIndex,
                KeyStateRoute.Null,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => ContainsScalar16NullRouteIdentity(identity),
            _ => throw new NotSupportedException($"Scalar null routes do not support resolved shape {shape}.")
        };
    }

    private bool ContainsScalar16NullRouteIdentity(TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        return session.ContainsScalar16KeyStateIdentity(SlotIndex, KeyStateRoute.Null, identityHigh, identityLow);
    }

    /// <summary>
    /// Deletes every identity currently present on the scalar null route.<br/>
    /// The snapshot is taken before mutation so the current inline route can be compacted after each exact delete without invalidating enumeration state.<br/>
    /// </summary>
    /// <returns>The number of removed identities.</returns>
    private long DeleteScalarNullRouteIdentities()
    {
        EnsureScalarNullKeyRouteSupported();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => DeleteScalar8NullRouteIdentities(),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => DeleteScalar16NullRouteIdentities(),
            _ => throw new NotSupportedException($"Scalar null route deletes do not support resolved shape {shape}.")
        };
    }

    private long DeleteScalar8NullRouteIdentities()
    {
        ulong[] encodedIdentities = session.ReadScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
        long deleted = 0;
        for (int i = 0; i < encodedIdentities.Length; i++)
        {
            if (session.DeleteScalar8KeyStateIdentity(SlotIndex, KeyStateRoute.Null, encodedIdentities[i]))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private long DeleteScalar16NullRouteIdentities()
    {
        (ulong[] highs, ulong[] lows) = session.ReadScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
        long deleted = 0;
        for (int i = 0; i < highs.Length; i++)
        {
            if (session.DeleteScalar16KeyStateIdentity(SlotIndex, KeyStateRoute.Null, highs[i], lows[i]))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Streams decoded identities from the scalar null route only.<br/>
    /// Missing routes return no identities, while present routes preserve encoded identity order from the route root.<br/>
    /// </summary>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only sequence of decoded scalar-null identities.</returns>
    private IEnumerable<object> IterateScalarNullRouteIdentityObjects(int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        EnsureScalarNullKeyRouteSupported();
        if (takeLimit == 0)
        {
            yield break;
        }

        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.FS328:
                int returned8 = 0;
                ulong[] encodedIdentities = session.ReadScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
                for (int i = 0; i < encodedIdentities.Length; i++)
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i])!;
                    returned8++;
                    if (takeLimit is not null && returned8 >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                yield break;
            case LibraDexGenericScalarShape.SS816:
            case LibraDexGenericScalarShape.SS1616:
            case LibraDexGenericScalarShape.FS3216:
                int returned16 = 0;
                (ulong[] highs, ulong[] lows) = session.ReadScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null);
                for (int i = 0; i < highs.Length; i++)
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(highs[i], lows[i])!;
                    returned16++;
                    if (takeLimit is not null && returned16 >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                yield break;
            default:
                throw new NotSupportedException($"Scalar null routes do not support resolved shape {shape}.");
        }
    }

    /// <summary>
    /// Counts decoded identities from the scalar null route only.<br/>
    /// The count reads route metadata directly and does not enumerate ordinary value routes.<br/>
    /// </summary>
    /// <returns>The number of identities on the scalar null route.</returns>
    private long CountScalarNullRouteIdentityObjects()
    {
        EnsureScalarNullKeyRouteSupported();
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.ReadScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null).LongLength,
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => session.ReadScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null).Highs.LongLength,
            _ => throw new NotSupportedException($"Scalar null routes do not support resolved shape {shape}.")
        };
    }

    /// <summary>
    /// Materializes tuple rows for a scalar-null presence primitive.<br/>
    /// `ScalarNull.Null` returns null-key tuple rows from the identity-keyed route, while `ScalarNull.NonNull` returns ordinary value-route tuples.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <returns>The matching key/identity tuples.</returns>
    private IReadOnlyList<LibraDexObjectTuple> MaterializeScalarNullTupleObjects(IReadOnlyList<object?> values)
    {
        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            return MaterializeTupleObjects(OpenAllRangeReader());
        }

        return IterateScalarNullRouteIdentityObjects()
            .Select(static identity => new LibraDexObjectTuple(null, identity))
            .ToList();
    }

    private static bool SupportsScalarNullKeyRoute()
    {
        return typeof(TKey) != typeof(byte[]);
    }

    private static void EnsureScalarNullKeyRouteSupported()
    {
        if (!SupportsScalarNullKeyRoute())
        {
            throw new NotSupportedException("ScalarNull routes are for scalar key families. Use NullKey for binary key states.");
        }
    }

    /// <summary>
    /// Materializes identities for a null or empty binary key-state primitive.<br/>
    /// Null and empty routes are identity-keyed roots, so this reads only compact route state and never scans ordinary fixed-width byte-key shelves.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to materialize.</param>
    /// <returns>The decoded identities selected by the key state.</returns>
    private IReadOnlyList<object> MaterializeNullKeyIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateNullKeyIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities for a null or empty binary key-state primitive without materializing the route first.<br/>
    /// `NullKey.NullOrEmpty` streams null before empty, matching the index-natural all-scan ordering contract.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<object> IterateNullKeyIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        NullKey keyState = RequireNullKeyState(values);
        if (keyState != NullKey.NullOrEmpty)
        {
            foreach (object identity in IterateNullKeyRouteIdentityObjects(keyState, takeLimit))
            {
                yield return identity;
            }

            yield break;
        }

        int returned = 0;
        foreach (object identity in IterateNullKeyRouteIdentityObjects(NullKey.Null, takeLimit))
        {
            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }

        int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (object identity in IterateNullKeyRouteIdentityObjects(NullKey.Empty, remaining))
        {
            yield return identity;
        }
    }

    /// <summary>
    /// Counts identities for a null or empty binary key-state primitive.<br/>
    /// The count reads route metadata directly, and `NullKey.NullOrEmpty` is the sum of the two compact route counts.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <returns>The selected identity count.</returns>
    private long CountNullKeyIdentityObjects(IReadOnlyList<object?> values)
    {
        NullKey keyState = RequireNullKeyState(values);
        return keyState == NullKey.NullOrEmpty
            ? CountNullKeyRouteIdentityObjects(NullKey.Null) + CountNullKeyRouteIdentityObjects(NullKey.Empty)
            : CountNullKeyRouteIdentityObjects(keyState);
    }

    /// <summary>
    /// Deletes identities selected by a null or empty binary key-state primitive.<br/>
    /// `NullKey.NullOrEmpty` clears null first, then empty, matching the key-state ordering used by reads.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <returns>The number of deleted identities.</returns>
    private long DeleteNullKeyIdentityPrimitive(IReadOnlyList<object?> values)
    {
        NullKey keyState = RequireNullKeyState(values);
        return keyState == NullKey.NullOrEmpty
            ? DeleteNullKeyRouteIdentities(NullKey.Null) + DeleteNullKeyRouteIdentities(NullKey.Empty)
            : DeleteNullKeyRouteIdentities(keyState);
    }

    /// <summary>
    /// Materializes tuple rows for a null or empty binary key-state primitive.<br/>
    /// The tuple key is returned as null for <see cref="NullKey.Null"/> and <see cref="Array.Empty{T}"/> for <see cref="NullKey.Empty"/> so mutation bridges can preserve caller-visible key semantics.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <returns>The matching key/identity tuples.</returns>
    private IReadOnlyList<LibraDexObjectTuple> MaterializeNullKeyTupleObjects(IReadOnlyList<object?> values)
    {
        NullKey keyState = RequireNullKeyState(values);
        List<LibraDexObjectTuple> tuples = new();
        if (keyState is NullKey.Null or NullKey.NullOrEmpty)
        {
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Null).Select(static identity => new LibraDexObjectTuple(null, identity)));
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Empty).Select(static identity => new LibraDexObjectTuple(Array.Empty<byte>(), identity)));
        }

        return tuples;
    }

    private LibraDexGenericInsertResult InsertNullKeyIdentity(NullKey keyState, TIdentity identity)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        bool inserted = shape switch
        {
            LibraDexGenericScalarShape.FS328 => InsertScalar8NullKeyRouteIdentity(keyState, identity),
            LibraDexGenericScalarShape.FS3216 => InsertScalar16NullKeyRouteIdentity(keyState, identity),
            _ => throw new NotSupportedException($"NullKey route inserts do not support resolved shape {shape}.")
        };

        LibraDexGenericInsertResult result = new(inserted, false, default, default);
        Stats.RecordInsert(result);
        Catalog?.Stats.RecordInsert(result);
        return result;
    }

    private bool InsertScalar8NullKeyRouteIdentity(NullKey keyState, TIdentity identity)
    {
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        KeyStateRoute route = ToKeyStateRoute(keyState);
        if (keyContract == IndexKeys.Unique)
        {
            ulong[] existing = session.ReadScalar8KeyStateIdentities(SlotIndex, route);
            if (existing.Length != 0 && !session.ContainsScalar8KeyStateIdentity(SlotIndex, route, encodedIdentity))
            {
                return false;
            }
        }

        return session.InsertScalar8KeyStateIdentity(SlotIndex, route, encodedIdentity);
    }

    private bool InsertScalar16NullKeyRouteIdentity(NullKey keyState, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        KeyStateRoute route = ToKeyStateRoute(keyState);
        if (keyContract == IndexKeys.Unique)
        {
            (ulong[] highs, _) = session.ReadScalar16KeyStateIdentities(SlotIndex, route);
            if (highs.Length != 0 && !session.ContainsScalar16KeyStateIdentity(SlotIndex, route, identityHigh, identityLow))
            {
                return false;
            }
        }

        return session.InsertScalar16KeyStateIdentity(SlotIndex, route, identityHigh, identityLow);
    }

    private bool DeleteNullKeyIdentity(NullKey keyState, TIdentity identity)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        KeyStateRoute route = ToKeyStateRoute(keyState);
        return shape switch
        {
            LibraDexGenericScalarShape.FS328 => session.DeleteScalar8KeyStateIdentity(
                SlotIndex,
                route,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.FS3216 => DeleteScalar16NullKeyRouteIdentity(route, identity),
            _ => throw new NotSupportedException($"NullKey route deletes do not support resolved shape {shape}.")
        };
    }

    private bool DeleteScalar16NullKeyRouteIdentity(KeyStateRoute route, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        return session.DeleteScalar16KeyStateIdentity(SlotIndex, route, identityHigh, identityLow);
    }

    private bool ContainsNullKeyIdentity(NullKey keyState, TIdentity identity)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        KeyStateRoute route = ToKeyStateRoute(keyState);
        return shape switch
        {
            LibraDexGenericScalarShape.FS328 => session.ContainsScalar8KeyStateIdentity(
                SlotIndex,
                route,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.FS3216 => ContainsScalar16NullKeyRouteIdentity(route, identity),
            _ => throw new NotSupportedException($"NullKey routes do not support resolved shape {shape}.")
        };
    }

    private bool ContainsScalar16NullKeyRouteIdentity(KeyStateRoute route, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        return session.ContainsScalar16KeyStateIdentity(SlotIndex, route, identityHigh, identityLow);
    }

    private long DeleteNullKeyRouteIdentities(NullKey keyState)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        return shape switch
        {
            LibraDexGenericScalarShape.FS328 => DeleteScalar8NullKeyRouteIdentities(ToKeyStateRoute(keyState)),
            LibraDexGenericScalarShape.FS3216 => DeleteScalar16NullKeyRouteIdentities(ToKeyStateRoute(keyState)),
            _ => throw new NotSupportedException($"NullKey route deletes do not support resolved shape {shape}.")
        };
    }

    private long DeleteScalar8NullKeyRouteIdentities(KeyStateRoute route)
    {
        ulong[] encodedIdentities = session.ReadScalar8KeyStateIdentities(SlotIndex, route);
        long deleted = 0;
        for (int i = 0; i < encodedIdentities.Length; i++)
        {
            if (session.DeleteScalar8KeyStateIdentity(SlotIndex, route, encodedIdentities[i]))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private long DeleteScalar16NullKeyRouteIdentities(KeyStateRoute route)
    {
        (ulong[] highs, ulong[] lows) = session.ReadScalar16KeyStateIdentities(SlotIndex, route);
        long deleted = 0;
        for (int i = 0; i < highs.Length; i++)
        {
            if (session.DeleteScalar16KeyStateIdentity(SlotIndex, route, highs[i], lows[i]))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private IEnumerable<object> IterateNullKeyRouteIdentityObjects(NullKey keyState, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        if (takeLimit == 0)
        {
            yield break;
        }

        KeyStateRoute route = ToKeyStateRoute(keyState);
        switch (shape)
        {
            case LibraDexGenericScalarShape.FS328:
                int returned8 = 0;
                ulong[] encodedIdentities = session.ReadScalar8KeyStateIdentities(SlotIndex, route);
                for (int i = 0; i < encodedIdentities.Length; i++)
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i])!;
                    returned8++;
                    if (takeLimit is not null && returned8 >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                yield break;
            case LibraDexGenericScalarShape.FS3216:
                int returned16 = 0;
                (ulong[] highs, ulong[] lows) = session.ReadScalar16KeyStateIdentities(SlotIndex, route);
                for (int i = 0; i < highs.Length; i++)
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(highs[i], lows[i])!;
                    returned16++;
                    if (takeLimit is not null && returned16 >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                yield break;
            default:
                throw new NotSupportedException($"NullKey routes do not support resolved shape {shape}.");
        }
    }

    private long CountNullKeyRouteIdentityObjects(NullKey keyState)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        KeyStateRoute route = ToKeyStateRoute(keyState);
        return shape switch
        {
            LibraDexGenericScalarShape.FS328 => session.ReadScalar8KeyStateIdentities(SlotIndex, route).LongLength,
            LibraDexGenericScalarShape.FS3216 => session.ReadScalar16KeyStateIdentities(SlotIndex, route).Highs.LongLength,
            _ => throw new NotSupportedException($"NullKey routes do not support resolved shape {shape}.")
        };
    }

    private static NullKey RequireNullKeyState(IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values[0] is not NullKey state)
        {
            throw new InvalidOperationException("Key-state criteria require a NullKey operand.");
        }

        return state;
    }

    private static bool TryClassifyNullKeyRouteKey(object? key, out NullKey keyState)
    {
        if (!SupportsNullKeyRoute())
        {
            keyState = default;
            return false;
        }

        switch (key)
        {
            case null:
            case DBNull:
                keyState = NullKey.Null;
                return true;
            case byte[] bytes when bytes.Length == 0:
                keyState = NullKey.Empty;
                return true;
            default:
                keyState = default;
                return false;
        }
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

    private static void EnsureConcreteNullKeyState(NullKey keyState, string parameterName)
    {
        EnsureNullKeyRouteSupported();
        if (keyState == NullKey.NullOrEmpty)
        {
            throw new ArgumentOutOfRangeException(parameterName, keyState, "NullKey.NullOrEmpty is a predicate state and is not one concrete key-state route.");
        }
    }

    private static bool SupportsNullKeyRoute()
    {
        return typeof(TKey) == typeof(byte[]);
    }

    private static void EnsureNullKeyRouteSupported()
    {
        if (!SupportsNullKeyRoute())
        {
            throw new NotSupportedException("NullKey routes are for binary key families. Use ScalarNull for scalar key states.");
        }
    }

    private IReadOnlyList<object> MaterializeMembershipIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (values.Count > 1)
        {
            return MaterializeMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue), takeLimit);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity execution requires an enumerable key value or prepared set.")
        };

        return MaterializeMembershipIdentityObjects(keys, takeLimit);
    }

    private IReadOnlyList<object> MaterializeMembershipIdentityObjects(IEnumerable<object> keys, int? takeLimit = null)
    {
        return IterateMembershipIdentityObjects(keys, takeLimit).ToList();
    }

    private IReadOnlyList<LibraDexObjectTuple> MaterializeMembershipTupleObjects(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return MaterializeMembershipTupleObjects(values.Select(RequireNonNullMembershipValue));
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership tuple execution requires an enumerable key value or prepared set.")
        };

        return MaterializeMembershipTupleObjects(keys);
    }

    /// <summary>
    /// Materializes tuple rows for a non-generic membership primitive by opening one equality reader per key.<br/>
    /// The method preserves supplied key order and leaves duplicate tuple handling to the caller because mutation uses physical tuple identity, not identity de-duplication.<br/>
    /// </summary>
    /// <param name="keys">The membership keys to read.</param>
    /// <returns>The matching key/identity tuples.</returns>
    private IReadOnlyList<LibraDexObjectTuple> MaterializeMembershipTupleObjects(IEnumerable<object> keys)
    {
        List<LibraDexObjectTuple> tuples = new();
        foreach (object keyValue in keys)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue, nameof(keys)),
                RequireObjectKey(keyValue, nameof(keys)));
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                tuples.Add(new LibraDexObjectTuple(key!, identity!));
            }
        }

        return tuples;
    }

    /// <summary>
    /// Streams identities for a membership primitive by opening one equality range reader for each supplied key.<br/>
    /// The method preserves the caller's key order and applies one shared take limit across all matching key runs.<br/>
    /// It intentionally leaves duplicate identities intact because duplicate handling belongs to the projection options above this primitive layer.<br/>
    /// </summary>
    /// <param name="values">The condition operand values, either inline keys, an enumerable of keys, or a prepared set.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateMembershipIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (values.Count > 1)
        {
            return IterateMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue), takeLimit);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity iteration requires an enumerable key value or prepared set.")
        };

        return IterateMembershipIdentityObjects(keys, takeLimit);
    }

    /// <summary>
    /// Streams identities for already-normalized membership keys through one equality reader per key.<br/>
    /// The supplied key enumerable is consumed lazily so prepared or generated sets do not need to become an intermediate array at this layer.<br/>
    /// The limit is cumulative across the whole membership request, matching the materialized primitive behavior.<br/>
    /// </summary>
    /// <param name="keys">The membership keys to read.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateMembershipIdentityObjects(IEnumerable<object> keys, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        int returned = 0;
        foreach (object keyValue in keys)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue, nameof(keys)),
                RequireObjectKey(keyValue, nameof(keys)));
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams tuple rows for direct membership criteria by opening one equality reader per captured key.<br/>
    /// Key order is preserved, skip/take are applied across the combined matching stream, and duplicate tuples remain intact at this primitive layer.<br/>
    /// De-duplication remains a projection/criteria option above the physical membership fan-out.<br/>
    /// </summary>
    /// <param name="operand">The captured membership operand.</param>
    /// <param name="skip">The number of matching tuples to skip.</param>
    /// <param name="take">The optional maximum number of tuples to return.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexTuple<TKey, TIdentity>> IterateMembershipTuples(object operand, int skip, int? take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip cannot be negative.");
        }

        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take cannot be negative.");
        }

        if (take == 0)
        {
            yield break;
        }

        int returned = 0;
        foreach (TKey keyValue in NormalizeMembershipKeys(operand))
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(keyValue, keyValue);
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                yield return new LibraDexTuple<TKey, TIdentity>(key, identity);
                returned++;
                if (take is not null && returned >= take.Value)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<TKey> NormalizeMembershipKeys(object operand)
    {
        return operand switch
        {
            LibraDexPreparedSet<TKey> prepared => prepared.Values,
            IEnumerable<TKey> typedKeys => typedKeys,
            _ => throw new InvalidOperationException("Direct membership criteria require an enumerable key value or prepared set.")
        };
    }

    private static object RequireCriteriaOperand(object? operand, bool hasOperand)
    {
        return hasOperand
            ? operand ?? throw new InvalidOperationException("The direct criteria descriptor captured a null operand.")
            : throw new InvalidOperationException("The direct criteria descriptor is missing a required operand.");
    }

    private long CountMembershipIdentityObjects(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return CountMembershipIdentityObjects(values.Select(RequireNonNullMembershipValue));
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>(),
            _ => throw new InvalidOperationException("Membership identity count requires an enumerable key value or prepared set.")
        };

        return CountMembershipIdentityObjects(keys);
    }

    private long CountMembershipIdentityObjects(IEnumerable<object> keys)
    {
        long count = 0;
        foreach (object keyValue in keys)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue, nameof(keys)),
                RequireObjectKey(keyValue, nameof(keys)));
            count += reader.Count;
        }

        return count;
    }

    private IReadOnlyList<object> MaterializeMultiRangeIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateMultiRangeIdentityObjects(values, takeLimit).ToList();
    }

    private IReadOnlyList<LibraDexObjectTuple> MaterializeMultiRangeTupleObjects(IReadOnlyList<object?> values)
    {
        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        List<LibraDexObjectTuple> tuples = new();
        for (int i = 0; i < ranges.Length; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)));
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                tuples.Add(new LibraDexObjectTuple(key!, identity!));
            }
        }

        return tuples;
    }

    /// <summary>
    /// Streams identities for a condition-derived multi-range primitive by opening one inclusive range reader per requested extent.<br/>
    /// The primitive exists because date condition permutations such as year membership and year/month tuple sets naturally produce multiple ordered extents over one index.<br/>
    /// Duplicate identity handling remains above this layer so the physical primitive can preserve exact range output semantics.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one range array.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all ranges.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateMultiRangeIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        int returned = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)));
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                yield return identity!;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    private long CountMultiRangeIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        long count = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)));
            count += reader.Count;
        }

        return count;
    }

    private IReadOnlyList<object> MaterializeStructuredComponentIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateStructuredComponentIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded structured date/time key satisfies a condition-derived component predicate.<br/>
    /// The predicate is evaluated against the sortable 64-bit key value, so component-only date branches avoid CLR date reconstruction and avoid text conversion while remaining visibly distinct from ordered range seeks.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled structured component predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateStructuredComponentIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexStructuredComponentPredicate predicate = RequireStructuredComponentPredicate(values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenStructuredComponentRangeReader();
        while (reader.TryReadNextEncodedScalar8KeyIdentity(out ulong encodedKey, out TIdentity identity))
        {
            if (!predicate.Matches(encodedKey))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountStructuredComponentIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexStructuredComponentPredicate predicate = RequireStructuredComponentPredicate(values);
        long count = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedScalar8KeyIdentity(out ulong encodedKey, out _))
        {
            if (predicate.Matches(encodedKey))
            {
                count++;
            }
        }

        return count;
    }

    private static LibraDexIdentityKeyRange[] RequireIdentityKeyRanges(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexIdentityKeyRange[] ranges
            ? ranges
            : throw new InvalidOperationException("Multi-range identity execution requires an array of key ranges.");
    }

    private static LibraDexStructuredComponentPredicate RequireStructuredComponentPredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexStructuredComponentPredicate predicate
            ? predicate
            : throw new InvalidOperationException("Structured component identity execution requires a compiled structured component predicate.");
    }

    private IReadOnlyList<object> MaterializeGuidPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateGuidPatternIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded GUID key satisfies a condition-derived canonical nibble predicate.<br/>
    /// The predicate reads the encoded 16-byte key directly, so GUID prefix/suffix/contains/pattern branches avoid per-row Guid.ToString conversion and remain separate from maintained text projections.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled GUID predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateGuidPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexGuidPatternPredicate predicate = RequireGuidPatternPredicate(values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedScalar16KeyIdentity(out ulong encodedHigh, out ulong encodedLow, out TIdentity identity))
        {
            if (!predicate.Matches(encodedHigh, encodedLow))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountGuidPatternIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexGuidPatternPredicate predicate = RequireGuidPatternPredicate(values);
        long count = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedScalar16KeyIdentity(out ulong encodedHigh, out ulong encodedLow, out _))
        {
            if (predicate.Matches(encodedHigh, encodedLow))
            {
                count++;
            }
        }

        return count;
    }

    private static LibraDexGuidPatternPredicate RequireGuidPatternPredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexGuidPatternPredicate predicate
            ? predicate
            : throw new InvalidOperationException("GUID pattern identity execution requires a compiled GUID pattern predicate.");
    }

    private IReadOnlyList<object> MaterializeBinaryPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateBinaryPatternIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded fixed-width byte-array key satisfies a condition-derived binary byte predicate.<br/>
    /// The predicate reads the key lanes into a stack buffer, so raw binary prefix, suffix, contains, and slice branches avoid decoding every candidate key to an owned byte array.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled binary predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateBinaryPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexBinaryPatternPredicate predicate = RequireBinaryPatternPredicate(values);
        int returned = 0;
        byte[] keyBuffer = new byte[32];
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedKeyBytes(keyBuffer.AsSpan(), out int keyByteCount, out TIdentity identity))
        {
            if (!predicate.Matches(keyBuffer.AsSpan(0, keyByteCount)))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountBinaryPatternIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexBinaryPatternPredicate predicate = RequireBinaryPatternPredicate(values);
        long count = 0;
        Span<byte> keyBuffer = stackalloc byte[32];
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedKeyBytes(keyBuffer, out int keyByteCount, out _))
        {
            if (predicate.Matches(keyBuffer[..keyByteCount]))
            {
                count++;
            }
        }

        return count;
    }

    private static LibraDexBinaryPatternPredicate RequireBinaryPatternPredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexBinaryPatternPredicate predicate
            ? predicate
            : throw new InvalidOperationException("Binary pattern identity execution requires a compiled binary pattern predicate.");
    }

    private IReadOnlyList<object> MaterializeBinaryTypedSliceIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateBinaryTypedSliceIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded fixed-width byte-array key satisfies a condition-derived typed binary-slice predicate.<br/>
    /// The predicate reads only the requested slice from a stack-sized key buffer, so typed binary conditions avoid whole-key decoding while preserving Abraxas-compatible slice semantics.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled typed binary-slice predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateBinaryTypedSliceIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexBinaryTypedSlicePredicate predicate = RequireBinaryTypedSlicePredicate(values);
        int returned = 0;
        byte[] keyBuffer = new byte[32];
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedKeyBytes(keyBuffer.AsSpan(), out int keyByteCount, out TIdentity identity))
        {
            if (!predicate.Matches(keyBuffer.AsSpan(0, keyByteCount)))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountBinaryTypedSliceIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexBinaryTypedSlicePredicate predicate = RequireBinaryTypedSlicePredicate(values);
        long count = 0;
        Span<byte> keyBuffer = stackalloc byte[32];
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNextEncodedKeyBytes(keyBuffer, out int keyByteCount, out _))
        {
            if (predicate.Matches(keyBuffer[..keyByteCount]))
            {
                count++;
            }
        }

        return count;
    }

    private static LibraDexBinaryTypedSlicePredicate RequireBinaryTypedSlicePredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexBinaryTypedSlicePredicate predicate
            ? predicate
            : throw new InvalidOperationException("Binary typed-slice identity execution requires a compiled binary typed-slice predicate.");
    }

    private IReadOnlyList<object> MaterializeBitmaskIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return IterateBitmaskIdentityObjects(values, takeLimit).ToList();
    }

    /// <summary>
    /// Streams identities whose decoded scalar key satisfies a condition-derived bitmask predicate.<br/>
    /// Arbitrary bitmask predicates are not contiguous in ordered key space, so this first bridge performs an explicit compact index scan rather than pretending a range seek exists.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled bitmask predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<object> IterateBitmaskIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexBitmaskPredicate predicate = RequireBitmaskPredicate(values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (!predicate.Matches(key!))
            {
                continue;
            }

            yield return identity!;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }
    }

    private long CountBitmaskIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexBitmaskPredicate predicate = RequireBitmaskPredicate(values);
        long count = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out _))
        {
            if (predicate.Matches(key!))
            {
                count++;
            }
        }

        return count;
    }

    private IReadOnlyList<LibraDexObjectTuple> MaterializeBitmaskTupleObjects(IReadOnlyList<object?> values)
    {
        LibraDexBitmaskPredicate predicate = RequireBitmaskPredicate(values);
        List<LibraDexObjectTuple> tuples = new();
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (predicate.Matches(key!))
            {
                tuples.Add(new LibraDexObjectTuple(key!, identity!));
            }
        }

        return tuples;
    }

    private static LibraDexBitmaskPredicate RequireBitmaskPredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexBitmaskPredicate predicate
            ? predicate
            : throw new InvalidOperationException("Bitmask identity execution requires a compiled bitmask predicate.");
    }

    /// <summary>
    /// Opens the valid CLR key domain for an Abraxas-compatible structured date/time component predicate.<br/>
    /// The ordinary all-reader uses raw encoded bounds, which are not always decodable as DateTime-like CLR values; this helper keeps component scans inside valid typed min/max keys while still reading encoded keys from the cursor.<br/>
    /// </summary>
    /// <returns>A range reader over the full valid structured date/time key domain.</returns>
    private LibraDexRangeReader<TKey, TIdentity> OpenStructuredComponentRangeReader()
    {
        Type keyType = typeof(TKey);
        if (keyType == typeof(DateTime))
        {
            return OpenRangeReader(
                RequireObjectKey(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), nameof(keyType)),
                RequireObjectKey(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), nameof(keyType)));
        }

        if (keyType == typeof(DateTimeOffset))
        {
            return OpenRangeReader(
                RequireObjectKey(DateTimeOffset.MinValue.ToUniversalTime(), nameof(keyType)),
                RequireObjectKey(DateTimeOffset.MaxValue.ToUniversalTime(), nameof(keyType)));
        }

        if (keyType == typeof(DateOnly))
        {
            return OpenRangeReader(
                RequireObjectKey(DateOnly.MinValue, nameof(keyType)),
                RequireObjectKey(DateOnly.MaxValue, nameof(keyType)));
        }

        if (keyType == typeof(TimeOnly))
        {
            return OpenRangeReader(
                RequireObjectKey(TimeOnly.MinValue, nameof(keyType)),
                RequireObjectKey(TimeOnly.MaxValue, nameof(keyType)));
        }

        throw new NotSupportedException($"Structured component predicates require a structured date/time key type, not {keyType.FullName}.");
    }

    private static object RequireNonNullMembershipValue(object? value)
    {
        return value ?? throw new InvalidOperationException("Membership identity execution cannot use a null key value.");
    }

    private static TKey[] RequirePreparedObjectSet(LibraDexPreparedObjectSet set, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.KeyType != typeof(TKey))
        {
            throw new ArgumentException($"Expected a prepared set for key type {typeof(TKey).FullName}; received {set.KeyType.FullName}.", parameterName);
        }

        TKey[] typedKeys = new TKey[set.Values.Count];
        for (int i = 0; i < set.Values.Count; i++)
        {
            typedKeys[i] = RequireObjectKey(set.Values[i], parameterName);
        }

        return typedKeys;
    }

    private LibraDexGenericRangeReadResult ReadScalar8Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities,
        bool enableCoalescing,
        int coalescedShelfScratchCount)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        try
        {
            Scalar8Scalar8RangeReadOptions options = enableCoalescing
                ? Scalar8Scalar8RangeReadOptions.CoalescingCached with { CoalescedShelfScratchCount = coalescedShelfScratchCount }
                : Scalar8Scalar8RangeReadOptions.ConservativeCached;
            Scalar8Scalar8RangeReadResult result = session.ReadEncodedScalar8Scalar8IdentityRangePooled(
                new Scalar8Scalar8IndexHandle(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB),
                lowerEncodedKey,
                upperEncodedKey,
                options,
                encodedIdentities.AsSpan(0, identities.Length));

            for (int i = 0; i < result.IdentityCount; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(
                result.IdentityCount,
                result.UsedPooledScratch,
                UsedEncodedIdentityScratch: true,
                result.CoalescingEnabled,
                result.RangeScratchShelfCapacity);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
        }
    }

    private Scalar8Scalar8RangeReader OpenScalar8Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        return session.OpenScalar8Scalar8RangeReader(RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, lowerEncodedKey, upperEncodedKey);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar16Scalar8Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar8IdentityRange(
                RootRouterOffset,
                Scalar16Scalar8Profile.Default32KiB,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: 8,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar16Scalar8Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar16Scalar8RangeReader OpenScalar16Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar8RangeReader(RootRouterOffset, Scalar16Scalar8Profile.Default32KiB, lowerHigh, lowerLow, upperHigh, upperLow);
    }

    private LibraDexGenericRangeReadResult ReadScalar8Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar8Scalar16Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar8Scalar16IdentityRange(
                RootRouterOffset,
                Scalar8Scalar16Profile.Default32KiB,
                lowerEncodedKey,
                upperEncodedKey,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar8Scalar16Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar8Scalar16RangeReader OpenScalar8Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        return session.OpenScalar8Scalar16RangeReader(RootRouterOffset, Scalar8Scalar16Profile.Default32KiB, lowerEncodedKey, upperEncodedKey);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Scalar16Scalar16Profile.Default32KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar16IdentityRange(
                RootRouterOffset,
                Scalar16Scalar16Profile.Default32KiB,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Scalar16Scalar16Profile.Default32KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Scalar16Scalar16RangeReader OpenScalar16Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar16RangeReader(RootRouterOffset, Scalar16Scalar16Profile.Default32KiB, lowerHigh, lowerLow, upperHigh, upperLow);
    }

    private LibraDexGenericRangeReadResult ReadFixed32Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Fixed32Scalar8Profile.Default64KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar8IdentityRange(
                RootRouterOffset,
                Fixed32Scalar8Profile.Default64KiB,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 32,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Fixed32Scalar8Profile.Default64KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentities[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(encodedIdentities);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Fixed32Scalar8RangeReader OpenFixed32Scalar8RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar8RangeReader(RootRouterOffset, Fixed32Scalar8Profile.Default64KiB, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }

    /// <summary>
    /// Reads a generic inclusive key range from the routed `FS32-16` physical shape.<br/>
    /// The public lower and upper keys are encoded as four fixed-key lanes, matching the persisted shelf order.<br/>
    /// Matching 16-byte identities are copied into pooled high/low scratch lanes and decoded into the caller-owned destination span.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <param name="identities">The caller-owned destination span that receives decoded identities.</param>
    /// <returns>The generic range-read result describing copied identity count and scratch usage.</returns>
    private LibraDexGenericRangeReadResult ReadFixed32Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(Fixed32Scalar16Profile.Default64KiB.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar16IdentityRange(
                RootRouterOffset,
                Fixed32Scalar16Profile.Default64KiB,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 32,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, Fixed32Scalar16Profile.Default64KiB.ShelfExtentSize),
                targetKindCache);

            for (int i = 0; i < count; i++)
            {
                identities[i] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHighs[i], identityLows[i]);
            }

            return new LibraDexGenericRangeReadResult(count, UsedPooledScratch: true, UsedEncodedIdentityScratch: true, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(identityHighs);
            ArrayPool<ulong>.Shared.Return(identityLows);
            ArrayPool<byte>.Shared.Return(shelfScratch);
        }
    }

    private Fixed32Scalar16RangeReader OpenFixed32Scalar16RangeReader(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar16RangeReader(RootRouterOffset, Fixed32Scalar16Profile.Default64KiB, lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3);
    }
}
