using System.Collections;
using System.Diagnostics;
using System.Globalization;

namespace LibraDex;

/// <summary>
/// Provides the optional identity-to-indexed-key lookup layer for one index set.<br/>
/// This first implementation is a runtime inverse map built from selected forward indexes; it reserves the public lifecycle and condition-builder shape for the durable maintained inverse index.<br/>
/// </summary>
public sealed class CatalogIndexSetInverse
{
    private readonly CatalogIdentityGroupIndexes indexSet;
    private readonly HashSet<string> selectedIndexes = new(StringComparer.Ordinal);
    private readonly List<string> selectedIndexOrder = new();
    private readonly object sync = new();
    private Dictionary<object, CatalogIndexSetInverseEntry> identityKeys = new(LibraDexObjectValueComparer.Instance);
    private CatalogIndexSetInverseSchema schema = CatalogIndexSetInverseSchema.Empty;
    private LibraDexStatsDelta lastCatalogStats;
    private CatalogIndexSetInverseBuildStats lastBuildStats;
    private readonly Dictionary<int, IEnumerator<LibraDexObjectTuple>> lazyCursors = new();
    private readonly Dictionary<string, IIndex> openedIndexes = new(StringComparer.Ordinal);
    private bool[] lazyIndexComplete = Array.Empty<bool>();
    private Stopwatch? lazyBuildStopwatch;
    private long lazyTupleCount;
    private bool isCreated;
    private bool includeAllWhenEmpty = true;
    private bool hasBuildSnapshot;
    private bool isLazyBuild;

    internal CatalogIndexSetInverse(CatalogIdentityGroupIndexes indexSet)
    {
        this.indexSet = indexSet;
    }

    /// <summary>
    /// Gets the index-set name that owns this inverse key map.<br/>
    /// </summary>
    public string IndexSetName => indexSet.Name;

    /// <summary>
    /// Gets whether the optional inverse key map has been enabled for this index set.<br/>
    /// </summary>
    public bool IsCreated => isCreated;

    /// <summary>
    /// Gets whether the current runtime inverse map reflects the catalog mutation counters observed at the last build.<br/>
    /// This is a cheap session-local freshness check; explicit durable validation should use <see cref="Validate"/>.<br/>
    /// </summary>
    public bool IsFresh
    {
        get
        {
            lock (sync)
            {
                return isCreated && hasBuildSnapshot && !NeedsRebuildCore();
            }
        }
    }

    /// <summary>
    /// Gets whether the configured inverse has completely scanned every participating forward index and still matches the observed catalog mutation counters.<br/>
    /// Positive rows discovered during a lazy build may be used before this becomes true; only an in-sync inverse may treat a missing row as proof of absence without continuing the forward walk.<br/>
    /// </summary>
    public bool IsInSync => IsFresh;

    /// <summary>
    /// Gets the current runtime lifecycle state of this optional inverse map.<br/>
    /// The state is diagnostic metadata for the current catalog session; durable inverse storage can preserve the same public state model when connected.<br/>
    /// </summary>
    public CatalogIndexSetInverseState State
    {
        get
        {
            lock (sync)
            {
                if (!isCreated)
                    return CatalogIndexSetInverseState.Disabled;
                if (hasBuildSnapshot && !NeedsRebuildCore())
                    return CatalogIndexSetInverseState.Current;
                if (isLazyBuild && !hasBuildSnapshot)
                    return CatalogIndexSetInverseState.Building;
                return CatalogIndexSetInverseState.Stale;
            }
        }
    }

    /// <summary>
    /// Gets the most recent inverse build or validation statistics.<br/>
    /// Values are session-local and describe the runtime map, not a durable physical inverse page layout.<br/>
    /// </summary>
    public CatalogIndexSetInverseBuildStats LastBuildStats
    {
        get
        {
            lock (sync)
            {
                return lastBuildStats;
            }
        }
    }

    /// <summary>
    /// Gets the currently selected forward index names for this inverse map.<br/>
    /// When the inverse was created without an explicit selection, this resolves to the current index names in the index set.<br/>
    /// </summary>
    public string[] IncludedIndexNames
    {
        get
        {
            lock (sync)
            {
                return ResolveIncludedIndexNames(indexSet.List());
            }
        }
    }

