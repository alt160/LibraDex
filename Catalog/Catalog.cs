using LibraDex.Layouts;
using System.Numerics;

namespace LibraDex;

/// <summary>
/// Represents a public grouping of LibraDex indexes over one memory-backed or file-backed catalog session.<br/>
/// The catalog is the first public construction concept: callers open or create a catalog, then create/open individual indexes beneath `catalog.Indexes`.<br/>
/// Active catalog sessions are single-owner by default; use external serialization or isolated catalogs when multiple callers may write concurrently.<br/>
/// </summary>
public sealed class Catalog : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly Dictionary<string, CatalogIdentityGroupBatchManager> groupBatchManagers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogIndexSetInverse> inverseIndexSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdentityLookupMode> identityLookupModes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> singleKeyIdentityMaps = new(StringComparer.Ordinal);
    private readonly object singleKeyIdentityMapsSync = new();
    private readonly Dictionary<int, LibraDexRoutedCompositeIndex> compositeIndexes = [];
    private readonly object compositeIndexesSync = new();
    private bool disposed;

    private Catalog(
        LibraDexFileSession session,
        string? path,
        DataKernelBackingKind backingKind,
        CatalogOptions options)
    {
        this.session = session;
        Path = path;
        BackingKind = backingKind;
        Options = options;
        Indexes = new CatalogIndexFactories(this);
        Stats = new CatalogStats(this);
        Maintenance = new CatalogMaintenance(this);
        Diagnostics = new CatalogDiagnostics(this);
        Tools = new CatalogTools(this);
        Compatibility = new CatalogCompatibility(this);
    }

    internal static Catalog Attach(
        LibraDexFileSession session,
        string? path,
        CatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Catalog(
            session,
            path,
            session.BackingKind,
            options ?? new CatalogOptions());
    }

    /// <summary>
    /// Gets the file path for a file-backed catalog, or null for a memory-backed catalog.<br/>
    /// The path is metadata for diagnostics and reopen flows; the active catalog uses its already-open session.<br/>
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Gets whether this catalog is memory-backed or file-backed.<br/>
    /// Memory-backed catalogs are temporary and cannot be reopened after disposal.<br/>
    /// </summary>
    internal DataKernelBackingKind BackingKind { get; }

    /// <summary>
    /// Gets the options supplied when this catalog was opened or created.<br/>
    /// Diagnostics options may enable fail-fast unsupported-concurrency checks, but they do not turn the active session into a concurrent writer or published-reader domain.<br/>
    /// </summary>
    public CatalogOptions Options { get; }

    /// <summary>
    /// Gets the grouped index factory surface for this catalog.<br/>
    /// The nested shape keeps IntelliSense exploratory while preserving short terminal verbs such as `Create`, `Open`, and `CreateOrOpen`.<br/>
    /// </summary>
    public CatalogIndexFactories Indexes { get; }

    /// <summary>
    /// Gets the grouped index factory surface for the supplied identity group.<br/>
    /// This is the compact counterpart to `catalog.Indexes[group]`, keeping catalog-first code short without adding another index resolution path.<br/>
    /// </summary>
    /// <param name="group">The identity group name.</param>
    /// <returns>The grouped index factory surface for the supplied identity group.</returns>
    public CatalogIdentityGroupIndexes this[string group] => Indexes[group];

    /// <summary>
    /// Gets the index-set factory surface for the supplied identity universe.<br/>
    /// An index set is the named collection of indexes that share one identity space, so fluent condition code can read as `catalog.IndexSet("users").Where("firstName")...` while `.Group(...)` remains available for logical parenthesized condition fragments.<br/>
    /// </summary>
    /// <param name="name">The index-set name.</param>
    /// <returns>The index-set factory surface for the supplied identity universe.</returns>
    public CatalogIdentityGroupIndexes IndexSet(string name)
    {
        return Indexes.IndexSet(name);
    }

    /// <summary>
    /// Gets whether an active index definition exists in the supplied identity group.<br/>
    /// This is a metadata discovery check; it does not answer whether the index contains entries and does not open the index handle.<br/>
    /// </summary>
    /// <param name="group">The identity group containing the index.</param>
    /// <param name="indexName">The index definition name.</param>
    /// <returns><see langword="true"/> when the grouped index definition is active.</returns>
    public bool HasIndex(string group, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return Indexes.TryGetInfo(group, indexName, out _);
    }

    /// <summary>
    /// Creates or selects an index set and optionally enables its runtime inverse key map.<br/>
    /// The inverse key map is an index-set scoped identity-to-indexed-key lookup layer used by `.WhereInverse` conditions and direct inverse reads.<br/>
    /// This first implementation builds from current forward index tuples on demand; durable inverse storage is a separate physical-storage step.<br/>
    /// </summary>
    /// <param name="name">The index-set name.</param>
    /// <param name="createInverseIndex">When true, enables the index-set inverse key map immediately.</param>
    /// <returns>The index-set factory surface.</returns>
    public CatalogIdentityGroupIndexes CreateIndexSet(string name, bool createInverseIndex = false)
    {
        CatalogIdentityGroupIndexes set = Indexes.IndexSet(name);
        if (createInverseIndex)
        {
            set.Inverse.Create();
        }

        return set;
    }

    /// <summary>
    /// Gets disconnected metadata for every non-empty index set currently declared in the catalog.<br/>
    /// Each entry contains the index-set name and the active index metadata snapshots that belong to that identity universe.<br/>
    /// </summary>
    public CatalogIndexSetInfo[] IndexSets => Indexes.IndexSets();

    /// <summary>
    /// Gets passive catalog-level stats and marker/delta helpers.<br/>
    /// Catalog stats answer file/group-level questions without forcing callers into benchmark-only APIs or hidden expensive layout scans.<br/>
    /// </summary>
    public CatalogStats Stats { get; }

    /// <summary>
    /// Gets explicit catalog-level maintenance operations.<br/>
    /// Maintenance is separated from normal query/mutation paths so validation and optimization remain developer-controlled.<br/>
    /// </summary>
    public CatalogMaintenance Maintenance { get; }

    /// <summary>
    /// Gets opt-in catalog query diagnostics and bounded recent-execution telemetry.<br/>
    /// Query telemetry is disabled by default; explicit one-execution measurement remains available from an identity-group producer without enabling history.<br/>
    /// </summary>
    public CatalogDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets catalog-level tools and utilities.<br/>
    /// Tools are outside the hot path and are intended for inspection, export, repair, and developer support workflows.<br/>
    /// </summary>
    public CatalogTools Tools { get; }

    /// <summary>
    /// Gets compatibility and migration helpers for this catalog.<br/>
    /// Compatibility checks and migrations are explicit so applications can review upgrade or repair decisions before changing storage.<br/>
    /// </summary>
    public CatalogCompatibility Compatibility { get; }

    internal LibraDexFileSession Session => session;

    /// <summary>
    /// Gets the current memory-backed DataKernel arena diagnostics for internal workbench tooling.<br/>
    /// File-backed catalogs return a zero-valued snapshot because their committed bytes are not held by the managed arena.<br/>
    /// </summary>
    /// <returns>The current DataKernel memory arena diagnostic snapshot.</returns>
    internal DataKernelMemoryDiagnostics GetMemoryDiagnostics()
    {
        return session.GetMemoryDiagnostics();
    }

    /// <summary>
    /// Creates a new file-backed catalog.<br/>
    /// Creation fails if the target file already exists, preserving the explicit create/open split and avoiding accidental reuse of an unexpected file.<br/>
    /// </summary>
    /// <param name="path">The `.lbdx` catalog file path to create.</param>
    /// <param name="options">Optional catalog options; currently reserved and empty by design.</param>
    /// <returns>A disposable catalog over the newly initialized file.</returns>
    public static Catalog Create(string path, CatalogOptions? options = null)
    {
        string requiredPath = RequirePath(path);
        if (File.Exists(requiredPath))
        {
            throw new IOException($"The LibraDex catalog file already exists: {requiredPath}");
        }

        string? directory = System.IO.Path.GetDirectoryName(requiredPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        CatalogOptions effectiveOptions = options ?? new CatalogOptions();
        LibraDexFileSession session = LibraDexFileSession.Initialize(
            requiredPath,
            CreateDefaultDataKernelOptions(),
            CreateCatalogDeveloperMetadata(),
            DataKernelTelemetryOptions.FromLevel(effectiveOptions.DiagnosticsLevel));
        return new Catalog(session, requiredPath, DataKernelBackingKind.File, effectiveOptions);
    }

    /// <summary>
    /// Opens an existing file-backed catalog.<br/>
    /// The catalog remains responsible for closing the underlying file session through `Close`, `Dispose`, or a `using` scope.<br/>
    /// </summary>
    /// <param name="path">The existing `.lbdx` catalog file path.</param>
    /// <param name="options">Optional catalog options; currently reserved and empty by design.</param>
    /// <returns>A disposable catalog over the existing file.</returns>
    public static Catalog Open(string path, CatalogOptions? options = null)
    {
        string requiredPath = RequirePath(path);
        if (!File.Exists(requiredPath))
        {
            throw new FileNotFoundException("The LibraDex catalog file does not exist.", requiredPath);
        }

        CatalogOptions effectiveOptions = options ?? new CatalogOptions();
        LibraDexFileSession session = LibraDexFileSession.Open(
            requiredPath,
            CreateDefaultDataKernelOptions(),
            DataKernelTelemetryOptions.FromLevel(effectiveOptions.DiagnosticsLevel));
        Catalog catalog = new(session, requiredPath, DataKernelBackingKind.File, effectiveOptions);
        catalog.ValidateExistingIdentityContracts();
        return catalog;
    }

    /// <summary>
    /// Opens an existing file-backed catalog or creates it when it does not exist.<br/>
    /// This preserves explicit file-backed catalog lifetime while giving setup code a low-friction idempotent entry point.<br/>
    /// </summary>
    /// <param name="path">The `.lbdx` catalog file path to open or create.</param>
    /// <param name="options">Optional catalog options; currently reserved and empty by design.</param>
    /// <returns>A disposable catalog over the opened or newly initialized file.</returns>
    public static Catalog CreateOrOpen(string path, CatalogOptions? options = null)
    {
        return File.Exists(RequirePath(path))
            ? Open(path, options)
            : Create(path, options);
    }

    /// <summary>
    /// Creates a new memory-backed catalog.<br/>
    /// Memory catalogs use the same catalog and index API shape as file-backed catalogs but do not have a durable reopen boundary.<br/>
    /// </summary>
    /// <param name="options">Optional catalog options; currently reserved and empty by design.</param>
    /// <returns>A disposable process-local memory catalog.</returns>
    public static Catalog CreateMemory(CatalogOptions? options = null)
    {
        CatalogOptions effectiveOptions = options ?? new CatalogOptions();
        LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
            CreateDefaultDataKernelOptions(),
            CreateCatalogDeveloperMetadata(),
            DataKernelTelemetryOptions.FromLevel(effectiveOptions.DiagnosticsLevel));
        return new Catalog(session, null, DataKernelBackingKind.Memory, effectiveOptions);
    }

    /// <summary>
    /// Compacts one closed file-backed catalog by rebuilding its active logical indexes into a dense sibling file, validating that shadow catalog, and replacing the source with rollback protection.<br/>
    /// The source path must not have another active LibraDex owner; callers such as Abraxas should enter their write lockout, close the current catalog, call this method, and reopen the returned path after completion.<br/>
    /// Dropped indexes and other unreachable physical extents are not copied, while maintained projection companions are recreated through their logical source index rather than copied independently.<br/>
    /// </summary>
    /// <param name="path">The existing closed `.lbdx` catalog path to compact.<br/></param>
    /// <param name="options">Optional compaction and catalog-open policy.<br/></param>
    /// <param name="cancellationToken">A token observed before replacement and between logical-index copy/validation boundaries.<br/></param>
    /// <returns>Exact file-size, index-count, tuple-count, and rollback-path results for the completed replacement.<br/></returns>
    public static LibraDexCompactionResult Compact(
        string path,
        LibraDexCompactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return LibraDexCatalogCompactor.Compact(path, options, cancellationToken);
    }

    /// <summary>
    /// Creates a durable, validated backup while this file-backed catalog remains open and usable.<br/>
    /// The method flushes committed source bytes, blocks storage publication and reads while physically copying them, verifies an exact SHA-256 image, and reopens the staged catalog before installation.<br/>
    /// Reader and writer blocking lasts through the source copy and destination durable flush; hashing and staged-catalog validation occur after that storage gate is released.<br/>
    /// An active durability batch is rejected rather than silently omitted; commit or abort that batch and retry.<br/>
    /// </summary>
    /// <param name="path">The destination backup path.<br/></param>
    /// <param name="options">Optional overwrite policy; the default preserves any existing destination.<br/></param>
    /// <param name="cancellationToken">A token observed before installation and between physical copy blocks.<br/></param>
    /// <returns>The installed backup path, exact byte count, hash, and validated index-definition count.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown for a memory-backed catalog or while a durability batch is active.<br/></exception>
    public LibraDexBackupResult Backup(
        string path,
        LibraDexBackupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return LibraDexCatalogBackup.Create(this, path, options, cancellationToken);
    }

    /// <summary>
    /// Closes the catalog by delegating to `Dispose`.<br/>
    /// This method exists for callers who prefer explicit verb-style lifetime control over `using` while preserving the same cleanup path.<br/>
    /// </summary>
    public void Close()
    {
        Dispose();
    }

    /// <summary>
    /// Releases the underlying catalog session.<br/>
    /// File-backed disposal releases the file handle; memory-backed disposal releases process-local committed memory segments.<br/>
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (CatalogIdentityGroupBatchManager batch in groupBatchManagers.Values)
        {
            if (batch.IsEnabled)
            {
                _ = batch.CommitAndDisable();
            }
        }

        session.Dispose();
        lock (compositeIndexesSync)
            compositeIndexes.Clear();
        disposed = true;
    }

    internal CatalogIdentityGroupBatchManager GetIdentityGroupBatch(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (!groupBatchManagers.TryGetValue(group, out CatalogIdentityGroupBatchManager? batch))
        {
            batch = new CatalogIdentityGroupBatchManager(this, group);
            groupBatchManagers.Add(group, batch);
        }

        return batch;
    }

    internal CatalogIndexSetInverse GetIndexSetInverse(CatalogIdentityGroupIndexes indexSet)
    {
        ArgumentNullException.ThrowIfNull(indexSet);
        if (!inverseIndexSets.TryGetValue(indexSet.Name, out CatalogIndexSetInverse? inverse))
        {
            inverse = new CatalogIndexSetInverse(indexSet);
            inverseIndexSets.Add(indexSet.Name, inverse);
        }

        return inverse;
    }

    /// <summary>
    /// Gets the catalog-session map that enforces and accelerates one persisted single-key-per-identity index.<br/>
    /// Handles opened more than once share the same map by group and index name, so identity-side reads do not rebuild one forward scan per handle.<br/>
    /// </summary>
    /// <typeparam name="TKey">The persisted index key type.<br/></typeparam>
    /// <typeparam name="TIdentity">The persisted index identity type.<br/></typeparam>
    /// <param name="index">The opened single-key-per-identity index.<br/></param>
    /// <returns>The shared typed identity-to-key map for the catalog session.<br/></returns>
    internal CatalogSingleKeyIdentityMap<TKey, TIdentity> GetSingleKeyIdentityMap<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        string key = string.Concat(index.Group, "\0", index.Name);
        lock (singleKeyIdentityMapsSync)
        {
            if (singleKeyIdentityMaps.TryGetValue(key, out object? existing))
            {
                if (existing is CatalogSingleKeyIdentityMap<TKey, TIdentity> typed)
                    return typed;

                throw new InvalidDataException(
                    $"Index '{index.Group}/{index.Name}' was opened with incompatible single-key map types. Expected '{typeof(TKey)}'/'{typeof(TIdentity)}'.");
            }

            CatalogSingleKeyIdentityMap<TKey, TIdentity> created = new();
            singleKeyIdentityMaps.Add(key, created);
            return created;
        }
    }

    /// <summary>
    /// Finds an existing single-key inverse map without allocating or initializing one.<br/>
    /// Callers must separately test readiness while consuming the map's protected committed state.<br/>
    /// </summary>
    /// <typeparam name="TKey">Native key type.<br/></typeparam>
    /// <typeparam name="TIdentity">Native identity type.<br/></typeparam>
    /// <param name="index">Owning index handle.<br/></param>
    /// <param name="map">Existing typed map when present.<br/></param>
    /// <returns>Whether an existing map has matching types.<br/></returns>
    internal bool TryGetExistingSingleKeyIdentityMap<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index,
        out CatalogSingleKeyIdentityMap<TKey, TIdentity>? map)
    {
        string key = string.Concat(index.Group, "\0", index.Name);
        lock (singleKeyIdentityMapsSync)
        {
            map = singleKeyIdentityMaps.TryGetValue(key, out object? existing)
                ? existing as CatalogSingleKeyIdentityMap<TKey, TIdentity> : null;
            return map is not null;
        }
    }

    internal bool TryIdentityExistsFromInverse(
        IIndex index,
        object identity,
        out bool exists)
    {
        ArgumentNullException.ThrowIfNull(index);
        string group = index.Group;
        string indexName = index.Name;
        string key = CreateIdentityLookupKey(group, indexName);
        IdentityLookupMode mode = identityLookupModes.TryGetValue(key, out IdentityLookupMode configured)
            ? configured
            : IdentityLookupMode.Explicit;
        if (mode == IdentityLookupMode.Disabled)
        {
            exists = false;
            return false;
        }

        if (!inverseIndexSets.TryGetValue(group, out CatalogIndexSetInverse? inverse))
        {
            if (mode != IdentityLookupMode.CreateOnFirstUse)
            {
                exists = false;
                return false;
            }

            inverse = GetIndexSetInverse(Indexes.IndexSet(group));
            inverse.EnsureLazyIndex(index);
        }
        else if (mode == IdentityLookupMode.CreateOnFirstUse &&
                 !inverse.Includes(indexName))
        {
            inverse.EnsureLazyIndex(index);
        }

        if (inverse.TryIdentityExists(indexName, identity, out exists))
            return true;

        exists = false;
        return false;
    }

    internal void ConfigureIdentityLookup(IIndex index, IdentityLookupMode mode)
    {
        ArgumentNullException.ThrowIfNull(index);
        string group = index.Group;
        string indexName = index.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        LibraDexIdentityLookup.ValidateMode(mode);
        if (mode is not IdentityLookupMode.Disabled and not IdentityLookupMode.Explicit &&
            index is not IIdentityPrimitiveTupleStreamer &&
            index is not IIdentityPrimitiveTupleExecutor)
        {
            throw new NotSupportedException(
                $"Index '{group}/{indexName}' cannot create an identity inversion because its physical facade does not expose key/identity tuple traversal.");
        }
        identityLookupModes[CreateIdentityLookupKey(group, indexName)] = mode;
        if (mode == IdentityLookupMode.Disabled)
        {
            if (inverseIndexSets.TryGetValue(group, out CatalogIndexSetInverse? disabledInverse))
                disabledInverse.Exclude(indexName);
            return;
        }

        if (mode is IdentityLookupMode.Explicit or IdentityLookupMode.CreateOnFirstUse)
            return;

        CatalogIndexSetInverse inverse = GetIndexSetInverse(Indexes.IndexSet(group));
        inverse.EnsureLazyIndex(index);
        if (mode == IdentityLookupMode.BuildOnOpen)
            inverse.BuildIndex(indexName);
    }

    internal void BuildIdentityLookup(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        string group = index.Group;
        string indexName = index.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (index is not IIdentityPrimitiveTupleStreamer &&
            index is not IIdentityPrimitiveTupleExecutor)
        {
            throw new NotSupportedException(
                $"Index '{group}/{indexName}' cannot build an identity inversion because its physical facade does not expose key/identity tuple traversal.");
        }
        CatalogIndexSetInverse inverse = GetIndexSetInverse(Indexes.IndexSet(group));
        inverse.EnsureLazyIndex(index);
        inverse.BuildIndex(indexName);
    }

    internal void ClearIdentityLookup(string group, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (inverseIndexSets.TryGetValue(group, out CatalogIndexSetInverse? inverse))
            inverse.Exclude(indexName);
    }

    internal IdentityLookupState GetIdentityLookupState(
        string group,
        string indexName,
        IdentityLookupMode mode)
    {
        if (mode == IdentityLookupMode.Disabled)
            return IdentityLookupState.Unavailable;
        if (!inverseIndexSets.TryGetValue(group, out CatalogIndexSetInverse? inverse))
            return IdentityLookupState.NotCreated;
        return inverse.GetIndexState(indexName);
    }

    internal IdentityLookupMode GetIdentityLookupMode(string group, string indexName)
    {
        return identityLookupModes.TryGetValue(
            CreateIdentityLookupKey(group, indexName),
            out IdentityLookupMode mode)
            ? mode
            : IdentityLookupMode.Explicit;
    }

    internal bool TryGetDuplicateIdentities(IIndex index, out object[] identities)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!TryPrepareIdentityLookupForCompleteRead(index, out CatalogIndexSetInverse? inverse))
        {
            identities = Array.Empty<object>();
            return false;
        }

        identities = inverse.GetDuplicateIdentities(index.Name);
        return true;
    }

    internal bool TryGetEntriesForDuplicateIdentities(IIndex index, out LibraDexObjectTuple[] entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!TryPrepareIdentityLookupForCompleteRead(index, out CatalogIndexSetInverse? inverse))
        {
            entries = Array.Empty<LibraDexObjectTuple>();
            return false;
        }

        entries = inverse.GetEntriesForDuplicateIdentities(index.Name);
        return true;
    }

    internal bool TryGetSingletonIdentities(IIndex index, out object[] identities)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!TryPrepareIdentityLookupForCompleteRead(index, out CatalogIndexSetInverse? inverse))
        {
            identities = Array.Empty<object>();
            return false;
        }

        identities = inverse.GetSingletonIdentities(index.Name);
        return true;
    }

    internal bool TryGetEntriesForSingletonIdentities(IIndex index, out LibraDexObjectTuple[] entries)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!TryPrepareIdentityLookupForCompleteRead(index, out CatalogIndexSetInverse? inverse))
        {
            entries = Array.Empty<LibraDexObjectTuple>();
            return false;
        }

        entries = inverse.GetEntriesForSingletonIdentities(index.Name);
        return true;
    }

    private bool TryPrepareIdentityLookupForCompleteRead(
        IIndex index,
        out CatalogIndexSetInverse inverse)
    {
        string key = CreateIdentityLookupKey(index.Group, index.Name);
        IdentityLookupMode mode = identityLookupModes.TryGetValue(key, out IdentityLookupMode configured)
            ? configured
            : IdentityLookupMode.Explicit;
        if (mode == IdentityLookupMode.Disabled)
        {
            inverse = null!;
            return false;
        }

        if (!inverseIndexSets.TryGetValue(index.Group, out inverse!) ||
            !inverse.Includes(index.Name))
        {
            if (mode == IdentityLookupMode.Explicit)
            {
                inverse = null!;
                return false;
            }

            inverse = GetIndexSetInverse(Indexes.IndexSet(index.Group));
            inverse.EnsureLazyIndex(index);
        }

        inverse.BuildIndex(index.Name);
        return true;
    }

    private static string CreateIdentityLookupKey(string group, string indexName)
        => string.Concat(group, "\0", indexName);

    internal bool TryGetActiveIdentityGroupBatch(string group, out CatalogIdentityGroupBatchManager batch)
    {
        if (!string.IsNullOrWhiteSpace(group) &&
            groupBatchManagers.TryGetValue(group, out CatalogIdentityGroupBatchManager? candidate) &&
            candidate.IsEnabled)
        {
            batch = candidate;
            return true;
        }

        batch = null!;
        return false;
    }

    /// <summary>
    /// Atomically deactivates one logical catalog index and its owned physical projection companions.<br/>
    /// Runtime inverse, identity-lookup, and single-key caches are invalidated only after the durable directory commit succeeds.<br/>
    /// </summary>
    /// <param name="info">Disconnected metadata for the active logical owner being dropped.<br/></param>
    internal void DropIndex(CatalogIndexInfo info)
    {
        if (TryGetActiveIdentityGroupBatch(info.Group, out _))
        {
            throw new InvalidOperationException(
                $"Index '{info.Group}/{info.Name}' cannot be dropped while its index-set durability batch is active.");
        }

        List<int> slots =
        [
            info.SlotIndex,
            info.ExactReversedProjectionSlotIndex,
            info.FoldedProjectionSlotIndex,
            info.SortKeyProjectionSlotIndex,
            info.FoldedReversedProjectionSlotIndex,
            info.NormalizedProjectionSlotIndex,
            info.NormalizedReversedProjectionSlotIndex
        ];
        AddAdditionalSortKeySlots(slots, info.SortKeyProfiles);
        session.DeactivateIndexDirectorySlots(slots.ToArray());

        string identityLookupKey = CreateIdentityLookupKey(info.Group, info.Name);
        identityLookupModes.Remove(identityLookupKey);
        if (inverseIndexSets.TryGetValue(info.Group, out CatalogIndexSetInverse? inverse))
            inverse.Exclude(info.Name);
        lock (singleKeyIdentityMapsSync)
            singleKeyIdentityMaps.Remove(identityLookupKey);
        lock (compositeIndexesSync)
            compositeIndexes.Remove(info.SlotIndex);
    }

    /// <summary>
    /// Deactivates one unpublished physical projection slot created during an interrupted additive lifecycle operation.<br/>
    /// This narrow recovery path must never be used for a slot already referenced by logical-owner metadata.<br/>
    /// </summary>
    /// <param name="slotIndex">The unpublished physical projection slot to deactivate.<br/></param>
    internal void DropUnpublishedProjection(int slotIndex)
    {
        session.DeactivateIndexDirectorySlots(new[] { slotIndex });
    }

    /// <summary>
    /// Publishes replacement metadata for one logical catalog owner after an additive physical projection has been completely populated.<br/>
    /// </summary>
    /// <param name="slotIndex">The logical owner's fixed catalog slot.<br/></param>
    /// <param name="metadata">The complete replacement metadata.<br/></param>
    internal void UpdateCatalogIndexMetadata(int slotIndex, CatalogIndexMetadata metadata)
    {
        _ = session.UpdateCatalogIndexMetadata(slotIndex, metadata);
    }

    /// <summary>
    /// Reads the complete persisted metadata for one active logical catalog owner.<br/>
    /// This internal form preserves fields that are intentionally omitted from the disconnected public discovery snapshot during additive metadata replacement.<br/>
    /// </summary>
    /// <param name="slotIndex">The active logical owner's fixed catalog slot.<br/></param>
    /// <param name="metadata">The complete decoded catalog metadata when available.<br/></param>
    /// <returns><c>true</c> when the slot is active and contains decodable metadata; otherwise <c>false</c>.<br/></returns>
    internal bool TryGetCatalogIndexMetadata(int slotIndex, out CatalogIndexMetadata metadata)
    {
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = session.IndexDirectory.ActiveSlots;
        for (var i = 0; i < activeSlots.Length; i++)
        {
            if (activeSlots[i].SlotIndex == slotIndex)
                return session.TryReadCatalogIndexMetadata(activeSlots[i], out metadata);
        }

        metadata = default;
        return false;
    }

    /// <summary>
    /// Atomically deactivates every logical owner and owned projection slot in one index set.<br/>
    /// Runtime inverse, identity-lookup, and single-key caches are invalidated only after the durable full-directory commit succeeds.<br/>
    /// </summary>
    /// <param name="group">The index-set identity universe to deactivate.<br/></param>
    /// <param name="infos">Disconnected metadata for every active logical owner in the set.<br/></param>
    internal void DropIndexSet(string group, ReadOnlySpan<CatalogIndexInfo> infos)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (TryGetActiveIdentityGroupBatch(group, out _))
            throw new InvalidOperationException($"Index set '{group}' cannot be dropped while its durability batch is active.");
        if (infos.Length == 0)
            return;

        List<int> slots = new(infos.Length * 7);
        for (var i = 0; i < infos.Length; i++)
        {
            ref readonly CatalogIndexInfo info = ref infos[i];
            if (!string.Equals(info.Group, group, StringComparison.Ordinal))
                throw new ArgumentException($"Index '{info.Group}/{info.Name}' does not belong to index set '{group}'.", nameof(infos));

            slots.Add(info.SlotIndex);
            slots.Add(info.ExactReversedProjectionSlotIndex);
            slots.Add(info.FoldedProjectionSlotIndex);
            slots.Add(info.SortKeyProjectionSlotIndex);
            slots.Add(info.FoldedReversedProjectionSlotIndex);
            slots.Add(info.NormalizedProjectionSlotIndex);
            slots.Add(info.NormalizedReversedProjectionSlotIndex);
            AddAdditionalSortKeySlots(slots, info.SortKeyProfiles);
        }

        session.DeactivateIndexDirectorySlots(slots.ToArray());

        if (inverseIndexSets.TryGetValue(group, out CatalogIndexSetInverse? inverse))
        {
            inverse.Drop();
            inverseIndexSets.Remove(group);
        }

        for (var i = 0; i < infos.Length; i++)
        {
            string identityLookupKey = CreateIdentityLookupKey(group, infos[i].Name);
            identityLookupModes.Remove(identityLookupKey);
            lock (singleKeyIdentityMapsSync)
                singleKeyIdentityMaps.Remove(identityLookupKey);
            lock (compositeIndexesSync)
                compositeIndexes.Remove(infos[i].SlotIndex);
        }
    }

    /// <summary>
    /// Adds owned sort-key profile slots after the legacy primary slot to one catalog-deactivation plan.<br/>
    /// Ordinal zero is already represented by <see cref="CatalogIndexInfo.SortKeyProjectionSlotIndex"/>, so this helper deliberately starts at ordinal one and avoids duplicate slot requests.<br/>
    /// </summary>
    /// <param name="slots">The mutable catalog slot plan.<br/></param>
    /// <param name="profiles">The persisted sort-key profile metadata, or null for legacy/non-string entries.<br/></param>
    private static void AddAdditionalSortKeySlots(
        List<int> slots,
        IReadOnlyList<LibraDexStringSortKeyProjectionInfo>? profiles)
    {
        if (profiles is null)
        {
            return;
        }

        for (int i = 1; i < profiles.Count; i++)
        {
            slots.Add(profiles[i].SlotIndex);
        }
    }

    internal LibraDexIndex<TKey, TIdentity> CreateGenericIndex<TKey, TIdentity>(
        string name,
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        CatalogIndexKeyFamily keyFamily = CatalogIndexKeyFamily.Scalar,
        CatalogIndexIdentityFamily identityFamily = CatalogIndexIdentityFamily.Scalar,
        string group = "",
        LibraDexIndexShapeSpec? logicalShape = null,
        int exactReversedProjectionSlotIndex = -1,
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(TIdentity), identityFamily, nameof(CreateGenericIndex));
        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index slot is already active.");
        }

        LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
        CatalogIndexMetadata metadata = CreateGenericMetadata<TKey, TIdentity>(
            group,
            name,
            options,
            keyWidth,
            identityWidth,
            keyFamily,
            identityFamily,
            logicalShape,
            exactReversedProjectionSlotIndex);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateGenericSlot(slotIndex, name), metadata);
        return new LibraDexIndex<TKey, TIdentity>(
            session,
            slotIndex,
            name,
            root.Offset,
            shape,
            ownsSession: false,
            this,
            options.Keys,
            options.IdentityKeyMultiplicity,
            group,
            keyFamily,
            identityFamily,
            logicalShape,
            exactReversedProjection,
            options.DateTimeKeyEncoding,
            options.ReadCacheMaxBytes,
            identityLookupMode);
    }

    internal LibraDexIndex<TKey, TIdentity> OpenGenericIndex<TKey, TIdentity>(
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(OpenGenericIndex));
        if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
        {
            throw new InvalidDataException("The requested LibraDex index slot is not active.");
        }

        LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
        string name = slot.Name;
        string group = string.Empty;
        CatalogIndexKeyFamily keyFamily = CatalogIndexKeyFamily.Unknown;
        CatalogIndexIdentityFamily identityFamily = CatalogIndexIdentityFamily.Unknown;
        IndexKeys keyContract = options.Keys;
        LibraDexIndexShapeSpec? logicalShape = null;
        CatalogIndexMetadata? persistedMetadata = null;
        if (session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
        {
            persistedMetadata = metadata;
            ValidateIdentityType(ResolvePersistedType(metadata.IdentityTypeName), metadata.IdentityFamily, nameof(OpenGenericIndex));
            name = string.IsNullOrWhiteSpace(metadata.IndexName) ? slot.Name : metadata.IndexName;
            group = metadata.Group;
            keyFamily = metadata.KeyFamily;
            identityFamily = metadata.IdentityFamily;
            keyContract = metadata.KeyContract;
            logicalShape = CreateLogicalShape(metadata);
        }

        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = null;
        if (typeof(TKey) == typeof(byte[]) &&
            persistedMetadata is CatalogIndexMetadata projectionOwnerMetadata &&
            projectionOwnerMetadata.ExactReversedProjectionSlotIndex >= 0)
        {
            exactReversedProjection = OpenGenericIndex<TKey, TIdentity>(
                projectionOwnerMetadata.ExactReversedProjectionSlotIndex,
                new IndexOptions { Keys = keyContract, IdentityKeyMultiplicity = projectionOwnerMetadata.IdentityKeyMultiplicity },
                keyWidth,
                identityWidth,
                IdentityLookupMode.Explicit);
        }

        return new LibraDexIndex<TKey, TIdentity>(
            session,
            slotIndex,
            name,
            slot.RootRouterOffset,
            shape,
            ownsSession: false,
            this,
            keyContract,
            persistedMetadata?.IdentityKeyMultiplicity ?? options.IdentityKeyMultiplicity,
            group,
            keyFamily,
            identityFamily,
            logicalShape,
            exactReversedProjection,
            persistedMetadata?.DateTimeKeyEncoding ?? options.DateTimeKeyEncoding,
            options.ReadCacheMaxBytes,
            identityLookupMode);
    }

    internal LibraDexIndex<TKey, TIdentity> CreateOrOpenGenericIndex<TKey, TIdentity>(
        string name,
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ThrowIfDisposed();
        return TryFindSlot(session, slotIndex, out _)
            ? OpenGenericIndex<TKey, TIdentity>(slotIndex, options, keyWidth, identityWidth, identityLookupMode)
            : CreateGenericIndex<TKey, TIdentity>(
                name,
                slotIndex,
                options,
                keyWidth,
                identityWidth,
                identityLookupMode: identityLookupMode);
    }

    internal IIndex OpenIndex(CatalogIndexInfo info)
    {
        ThrowIfDisposed();
        ValidateIdentityType(ResolvePersistedType(info.IdentityTypeName), info.IdentityFamily, nameof(OpenIndex));
        if (info.KeyFamily == CatalogIndexKeyFamily.Composite)
        {
            return OpenCompositeIndex(info);
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.String)
        {
            return new CatalogNamedStringKeyBuilder(this, Indexes, info.Group, info.Name).Open();
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.Blob &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Scalar &&
            info.Projections.Count > 0 &&
            info.Projections[0].Kind == LibraDexIndexProjectionKind.VariableBlobExact)
        {
            Type persistedIdentityType = ResolvePersistedType(info.IdentityTypeName);
            System.Reflection.MethodInfo openVariableBlobMethod = typeof(Catalog).GetMethod(
                nameof(OpenVariableBlobScalar8Index),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(Catalog), nameof(OpenVariableBlobScalar8Index));
            try
            {
                object? opened = openVariableBlobMethod
                    .MakeGenericMethod(persistedIdentityType)
                    .Invoke(this, new object?[] { info, checked(info.VarKeyMaxKeyLength - 1) });
                return opened is IIndex index
                    ? index
                    : throw new InvalidOperationException("The metadata-driven variable-blob index open did not return an index handle.");
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.BigInt)
        {
            if (info.Projections.Count > 0 && info.Projections[0].Kind == LibraDexIndexProjectionKind.BigIntFixedVarIdentity)
            {
                return OpenBigIntVarIdentityIndex(info);
            }

            Type persistedIdentityType = ResolvePersistedType(info.IdentityTypeName);
            LibraDexBigIntKeyStorage storage = info.Projections.Count > 0 && info.Projections[0].Kind == LibraDexIndexProjectionKind.BigIntVarLen
                ? LibraDexBigIntKeyStorage.VariableWidth
                : LibraDexBigIntKeyStorage.FixedWidth;
            System.Reflection.MethodInfo openBigIntMethod = typeof(Catalog).GetMethod(
                nameof(OpenBigIntScalar8Index),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(Catalog), nameof(OpenBigIntScalar8Index));
            try
            {
                object? opened = openBigIntMethod
                    .MakeGenericMethod(persistedIdentityType)
                    .Invoke(this, new object?[] { info, storage });
                return opened is IIndex index
                    ? index
                    : throw new InvalidOperationException("The metadata-driven LibraDex BigInt index open did not return an index handle.");
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        if (info.KeyFamily == CatalogIndexKeyFamily.Scalar &&
            info.IdentityFamily == CatalogIndexIdentityFamily.Blob &&
            info.VarIdentityMaxLength > 0)
        {
            Type persistedKeyType = ResolvePersistedType(info.KeyTypeName);
            if (persistedKeyType == typeof(ulong))
            {
                return OpenUInt64VarIdentityIndex(info);
            }

            throw new NotSupportedException("Only UInt64-key variable-identity scalar indexes are currently connected to metadata-driven open.");
        }

        Type keyType = ResolvePersistedType(info.KeyTypeName);
        Type identityType = ResolvePersistedType(info.IdentityTypeName);
        LibraDexScalarWidth? keyWidth = keyType == typeof(byte[])
            ? ResolvePersistedBlobWidth(info.VarKeyMaxKeyLength, "key")
            : null;
        LibraDexScalarWidth? identityWidth = identityType == typeof(byte[])
            ? ResolvePersistedBlobWidth(info.VarIdentityMaxLength, "identity")
            : null;

        System.Reflection.MethodInfo openMethod = typeof(Catalog).GetMethod(
            nameof(OpenGenericIndex),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Catalog), nameof(OpenGenericIndex));
        try
        {
            object? opened = openMethod
                .MakeGenericMethod(keyType, identityType)
                .Invoke(this, new object?[]
                {
                    info.SlotIndex,
                    new IndexOptions { Keys = info.KeyContract, IdentityKeyMultiplicity = info.IdentityKeyMultiplicity },
                    keyWidth,
                    identityWidth,
                    IdentityLookupMode.Explicit
                });
            return opened is IIndex index
                ? index
                : throw new InvalidOperationException("The metadata-driven LibraDex index open did not return an index handle.");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    internal IIndex CreateIndex(
        LibraDexIndexShapeSpec shape,
        int slotIndex,
        IndexOptions? options = null,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(shape);
        ValidateIdentityType(shape.IdentityType, shape.IdentityFamily, nameof(CreateIndex));
        if (shape.KeyType == typeof(byte[]) || shape.IdentityType == typeof(byte[]))
        {
            throw new NotSupportedException("Shape-driven byte[] index creation requires persisted scalar-width metadata.");
        }

        IndexOptions resolvedOptions = options ?? shape.ToIndexOptions();
        System.Reflection.MethodInfo createMethod = typeof(Catalog).GetMethod(
            nameof(CreateGenericIndex),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Catalog), nameof(CreateGenericIndex));
        try
        {
            object? created = createMethod
                .MakeGenericMethod(shape.KeyType, shape.IdentityType)
                .Invoke(this, new object?[]
                {
                    shape.Name,
                    slotIndex,
                    resolvedOptions,
                    null,
                    null,
                    shape.KeyFamily,
                    shape.IdentityFamily,
                    shape.Group,
                    shape,
                    -1,
                    null,
                    identityLookupMode
                });
            return created is IIndex index
                ? index
                : throw new InvalidOperationException("The shape-driven LibraDex index create did not return an index handle.");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// Creates a metadata-backed routed composite index from a logical shape descriptor.<br/>
    /// This slice persists the composite part contract into the fixed catalog directory and returns the in-process routed-component proof; durable mini-router contents are connected in a later storage slice.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape to persist and expose.</param>
    /// <param name="slotIndex">The fixed catalog slot used as the durable metadata anchor.</param>
    /// <param name="options">Optional per-index options overriding the shape's default index options.</param>
    /// <returns>A routed composite index handle for the current process.</returns>
    internal LibraDexRoutedCompositeIndex CreateCompositeIndex(LibraDexIndexShapeSpec shape, int slotIndex, IndexOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(shape);
        ValidateIdentityType(shape.IdentityType, shape.IdentityFamily, nameof(CreateCompositeIndex));
        if (shape.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new ArgumentException("Composite index creation requires a composite logical shape.", nameof(shape));
        }

        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index slot is already active.");
        }

        CatalogIndexMetadata metadata = CreateCompositeMetadata(shape, options ?? shape.ToIndexOptions());
        byte[] rootNode = LibraDexCompositeNodePageCodec.Encode(
            shape,
            tier: 0,
            Array.Empty<object>(),
            Array.Empty<LibraDexCompositeNodeChildPage>());
        (long rootOffset, _) = session.CreateCompositeNodePageIndex(CreateGenericSlot(slotIndex, shape.Name), metadata, rootNode);
        LibraDexRoutedCompositeIndex created = new(this, shape, session, slotIndex, rootOffset);
        lock (compositeIndexesSync)
            compositeIndexes.Add(slotIndex, created);
        return created;
    }

    /// <summary>
    /// Opens a metadata-backed routed composite index from catalog discovery information.<br/>
    /// The returned handle reconstructs the logical shape and can classify/materialize condition-builder routes, while persisted tuple contents remain unavailable until durable composite mini-router storage is implemented.<br/>
    /// </summary>
    /// <param name="info">The catalog metadata snapshot for the composite index.</param>
    /// <returns>A routed composite index handle for the current process.</returns>
    internal LibraDexRoutedCompositeIndex OpenCompositeIndex(CatalogIndexInfo info)
    {
        ThrowIfDisposed();
        ValidateIdentityType(ResolvePersistedType(info.IdentityTypeName), info.IdentityFamily, nameof(OpenCompositeIndex));
        if (info.KeyFamily != CatalogIndexKeyFamily.Composite)
        {
            throw new ArgumentException("The supplied catalog index metadata does not describe a composite index.", nameof(info));
        }

        lock (compositeIndexesSync)
        {
            if (compositeIndexes.TryGetValue(info.SlotIndex, out LibraDexRoutedCompositeIndex? existing))
                return existing;

            LibraDexIndexShapeSpec shape = info.CreateShape();
            LibraDexRoutedCompositeIndex opened;
            if (!TryFindSlot(session, info.SlotIndex, out IndexDirectorySlotSnapshot slot))
            {
                opened = new LibraDexRoutedCompositeIndex(this, shape, session, info.SlotIndex);
            }
            else if (session.TryReadCompositeNodePage(slot.RootRouterOffset, out _))
            {
                opened = LibraDexRoutedCompositeIndex.OpenFromNodePages(this, shape, session, info.SlotIndex, slot.RootRouterOffset, info.ItemCount);
            }
            else if (session.TryReadCompositeSnapshot(slot, out byte[] snapshot))
            {
                IReadOnlyList<LibraDexCompositeEntry> entries = LibraDexCompositeSnapshotCodec.Decode(shape, snapshot);
                opened = new LibraDexRoutedCompositeIndex(this, shape, session, info.SlotIndex, entries);
            }
            else
            {
                opened = new LibraDexRoutedCompositeIndex(this, shape, session, info.SlotIndex);
            }

            compositeIndexes.Add(info.SlotIndex, opened);
            return opened;
        }
    }

    internal VarKeyScalar8Index CreateVarKeyScalar8Index(string name, int slotIndex, int maxKeyLength)
    {
        return CreateVarKeyScalar8Index(name, slotIndex, maxKeyLength, metadata: null);
    }

    internal VarKeyScalar8Index CreateVarKeyScalar8Index(string name, int slotIndex, int maxKeyLength, CatalogIndexMetadata? metadata)
    {
        ThrowIfDisposed();
        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index slot is already active.");
        }

        VarLenOptimizerMaintenancePolicy policy = VarLenOptimizerMaintenancePolicy.Automatic(
            depthThreshold: 3,
            shelfItemThreshold: 32,
            hitThreshold: 12);
        (VarKeyScalar8IndexHandle handle, _, _) = metadata.HasValue
            ? session.CreateVarKeyScalar8RootRouterIndex(
                CreateGenericSlot(slotIndex, name),
                metadata.Value,
                maxKeyLength,
                optimizerRouteFanout: 16,
                policy)
            : session.CreateVarKeyScalar8RootRouterIndex(
                CreateGenericSlot(slotIndex, name),
                maxKeyLength,
                optimizerRouteFanout: 16,
                policy);
        return new VarKeyScalar8Index(session, handle, slotIndex, name, ownsSession: false);
    }

    internal VarKeyScalar8Index OpenVarKeyScalar8Index(int slotIndex, int maxKeyLength)
    {
        ThrowIfDisposed();
        if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
        {
            throw new InvalidDataException("The requested LibraDex variable-key index slot is not active.");
        }

        VarKeyScalar8IndexHandle handle = new(
            slot.RootRouterOffset,
            maxKeyLength,
            OptimizerRouteFanout: 16,
            VarLenOptimizerMaintenancePolicy.Automatic(
                depthThreshold: 3,
                shelfItemThreshold: 32,
                hitThreshold: 12));
        handle.Validate();
        return new VarKeyScalar8Index(session, handle, slotIndex, slot.Name, ownsSession: false);
    }

    /// <summary>
    /// Creates a metadata-backed bounded variable-length blob-key index over scalar 8-byte identities.<br/>
    /// The public maximum counts payload bytes only; the physical `VS8` profile receives one additional byte for LibraDex's null/empty/value sentinel.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type encoded into the 8-byte identity lane.<br/></typeparam>
    /// <param name="group">The identity group that owns the index.<br/></param>
    /// <param name="name">The logical index name inside the group.<br/></param>
    /// <param name="slotIndex">The catalog slot allocated to the index.<br/></param>
    /// <param name="maxKeyBytes">The maximum developer-facing blob payload length.<br/></param>
    /// <param name="options">The resolved persisted index options.<br/></param>
    /// <returns>A public variable-blob facade over the new routed index.</returns>
    internal LibraDexVariableBlobScalar8Index<TIdentity> CreateVariableBlobScalar8Index<TIdentity>(
        string group,
        string name,
        int slotIndex,
        int maxKeyBytes,
        IndexOptions options)
    {
        ThrowIfDisposed();
        EnsureSupportedIdentityKeyMultiplicity(options, supportsSingleKeyPerIdentity: false, nameof(CreateVariableBlobScalar8Index));
        ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(CreateVariableBlobScalar8Index));
        if (maxKeyBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxKeyBytes), maxKeyBytes, "Variable blob indexes require a positive maximum payload byte count.");
        if (LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null) != LibraDexScalarWidth.Bytes8)
            throw new NotSupportedException("Variable blob indexes currently require an identity type that encodes into the 8-byte scalar identity lane.");

        int physicalMaxKeyLength = checked(maxKeyBytes + 1);
        CatalogIndexMetadata metadata = CreateVariableBlobScalar8Metadata<TIdentity>(
            group,
            name,
            physicalMaxKeyLength,
            options);
        VarKeyScalar8Index inner = CreateVarKeyScalar8Index(name, slotIndex, physicalMaxKeyLength, metadata);
        return new LibraDexVariableBlobScalar8Index<TIdentity>(this, group, name, inner, options.Keys);
    }

    /// <summary>
    /// Opens a persisted bounded variable-length blob-key index after validating its public payload cap and scalar identity type.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The expected scalar identity type.<br/></typeparam>
    /// <param name="info">The discovered catalog metadata for the index.<br/></param>
    /// <param name="maxKeyBytes">The expected developer-facing maximum blob payload length.<br/></param>
    /// <returns>A reopened public variable-blob facade.</returns>
    internal LibraDexVariableBlobScalar8Index<TIdentity> OpenVariableBlobScalar8Index<TIdentity>(
        CatalogIndexInfo info,
        int maxKeyBytes)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(OpenVariableBlobScalar8Index));
        if (info.KeyFamily != CatalogIndexKeyFamily.Blob ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            info.Projections.Count == 0 ||
            info.Projections[0].Kind != LibraDexIndexProjectionKind.VariableBlobExact)
        {
            throw new InvalidDataException("The requested catalog entry is not a bounded variable-blob/scalar-identity index.");
        }

        Type persistedIdentityType = ResolvePersistedType(info.IdentityTypeName);
        if (persistedIdentityType != typeof(TIdentity))
            throw new InvalidDataException($"The persisted variable-blob identity type is {persistedIdentityType.FullName}, not {typeof(TIdentity).FullName}.");

        int persistedMaxKeyBytes = checked(info.VarKeyMaxKeyLength - 1);
        if (persistedMaxKeyBytes != maxKeyBytes)
            throw new InvalidDataException($"The persisted variable-blob payload cap is {persistedMaxKeyBytes} bytes, not {maxKeyBytes} bytes.");

        VarKeyScalar8Index inner = OpenVarKeyScalar8Index(info.SlotIndex, info.VarKeyMaxKeyLength);
        return new LibraDexVariableBlobScalar8Index<TIdentity>(this, info.Group, info.Name, inner, info.KeyContract);
    }

    internal LibraDexBigIntScalar8Index<TIdentity> CreateBigIntScalar8Index<TIdentity>(
        string group,
        string name,
        int slotIndex,
        int maxBytes,
        LibraDexBigIntKeyStorage storage,
        IndexOptions options)
    {
        ThrowIfDisposed();
        EnsureSupportedIdentityKeyMultiplicity(options, supportsSingleKeyPerIdentity: false, nameof(CreateBigIntScalar8Index));
        ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(CreateBigIntScalar8Index));
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));

        int maxKeyLength = GetBigIntScalarPhysicalMaxKeyLength(maxBytes, storage);
        CatalogIndexMetadata metadata = CreateBigIntMetadata<TIdentity>(group, name, maxKeyLength, storage, options);
        if (storage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            LibraDexScalarWidth identityWidth = LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null);
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Profile profile = FixedNScalar8Profile.Default64KiB(maxKeyLength);
                (FixedNScalar8IndexHandle handle, _) = session.CreateFixedNScalar8RootRouterIndex(
                    CreateGenericSlot(slotIndex, name),
                    metadata,
                    profile);
                FixedNScalar8Index inner = new(session, handle, slotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(this, group, name, inner, maxBytes, storage, options.Keys);
            }

            if (identityWidth == LibraDexScalarWidth.Bytes16)
            {
                FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(maxKeyLength);
                (FixedNScalar16IndexHandle handle, _) = session.CreateFixedNScalar16RootRouterIndex(
                    CreateGenericSlot(slotIndex, name),
                    metadata,
                    profile);
                FixedNScalar16Index inner = new(session, handle, slotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(this, group, name, inner, maxBytes, storage, options.Keys);
            }

            throw new NotSupportedException("Fixed BigInt indexes require scalar-8 or scalar-16 identity types.");
        }
        else
        {
            VarKeyScalar8Index inner = CreateVarKeyScalar8Index(name, slotIndex, maxKeyLength, metadata);
            return new LibraDexBigIntScalar8Index<TIdentity>(this, group, name, inner, maxBytes, storage, options.Keys);
        }
    }

    internal LibraDexBigIntScalar8Index<TIdentity> OpenBigIntScalar8Index<TIdentity>(
        CatalogIndexInfo info,
        LibraDexBigIntKeyStorage expectedStorage)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(TIdentity), CatalogIndexIdentityFamily.Scalar, nameof(OpenBigIntScalar8Index));
        if (info.KeyFamily != CatalogIndexKeyFamily.BigInt ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Scalar ||
            info.VarKeyMaxKeyLength < 4)
        {
            throw new InvalidDataException("The persisted catalog entry is not a reopenable BigInt/scalar index.");
        }

        LibraDexIndexProjectionKind expectedProjection = expectedStorage == LibraDexBigIntKeyStorage.FixedWidth
            ? LibraDexIndexProjectionKind.BigIntFixed
            : LibraDexIndexProjectionKind.BigIntVarLen;
        if (info.Projections.Count == 0 || info.Projections[0].Kind != expectedProjection)
        {
            throw new InvalidDataException("The requested BigInt storage shape does not match persisted catalog metadata.");
        }

        Type identityType = ResolvePersistedType(info.IdentityTypeName);
        if (identityType != typeof(TIdentity))
        {
            throw new InvalidDataException($"The requested BigInt identity type {typeof(TIdentity).FullName} does not match persisted metadata type {identityType.FullName}.");
        }

        int maxBytes = GetBigIntScalarMaxBytes(info.VarKeyMaxKeyLength, expectedStorage);
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(info.VarKeyMaxKeyLength));
        if (expectedStorage == LibraDexBigIntKeyStorage.FixedWidth)
        {
            LibraDexScalarWidth identityWidth = LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(null);
            if (identityWidth == LibraDexScalarWidth.Bytes8)
            {
                FixedNScalar8Profile profile = FixedNScalar8Profile.Default64KiB(info.VarKeyMaxKeyLength);
                FixedNScalar8IndexHandle handle = session.OpenFixedNScalar8ShelfIndex(info.SlotIndex, profile);
                FixedNScalar8Index inner = new(session, handle, info.SlotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(this, info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
            }

            if (identityWidth == LibraDexScalarWidth.Bytes16)
            {
                FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(info.VarKeyMaxKeyLength);
                FixedNScalar16IndexHandle handle = session.OpenFixedNScalar16ShelfIndex(info.SlotIndex, profile);
                FixedNScalar16Index inner = new(session, handle, info.SlotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(this, info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
            }

            throw new NotSupportedException("Fixed BigInt indexes require scalar-8 or scalar-16 identity types.");
        }
        else
        {
            VarKeyScalar8Index inner = OpenVarKeyScalar8Index(info.SlotIndex, info.VarKeyMaxKeyLength);
            return new LibraDexBigIntScalar8Index<TIdentity>(this, info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
        }
    }

    internal LibraDexBigIntVarIdentityIndex CreateBigIntVarIdentityIndex(
        string group,
        string name,
        int slotIndex,
        int maxBytes,
        int maxIdentityBytes,
        IndexOptions options)
    {
        ThrowIfDisposed();
        EnsureSupportedIdentityKeyMultiplicity(options, supportsSingleKeyPerIdentity: false, nameof(CreateBigIntVarIdentityIndex));
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(CreateBigIntVarIdentityIndex));
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        int maxKeyLength = checked(maxBytes + 3);
        FixedNVarIdentityProfile profile = FixedNVarIdentityProfile.Default64KiB(maxKeyLength, maxIdentityBytes);
        CatalogIndexMetadata metadata = CreateBigIntVarIdentityMetadata(group, name, maxKeyLength, maxIdentityBytes, options);
        (FixedNVarIdentityIndexHandle handle, _) = session.CreateFixedNVarIdentityRootRouterIndex(
            CreateGenericSlot(slotIndex, name),
            metadata,
            profile);
        FixedNVarIdentityIndex inner = new(session, handle, slotIndex);
        return new LibraDexBigIntVarIdentityIndex(this, group, name, inner, maxBytes, maxIdentityBytes, options.Keys);
    }

    internal LibraDexBigIntVarIdentityIndex OpenBigIntVarIdentityIndex(CatalogIndexInfo info)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(OpenBigIntVarIdentityIndex));
        if (info.KeyFamily != CatalogIndexKeyFamily.BigInt ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Blob ||
            info.Projections.Count == 0 ||
            info.Projections[0].Kind != LibraDexIndexProjectionKind.BigIntFixedVarIdentity ||
            info.VarKeyMaxKeyLength < 4 ||
            info.VarIdentityMaxLength <= 0)
        {
            throw new InvalidDataException("The persisted catalog entry is not a reopenable BigInt variable-identity index.");
        }

        int maxBytes = info.VarKeyMaxKeyLength - 3;
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(info.VarKeyMaxKeyLength));
        FixedNVarIdentityProfile profile = FixedNVarIdentityProfile.Default64KiB(info.VarKeyMaxKeyLength, info.VarIdentityMaxLength);
        FixedNVarIdentityIndexHandle handle = new(info.RootRouterOffset, profile, IsRouted: true);
        FixedNVarIdentityIndex inner = new(session, handle, info.SlotIndex);
        return new LibraDexBigIntVarIdentityIndex(this, info.Group, info.Name, inner, maxBytes, info.VarIdentityMaxLength, info.KeyContract);
    }

    /// <summary>
    /// Creates a metadata-backed UInt64-key variable-identity index over routed `SV8` storage.<br/>
    /// The catalog metadata marks the identity family as blob with a variable identity cap, distinguishing this shape from fixed-width byte-array identities.<br/>
    /// </summary>
    /// <param name="group">The identity group that owns the index.</param>
    /// <param name="name">The logical index name inside the group.</param>
    /// <param name="slotIndex">The fixed catalog slot used for the root router.</param>
    /// <param name="maxIdentityBytes">The maximum raw identity byte count accepted by this index.</param>
    /// <param name="options">The index options for duplicate-key behavior.</param>
    /// <returns>A catalog-owned UInt64/SV8 variable-identity index facade.</returns>
    internal LibraDexUInt64VarIdentityIndex CreateUInt64VarIdentityIndex(
        string group,
        string name,
        int slotIndex,
        int maxIdentityBytes,
        IndexOptions options)
    {
        ThrowIfDisposed();
        EnsureSupportedIdentityKeyMultiplicity(options, supportsSingleKeyPerIdentity: false, nameof(CreateUInt64VarIdentityIndex));
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(CreateUInt64VarIdentityIndex));
        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex UInt64 variable-identity index slot is already active.");
        }

        Scalar8VarIdentityProfile.Create(Scalar8VarIdentityProfile.Default8KiB.ShelfExtentSize, maxIdentityBytes);
        CatalogIndexMetadata metadata = CreateUInt64VarIdentityMetadata(group, name, maxIdentityBytes, options);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateGenericSlot(slotIndex, name), metadata);
        Scalar8VarIdentityIndex inner = new(session, slotIndex, name, root.Offset, maxIdentityBytes, ownsSession: false);
        return new LibraDexUInt64VarIdentityIndex(this, group, name, inner, options.Keys);
    }

    /// <summary>
    /// Opens a metadata-backed UInt64-key variable-identity index over routed `SV8` storage.<br/>
    /// Reopen validates the persisted key family and variable identity cap before returning the condition-builder facade.<br/>
    /// </summary>
    /// <param name="info">The persisted catalog metadata for the index.</param>
    /// <returns>A catalog-owned UInt64/SV8 variable-identity index facade.</returns>
    internal LibraDexUInt64VarIdentityIndex OpenUInt64VarIdentityIndex(CatalogIndexInfo info)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(OpenUInt64VarIdentityIndex));
        if (info.KeyFamily != CatalogIndexKeyFamily.Scalar ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Blob ||
            info.VarIdentityMaxLength <= 0 ||
            ResolvePersistedType(info.KeyTypeName) != typeof(ulong) ||
            ResolvePersistedType(info.IdentityTypeName) != typeof(byte[]))
        {
            throw new InvalidDataException("The persisted catalog entry is not a reopenable UInt64 variable-identity index.");
        }

        Scalar8VarIdentityIndex inner = new(session, info.SlotIndex, info.Name, info.RootRouterOffset, info.VarIdentityMaxLength, ownsSession: false);
        return new LibraDexUInt64VarIdentityIndex(this, info.Group, info.Name, inner, info.KeyContract);
    }

    /// <summary>
    /// Creates a metadata-backed raw-byte variable-key/variable-identity index over routed `VV` storage.<br/>
    /// The catalog metadata carries both physical byte caps so file-backed catalogs can reopen the same raw tuple index for workbench and adapter paths.<br/>
    /// </summary>
    /// <param name="group">The identity group that owns the index.<br/></param>
    /// <param name="name">The logical index name inside the group.<br/></param>
    /// <param name="slotIndex">The fixed catalog slot used for the root router.<br/></param>
    /// <param name="maxKeyBytes">The maximum physical variable-key bytes, including the internal sentinel byte.<br/></param>
    /// <param name="maxIdentityBytes">The maximum raw identity byte count accepted by this index.<br/></param>
    /// <param name="options">The index options for duplicate-key behavior.<br/></param>
    /// <returns>A catalog-owned raw `VV` index facade.</returns>
    internal VarKeyVarIdentityIndex CreateVarKeyVarIdentityIndex(
        string group,
        string name,
        int slotIndex,
        int maxKeyBytes,
        int maxIdentityBytes,
        IndexOptions options)
    {
        ThrowIfDisposed();
        EnsureSupportedIdentityKeyMultiplicity(options, supportsSingleKeyPerIdentity: false, nameof(CreateVarKeyVarIdentityIndex));
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(CreateVarKeyVarIdentityIndex));
        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex VV index slot is already active.");
        }

        VarKeyVarIdentityProfile.Create(VarKeyVarIdentityProfile.Default8KiB.ShelfExtentSize, maxKeyBytes, maxIdentityBytes);
        CatalogIndexMetadata metadata = CreateVarKeyVarIdentityMetadata(group, name, maxKeyBytes, maxIdentityBytes, options);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateGenericSlot(slotIndex, name), metadata);
        return new VarKeyVarIdentityIndex(session, slotIndex, name, root.Offset, maxKeyBytes, maxIdentityBytes, ownsSession: false);
    }

    /// <summary>
    /// Opens a metadata-backed raw-byte variable-key/variable-identity index over routed `VV` storage.<br/>
    /// Reopen validates the persisted byte caps before returning the catalog-owned raw tuple facade.<br/>
    /// </summary>
    /// <param name="info">The persisted catalog metadata for the index.<br/></param>
    /// <returns>A catalog-owned raw `VV` index facade.</returns>
    internal VarKeyVarIdentityIndex OpenVarKeyVarIdentityIndex(CatalogIndexInfo info)
    {
        ThrowIfDisposed();
        ValidateIdentityType(typeof(byte[]), CatalogIndexIdentityFamily.Blob, nameof(OpenVarKeyVarIdentityIndex));
        if (info.KeyFamily != CatalogIndexKeyFamily.Blob ||
            info.IdentityFamily != CatalogIndexIdentityFamily.Blob ||
            info.VarKeyMaxKeyLength <= 0 ||
            info.VarIdentityMaxLength <= 0 ||
            ResolvePersistedType(info.KeyTypeName) != typeof(byte[]) ||
            ResolvePersistedType(info.IdentityTypeName) != typeof(byte[]))
        {
            throw new InvalidDataException("The persisted catalog entry is not a reopenable raw VV index.");
        }

        return new VarKeyVarIdentityIndex(session, info.SlotIndex, info.Name, info.RootRouterOffset, info.VarKeyMaxKeyLength, info.VarIdentityMaxLength, ownsSession: false);
    }

    /// <summary>
    /// Validates every existing logical rich-metadata index against the catalog-level required identity type.<br/>
    /// Hidden string-projection slots inherit their owner's identity contract and intentionally have no independent rich metadata, so the first pass identifies those owned slots before the second pass rejects genuinely unowned legacy slots.<br/>
    /// This runs only for constrained catalogs, so ordinary mixed-identity LibraDex catalogs do not pay for an open-time scan.<br/>
    /// </summary>
    private void ValidateExistingIdentityContracts()
    {
        Type? required = Options.RequiredIdentityType;
        if (required is null)
        {
            return;
        }

        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = session.IndexDirectory.ActiveSlots;
        HashSet<int> ownedProjectionSlots = new();
        for (int i = 0; i < activeSlots.Length; i++)
        {
            if (!session.TryReadCatalogIndexMetadata(activeSlots[i], out CatalogIndexMetadata metadata))
            {
                continue;
            }

            if (metadata.ExactReversedProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.ExactReversedProjectionSlotIndex);
            }
            if (metadata.FoldedProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.FoldedProjectionSlotIndex);
            }
            if (metadata.SortKeyProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.SortKeyProjectionSlotIndex);
            }
            if (metadata.SortKeyProfiles is { Count: > 1 } sortKeyProfiles)
            {
                for (int j = 1; j < sortKeyProfiles.Count; j++)
                {
                    ownedProjectionSlots.Add(sortKeyProfiles[j].SlotIndex);
                }
            }
            if (metadata.FoldedReversedProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.FoldedReversedProjectionSlotIndex);
            }
            if (metadata.NormalizedProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.NormalizedProjectionSlotIndex);
            }
            if (metadata.NormalizedReversedProjectionSlotIndex >= 0)
            {
                ownedProjectionSlots.Add(metadata.NormalizedReversedProjectionSlotIndex);
            }
        }

        for (int i = 0; i < activeSlots.Length; i++)
        {
            IndexDirectorySlotSnapshot slot = activeSlots[i];
            if (!session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
            {
                if (ownedProjectionSlots.Contains(slot.SlotIndex))
                {
                    continue;
                }

                throw new InvalidDataException($"Catalog identity policy requires {required.FullName}, but slot {slot.SlotIndex} has no rich identity metadata.");
            }

            ValidateIdentityType(ResolvePersistedType(metadata.IdentityTypeName), metadata.IdentityFamily, "Open");
        }
    }

    /// <summary>
    /// Enforces the catalog-level required identity type for create, open, and adapter-bound identity surfaces.<br/>
    /// The check is intentionally centralized so Abraxas can constrain a whole catalog to UInt64 identities without repeating guards at every index call site.<br/>
    /// </summary>
    /// <param name="identityType">The CLR identity type requested by the operation.<br/></param>
    /// <param name="identityFamily">The persisted identity family requested by the operation.<br/></param>
    /// <param name="operationName">The calling operation name used in the exception message.<br/></param>
    internal void ValidateIdentityType(Type identityType, CatalogIndexIdentityFamily identityFamily, string operationName)
    {
        Type? required = Options.RequiredIdentityType;
        if (required is null)
        {
            return;
        }

        if (identityFamily != CatalogIndexIdentityFamily.Scalar || identityType != required)
        {
            throw new InvalidOperationException($"Catalog operation '{operationName}' requires scalar identity type {required.FullName}, but received {identityFamily} identity type {identityType.FullName}.");
        }
    }

    /// <summary>
    /// Verifies that a requested identity-to-key multiplicity contract is enforced before catalog metadata records it.<br/>
    /// This keeps `SingleKeyPerIdentity` from becoming a metadata-only promise on index facades whose mutation paths do not yet maintain that invariant.<br/>
    /// </summary>
    /// <param name="options">The resolved index options for the create path.</param>
    /// <param name="supportsSingleKeyPerIdentity">Whether the create path enforces <see cref="IdentityKeyMultiplicity.SingleKeyPerIdentity"/>.</param>
    /// <param name="operationName">The operation name used in failure messages.</param>
    private static void EnsureSupportedIdentityKeyMultiplicity(
        IndexOptions options,
        bool supportsSingleKeyPerIdentity,
        string operationName)
    {
        if (options.IdentityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity &&
            !supportsSingleKeyPerIdentity)
        {
            throw new NotSupportedException($"Catalog operation '{operationName}' does not enforce {nameof(IdentityKeyMultiplicity.SingleKeyPerIdentity)} yet.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Catalog));
        }
    }

    private static string RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file-backed LibraDex catalog requires a path.", nameof(path));
        }

        return path;
    }

    private static Type ResolvePersistedType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidDataException("The requested LibraDex index does not have persisted CLR type metadata.");
        }

        return Type.GetType(typeName, throwOnError: false)
            ?? throw new NotSupportedException($"The persisted LibraDex CLR type '{typeName}' could not be resolved.");
    }

    private static bool TryFindSlot(
        LibraDexFileSession session,
        int slotIndex,
        out IndexDirectorySlotSnapshot slot)
    {
        ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = session.IndexDirectory.ActiveSlots;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            if (activeSlots[i].SlotIndex == slotIndex)
            {
                slot = activeSlots[i];
                return true;
            }
        }

        slot = default;
        return false;
    }

    private static LibraDexGenericScalarShape ResolveGenericShape<TKey, TIdentity>(
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth)
    {
        LibraDexScalarWidth resolvedKeyWidth = LibraDexGenericScalarCodec<TKey>.ResolveWidth(keyWidth);
        LibraDexScalarWidth resolvedIdentityWidth = LibraDexGenericScalarCodec<TIdentity>.ResolveWidth(identityWidth);
        return (resolvedKeyWidth, resolvedIdentityWidth) switch
        {
            (LibraDexScalarWidth.Bytes8, LibraDexScalarWidth.Bytes8) => LibraDexGenericScalarShape.SS88,
            (LibraDexScalarWidth.Bytes16, LibraDexScalarWidth.Bytes8) => LibraDexGenericScalarShape.SS168,
            (LibraDexScalarWidth.Bytes8, LibraDexScalarWidth.Bytes16) => LibraDexGenericScalarShape.SS816,
            (LibraDexScalarWidth.Bytes16, LibraDexScalarWidth.Bytes16) => LibraDexGenericScalarShape.SS1616,
            (LibraDexScalarWidth.Bytes32, LibraDexScalarWidth.Bytes8) => LibraDexGenericScalarShape.FS328,
            (LibraDexScalarWidth.Bytes32, LibraDexScalarWidth.Bytes16) => LibraDexGenericScalarShape.FS3216,
            _ => throw new NotSupportedException("The requested generic LibraDex scalar-width combination is not supported.")
        };
    }

    private static IndexDirectorySlotSnapshot CreateGenericSlot(int slotIndex, string name)
    {
        if ((uint)slotIndex >= IndexDirectoryLayout.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Index directory slot is outside the fixed directory.");
        }

        return new IndexDirectorySlotSnapshot(
            SlotIndex: slotIndex,
            State: IndexDirectoryLayout.ActiveState,
            Flags: 0,
            RootRouterOffset: 0,
            MetadataOffset: 0,
            ItemCount: 0,
            Generation: 1,
            KeyProfileId: 1,
            IdentityProfileId: 1,
            RouterProfileId: 1,
            AllocationClassId: 1,
            Name: name);
    }

    private static CatalogIndexMetadata CreateGenericMetadata<TKey, TIdentity>(
        string group,
        string name,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth,
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily,
        LibraDexIndexShapeSpec? logicalShape,
        int exactReversedProjectionSlotIndex = -1)
    {
        LibraDexProjectionDirectionSet directions = logicalShape?.Directions ?? LibraDexProjectionDirectionSet.Forward;
        LibraDexIndexSortOrder sortOrder = logicalShape?.SortOrder ?? options.SortOrder;
        IReadOnlyList<LibraDexIndexProjectionSpec> projections = logicalShape?.Projections ?? Array.Empty<LibraDexIndexProjectionSpec>();
        IReadOnlyList<LibraDexCompositeKeyPartSpec> compositeParts = logicalShape?.CompositeParts ?? Array.Empty<LibraDexCompositeKeyPartSpec>();
        int fixedKeyBytes = typeof(TKey) == typeof(byte[]) && keyWidth.HasValue
            ? checked((int)keyWidth.Value)
            : 0;
        int fixedIdentityBytes = typeof(TIdentity) == typeof(byte[]) && identityWidth.HasValue
            ? checked((int)identityWidth.Value)
            : 0;
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(TKey)),
            GetStableTypeName(typeof(TIdentity)),
            keyFamily,
            identityFamily,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            directions,
            sortOrder,
            projections,
            compositeParts,
            fixedKeyBytes,
            fixedIdentityBytes,
            exactReversedProjectionSlotIndex,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            logicalShape is not null,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    private static LibraDexScalarWidth ResolvePersistedBlobWidth(int width, string side)
    {
        return width switch
        {
            8 => LibraDexScalarWidth.Bytes8,
            16 => LibraDexScalarWidth.Bytes16,
            32 => LibraDexScalarWidth.Bytes32,
            _ => throw new NotSupportedException($"Metadata-driven byte[] index opening requires a persisted {side} scalar width.")
        };
    }

    /// <summary>
    /// Creates the persisted catalog metadata record for a composite index shape.<br/>
    /// The record stores `LibraDexCompositeKey` as the public key container type and preserves the ordered part descriptors so reopen can validate part names, types, and projection intent before exposing a handle.<br/>
    /// </summary>
    /// <param name="shape">The logical composite shape whose descriptor should be persisted.</param>
    /// <param name="options">The resolved index options, including duplicate-key and comparison-policy metadata.</param>
    /// <returns>A catalog metadata record ready for encoding beside the fixed directory slot.</returns>
    private static CatalogIndexMetadata CreateCompositeMetadata(LibraDexIndexShapeSpec shape, IndexOptions options)
    {
        return new CatalogIndexMetadata(
            shape.Group,
            shape.Name,
            GetStableTypeName(typeof(LibraDexCompositeKey)),
            GetStableTypeName(shape.IdentityType),
            CatalogIndexKeyFamily.Composite,
            shape.IdentityFamily,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            shape.Directions,
            shape.SortOrder,
            shape.Projections,
            shape.CompositeParts,
            0,
            0,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    private static CatalogIndexMetadata CreateBigIntMetadata<TIdentity>(
        string group,
        string name,
        int maxKeyLength,
        LibraDexBigIntKeyStorage storage,
        IndexOptions options)
    {
        LibraDexIndexProjectionKind projectionKind = storage == LibraDexBigIntKeyStorage.FixedWidth
            ? LibraDexIndexProjectionKind.BigIntFixed
            : LibraDexIndexProjectionKind.BigIntVarLen;
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(BigInteger)),
            GetStableTypeName(typeof(TIdentity)),
            CatalogIndexKeyFamily.BigInt,
            CatalogIndexIdentityFamily.Scalar,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            new[] { new LibraDexIndexProjectionSpec(projectionKind, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending) },
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            maxKeyLength,
            0,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    /// <summary>
    /// Gets the persisted physical key limit for a BigInteger/scalar index.<br/>
    /// Fixed-width storage persists LibraDex's sortable BigInt bytes directly, while variable-width storage wraps those bytes in the var-key sentinel contract and therefore needs one extra byte.<br/>
    /// </summary>
    /// <param name="maxBytes">The developer-facing maximum BigInteger magnitude byte count.<br/></param>
    /// <param name="storage">The BigInteger key storage shape.<br/></param>
    /// <returns>The physical maximum key length stored in catalog metadata.<br/></returns>
    private static int GetBigIntScalarPhysicalMaxKeyLength(int maxBytes, LibraDexBigIntKeyStorage storage)
    {
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxBytes));
        return storage == LibraDexBigIntKeyStorage.VariableWidth
            ? checked(maxBytes + 4)
            : LibraDexBigIntCodec.GetFixedEncodedLength(maxBytes);
    }

    /// <summary>
    /// Gets the developer-facing BigInteger magnitude limit from a persisted BigInteger/scalar physical key limit.<br/>
    /// Variable-width scalar indexes reserve one physical byte for the var-key sentinel in addition to LibraDex's sortable BigInt header bytes.<br/>
    /// </summary>
    /// <param name="maxKeyLength">The persisted physical maximum key length from catalog metadata.<br/></param>
    /// <param name="storage">The BigInteger key storage shape.<br/></param>
    /// <returns>The developer-facing maximum BigInteger magnitude byte count.<br/></returns>
    private static int GetBigIntScalarMaxBytes(int maxKeyLength, LibraDexBigIntKeyStorage storage)
    {
        int maxBytes = storage == LibraDexBigIntKeyStorage.VariableWidth
            ? maxKeyLength - 4
            : maxKeyLength - 3;
        LibraDexBigIntCodec.ValidateMaxBytes(maxBytes, nameof(maxKeyLength));
        return maxBytes;
    }

    private static CatalogIndexMetadata CreateBigIntVarIdentityMetadata(
        string group,
        string name,
        int maxKeyLength,
        int maxIdentityBytes,
        IndexOptions options)
    {
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(BigInteger)),
            GetStableTypeName(typeof(byte[])),
            CatalogIndexKeyFamily.BigInt,
            CatalogIndexIdentityFamily.Blob,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            new[] { new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.BigIntFixedVarIdentity, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending) },
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            maxKeyLength,
            maxIdentityBytes,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    /// <summary>
    /// Creates catalog metadata for a UInt64-key variable-identity `SV8` index.<br/>
    /// The exact projection kind describes the scalar key route while `VarIdentityMaxLength` carries the raw identity byte cap used by the physical shelf profile.<br/>
    /// </summary>
    /// <param name="group">The identity group that owns the index.</param>
    /// <param name="name">The logical index name inside the group.</param>
    /// <param name="maxIdentityBytes">The maximum raw identity byte count accepted by this index.</param>
    /// <param name="options">The resolved index options.</param>
    /// <returns>A catalog metadata record ready to persist beside the root-router slot.</returns>
    private static CatalogIndexMetadata CreateUInt64VarIdentityMetadata(
        string group,
        string name,
        int maxIdentityBytes,
        IndexOptions options)
    {
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(ulong)),
            GetStableTypeName(typeof(byte[])),
            CatalogIndexKeyFamily.Scalar,
            CatalogIndexIdentityFamily.Blob,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            new[] { new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending) },
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            0,
            maxIdentityBytes,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    /// <summary>
    /// Creates catalog metadata for a raw-byte variable-key/variable-identity `VV` index.<br/>
    /// The exact projection marks normal forward key ordering while `VarKeyMaxKeyLength` and `VarIdentityMaxLength` carry the physical tuple caps.<br/>
    /// </summary>
    /// <param name="group">The identity group that owns the index.<br/></param>
    /// <param name="name">The logical index name inside the group.<br/></param>
    /// <param name="maxKeyBytes">The maximum physical variable-key bytes, including the internal sentinel byte.<br/></param>
    /// <param name="maxIdentityBytes">The maximum raw identity byte count accepted by this index.<br/></param>
    /// <param name="options">The resolved index options.<br/></param>
    /// <returns>A catalog metadata record ready to persist beside the root-router slot.</returns>
    private static CatalogIndexMetadata CreateVarKeyVarIdentityMetadata(
        string group,
        string name,
        int maxKeyBytes,
        int maxIdentityBytes,
        IndexOptions options)
    {
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(byte[])),
            GetStableTypeName(typeof(byte[])),
            CatalogIndexKeyFamily.Blob,
            CatalogIndexIdentityFamily.Blob,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            new[] { new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.Exact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending) },
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            maxKeyBytes,
            maxIdentityBytes,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    /// <summary>
    /// Creates catalog metadata for an exact bounded variable-length blob-key/scalar-8-identity index.<br/>
    /// `VarKeyMaxKeyLength` stores the physical cap including LibraDex's sentinel byte, while the public facade reports the logical payload cap.<br/>
    /// </summary>
    /// <typeparam name="TIdentity">The scalar identity type persisted in catalog metadata.<br/></typeparam>
    /// <param name="group">The identity group that owns the index.<br/></param>
    /// <param name="name">The logical index name inside the group.<br/></param>
    /// <param name="physicalMaxKeyLength">The physical maximum key length including the sentinel byte.<br/></param>
    /// <param name="options">The resolved index options.<br/></param>
    /// <returns>A catalog metadata record for the variable-blob facade.</returns>
    private static CatalogIndexMetadata CreateVariableBlobScalar8Metadata<TIdentity>(
        string group,
        string name,
        int physicalMaxKeyLength,
        IndexOptions options)
    {
        return new CatalogIndexMetadata(
            group,
            name,
            GetStableTypeName(typeof(byte[])),
            GetStableTypeName(typeof(TIdentity)),
            CatalogIndexKeyFamily.Blob,
            CatalogIndexIdentityFamily.Scalar,
            options.Keys,
            options.StringKeys,
            options.GuidKeys,
            options.DateKeys,
            options.DateTimeKeyEncoding,
            LibraDexProjectionDirectionSet.Forward,
            LibraDexIndexSortOrder.Ascending,
            new[] { new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.VariableBlobExact, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Ascending) },
            Array.Empty<LibraDexCompositeKeyPartSpec>(),
            physicalMaxKeyLength,
            0,
            -1,
            -1,
            -1,
            -1,
            string.Empty,
            string.Empty,
            options.StringComparisonPolicy?.Kind ?? LibraDexStringComparisonPolicyKind.Invariant,
            options.StringComparisonPolicy?.CompareOptions ?? System.Globalization.CompareOptions.None,
            options.StringComparisonPolicy?.CultureName ?? string.Empty,
            options.StringComparisonPolicy?.CustomComparerTypeName ?? string.Empty,
            true,
            IdentityKeyMultiplicity: options.IdentityKeyMultiplicity);
    }

    private static LibraDexIndexShapeSpec? CreateLogicalShape(CatalogIndexMetadata metadata)
    {
        if (!metadata.HasShapeMetadata)
        {
            return null;
        }

        Type keyType = ResolvePersistedType(metadata.KeyTypeName);
        Type identityType = ResolvePersistedType(metadata.IdentityTypeName);
        return new LibraDexIndexShapeSpec(
            metadata.Group,
            metadata.IndexName,
            keyType,
            identityType,
            metadata.KeyFamily,
            metadata.IdentityFamily,
            metadata.KeyContract,
            metadata.StringKeys,
            metadata.GuidKeys,
            metadata.DateKeys,
            metadata.DateTimeKeyEncoding,
            metadata.Directions,
            metadata.SortOrder,
            metadata.Projections,
            metadata.CompositeParts,
            metadata.IdentityKeyMultiplicity);
    }

    private static string GetStableTypeName(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    private static DataKernelOptions CreateDefaultDataKernelOptions()
    {
        return new DataKernelOptions(
            AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
    }

    private static SuperblockDeveloperMetadata CreateCatalogDeveloperMetadata()
    {
        return new SuperblockDeveloperMetadata(
            DevIdentity: "LibraDex",
            DevCustomText: "catalog",
            DevGuid: Guid.NewGuid(),
            DevDate1UtcTicks: DateTimeOffset.UtcNow.UtcDateTime.Ticks,
            DevDate2UtcTicks: 0,
            DevNumber: 0);
    }
}

/// <summary>
/// Maintains one catalog-session identity-to-key acceleration map for a persisted `SingleKeyPerIdentity` index.<br/>
/// The forward LibraDex index remains authoritative; this map is built once on first identity-side use and is updated by immediate insert, delete, and rekey paths.<br/>
/// </summary>
/// <typeparam name="TKey">The public index key type.<br/></typeparam>
/// <typeparam name="TIdentity">The public index identity type.<br/></typeparam>
internal sealed class CatalogSingleKeyIdentityMap<TKey, TIdentity>
{
    private readonly object sync = new();
    private readonly Dictionary<LibraDexIdentityMapKey<TIdentity>, TKey> keys = new();
    private readonly Dictionary<LibraDexIdentityMapKey<TIdentity>, object> stagedOwners = new();
    private readonly Dictionary<object, HashSet<LibraDexIdentityMapKey<TIdentity>>> stagedKeysByOwner =
        new(ReferenceEqualityComparer.Instance);
    private bool initialized;

    /// <summary>
    /// Visits selected committed keys only when the inverse map is already complete.<br/>
    /// Readiness and traversal share the map lock, preventing invalidation halfway through reduction.<br/>
    /// Never initializes a map. The trusted internal visitor must not mutate the catalog or escape the callback.<br/>
    /// </summary>
    /// <param name="identities">Distinct requested identities, enumerated once in caller order.<br/></param>
    /// <param name="visitor">Typed synchronous reducer for keys that exist.<br/></param>
    /// <param name="matched">Number of existing keys visited.<br/></param>
    /// <returns>False without calling the visitor when the map needs initialization.<br/></returns>
    internal bool TryVisitReadyKeys(IEnumerable<TIdentity> identities, Action<TKey> visitor, out long matched)
    {
        lock (sync)
        {
            matched = 0;
            if (!initialized) return false;
            foreach (TIdentity identity in identities)
            {
                if (!keys.TryGetValue(new LibraDexIdentityMapKey<TIdentity>(identity), out TKey? key)) continue;
                visitor(key);
                matched++;
            }
            return true;
        }
    }

    internal bool TryGet(LibraDexIndex<TKey, TIdentity> index, TIdentity identity, out TKey key)
    {
        lock (sync)
        {
            EnsureInitialized(index);
            return keys.TryGetValue(new LibraDexIdentityMapKey<TIdentity>(identity), out key!);
        }
    }

    internal bool HasDifferentKey(
        LibraDexIndex<TKey, TIdentity> index,
        TIdentity identity,
        TKey allowedKey,
        bool hasAlternateAllowedKey,
        TKey alternateAllowedKey)
    {
        lock (sync)
        {
            EnsureInitialized(index);
            LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
            if (stagedOwners.ContainsKey(mapKey))
                return true;
            if (!keys.TryGetValue(mapKey, out TKey? current))
                return false;
            if (LibraDexTupleEqualityComparer<TKey>.Instance.Equals(current, allowedKey))
                return false;
            return !hasAlternateAllowedKey ||
                !LibraDexTupleEqualityComparer<TKey>.Instance.Equals(current, alternateAllowedKey);
        }
    }

    internal void RecordInsert(LibraDexIndex<TKey, TIdentity> index, TIdentity identity, TKey key)
    {
        lock (sync)
        {
            EnsureInitialized(index);
            keys[new LibraDexIdentityMapKey<TIdentity>(identity)] = key;
        }
    }

    internal void RecordDelete(LibraDexIndex<TKey, TIdentity> index, TIdentity identity, TKey key)
    {
        lock (sync)
        {
            EnsureInitialized(index);
            LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
            if (keys.TryGetValue(mapKey, out TKey? current) &&
                LibraDexTupleEqualityComparer<TKey>.Instance.Equals(current, key))
            {
                keys.Remove(mapKey);
            }
        }
    }

    internal void Invalidate()
    {
        lock (sync)
        {
            initialized = false;
            keys.Clear();
        }
    }

    internal void AddLoaded(TIdentity identity, TKey key)
    {
        LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
        if (keys.TryGetValue(mapKey, out TKey? existing) &&
            !LibraDexTupleEqualityComparer<TKey>.Instance.Equals(existing, key))
        {
            throw new InvalidDataException("A SingleKeyPerIdentity index contains more than one key for the same identity.");
        }

        keys[mapKey] = key;
    }

    private void EnsureInitialized(LibraDexIndex<TKey, TIdentity> index)
    {
        if (initialized)
            return;

        keys.Clear();
        index.PopulateSingleKeyIdentityMap(this);
        initialized = true;
    }

    /// <summary>
    /// Atomically validates committed identity ownership and reserves one identity for a staged writer owner.<br/>
    /// The reservation is intentionally identity-wide rather than key-wide: two unpublished writers may not safely decide that the same identity belongs to either the same or a different key until one publication outcome becomes authoritative.<br/>
    /// Owners may revisit their own reservation so a batch can retry shelf ownership conflicts without losing its contract, while any other owner or immediate writer observes the identity as unavailable.<br/>
    /// </summary>
    /// <param name="index">The authoritative forward index used to initialize committed identity state when necessary.<br/></param>
    /// <param name="owner">The stable writer-facade token that retains the reservation through publication or abort.<br/></param>
    /// <param name="identity">The identity being reserved.<br/></param>
    /// <param name="allowedKey">The candidate key allowed by the staged operation.<br/></param>
    /// <param name="hasAlternateAllowedKey">Whether rekey semantics also permit one known old key.<br/></param>
    /// <param name="alternateAllowedKey">The optional old key allowed until replacement publication completes.<br/></param>
    /// <returns><see langword="true"/> when committed and staged ownership permit this owner to retain the identity.<br/></returns>
    internal bool TryReserve(
        LibraDexIndex<TKey, TIdentity> index,
        object owner,
        TIdentity identity,
        TKey allowedKey,
        bool hasAlternateAllowedKey,
        TKey alternateAllowedKey)
    {
        lock (sync)
        {
            EnsureInitialized(index);
            LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
            if (stagedOwners.TryGetValue(mapKey, out object? currentOwner))
                return ReferenceEquals(currentOwner, owner);

            if (keys.TryGetValue(mapKey, out TKey? currentKey) &&
                !LibraDexTupleEqualityComparer<TKey>.Instance.Equals(currentKey, allowedKey) &&
                (!hasAlternateAllowedKey ||
                    !LibraDexTupleEqualityComparer<TKey>.Instance.Equals(currentKey, alternateAllowedKey)))
            {
                return false;
            }

            stagedOwners.Add(mapKey, owner);
            if (!stagedKeysByOwner.TryGetValue(owner, out HashSet<LibraDexIdentityMapKey<TIdentity>>? ownerKeys))
            {
                ownerKeys = [];
                stagedKeysByOwner.Add(owner, ownerKeys);
            }

            ownerKeys.Add(mapKey);
            return true;
        }
    }

    /// <summary>
    /// Releases one identity reservation when its proposed physical insert was rejected or failed before publication.<br/>
    /// Ownership is checked by reference so one writer cannot accidentally release a reservation retained by a later or competing writer.<br/>
    /// </summary>
    /// <param name="owner">The writer-facade token that acquired the reservation.<br/></param>
    /// <param name="identity">The identity whose unused reservation should be released.<br/></param>
    internal void Release(object owner, TIdentity identity)
    {
        lock (sync)
        {
            LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
            if (!stagedOwners.TryGetValue(mapKey, out object? currentOwner) ||
                !ReferenceEquals(currentOwner, owner))
            {
                return;
            }

            stagedOwners.Remove(mapKey);
            if (stagedKeysByOwner.TryGetValue(owner, out HashSet<LibraDexIdentityMapKey<TIdentity>>? ownerKeys))
            {
                ownerKeys.Remove(mapKey);
                if (ownerKeys.Count == 0)
                    stagedKeysByOwner.Remove(owner);
            }
        }
    }

    /// <summary>
    /// Releases every identity reservation retained by one completed or aborted staged writer facade.<br/>
    /// The reverse owner map keeps batch cleanup proportional to that owner's identities rather than requiring a scan of all active reservations.<br/>
    /// Committed map invalidation remains a separate publication-boundary responsibility because an aborted facade may still have used immediate topology fallbacks before its final unpublished context was discarded.<br/>
    /// </summary>
    /// <param name="owner">The writer-facade token whose reservation set is complete.<br/></param>
    internal void ReleaseAll(object owner)
    {
        lock (sync)
        {
            if (!stagedKeysByOwner.Remove(owner, out HashSet<LibraDexIdentityMapKey<TIdentity>>? ownerKeys))
                return;

            foreach (LibraDexIdentityMapKey<TIdentity> mapKey in ownerKeys)
            {
                if (stagedOwners.TryGetValue(mapKey, out object? currentOwner) &&
                    ReferenceEquals(currentOwner, owner))
                {
                    stagedOwners.Remove(mapKey);
                }
            }
        }
    }
}

/// <summary>
/// Applies LibraDex tuple-component equality to typed dictionary keys without boxing scalar values.<br/>
/// Byte arrays use content equality and hashing; other supported scalar types use their default equality comparer.<br/>
/// </summary>
/// <typeparam name="TValue">The tuple component type.<br/></typeparam>
internal sealed class LibraDexTupleEqualityComparer<TValue> : IEqualityComparer<TValue>
{
    internal static readonly LibraDexTupleEqualityComparer<TValue> Instance = new();

    public bool Equals(TValue? left, TValue? right)
    {
        if (typeof(TValue).IsValueType)
            return EqualityComparer<TValue>.Default.Equals(left, right);
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        return EqualityComparer<TValue>.Default.Equals(left, right);
    }

    public int GetHashCode(TValue value)
    {
        if (typeof(TValue).IsValueType)
            return EqualityComparer<TValue>.Default.GetHashCode(value!);
        if (value is null)
            return 0;
        if (value is byte[] bytes)
        {
            HashCode hash = new();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        return EqualityComparer<TValue>.Default.GetHashCode(value);
    }
}

/// <summary>
/// Wraps a possibly nullable LibraDex identity in a non-null dictionary key without allocating a reference wrapper.<br/>
/// Equality follows LibraDex tuple semantics, including content comparison for byte-array identities.<br/>
/// </summary>
/// <typeparam name="TIdentity">The public index identity type.<br/></typeparam>
internal readonly struct LibraDexIdentityMapKey<TIdentity> : IEquatable<LibraDexIdentityMapKey<TIdentity>>
{
    private readonly TIdentity value;

    /// <summary>
    /// Initializes a typed dictionary key for one LibraDex identity.<br/>
    /// </summary>
    /// <param name="value">The identity value to wrap.<br/></param>
    internal LibraDexIdentityMapKey(TIdentity value)
    {
        this.value = value;
    }

    /// <summary>
    /// Compares two wrapped identities using LibraDex tuple equality.<br/>
    /// </summary>
    /// <param name="other">The wrapped identity to compare.<br/></param>
    /// <returns><see langword="true"/> when the identities are tuple-equal; otherwise <see langword="false"/>.<br/></returns>
    public bool Equals(LibraDexIdentityMapKey<TIdentity> other)
    {
        return LibraDexTupleEqualityComparer<TIdentity>.Instance.Equals(value, other.value);
    }

    /// <summary>
    /// Compares this wrapped identity with an object value.<br/>
    /// </summary>
    /// <param name="obj">The candidate wrapped identity.<br/></param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> contains a tuple-equal identity; otherwise <see langword="false"/>.<br/></returns>
    public override bool Equals(object? obj)
    {
        return obj is LibraDexIdentityMapKey<TIdentity> other && Equals(other);
    }

    /// <summary>
    /// Produces the LibraDex tuple hash for the wrapped identity.<br/>
    /// </summary>
    /// <returns>The tuple-compatible hash code.<br/></returns>
    public override int GetHashCode()
    {
        return LibraDexTupleEqualityComparer<TIdentity>.Instance.GetHashCode(value);
    }
}
