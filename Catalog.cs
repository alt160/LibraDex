using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Represents a public grouping of LibraDex indexes over one memory-backed or file-backed catalog session.<br/>
/// The catalog is the first public construction concept: callers open or create a catalog, then create/open individual indexes beneath `catalog.Indexes`.<br/>
/// </summary>
public sealed class Catalog : IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly Dictionary<string, CatalogIdentityGroupBatchManager> groupBatchManagers = new(StringComparer.Ordinal);
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
    public DataKernelBackingKind BackingKind { get; }

    /// <summary>
    /// Gets the options supplied when this catalog was opened or created.<br/>
    /// `CatalogOptions` is intentionally empty in this slice, but the property gives future catalog-level policy a stable home.<br/>
    /// </summary>
    public CatalogOptions Options { get; }

    /// <summary>
    /// Gets the grouped index factory surface for this catalog.<br/>
    /// The nested shape keeps IntelliSense exploratory while preserving short terminal verbs such as `Create`, `Open`, and `CreateOrOpen`.<br/>
    /// </summary>
    public CatalogIndexFactories Indexes { get; }

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
            DataKernelTelemetryOptions.EnabledOptions);
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
            DataKernelTelemetryOptions.EnabledOptions);
        return new Catalog(session, requiredPath, DataKernelBackingKind.File, effectiveOptions);
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
            DataKernelTelemetryOptions.EnabledOptions);
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
        LibraDexIndexShapeSpec? logicalShape = null)
    {
        ThrowIfDisposed();
        if (TryFindSlot(session, slotIndex, out _))
        {
            throw new InvalidOperationException("The requested LibraDex index slot is already active.");
        }

        LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
        CatalogIndexMetadata metadata = CreateGenericMetadata<TKey, TIdentity>(group, name, options, keyFamily, identityFamily, logicalShape);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateGenericSlot(slotIndex, name), metadata);
        return new LibraDexIndex<TKey, TIdentity>(
            session,
            slotIndex,
            name,
            root.Offset,
            shape,
            ownsSession: false,
            options.Keys,
            this,
            group,
            keyFamily,
            identityFamily,
            logicalShape);
    }

    internal LibraDexIndex<TKey, TIdentity> OpenGenericIndex<TKey, TIdentity>(
        int slotIndex,
        IndexOptions options,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth)
    {
        ThrowIfDisposed();
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
        if (session.TryReadCatalogIndexMetadata(slot, out CatalogIndexMetadata metadata))
        {
            name = string.IsNullOrWhiteSpace(metadata.IndexName) ? slot.Name : metadata.IndexName;
            group = metadata.Group;
            keyFamily = metadata.KeyFamily;
            identityFamily = metadata.IdentityFamily;
            keyContract = metadata.KeyContract;
            logicalShape = CreateLogicalShape(metadata);
        }

        return new LibraDexIndex<TKey, TIdentity>(
            session,
            slotIndex,
            name,
            slot.RootRouterOffset,
            shape,
            ownsSession: false,
            keyContract,
            this,
            group,
            keyFamily,
            identityFamily,
            logicalShape);
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
        Type keyType = ResolvePersistedType(info.KeyTypeName);
        Type identityType = ResolvePersistedType(info.IdentityTypeName);
        if (keyType == typeof(byte[]) || identityType == typeof(byte[]))
        {
            throw new NotSupportedException("Metadata-driven byte[] index opening requires persisted scalar-width metadata.");
        }

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
                    new IndexOptions { Keys = info.KeyContract },
                    null,
                    null
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
                    shape
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
        CatalogIndexKeyFamily keyFamily,
        CatalogIndexIdentityFamily identityFamily,
        LibraDexIndexShapeSpec? logicalShape)
    {
        LibraDexProjectionDirectionSet directions = logicalShape?.Directions ?? LibraDexProjectionDirectionSet.Forward;
        LibraDexIndexSortOrder sortOrder = logicalShape?.SortOrder ?? LibraDexIndexSortOrder.Ascending;
        IReadOnlyList<LibraDexIndexProjectionSpec> projections = logicalShape?.Projections ?? Array.Empty<LibraDexIndexProjectionSpec>();
        IReadOnlyList<LibraDexCompositeKeyPartSpec> compositeParts = logicalShape?.CompositeParts ?? Array.Empty<LibraDexCompositeKeyPartSpec>();
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
            directions,
            sortOrder,
            projections,
            compositeParts,
            logicalShape is not null);
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
            metadata.Directions,
            metadata.SortOrder,
            metadata.Projections,
            metadata.CompositeParts);
    }

    private static string GetStableTypeName(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    private static DataKernelOptions CreateDefaultDataKernelOptions()
    {
        return new DataKernelOptions(
            AppendBufferSize: 1024 * 1024,
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