    /// <summary>
    /// Enables the inverse key map and optionally chooses the forward indexes that participate.<br/>
    /// Creation also performs the first runtime build from current forward index tuples; later query calls refresh from the current forward indexes until durable maintenance is connected.<br/>
    /// </summary>
    /// <param name="indexNames">Optional index names to include; when omitted, all current indexes in the index set participate.</param>
    /// <returns>This inverse surface.</returns>
    public CatalogIndexSetInverse Create(params string[] indexNames)
    {
        lock (sync)
        {
            isCreated = true;
            if (indexNames.Length > 0)
            {
                includeAllWhenEmpty = false;
                selectedIndexes.Clear();
                selectedIndexOrder.Clear();
                for (int i = 0; i < indexNames.Length; i++)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(indexNames[i]);
                    if (selectedIndexes.Add(indexNames[i]))
                    {
                        selectedIndexOrder.Add(indexNames[i]);
                    }
                }
            }

            isLazyBuild = false;
            DisposeLazyCursors();
            RebuildCore();
            return this;
        }
    }

    /// <summary>
    /// Enables an explicitly configured lazy runtime inverse over the selected forward indexes.<br/>
    /// Creation records the schema and mutation snapshot but performs no forward scan; later identity checks first consult discovered inverse rows and then resume one forward-index walk while adding every encountered tuple to the inverse.<br/>
    /// This is opt-in because an ordinary read should not silently create inverse state for an index set whose owner did not request it.<br/>
    /// </summary>
    /// <param name="indexNames">Optional index names to include; when omitted, all indexes currently in the index set participate.<br/></param>
    /// <returns>This inverse surface.<br/></returns>
    public CatalogIndexSetInverse CreateLazy(params string[] indexNames)
    {
        lock (sync)
        {
            isCreated = true;
            ConfigureSelection(indexNames);
            isLazyBuild = true;
            ResetLazyBuildCore();
            return this;
        }
    }

    /// <summary>
    /// Adds one forward index to the inverse key-map participation set.<br/>
    /// This is useful when an index set was created with a selected-key inverse and a later condition needs another indexed value by identity.<br/>
    /// </summary>
    /// <param name="indexName">The forward index name inside this index set.</param>
    /// <returns>This inverse surface.</returns>
    public CatalogIndexSetInverse Include(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            isCreated = true;
            includeAllWhenEmpty = false;
            if (selectedIndexes.Add(indexName))
            {
                selectedIndexOrder.Add(indexName);
            }
            if (isLazyBuild)
                ResetLazyBuildCore();
            else
                RebuildCore();
            return this;
        }
    }

    /// <summary>
    /// Adds one opened forward index to the inverse key-map participation set.<br/>
    /// The handle must belong to this index set, keeping inverse entries inside one identity universe.<br/>
    /// </summary>
    /// <param name="index">The opened forward index.</param>
    /// <returns>This inverse surface.</returns>
    public CatalogIndexSetInverse Include(IIndex index)
    {
        ValidateIndex(index);
        lock (sync)
        {
            openedIndexes[index.Name] = index;
        }
        return Include(index.Name);
    }

    internal bool Includes(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            return isCreated && schema.TryGetOrdinal(indexName, out _);
        }
    }

    internal void EnsureLazyIndex(IIndex index)
    {
        ValidateIndex(index);
        lock (sync)
        {
            openedIndexes[index.Name] = index;
            if (isCreated && schema.TryGetOrdinal(index.Name, out _))
                return;

            isCreated = true;
            includeAllWhenEmpty = false;
            if (selectedIndexes.Add(index.Name))
                selectedIndexOrder.Add(index.Name);
            isLazyBuild = true;
            ResetLazyBuildCore();
        }
    }

    internal void BuildIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            EnsureCreated();
            if (!schema.TryGetOrdinal(indexName, out int ordinal))
                throw new InvalidOperationException($"Index '{indexName}' is not included in the '{IndexSetName}' identity inversion.");
            if (!isLazyBuild)
            {
                if (NeedsRebuildCore())
                    RebuildCore();
                return;
            }
            if (!lastCatalogStats.Equals(indexSet.Catalog.Stats.Current))
                ResetLazyBuildCore();
            if (!schema.TryGetOrdinal(indexName, out ordinal))
                throw new InvalidOperationException($"Index '{indexName}' is no longer included in the '{IndexSetName}' identity inversion.");
            if (!lazyIndexComplete[ordinal])
                AdvanceLazyIndexCore(ordinal, targetIdentity: null, out _);
        }
    }

    internal void Exclude(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            openedIndexes.Remove(indexName);
            if (!isCreated || !schema.TryGetOrdinal(indexName, out _))
                return;

            includeAllWhenEmpty = false;
            selectedIndexes.Remove(indexName);
            selectedIndexOrder.RemoveAll(candidate => string.Equals(candidate, indexName, StringComparison.Ordinal));
            if (selectedIndexOrder.Count == 0)
            {
                isCreated = false;
                isLazyBuild = false;
                hasBuildSnapshot = false;
                lastBuildStats = default;
                DisposeLazyCursors();
                lazyIndexComplete = Array.Empty<bool>();
                lazyBuildStopwatch = null;
                lazyTupleCount = 0;
                schema = CatalogIndexSetInverseSchema.Empty;
                identityKeys = new Dictionary<object, CatalogIndexSetInverseEntry>(LibraDexObjectValueComparer.Instance);
                return;
            }

            isLazyBuild = true;
            ResetLazyBuildCore();
        }
    }

    internal IdentityLookupState GetIndexState(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            if (!isCreated || !schema.TryGetOrdinal(indexName, out int ordinal))
                return IdentityLookupState.NotCreated;
            if (!lastCatalogStats.Equals(indexSet.Catalog.Stats.Current))
                return IdentityLookupState.Partial;
            if (!isLazyBuild)
                return hasBuildSnapshot ? IdentityLookupState.Complete : IdentityLookupState.Partial;
            return lazyIndexComplete.Length > ordinal && lazyIndexComplete[ordinal]
                ? IdentityLookupState.Complete
                : IdentityLookupState.Partial;
        }
    }

    internal object[] GetDuplicateIdentities(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            PrepareIndexForCompleteRead(indexName, out int ordinal);
            List<object> duplicates = new();
            foreach ((object identity, CatalogIndexSetInverseEntry entry) in identityKeys)
            {
                List<object?>? values = entry.Values[ordinal];
                if (HasMultipleDistinctValues(values))
                    duplicates.Add(identity);
            }

            return duplicates.ToArray();
        }
    }

    internal LibraDexObjectTuple[] GetEntriesForDuplicateIdentities(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            PrepareIndexForCompleteRead(indexName, out int ordinal);
            List<LibraDexObjectTuple> entries = new();
            foreach ((object identity, CatalogIndexSetInverseEntry entry) in identityKeys)
            {
                List<object?>? values = entry.Values[ordinal];
                if (!HasMultipleDistinctValues(values))
                    continue;
                for (int i = 0; i < values!.Count; i++)
                    entries.Add(new LibraDexObjectTuple(values[i], identity));
            }

            return entries.ToArray();
        }
    }

    internal object[] GetSingletonIdentities(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            PrepareIndexForCompleteRead(indexName, out int ordinal);
            List<object> singletons = new();
            foreach ((object identity, CatalogIndexSetInverseEntry entry) in identityKeys)
            {
                if (HasExactlyOneDistinctValue(entry.Values[ordinal]))
                    singletons.Add(identity);
            }

            return singletons.ToArray();
        }
    }

    internal LibraDexObjectTuple[] GetEntriesForSingletonIdentities(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        lock (sync)
        {
            PrepareIndexForCompleteRead(indexName, out int ordinal);
            List<LibraDexObjectTuple> entries = new();
            foreach ((object identity, CatalogIndexSetInverseEntry entry) in identityKeys)
            {
                List<object?>? values = entry.Values[ordinal];
                if (!HasExactlyOneDistinctValue(values))
                    continue;

                object? first = values![0];
                entries.Add(new LibraDexObjectTuple(first, identity));
            }

            return entries.ToArray();
        }
    }

    private void PrepareIndexForCompleteRead(string indexName, out int ordinal)
    {
        EnsureCreated();
        if (!schema.TryGetOrdinal(indexName, out ordinal))
            throw new InvalidOperationException($"Index '{indexName}' is not included in the '{IndexSetName}' identity inversion.");
        if (!lastCatalogStats.Equals(indexSet.Catalog.Stats.Current))
            ResetLazyBuildCore();
        if (!schema.TryGetOrdinal(indexName, out ordinal))
            throw new InvalidOperationException($"Index '{indexName}' is no longer included in the '{IndexSetName}' identity inversion.");
        if (isLazyBuild && !lazyIndexComplete[ordinal])
            AdvanceLazyIndexCore(ordinal, targetIdentity: null, out _);
        else if (!isLazyBuild && NeedsRebuildCore())
            RebuildCore();
    }

    private static bool HasMultipleDistinctValues(List<object?>? values)
    {
        if (values is null || values.Count < 2)
            return false;
        object? first = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (!LibraDexObjectTuple.ValueEquals(first, values[i]))
                return true;
        }

        return false;
    }

    private static bool HasExactlyOneDistinctValue(List<object?>? values)
    {
        if (values is null || values.Count == 0)
            return false;
        object? first = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (!LibraDexObjectTuple.ValueEquals(first, values[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the runtime inverse key map from current forward index tuples.<br/>
    /// Durable inverse storage can later replace this with an on-disk repair/repack operation while preserving the same public verb.<br/>
    /// </summary>
    /// <returns>This inverse surface.</returns>
    public CatalogIndexSetInverse Rebuild()
    {
        lock (sync)
        {
            EnsureCreated();
            isLazyBuild = false;
            DisposeLazyCursors();
            RebuildCore();
            return this;
        }
    }

    /// <summary>
    /// Drops the runtime inverse key map and disables `.WhereInverse` execution for this index set until <see cref="Create"/> is called again.<br/>
    /// </summary>
    public void Drop()
    {
        lock (sync)
        {
            isCreated = false;
            isLazyBuild = false;
            hasBuildSnapshot = false;
            lastBuildStats = default;
            DisposeLazyCursors();
            lazyIndexComplete = Array.Empty<bool>();
            lazyBuildStopwatch = null;
            lazyTupleCount = 0;
            schema = CatalogIndexSetInverseSchema.Empty;
            identityKeys = new Dictionary<object, CatalogIndexSetInverseEntry>(LibraDexObjectValueComparer.Instance);
        }
    }

    /// <summary>
    /// Rebuilds and validates that the runtime inverse key map can read every selected forward index.<br/>
    /// </summary>
    /// <returns>A validation result containing the number of identities and key tuples captured.</returns>
    public CatalogIndexSetInverseValidationResult Validate()
    {
        lock (sync)
        {
            EnsureCreated();
            EnsureCurrentCore();

            return new CatalogIndexSetInverseValidationResult(
                lastBuildStats.IdentityCount,
                lastBuildStats.KeyTupleCount,
                lastBuildStats.IncludedIndexCount,
                lastBuildStats.Elapsed,
                lastBuildStats.EstimatedMapBytes);
        }
    }

    /// <summary>
    /// Gets a structural snapshot of the runtime inverse map.<br/>
    /// Shape metrics are intended for tests and diagnostics: they expose identity count, tuple count, selected-index count, multi-value identity/key buckets, freshness, and the estimated managed-map byte footprint.<br/>
    /// </summary>
    /// <returns>A shape snapshot for the current runtime inverse map.</returns>
    public CatalogIndexSetInverseShape GetShape()
    {
        lock (sync)
        {
            EnsureCurrentCore();
            long multiValueBuckets = 0;
            foreach (CatalogIndexSetInverseEntry entry in identityKeys.Values)
            {
                for (int i = 0; i < entry.Values.Length; i++)
                {
                    List<object?>? values = entry.Values[i];
                    if (values is not null && values.Count > 1)
                    {
                        multiValueBuckets++;
                    }
                }
            }

            return new CatalogIndexSetInverseShape(
                IndexSetName,
                lastBuildStats.IncludedIndexCount,
                lastBuildStats.IdentityCount,
                lastBuildStats.KeyTupleCount,
                multiValueBuckets,
                isCreated && hasBuildSnapshot && !NeedsRebuildCore(),
                lastBuildStats.EstimatedMapBytes,
                schema.Names);
        }
    }

    /// <summary>
    /// Gets selected indexed keys for caller-supplied identities without hydrating external objects.<br/>
    /// Missing identities are omitted; missing selected keys are simply absent from that identity's key dictionary.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The caller identity type.</typeparam>
    /// <param name="identities">The identities to resolve.</param>
    /// <param name="keys">Optional key names to project; when omitted, every inverse-enabled key for each identity is returned.</param>
    /// <returns>One inverse row per identity found in the map.</returns>
    public IReadOnlyList<CatalogIndexSetInverseRow<TIdentity>> Get<TIdentity>(IEnumerable<TIdentity> identities, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (sync)
        {
            EnsureCurrent();
            List<CatalogIndexSetInverseRow<TIdentity>> rows = new();
            foreach (TIdentity identity in identities)
            {
                if (identity is null || !identityKeys.TryGetValue(identity, out CatalogIndexSetInverseEntry? entry))
                {
                    continue;
                }

                Dictionary<string, IReadOnlyList<object?>> projected = new(StringComparer.Ordinal);
                if (keys.Length == 0)
                {
                    for (int i = 0; i < schema.Names.Length; i++)
                    {
                        List<object?>? values = entry.Values[i];
                        if (values is not null)
                        {
                            projected.Add(schema.Names[i], values);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < keys.Length; i++)
                    {
                        ArgumentException.ThrowIfNullOrWhiteSpace(keys[i]);
                        if (schema.TryGetOrdinal(keys[i], out int ordinal))
                        {
                            List<object?>? keyValues = entry.Values[ordinal];
                            if (keyValues is not null)
                            {
                                projected.Add(keys[i], keyValues);
                            }
                        }
                    }
                }

                rows.Add(new CatalogIndexSetInverseRow<TIdentity>(identity, projected));
            }

            return rows;
        }
    }

    internal IEnumerable<object> IdentitiesWhere(Func<object, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        PrepareForQuery();
        object[] identities = identityKeys.Keys.ToArray();
        for (int i = 0; i < identities.Length; i++)
        {
            object identity = identities[i];
            if (predicate(identity))
            {
                yield return identity;
            }
        }
    }

    internal bool TryIdentityExists(string indexName, object identity, out bool exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(identity);
        lock (sync)
        {
            if (!isCreated || !schema.TryGetOrdinal(indexName, out int ordinal))
            {
                exists = false;
                return false;
            }

            if (!isLazyBuild)
            {
                EnsureCurrentCore();
                exists = HasIndexedValue(identity, ordinal);
                return true;
            }

            if (!lastCatalogStats.Equals(indexSet.Catalog.Stats.Current))
                ResetLazyBuildCore();

            if (HasIndexedValue(identity, ordinal))
            {
                exists = true;
                return true;
            }

            if (lazyIndexComplete[ordinal])
            {
                exists = false;
                return true;
            }

            AdvanceLazyIndexCore(ordinal, identity, out exists);
            return true;
        }
    }

    internal bool AnyKey(object identity, string indexName, Func<object?, bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(predicate);
        EnsureAvailable();
        if (!schema.TryGetOrdinal(indexName, out int ordinal) ||
            !identityKeys.TryGetValue(identity, out CatalogIndexSetInverseEntry? entry))
        {
            return false;
        }

        List<object?>? keyValues = entry.Values[ordinal];
        if (keyValues is null)
        {
            return false;
        }

        for (int i = 0; i < keyValues.Count; i++)
        {
            if (predicate(keyValues[i]))
            {
                return true;
            }
        }

        return false;
    }

    internal bool AnyKeyPair(object identity, string leftIndexName, string rightIndexName, Func<object?, object?, bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftIndexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightIndexName);
        ArgumentNullException.ThrowIfNull(predicate);
        EnsureAvailable();
        if (!schema.TryGetOrdinal(leftIndexName, out int leftOrdinal) ||
            !schema.TryGetOrdinal(rightIndexName, out int rightOrdinal) ||
            !identityKeys.TryGetValue(identity, out CatalogIndexSetInverseEntry? entry))
        {
            return false;
        }

        List<object?>? leftValues = entry.Values[leftOrdinal];
        List<object?>? rightValues = entry.Values[rightOrdinal];
        if (leftValues is null || rightValues is null)
        {
            return false;
        }

        for (int i = 0; i < leftValues.Count; i++)
        {
            for (int j = 0; j < rightValues.Count; j++)
            {
                if (predicate(leftValues[i], rightValues[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal IEnumerable<object> IdentityInSet(IEnumerable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (object? value in source)
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    internal IIndex ValidateIndex(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(index.Group, IndexSetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied index handle belongs to a different LibraDex index set.");
        }

        return index;
    }

    internal void PrepareForQuery()
    {
        lock (sync)
        {
            EnsureCurrentCore();
        }
    }

    private void EnsureCurrent()
    {
        PrepareForQuery();
    }

    private void EnsureAvailable()
    {
        if (!isCreated)
        {
            throw new InvalidOperationException($"Index set '{IndexSetName}' does not have an inverse key map. Call catalog.CreateIndexSet(\"{IndexSetName}\", createInverseIndex: true) or catalog[\"{IndexSetName}\"].Inverse.Create(...).");
        }
    }

    private void EnsureCreated()
    {
        EnsureAvailable();
    }

    private void EnsureCurrentCore()
    {
        EnsureCreated();
        if (isLazyBuild)
        {
            if (!lastCatalogStats.Equals(indexSet.Catalog.Stats.Current))
                ResetLazyBuildCore();
            CompleteLazyBuildCore();
        }
        else if (NeedsRebuildCore())
            RebuildCore();
    }

    private void RebuildCore()
    {
        DisposeLazyCursors();
        Stopwatch stopwatch = Stopwatch.StartNew();
        CatalogIndexInfo[] infos = indexSet.List();
        CatalogIndexSetInverseSchema nextSchema = CatalogIndexSetInverseSchema.Create(ResolveIncludedIndexNames(infos));
        Dictionary<object, CatalogIndexSetInverseEntry> next = new(LibraDexObjectValueComparer.Instance);
        long tupleCount = 0;
        for (int schemaOrdinal = 0; schemaOrdinal < nextSchema.Names.Length; schemaOrdinal++)
        {
            string indexName = nextSchema.Names[schemaOrdinal];
            IIndex index = ResolveIndex(indexName);
            foreach (LibraDexObjectTuple tuple in IterateAllTuples(indexName, index))
            {
                if (!next.TryGetValue(tuple.Identity, out CatalogIndexSetInverseEntry? entry))
                {
                    entry = new CatalogIndexSetInverseEntry(nextSchema.Names.Length);
                    next.Add(tuple.Identity, entry);
                }

                entry.Add(schemaOrdinal, tuple.Key);
                tupleCount++;
            }
        }

        stopwatch.Stop();
        schema = nextSchema;
        identityKeys = next;
        lastCatalogStats = indexSet.Catalog.Stats.Current;
        hasBuildSnapshot = true;
        lastBuildStats = new CatalogIndexSetInverseBuildStats(
            DateTimeOffset.UtcNow,
            stopwatch.Elapsed,
            nextSchema.Names.Length,
            next.Count,
            tupleCount,
            EstimateMapBytes(next, nextSchema.Names.Length, tupleCount));
    }

    private void ConfigureSelection(string[] indexNames)
    {
        if (indexNames.Length == 0)
        {
            includeAllWhenEmpty = true;
            selectedIndexes.Clear();
            selectedIndexOrder.Clear();
            return;
        }

        includeAllWhenEmpty = false;
        selectedIndexes.Clear();
        selectedIndexOrder.Clear();
        for (int i = 0; i < indexNames.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(indexNames[i]);
            if (selectedIndexes.Add(indexNames[i]))
                selectedIndexOrder.Add(indexNames[i]);
        }
    }

    private void ResetLazyBuildCore()
    {
        DisposeLazyCursors();
        string[] names = ResolveIncludedIndexNames(indexSet.List());
        schema = CatalogIndexSetInverseSchema.Create(names);
        identityKeys = new Dictionary<object, CatalogIndexSetInverseEntry>(LibraDexObjectValueComparer.Instance);
        lazyIndexComplete = new bool[names.Length];
        lazyTupleCount = 0;
        lazyBuildStopwatch = Stopwatch.StartNew();
        lastCatalogStats = indexSet.Catalog.Stats.Current;
        lastBuildStats = new CatalogIndexSetInverseBuildStats(
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            names.Length,
            0,
            0,
            0);
        hasBuildSnapshot = names.Length == 0;
        if (hasBuildSnapshot)
            CompleteLazyBuildStatsCore();
    }

    private void CompleteLazyBuildCore()
    {
        for (int ordinal = 0; ordinal < schema.Names.Length; ordinal++)
        {
            if (!lazyIndexComplete[ordinal])
                AdvanceLazyIndexCore(ordinal, targetIdentity: null, out _);
        }
    }

    private void AdvanceLazyIndexCore(int ordinal, object? targetIdentity, out bool found)
    {
        found = false;
        if (!lazyCursors.TryGetValue(ordinal, out IEnumerator<LibraDexObjectTuple>? cursor))
        {
            string indexName = schema.Names[ordinal];
            IIndex index = ResolveIndex(indexName);
            cursor = IterateAllTuples(indexName, index).GetEnumerator();
            lazyCursors.Add(ordinal, cursor);
        }

        while (cursor.MoveNext())
        {
            LibraDexObjectTuple tuple = cursor.Current;
            AddInverseTuple(ordinal, tuple);
            if (targetIdentity is not null && LibraDexObjectValueComparer.Instance.Equals(tuple.Identity, targetIdentity))
            {
                found = true;
                return;
            }
        }

        cursor.Dispose();
        lazyCursors.Remove(ordinal);
        lazyIndexComplete[ordinal] = true;
        for (int i = 0; i < lazyIndexComplete.Length; i++)
        {
            if (!lazyIndexComplete[i])
                return;
        }

        hasBuildSnapshot = true;
        CompleteLazyBuildStatsCore();
    }

    private void AddInverseTuple(int ordinal, LibraDexObjectTuple tuple)
    {
        if (!identityKeys.TryGetValue(tuple.Identity, out CatalogIndexSetInverseEntry? entry))
        {
            entry = new CatalogIndexSetInverseEntry(schema.Names.Length);
            identityKeys.Add(tuple.Identity, entry);
        }

        entry.Add(ordinal, tuple.Key);
        lazyTupleCount++;
    }

    private bool HasIndexedValue(object identity, int ordinal)
        => identityKeys.TryGetValue(identity, out CatalogIndexSetInverseEntry? entry) && entry.Values[ordinal] is not null;

    private void CompleteLazyBuildStatsCore()
    {
        lazyBuildStopwatch?.Stop();
        TimeSpan elapsed = lazyBuildStopwatch?.Elapsed ?? TimeSpan.Zero;
        lastCatalogStats = indexSet.Catalog.Stats.Current;
        lastBuildStats = new CatalogIndexSetInverseBuildStats(
            DateTimeOffset.UtcNow,
            elapsed,
            schema.Names.Length,
            identityKeys.Count,
            lazyTupleCount,
            EstimateMapBytes(identityKeys, schema.Names.Length, lazyTupleCount));
    }

    private void DisposeLazyCursors()
    {
        foreach (IEnumerator<LibraDexObjectTuple> cursor in lazyCursors.Values)
            cursor.Dispose();
        lazyCursors.Clear();
    }

    private bool NeedsRebuildCore()
        => !hasBuildSnapshot || !lastCatalogStats.Equals(indexSet.Catalog.Stats.Current);

    private string[] ResolveIncludedIndexNames(CatalogIndexInfo[] infos)
    {
        if (!includeAllWhenEmpty)
        {
            return selectedIndexOrder.ToArray();
        }

        string[] current = new string[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            current[i] = infos[i].Name;
        }

        return current;
    }

    private static long EstimateMapBytes(Dictionary<object, CatalogIndexSetInverseEntry> map, int schemaKeyCount, long tupleCount)
    {
        long perIdentityBytes = map.Count * (64L + schemaKeyCount * 8L);
        long listBytes = 0;
        foreach (CatalogIndexSetInverseEntry entry in map.Values)
        {
            for (int i = 0; i < entry.Values.Length; i++)
            {
                if (entry.Values[i] is not null)
                {
                    listBytes += 40L;
                }
            }
        }

        return perIdentityBytes + listBytes + tupleCount * 16L;
    }

    private IIndex ResolveIndex(string indexName)
    {
        if (openedIndexes.TryGetValue(indexName, out IIndex? index))
            return index;
        return indexSet.Index(indexName);
    }

    private static IEnumerable<LibraDexObjectTuple> IterateAllTuples(string indexName, IIndex index)
    {
        LibraDexIdentityPrimitiveRequest request = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
        if (index is IIdentityPrimitiveTupleStreamer streamer)
        {
            return streamer.IterateTuplePrimitive(request);
        }

        if (index is IIdentityPrimitiveTupleExecutor executor)
        {
            return executor.ExecuteTuplePrimitive(request);
        }

        throw new NotSupportedException($"Index '{indexName}' cannot expose key/identity tuples for the inverse key map.");
    }
}

/// <summary>
/// Identifies the runtime lifecycle state of an optional index-set inverse map.<br/>
/// </summary>
public enum CatalogIndexSetInverseState
{
    /// <summary>
    /// No inverse map has been enabled for this index set.<br/>
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// An explicitly configured lazy inverse is accepting identity checks and incrementally scanning participating forward indexes.<br/>
    /// Positive discovered rows are usable, while an undiscovered identity still requires the resumable forward walk.<br/>
    /// </summary>
    Building = 1,

    /// <summary>
    /// Every participating forward index has been scanned and the inverse matches the observed catalog mutation counters.<br/>
    /// Both hits and misses are authoritative for the current session snapshot.<br/>
    /// </summary>
    Current = 2,

    /// <summary>
    /// The inverse was built previously but catalog mutation counters no longer match its observed source state.<br/>
    /// Eager maps rebuild before use; lazy maps reset their resumable build before the next identity check.<br/>
    /// </summary>
    Stale = 3
}

/// <summary>
/// Summarizes an inverse key-map validation or rebuild pass.<br/>
/// </summary>
/// <param name="IdentityCount">The number of identities captured in the inverse map.</param>
/// <param name="KeyTupleCount">The number of indexed key tuples captured across those identities.</param>
/// <param name="IncludedIndexCount">The number of forward indexes included in the inverse map.</param>
/// <param name="BuildElapsed">The elapsed time spent building the runtime map when a build was needed.</param>
/// <param name="EstimatedMapBytes">A rough managed-memory estimate for the runtime map.</param>
public readonly record struct CatalogIndexSetInverseValidationResult(
    long IdentityCount,
    long KeyTupleCount,
    int IncludedIndexCount = 0,
    TimeSpan BuildElapsed = default,
    long EstimatedMapBytes = 0);

/// <summary>
/// Describes the latest runtime inverse key-map build.<br/>
/// The counters are intentionally cheap and session-local so callers can reason about rebuild cost without forcing physical storage diagnostics.<br/>
/// </summary>
/// <param name="BuiltUtc">The UTC time when the runtime map was built.</param>
/// <param name="Elapsed">The elapsed build time.</param>
/// <param name="IncludedIndexCount">The number of forward indexes included in the map.</param>
/// <param name="IdentityCount">The number of distinct identities captured.</param>
/// <param name="KeyTupleCount">The number of key/identity tuples captured.</param>
/// <param name="EstimatedMapBytes">A rough managed-memory estimate for the runtime map.</param>
public readonly record struct CatalogIndexSetInverseBuildStats(
    DateTimeOffset BuiltUtc,
    TimeSpan Elapsed,
    int IncludedIndexCount,
    long IdentityCount,
    long KeyTupleCount,
    long EstimatedMapBytes);

/// <summary>
/// Describes the current runtime inverse key-map shape for diagnostics and shape tests.<br/>
/// This is not a persisted layout contract; durable inverse storage will need its own physical shape snapshot when implemented.<br/>
/// </summary>
/// <param name="IndexSetName">The owning index-set name.</param>
/// <param name="IncludedIndexCount">The number of forward indexes included in the map.</param>
/// <param name="IdentityCount">The number of distinct identities captured.</param>
/// <param name="KeyTupleCount">The number of key/identity tuples captured.</param>
/// <param name="MultiValueKeyBucketCount">The number of identity/key buckets with more than one key value.</param>
/// <param name="IsFresh">Whether the runtime map is current against catalog mutation counters.</param>
/// <param name="EstimatedMapBytes">A rough managed-memory estimate for the runtime map.</param>
public readonly record struct CatalogIndexSetInverseShape(
    string IndexSetName,
    int IncludedIndexCount,
    long IdentityCount,
    long KeyTupleCount,
    long MultiValueKeyBucketCount,
    bool IsFresh,
    long EstimatedMapBytes,
    IReadOnlyList<string> SchemaIndexNames);

/// <summary>
/// Stores the ordered inverse-key schema for one IndexSet runtime inverse map.<br/>
/// The schema is the durable-shape model for the future persisted inverse: key values are stored by ordinal, and index names are resolved once to ordinals before row access.<br/>
/// </summary>
internal sealed class CatalogIndexSetInverseSchema
{
    internal static readonly CatalogIndexSetInverseSchema Empty = new(Array.Empty<string>());

    private readonly Dictionary<string, int> ordinals;

    private CatalogIndexSetInverseSchema(string[] names)
    {
        Names = names;
        ordinals = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            ordinals.Add(names[i], i);
        }
    }

    /// <summary>
    /// Gets included forward-index names in inverse payload order.<br/>
    /// A row's value slot at ordinal `n` belongs to `Names[n]`.<br/>
    /// </summary>
    internal string[] Names { get; }

    /// <summary>
    /// Creates an ordered inverse schema from index names.<br/>
    /// Duplicate names are rejected because a row ordinal must map to exactly one forward index.<br/>
    /// </summary>
    /// <param name="names">Forward-index names in payload order.</param>
    /// <returns>An ordered inverse schema.</returns>
    internal static CatalogIndexSetInverseSchema Create(string[] names)
    {
        string[] copy = new string[names.Length];
        Array.Copy(names, copy, names.Length);
        return new CatalogIndexSetInverseSchema(copy);
    }

    /// <summary>
    /// Tries to resolve a forward-index name to its inverse payload ordinal.<br/>
    /// </summary>
    /// <param name="indexName">The forward-index name.</param>
    /// <param name="ordinal">Receives the payload ordinal when found.</param>
    /// <returns><see langword="true"/> when the index is included in the inverse schema.</returns>
    internal bool TryGetOrdinal(string indexName, out int ordinal)
    {
        return ordinals.TryGetValue(indexName, out ordinal);
    }
}

/// <summary>
/// Stores one identity's inverse values using schema-ordered slots.<br/>
/// Each slot is null when the identity has no tuple for that included index; multi-value indexes store multiple values in the slot list.<br/>
/// </summary>
internal sealed class CatalogIndexSetInverseEntry
{
    internal CatalogIndexSetInverseEntry(int schemaKeyCount)
    {
        Values = new List<object?>?[schemaKeyCount];
    }

    /// <summary>
    /// Gets schema-ordered value slots for one identity.<br/>
    /// Slot `n` corresponds to the owning schema's index name at ordinal `n`.<br/>
    /// </summary>
    internal List<object?>?[] Values { get; }

    /// <summary>
    /// Adds one key value to the schema-ordinal slot for this identity.<br/>
    /// </summary>
    /// <param name="ordinal">The schema ordinal.</param>
    /// <param name="value">The indexed key value.</param>
    internal void Add(int ordinal, object? value)
    {
        List<object?>? values = Values[ordinal];
        if (values is null)
        {
            values = new List<object?>(1);
            Values[ordinal] = values;
        }

        values.Add(value);
    }
}

/// <summary>
/// Represents selected inverse key-map values for one identity.<br/>
/// Multi-value indexes return more than one key value under the same key name.<br/>
/// </summary>
/// <typeparam name="TIdentity">The identity type requested by the caller.</typeparam>
/// <param name="Identity">The identity resolved from the inverse map.</param>
/// <param name="Keys">The selected indexed key values keyed by index name.</param>
public readonly record struct CatalogIndexSetInverseRow<TIdentity>(TIdentity Identity, IReadOnlyDictionary<string, IReadOnlyList<object?>> Keys);

/// <summary>
/// Selects the identity side of an inverse-key-map condition.<br/>
/// The selected identity predicate defines the candidate identities before `.Key(...)` reads indexed values from the inverse map.<br/>
/// </summary>
public sealed class LibraDexInverseIdentityTypeSelector
{
    private readonly string group;
    private readonly CatalogIndexSetInverse inverse;

    internal LibraDexInverseIdentityTypeSelector(string group, CatalogIndexSetInverse inverse)
    {
        this.group = group;
        this.inverse = inverse;
    }

    /// <summary>Selects Boolean identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<bool> AsBoolean => Select<bool>();

    /// <summary>Selects Byte identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<byte> AsByte => Select<byte>();

    /// <summary>Selects SByte identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<sbyte> AsSByte => Select<sbyte>();

    /// <summary>Selects Int16 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<short> AsInt16 => Select<short>();

    /// <summary>Selects UInt16 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<ushort> AsUInt16 => Select<ushort>();

    /// <summary>Selects Int32 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<int> AsInt32 => Select<int>();

    /// <summary>Selects UInt32 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<uint> AsUInt32 => Select<uint>();

    /// <summary>Selects Int64 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<long> AsInt64 => Select<long>();

    /// <summary>Selects UInt64 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<ulong> AsUInt64 => Select<ulong>();

    /// <summary>Selects Int128 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<Int128> AsInt128 => Select<Int128>();

    /// <summary>Selects UInt128 identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<UInt128> AsUInt128 => Select<UInt128>();

    /// <summary>Selects Single identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<float> AsSingle => Select<float>();

    /// <summary>Selects Double identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<double> AsDouble => Select<double>();

    /// <summary>Selects Decimal identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<decimal> AsDecimal => Select<decimal>();

    /// <summary>Selects BigInteger identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<System.Numerics.BigInteger> AsBigInt => Select<System.Numerics.BigInteger>();

    /// <summary>Selects Char identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<char> AsChar => Select<char>();

    /// <summary>
    /// Selects GUID identity predicates for the inverse identity source.<br/>
    /// </summary>
    public LibraDexInverseIdentityOperator<Guid> AsGuid => Select<Guid>();

    /// <summary>Selects raw binary identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<byte[]> AsBinary => Select<byte[]>();

    /// <summary>Selects DateTime identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<DateTime> AsDateTime => Select<DateTime>();

    /// <summary>Selects DateTimeOffset identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<DateTimeOffset> AsDateTimeOffset => Select<DateTimeOffset>();

    /// <summary>Selects DateOnly identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<DateOnly> AsDateOnly => Select<DateOnly>();

    /// <summary>Selects TimeOnly identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<TimeOnly> AsTimeOnly => Select<TimeOnly>();

    /// <summary>Selects TimeSpan identity predicates for the inverse identity source.<br/></summary>
    public LibraDexInverseIdentityOperator<TimeSpan> AsTimeSpan => Select<TimeSpan>();

    /// <summary>
    /// Selects string identity predicates for the inverse identity source.<br/>
    /// </summary>
    public LibraDexInverseStringIdentityOperator AsString => new(group, inverse);

    private LibraDexInverseIdentityOperator<TIdentity> Select<TIdentity>()
        => new(group, inverse);

    /// <summary>
    /// Starts the inverse condition from an explicit caller-supplied identity set.<br/>
    /// This is the low-friction form for UI selections, ACL-visible identity lists, search-result refinement, and service-provided identity candidates.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The identity value type.</typeparam>
    /// <param name="identities">The candidate identities.</param>
    /// <returns>An inverse continuation for key filters, forward filters, or condition completion.</returns>
    public LibraDexInverseConditionContinueOrEnd InSet<TIdentity>(IEnumerable<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        TIdentity[] captured = identities.ToArray();
        LibraDexConditionBuilder builder = new(group);
        builder.AddExternalSource(() => captured.Cast<object>());
        return new LibraDexInverseConditionContinueOrEnd(builder, inverse, () => captured.Cast<object>());
    }
}

/// <summary>
/// Captures typed identity-source predicates for `.WhereInverse`.<br/>
/// These predicates apply to the identity value itself, not to a named indexed key.<br/>
/// </summary>
/// <typeparam name="TIdentity">The identity type selected for the inverse source.</typeparam>
public class LibraDexInverseIdentityOperator<TIdentity>
{
    private readonly string group;
    private readonly CatalogIndexSetInverse inverse;

    internal LibraDexInverseIdentityOperator(string group, CatalogIndexSetInverse inverse)
    {
        this.group = group;
        this.inverse = inverse;
    }

    /// <summary>
    /// Starts from one exact identity value in the inverse map.<br/>
    /// </summary>
    /// <param name="identity">The identity value.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd EqualTo(TIdentity identity)
        => Source(value => TypedEquals(value, identity));

    /// <summary>
    /// Starts from identities present in a caller-supplied set.<br/>
    /// </summary>
    /// <param name="identities">The identity values.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd InSet(IEnumerable<TIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        HashSet<TIdentity> set = new(identities);
        return Source(value => value is TIdentity typed && set.Contains(typed));
    }

    /// <summary>
    /// Starts from identities inside an inclusive ordered range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower identity.</param>
    /// <param name="upper">The inclusive upper identity.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd Between(TIdentity lower, TIdentity upper)
        => Source(value => value is TIdentity typed && Comparer<TIdentity>.Default.Compare(typed, lower) >= 0 && Comparer<TIdentity>.Default.Compare(typed, upper) <= 0);

    private LibraDexInverseConditionContinueOrEnd Source(Func<object, bool> predicate)
    {
        LibraDexConditionBuilder builder = new(group);
        builder.AddExternalSource(() => inverse.IdentitiesWhere(predicate));
        return new LibraDexInverseConditionContinueOrEnd(builder, inverse, () => inverse.IdentitiesWhere(predicate));
    }

    private static bool TypedEquals(object value, TIdentity identity)
        => value is TIdentity typed && EqualityComparer<TIdentity>.Default.Equals(typed, identity);
}

/// <summary>
/// Captures string identity-source predicates for `.WhereInverse.AsString`.<br/>
/// These predicates apply to the identity value itself before inverse key filters are applied.<br/>
/// </summary>
public sealed class LibraDexInverseStringIdentityOperator : LibraDexInverseIdentityOperator<string>
{
    private readonly string group;
    private readonly CatalogIndexSetInverse inverse;

    internal LibraDexInverseStringIdentityOperator(string group, CatalogIndexSetInverse inverse)
        : base(group, inverse)
    {
        this.group = group;
        this.inverse = inverse;
    }

    /// <summary>
    /// Starts from string identities with the supplied prefix.<br/>
    /// </summary>
    /// <param name="prefix">The identity prefix.</param>
    /// <param name="ignoreCase">When true, uses culture-aware case-insensitive comparison.</param>
    /// <param name="culture">Optional culture name for case-insensitive comparison.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd StartsWith(string prefix, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        LibraDexConditionBuilder builder = new(group);
        builder.AddExternalSource(() => inverse.IdentitiesWhere(identity => identity is string text && StringStartsWith(text, prefix, ignoreCase, culture)));
        return new LibraDexInverseConditionContinueOrEnd(builder, inverse, () => inverse.IdentitiesWhere(identity => identity is string text && StringStartsWith(text, prefix, ignoreCase, culture)));
    }

    private static bool StringStartsWith(string value, string prefix, bool ignoreCase, string? culture)
        => value.StartsWith(prefix, ignoreCase ? ResolveStringComparison(culture) : StringComparison.Ordinal);

    private static StringComparison ResolveStringComparison(string? culture)
        => string.IsNullOrWhiteSpace(culture)
            ? System.StringComparison.OrdinalIgnoreCase
            : CultureInfo.GetCultureInfo(culture).CompareInfo == CultureInfo.InvariantCulture.CompareInfo
                ? System.StringComparison.InvariantCultureIgnoreCase
                : System.StringComparison.CurrentCultureIgnoreCase;
}

/// <summary>
/// Continues an inverse-key-map condition after the identity source or an inverse key predicate has been added.<br/>
/// `.And.Key(...)` and `.Or.Key(...)` stay inside the inverse branch, while `.And.Where(...)` and `.Or.Where(...)` add ordinary forward-index predicates.<br/>
/// </summary>
public sealed class LibraDexInverseConditionContinueOrEnd
{
    private readonly LibraDexConditionBuilder builder;
    private readonly CatalogIndexSetInverse inverse;
    private readonly Func<IEnumerable<object>> source;

    internal LibraDexInverseConditionContinueOrEnd(LibraDexConditionBuilder builder, CatalogIndexSetInverse inverse, Func<IEnumerable<object>> source)
    {
        this.builder = builder;
        this.inverse = inverse;
        this.source = source;
    }

    /// <summary>
    /// Adds an inverse or forward-index intersection clause.<br/>
    /// </summary>
    public LibraDexInverseConditionClause And
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.And);
            return new LibraDexInverseConditionClause(builder, inverse, source, keyAsSource: false);
        }
    }

    /// <summary>
    /// Adds an inverse or forward-index intersection clause using the uppercase spelling.<br/>
    /// </summary>
    public LibraDexInverseConditionClause AND => And;

    /// <summary>
    /// Adds an inverse or forward-index union clause.<br/>
    /// </summary>
    public LibraDexInverseConditionClause Or
    {
        get
        {
            builder.SetNextOperation(LibraDexConditionNodeKind.Or);
            return new LibraDexInverseConditionClause(builder, inverse, source, keyAsSource: true);
        }
    }

    /// <summary>
    /// Adds an inverse or forward-index union clause using the uppercase spelling.<br/>
    /// </summary>
    public LibraDexInverseConditionClause OR => Or;

    /// <summary>
    /// Completes the inverse-seeded condition.<br/>
    /// </summary>
    public LibraDexConditionEndCondition EndCondition => builder.End();

    /// <summary>
    /// Adds an implicit inverse-key intersection after the identity source.<br/>
    /// This supports `.WhereInverse.InSet(ids).Key("firstName")...` without requiring a separate `.And` token between the identity source and its first inverse key filter.<br/>
    /// </summary>
    /// <param name="indexName">The inverse-enabled forward index name.</param>
    /// <returns>A key type selector.</returns>
    public LibraDexInverseKeyTypeSelector Key(string indexName)
    {
        builder.SetNextOperation(LibraDexConditionNodeKind.And);
        return new LibraDexInverseConditionClause(builder, inverse, source, keyAsSource: false).Key(indexName);
    }

    /// <summary>
    /// Adds an implicit inverse-key intersection selected by opened index handle.<br/>
    /// </summary>
    /// <param name="index">The opened forward index handle.</param>
    /// <returns>A key type selector.</returns>
    public LibraDexInverseKeyTypeSelector Key(IIndex index)
    {
        builder.SetNextOperation(LibraDexConditionNodeKind.And);
        return new LibraDexInverseConditionClause(builder, inverse, source, keyAsSource: false).Key(index);
    }
}

/// <summary>
/// Selects the next inverse key predicate or bridges to an ordinary forward-index predicate.<br/>
/// </summary>
public sealed class LibraDexInverseConditionClause
{
    private readonly LibraDexConditionBuilder builder;
    private readonly CatalogIndexSetInverse inverse;
    private readonly Func<IEnumerable<object>> source;
    private readonly bool keyAsSource;

    internal LibraDexInverseConditionClause(LibraDexConditionBuilder builder, CatalogIndexSetInverse inverse, Func<IEnumerable<object>> source, bool keyAsSource)
    {
        this.builder = builder;
        this.inverse = inverse;
        this.source = source;
        this.keyAsSource = keyAsSource;
    }

    /// <summary>
    /// Selects an indexed key stored in the inverse key map by index name.<br/>
    /// The key predicate filters the current candidate identities without hydrating external source objects.<br/>
    /// </summary>
    /// <param name="indexName">The inverse-enabled forward index name.</param>
    /// <returns>A key type selector.</returns>
    public LibraDexInverseKeyTypeSelector Key(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexInverseKeyTypeSelector(builder, inverse, source, indexName, keyAsSource);
    }

    /// <summary>
    /// Selects an indexed key stored in the inverse key map by opened index handle.<br/>
    /// The handle validates index-set ownership and preserves low-friction typed overloads for callers that already have an index instance.<br/>
    /// </summary>
    /// <param name="index">The opened index handle.</param>
    /// <returns>A key type selector.</returns>
    public LibraDexInverseKeyTypeSelector Key(IIndex index)
    {
        inverse.ValidateIndex(index);
        return Key(index.Name);
    }

    /// <summary>
    /// Bridges from the inverse branch to an ordinary forward-index predicate selected by name.<br/>
    /// Use this when the next predicate should use key-to-identity lookup rather than inverse identity-to-key lookup.<br/>
    /// </summary>
    /// <param name="indexName">The forward index name.</param>
    /// <returns>A normal condition value selector.</returns>
    public LibraDexConditionValueTypeSelector Where(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return new LibraDexConditionClause(builder).Where(indexName);
    }

    /// <summary>
    /// Bridges from the inverse branch to an ordinary forward-index predicate selected by opened index handle.<br/>
    /// </summary>
    /// <param name="index">The opened forward index handle.</param>
    /// <returns>A normal condition value selector.</returns>
    public LibraDexConditionValueTypeSelector Where(IIndex index)
    {
        inverse.ValidateIndex(index);
        return Where(index.Name);
    }
}

/// <summary>
/// Selects the value family for an indexed key read from the inverse key map.<br/>
/// </summary>
public sealed class LibraDexInverseKeyTypeSelector
{
    private readonly LibraDexConditionBuilder builder;
    private readonly CatalogIndexSetInverse inverse;
    private readonly Func<IEnumerable<object>> source;
    private readonly string indexName;
    private readonly bool keyAsSource;

    internal LibraDexInverseKeyTypeSelector(LibraDexConditionBuilder builder, CatalogIndexSetInverse inverse, Func<IEnumerable<object>> source, string indexName, bool keyAsSource)
    {
        this.builder = builder;
        this.inverse = inverse;
        this.source = source;
        this.indexName = indexName;
        this.keyAsSource = keyAsSource;
    }

    private LibraDexInverseKeyOperator<TValue> Select<TValue>()
        => new(builder, inverse, source, indexName, keyAsSource);

    /// <summary>
    /// Selects string operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseStringKeyOperator AsString => new(builder, inverse, source, indexName, keyAsSource);

    /// <summary>
    /// Selects Boolean operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<bool> AsBoolean => Select<bool>();

    /// <summary>Selects Byte operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<byte> AsByte => Select<byte>();

    /// <summary>Selects SByte operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<sbyte> AsSByte => Select<sbyte>();

    /// <summary>Selects Int16 operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<short> AsInt16 => Select<short>();

    /// <summary>Selects UInt16 operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<ushort> AsUInt16 => Select<ushort>();

    /// <summary>Selects UInt32 operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<uint> AsUInt32 => Select<uint>();

    /// <summary>Selects Int128 operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<Int128> AsInt128 => Select<Int128>();

    /// <summary>Selects UInt128 operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<UInt128> AsUInt128 => Select<UInt128>();

    /// <summary>
    /// Selects GUID operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<Guid> AsGuid => Select<Guid>();

    /// <summary>Selects raw binary operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<byte[]> AsBinary => Select<byte[]>();

    /// <summary>
    /// Selects Int64 operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<long> AsInt64 => Select<long>();

    /// <summary>
    /// Selects UInt64 operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<ulong> AsUInt64 => Select<ulong>();

    /// <summary>
    /// Selects Int32 operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<int> AsInt32 => Select<int>();

    /// <summary>
    /// Selects exact Decimal operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<decimal> AsDecimal => Select<decimal>();

    /// <summary>
    /// Selects native Single operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<float> AsSingle => Select<float>();

    /// <summary>
    /// Selects native Double operators for the inverse key value.<br/>
    /// </summary>
    public LibraDexInverseKeyOperator<double> AsDouble => Select<double>();

    /// <summary>Selects BigInteger operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<System.Numerics.BigInteger> AsBigInt => Select<System.Numerics.BigInteger>();

    /// <summary>Selects Char operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<char> AsChar => Select<char>();

    /// <summary>Selects DateTime operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<DateTime> AsDateTime => Select<DateTime>();

    /// <summary>Selects DateTimeOffset operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<DateTimeOffset> AsDateTimeOffset => Select<DateTimeOffset>();

    /// <summary>Selects DateOnly operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<DateOnly> AsDateOnly => Select<DateOnly>();

    /// <summary>Selects TimeOnly operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<TimeOnly> AsTimeOnly => Select<TimeOnly>();

    /// <summary>Selects TimeSpan operators for the inverse key value.<br/></summary>
    public LibraDexInverseKeyOperator<TimeSpan> AsTimeSpan => Select<TimeSpan>();
}

/// <summary>
/// Captures typed predicates over one inverse key-map value.<br/>
/// The predicate is applied to the indexed key value associated with the current candidate identity.<br/>
/// </summary>
/// <typeparam name="TValue">The expected inverse key value type.</typeparam>
public class LibraDexInverseKeyOperator<TValue>
{
    private readonly LibraDexConditionBuilder builder;
    private readonly CatalogIndexSetInverse inverse;
    private readonly Func<IEnumerable<object>> source;
    private readonly string indexName;
    private readonly bool keyAsSource;

    internal LibraDexInverseKeyOperator(LibraDexConditionBuilder builder, CatalogIndexSetInverse inverse, Func<IEnumerable<object>> source, string indexName, bool keyAsSource)
    {
        this.builder = builder;
        this.inverse = inverse;
        this.source = source;
        this.indexName = indexName;
        this.keyAsSource = keyAsSource;
    }

    /// <summary>
    /// Filters identities whose inverse key equals <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The expected key value.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd EqualTo(TValue value)
        => Add(candidate => candidate is TValue typed && EqualityComparer<TValue>.Default.Equals(typed, value));

    /// <summary>
    /// Filters identities whose inverse key does not equal <paramref name="value"/>.<br/>
    /// Missing inverse keys do not match; use forward predicates or a presence policy when missing should be included.<br/>
    /// </summary>
    /// <param name="value">The excluded key value.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd NotEqualTo(TValue value)
        => Add(candidate => candidate is TValue typed && !EqualityComparer<TValue>.Default.Equals(typed, value));

    /// <summary>
    /// Filters identities whose inverse key falls inside an inclusive ordered range.<br/>
    /// </summary>
    /// <param name="lower">The inclusive lower key value.</param>
    /// <param name="upper">The inclusive upper key value.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd Between(TValue lower, TValue upper)
        => Add(candidate => candidate is TValue typed && Comparer<TValue>.Default.Compare(typed, lower) >= 0 && Comparer<TValue>.Default.Compare(typed, upper) <= 0);

    /// <summary>
    /// Filters identities whose inverse key is one of the supplied values.<br/>
    /// </summary>
    /// <param name="values">The allowed key values.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd InSet(IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        HashSet<TValue> set = new(values);
        return Add(candidate => candidate is TValue typed && set.Contains(typed));
    }

    /// <summary>
    /// Filters identities whose selected inverse key equals another inverse key for the same identity.<br/>
    /// Both key names must participate in the index-set inverse map; missing values do not match.<br/>
    /// </summary>
    /// <param name="otherIndexName">The other inverse-enabled forward index name.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd EqualToKey(string otherIndexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(otherIndexName);
        return AddPair(otherIndexName, (left, right) => left is TValue leftTyped && right is TValue rightTyped && EqualityComparer<TValue>.Default.Equals(leftTyped, rightTyped));
    }

    /// <summary>
    /// Filters identities whose selected inverse key equals another inverse key for the same identity.<br/>
    /// The opened handle supplies the other key name and validates index-set ownership before execution.<br/>
    /// </summary>
    /// <param name="otherIndex">The other inverse-enabled opened index.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd EqualToKey(IIndex otherIndex)
    {
        inverse.ValidateIndex(otherIndex);
        return EqualToKey(otherIndex.Name);
    }

    /// <summary>
    /// Filters identities whose selected inverse key differs from another inverse key for the same identity.<br/>
    /// This is the inverse-map path for unanticipated self-relative comparisons such as effective mask differing from inherited mask.<br/>
    /// </summary>
    /// <param name="otherIndexName">The other inverse-enabled forward index name.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd NotEqualToKey(string otherIndexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(otherIndexName);
        return AddPair(otherIndexName, (left, right) => left is TValue leftTyped && right is TValue rightTyped && !EqualityComparer<TValue>.Default.Equals(leftTyped, rightTyped));
    }

    /// <summary>
    /// Filters identities whose selected inverse key differs from another inverse key for the same identity.<br/>
    /// The opened handle supplies the other key name and validates index-set ownership before execution.<br/>
    /// </summary>
    /// <param name="otherIndex">The other inverse-enabled opened index.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd NotEqualToKey(IIndex otherIndex)
    {
        inverse.ValidateIndex(otherIndex);
        return NotEqualToKey(otherIndex.Name);
    }

    private protected LibraDexInverseConditionContinueOrEnd Add(Func<object?, bool> predicate)
    {
        inverse.PrepareForQuery();
        if (keyAsSource)
        {
            builder.AddExternalSource(() => source().Where(identity => inverse.AnyKey(identity, indexName, predicate)));
        }
        else
        {
            builder.AddExternal(context => inverse.AnyKey(context.Identity, indexName, predicate));
        }

        return new LibraDexInverseConditionContinueOrEnd(builder, inverse, source);
    }

    private LibraDexInverseConditionContinueOrEnd AddPair(string otherIndexName, Func<object?, object?, bool> predicate)
    {
        inverse.PrepareForQuery();
        if (keyAsSource)
        {
            builder.AddExternalSource(() => source().Where(identity => inverse.AnyKeyPair(identity, indexName, otherIndexName, predicate)));
        }
        else
        {
            builder.AddExternal(context => inverse.AnyKeyPair(context.Identity, indexName, otherIndexName, predicate));
        }

        return new LibraDexInverseConditionContinueOrEnd(builder, inverse, source);
    }
}

/// <summary>
/// Captures string predicates over one inverse key-map value.<br/>
/// </summary>
public sealed class LibraDexInverseStringKeyOperator : LibraDexInverseKeyOperator<string>
{
    internal LibraDexInverseStringKeyOperator(LibraDexConditionBuilder builder, CatalogIndexSetInverse inverse, Func<IEnumerable<object>> source, string indexName, bool keyAsSource)
        : base(builder, inverse, source, indexName, keyAsSource)
    {
    }

    /// <summary>
    /// Filters identities whose inverse string key starts with <paramref name="prefix"/>.<br/>
    /// </summary>
    /// <param name="prefix">The required prefix.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">Reserved culture name for future culture-specific comparison; ordinal ignore-case is used in this runtime inverse slice.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd StartsWith(string prefix, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _ = culture;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Add(candidate => candidate is string text && text.StartsWith(prefix, comparison));
    }

    /// <summary>
    /// Filters identities whose inverse string key ends with <paramref name="suffix"/>.<br/>
    /// </summary>
    /// <param name="suffix">The required suffix.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">Reserved culture name for future culture-specific comparison; ordinal ignore-case is used in this runtime inverse slice.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd EndsWith(string suffix, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        _ = culture;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Add(candidate => candidate is string text && text.EndsWith(suffix, comparison));
    }

    /// <summary>
    /// Filters identities whose inverse string key contains <paramref name="value"/>.<br/>
    /// </summary>
    /// <param name="value">The required substring.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">Reserved culture name for future culture-specific comparison; ordinal ignore-case is used in this runtime inverse slice.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd Contains(string value, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = culture;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Add(candidate => candidate is string text && text.Contains(value, comparison));
    }

    /// <summary>
    /// Filters identities whose inverse string key does not contain <paramref name="value"/>.<br/>
    /// Null and non-string inverse values do not match the negated string predicate.<br/>
    /// </summary>
    /// <param name="value">The substring to exclude.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">The optional culture name governing comparison.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd NotContains(string value, bool ignoreCase = false, string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        LibraDexStringComparisonPolicy policy = LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture);
        return Add(candidate => candidate is string text &&
            policy.ResolveCulture().CompareInfo.IndexOf(text, value, policy.CompareOptions) < 0);
    }

    /// <summary>
    /// Filters identities whose inverse string key matches a wildcard pattern.<br/>
    /// `*` matches zero or more characters and `?` matches one; an ordinary backslash remains literal unless it escapes wildcard syntax.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">The optional culture name governing comparison.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd Like(string pattern, bool ignoreCase = false, string? culture = null)
        => AddWildcard(pattern, negate: false, ignoreCase, culture);

    /// <summary>
    /// Filters identities whose inverse string key does not match a wildcard pattern.<br/>
    /// Null and non-string inverse values do not match the negated string predicate.<br/>
    /// </summary>
    /// <param name="pattern">The wildcard pattern to exclude.</param>
    /// <param name="ignoreCase">When true, uses case-insensitive comparison.</param>
    /// <param name="culture">The optional culture name governing comparison.</param>
    /// <returns>An inverse continuation.</returns>
    public LibraDexInverseConditionContinueOrEnd NotLike(string pattern, bool ignoreCase = false, string? culture = null)
        => AddWildcard(pattern, negate: true, ignoreCase, culture);

    private LibraDexInverseConditionContinueOrEnd AddWildcard(string pattern, bool negate, bool ignoreCase, string? culture)
    {
        LibraDexStringComparisonPolicy policy = LibraDexStringComparisonPolicy.FromLegacy(ignoreCase, culture);
        LibraDexWildcardPattern wildcard = LibraDexWildcardPattern.Create(pattern, policy);
        return Add(candidate => candidate is string text &&
            (wildcard.Matches(text, policy.ResolveCulture().CompareInfo, policy.CompareOptions) != negate));
    }
}

internal sealed class LibraDexObjectValueComparer : IEqualityComparer<object>
{
    internal static readonly LibraDexObjectValueComparer Instance = new();

    public new bool Equals(object? x, object? y)
        => LibraDexObjectTuple.ValueEquals(x, y);

    public int GetHashCode(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj is byte[] bytes)
        {
            HashCode hash = new();
            for (int i = 0; i < bytes.Length; i++)
            {
                hash.Add(bytes[i]);
            }

            return hash.ToHashCode();
        }

        return obj.GetHashCode();
    }
}
