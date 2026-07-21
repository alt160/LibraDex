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
        Tools = new CatalogTools(this);
        Compatibility = new CatalogCompatibility(this);
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
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = null)
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
            options.Keys,
            options.IdentityKeyMultiplicity,
            this,
            group,
            keyFamily,
            identityFamily,
            logicalShape,
            exactReversedProjection,
            options.DateTimeKeyEncoding,
            options.ReadCacheMaxBytes);
    }

    internal LibraDexIndex<TKey, TIdentity> OpenGenericIndex<TKey, TIdentity>(
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth)
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
                identityWidth);
        }

        return new LibraDexIndex<TKey, TIdentity>(
            session,
            slotIndex,
            name,
            slot.RootRouterOffset,
            shape,
            ownsSession: false,
            keyContract,
            persistedMetadata?.IdentityKeyMultiplicity ?? options.IdentityKeyMultiplicity,
            this,
            group,
            keyFamily,
            identityFamily,
            logicalShape,
            exactReversedProjection,
            persistedMetadata?.DateTimeKeyEncoding ?? options.DateTimeKeyEncoding,
            options.ReadCacheMaxBytes);
    }

    internal LibraDexIndex<TKey, TIdentity> CreateOrOpenGenericIndex<TKey, TIdentity>(
        string name,
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth)
    {
        ThrowIfDisposed();
        return TryFindSlot(session, slotIndex, out _)
            ? OpenGenericIndex<TKey, TIdentity>(slotIndex, options, keyWidth, identityWidth)
            : CreateGenericIndex<TKey, TIdentity>(name, slotIndex, options, keyWidth, identityWidth);
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
                    identityWidth
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

    internal IIndex CreateIndex(LibraDexIndexShapeSpec shape, int slotIndex, IndexOptions? options = null)
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
                    null
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
        return new LibraDexRoutedCompositeIndex(shape, session, slotIndex, rootOffset);
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

        LibraDexIndexShapeSpec shape = info.CreateShape();
        if (!TryFindSlot(session, info.SlotIndex, out IndexDirectorySlotSnapshot slot))
        {
            return new LibraDexRoutedCompositeIndex(shape, session, info.SlotIndex);
        }

        if (session.TryReadCompositeNodePage(slot.RootRouterOffset, out _))
        {
            return LibraDexRoutedCompositeIndex.OpenFromNodePages(shape, session, info.SlotIndex, slot.RootRouterOffset, info.ItemCount);
        }

        if (session.TryReadCompositeSnapshot(slot, out byte[] snapshot))
        {
            IReadOnlyList<LibraDexCompositeEntry> entries = LibraDexCompositeSnapshotCodec.Decode(shape, snapshot);
            return new LibraDexRoutedCompositeIndex(shape, session, info.SlotIndex, entries);
        }

        return new LibraDexRoutedCompositeIndex(shape, session, info.SlotIndex);
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
                return new LibraDexBigIntScalar8Index<TIdentity>(group, name, inner, maxBytes, storage, options.Keys);
            }

            if (identityWidth == LibraDexScalarWidth.Bytes16)
            {
                FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(maxKeyLength);
                (FixedNScalar16IndexHandle handle, _) = session.CreateFixedNScalar16RootRouterIndex(
                    CreateGenericSlot(slotIndex, name),
                    metadata,
                    profile);
                FixedNScalar16Index inner = new(session, handle, slotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(group, name, inner, maxBytes, storage, options.Keys);
            }

            throw new NotSupportedException("Fixed BigInt indexes require scalar-8 or scalar-16 identity types.");
        }
        else
        {
            VarKeyScalar8Index inner = CreateVarKeyScalar8Index(name, slotIndex, maxKeyLength, metadata);
            return new LibraDexBigIntScalar8Index<TIdentity>(group, name, inner, maxBytes, storage, options.Keys);
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
                return new LibraDexBigIntScalar8Index<TIdentity>(info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
            }

            if (identityWidth == LibraDexScalarWidth.Bytes16)
            {
                FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(info.VarKeyMaxKeyLength);
                FixedNScalar16IndexHandle handle = session.OpenFixedNScalar16ShelfIndex(info.SlotIndex, profile);
                FixedNScalar16Index inner = new(session, handle, info.SlotIndex);
                return new LibraDexBigIntScalar8Index<TIdentity>(info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
            }

            throw new NotSupportedException("Fixed BigInt indexes require scalar-8 or scalar-16 identity types.");
        }
        else
        {
            VarKeyScalar8Index inner = OpenVarKeyScalar8Index(info.SlotIndex, info.VarKeyMaxKeyLength);
            return new LibraDexBigIntScalar8Index<TIdentity>(info.Group, info.Name, inner, maxBytes, expectedStorage, info.KeyContract);
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
        return new LibraDexBigIntVarIdentityIndex(group, name, inner, maxBytes, maxIdentityBytes, options.Keys);
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
        return new LibraDexBigIntVarIdentityIndex(info.Group, info.Name, inner, maxBytes, info.VarIdentityMaxLength, info.KeyContract);
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
        return new LibraDexUInt64VarIdentityIndex(group, name, inner, options.Keys);
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
        return new LibraDexUInt64VarIdentityIndex(info.Group, info.Name, inner, info.KeyContract);
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
    /// Validates every existing rich-metadata index against the catalog-level required identity type.<br/>
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
        for (int i = 0; i < activeSlots.Length; i++)
        {
            IndexDirectorySlotSnapshot slot = activeSlots[i];
            if (!session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
            {
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
