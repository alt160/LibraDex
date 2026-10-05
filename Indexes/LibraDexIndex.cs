using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Provides the first generic public wrapper over routed fixed-scalar LibraDex indexes.<br/>
/// The wrapper owns a concrete physical shape selected from `TKey`, `TIdentity`, and any required `byte[]` scalar-width options while keeping the call site in familiar .NET generic form.<br/>
/// Index handles share their owning catalog session, so overlapping writes through the same active session must be serialized by the caller or by an outer owner such as Abraxas.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type.</typeparam>
/// <typeparam name="TIdentity">The public identity type.</typeparam>
public sealed class LibraDexIndex<TKey, TIdentity> : IIndex, IFixedBinaryKeyIndex<TIdentity>, IIdentityPrimitiveExecutor, IIdentityPrimitiveExecutor<TIdentity>, IIdentityPrimitiveMutator, IIdentityPrimitiveTupleExecutor, IIdentityPrimitiveTupleStreamer, IIdentityExactTupleMutator, ILibraDexIdentityInverseLookup, ILibraDexNativeSortedBuild, IDisposable
{
    private readonly LibraDexFileSession session;
    private readonly LibraDexGenericScalarShape shape;
    private readonly bool ownsSession;
    private readonly IndexKeys keyContract;
    private readonly IdentityKeyMultiplicity identityKeyMultiplicity;
    private readonly Catalog catalog;
    private readonly bool ownsCatalog;
    private readonly LibraDexIndex<TKey, TIdentity>? exactReversedProjection;
    private readonly DateTimeKeyEncoding dateTimeKeyEncoding;
    private readonly LibraDexIndexSortOrder sortOrder;
    private readonly object internalScalar8Scalar8IndexSync = new();
    private readonly object internalQueuedWriterSync = new();
    private Scalar8Scalar8Index? internalScalar8Scalar8Index;
    private LibraDexQueuedWriter<TKey, TIdentity>? internalQueuedWriter;
    private bool disposed;
    private static readonly Scalar8Scalar8Profile MemoryScalar8Scalar8Profile = CreateMemoryScalar8Scalar8Profile();
    private static readonly Scalar16Scalar8Profile MemoryScalar16Scalar8Profile = CreateMemoryScalar16Scalar8Profile();
    private static readonly Scalar8Scalar16Profile MemoryScalar8Scalar16Profile = CreateMemoryScalar8Scalar16Profile();
    private static readonly Scalar16Scalar16Profile MemoryScalar16Scalar16Profile = CreateMemoryScalar16Scalar16Profile();
    private static readonly Fixed32Scalar8Profile MemoryFixed32Scalar8Profile = CreateMemoryFixed32Scalar8Profile();
    private static readonly Fixed32Scalar16Profile MemoryFixed32Scalar16Profile = CreateMemoryFixed32Scalar16Profile();

    internal LibraDexIndex(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        long rootRouterOffset,
        LibraDexGenericScalarShape shape,
        bool ownsSession,
        Catalog catalog,
        IndexKeys keyContract = IndexKeys.NonUnique,
        IdentityKeyMultiplicity identityKeyMultiplicity = IdentityKeyMultiplicity.MultipleKeysPerIdentity,
        string group = "",
        CatalogIndexKeyFamily keyFamily = CatalogIndexKeyFamily.Unknown,
        CatalogIndexIdentityFamily identityFamily = CatalogIndexIdentityFamily.Unknown,
        LibraDexIndexShapeSpec? logicalShape = null,
        LibraDexIndex<TKey, TIdentity>? exactReversedProjection = null,
        DateTimeKeyEncoding dateTimeKeyEncoding = DateTimeKeyEncoding.CalendarSdt,
        LibraDexIndexSortOrder sortOrder = LibraDexIndexSortOrder.Ascending,
        long readCacheMaxBytes = 0,
        IdentityLookupMode identityLookupMode = IdentityLookupMode.Explicit,
        bool ownsCatalog = false)
    {
        if (readCacheMaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readCacheMaxBytes), readCacheMaxBytes, "The per-index read-cache limit cannot be negative.");
        }

        this.session = session;
        this.shape = shape;
        this.ownsSession = ownsSession;
        this.keyContract = keyContract;
        this.identityKeyMultiplicity = identityKeyMultiplicity;
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog), "A LibraDex index must belong to an opened catalog.");
        this.ownsCatalog = ownsCatalog;
        Group = group;
        KeyFamily = keyFamily;
        IdentityFamily = identityFamily;
        LogicalShape = logicalShape;
        this.exactReversedProjection = exactReversedProjection;
        this.dateTimeKeyEncoding = logicalShape?.DateTimeKeyEncoding ?? dateTimeKeyEncoding;
        this.sortOrder = sortOrder;
        LibraDexIdentityLookup.ValidateMode(identityLookupMode);
        SlotIndex = slotIndex;
        Name = name;
        RootRouterOffset = rootRouterOffset;
        ReadCacheMaxBytes = readCacheMaxBytes;
        if (shape == LibraDexGenericScalarShape.FS328)
        {
            session.ConfigureFixed32Scalar8ReadCache(rootRouterOffset, readCacheMaxBytes);
        }
        else if (shape == LibraDexGenericScalarShape.FS3216)
        {
            session.ConfigureFixed32Scalar16ReadCache(rootRouterOffset, readCacheMaxBytes);
        }

        BatchManager = new IndexBatchManager<TKey, TIdentity>(this);
        Stats = new LibraDexIndexStats<TKey, TIdentity>(this);
        Maintenance = new LibraDexIndexMaintenance<TKey, TIdentity>(this);
        if (!string.IsNullOrWhiteSpace(Group))
            catalog.ConfigureIdentityLookup(this, identityLookupMode);
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
    /// Gets the persisted natural key traversal order for this index.<br/>
    /// </summary>
    public LibraDexIndexSortOrder SortOrder => sortOrder;

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
    /// Gets the session-local immutable-shelf read-cache ceiling for this index.<br/>
    /// Zero means no limit; positive values currently apply to the `FS32-8` and `FS32-16` physical shelf families.<br/>
    /// </summary>
    public long ReadCacheMaxBytes { get; }

    /// <summary>
    /// Gets the DataKernel backing kind used by the owning session.<br/>
    /// File-backed indexes can be reopened after disposal, while memory-backed indexes are temporary and process-local.<br/>
    /// </summary>
    internal DataKernelBackingKind BackingKind => session.BackingKind;

    /// <summary>
    /// Gets the runtime `SS8-8` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the 32 KiB SSD-oriented profile, while memory-backed catalogs use the process-level memory profile selected for heap/slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `SS8-8` shelf profile for reads, writes, and delete range plans.</returns>
    internal Scalar8Scalar8Profile GetScalar8Scalar8Profile()
    {
        Scalar8Scalar8Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryScalar8Scalar8Profile
            : Scalar8Scalar8Profile.Default32KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Creates the process-level memory `SS8-8` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_SS88_SHELF_KB` is intentionally read once per closed generic index type so sweep runs can vary the profile without adding per-insert environment lookup cost.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `SS8-8` shelf profile.</returns>
    private static Scalar8Scalar8Profile CreateMemoryScalar8Scalar8Profile()
    {
        string? value = Environment.GetEnvironmentVariable("LIBRADEX_MEMORY_SS88_SHELF_KB");
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib))
        {
            return kib switch
            {
                4 => Scalar8Scalar8Profile.Default4KiB,
                8 => Scalar8Scalar8Profile.Default8KiB,
                16 => Scalar8Scalar8Profile.Default16KiB,
                24 => Scalar8Scalar8Profile.Default24KiB,
                32 => Scalar8Scalar8Profile.Default32KiB,
                48 => Scalar8Scalar8Profile.Default48KiB,
                64 => Scalar8Scalar8Profile.Default64KiB,
                _ => Scalar8Scalar8Profile.Default8KiB
            };
        }

        return Scalar8Scalar8Profile.Default8KiB;
    }

    /// <summary>
    /// Gets the runtime `SS16-8` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the disk-oriented 32 KiB profile, while memory-backed catalogs use the process-level memory profile selected for retained-slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `SS16-8` shelf profile for reads, writes, and range plans.</returns>
    internal Scalar16Scalar8Profile GetScalar16Scalar8Profile()
    {
        Scalar16Scalar8Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryScalar16Scalar8Profile
            : Scalar16Scalar8Profile.Default32KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Gets the runtime `SS8-16` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the disk-oriented 32 KiB profile, while memory-backed catalogs use the process-level memory profile selected for retained-slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `SS8-16` shelf profile for reads, writes, and range plans.</returns>
    internal Scalar8Scalar16Profile GetScalar8Scalar16Profile()
    {
        Scalar8Scalar16Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryScalar8Scalar16Profile
            : Scalar8Scalar16Profile.Default32KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Gets the runtime `SS16-16` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the read-leaning balanced 24 KiB profile, while memory-backed catalogs use the process-level memory profile selected for retained-slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `SS16-16` shelf profile for reads, writes, and range plans.</returns>
    internal Scalar16Scalar16Profile GetScalar16Scalar16Profile()
    {
        Scalar16Scalar16Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryScalar16Scalar16Profile
            : Scalar16Scalar16Profile.Default24KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Gets the runtime `FS32-8` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the tuned 40 KiB wide-key profile, while memory-backed catalogs use the process-level memory profile selected for retained-slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `FS32-8` shelf profile for reads, writes, and range plans.</returns>
    internal Fixed32Scalar8Profile GetFixed32Scalar8Profile()
    {
        Fixed32Scalar8Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryFixed32Scalar8Profile
            : Fixed32Scalar8Profile.Default40KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Gets the runtime `FS32-16` shelf profile for this index backing kind.<br/>
    /// File-backed catalogs keep the tuned 40 KiB wide-key profile, while memory-backed catalogs use the process-level memory profile selected for retained-slack tuning.<br/>
    /// </summary>
    /// <returns>The runtime `FS32-16` shelf profile for reads, writes, and range plans.</returns>
    internal Fixed32Scalar16Profile GetFixed32Scalar16Profile()
    {
        Fixed32Scalar16Profile profile = BackingKind == DataKernelBackingKind.Memory
            ? MemoryFixed32Scalar16Profile
            : Fixed32Scalar16Profile.Default40KiB;
        return profile with { Descending = sortOrder == LibraDexIndexSortOrder.Descending };
    }

    /// <summary>
    /// Creates the process-level memory `SS16-8` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_SS168_SHELF_KB` is read once per closed generic index type so sweeps avoid per-insert environment lookups.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `SS16-8` shelf profile.</returns>
    private static Scalar16Scalar8Profile CreateMemoryScalar16Scalar8Profile()
    {
        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_SS168_SHELF_KB", 8) switch
        {
            4 => Scalar16Scalar8Profile.Default4KiB,
            8 => Scalar16Scalar8Profile.Default8KiB,
            32 => Scalar16Scalar8Profile.Default32KiB,
            _ => Scalar16Scalar8Profile.Default8KiB
        };
    }

    /// <summary>
    /// Creates the process-level memory `SS8-16` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_SS816_SHELF_KB` is read once per closed generic index type so sweeps avoid per-insert environment lookups.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `SS8-16` shelf profile.</returns>
    private static Scalar8Scalar16Profile CreateMemoryScalar8Scalar16Profile()
    {
        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_SS816_SHELF_KB", 8) switch
        {
            4 => Scalar8Scalar16Profile.Default4KiB,
            8 => Scalar8Scalar16Profile.Default8KiB,
            16 => Scalar8Scalar16Profile.Default16KiB,
            24 => Scalar8Scalar16Profile.Default24KiB,
            32 => Scalar8Scalar16Profile.Default32KiB,
            48 => Scalar8Scalar16Profile.Default48KiB,
            64 => Scalar8Scalar16Profile.Default64KiB,
            _ => Scalar8Scalar16Profile.Default8KiB
        };
    }

    /// <summary>
    /// Creates the process-level memory `SS16-16` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_SS1616_SHELF_KB` is read once per closed generic index type so sweeps avoid per-insert environment lookups.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `SS16-16` shelf profile.</returns>
    private static Scalar16Scalar16Profile CreateMemoryScalar16Scalar16Profile()
    {
        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_SS1616_SHELF_KB", 8) switch
        {
            4 => Scalar16Scalar16Profile.Default4KiB,
            8 => Scalar16Scalar16Profile.Default8KiB,
            24 => Scalar16Scalar16Profile.Default24KiB,
            32 => Scalar16Scalar16Profile.Default32KiB,
            _ => Scalar16Scalar16Profile.Default8KiB
        };
    }

    /// <summary>
    /// Creates the process-level memory `FS32-8` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_FS328_SHELF_KB` is read once per closed generic index type so sweeps avoid per-insert environment lookups.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `FS32-8` shelf profile.</returns>
    private static Fixed32Scalar8Profile CreateMemoryFixed32Scalar8Profile()
    {
        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_FS328_SHELF_KB", 8) switch
        {
            4 => Fixed32Scalar8Profile.Default4KiB,
            8 => Fixed32Scalar8Profile.Default8KiB,
            32 => Fixed32Scalar8Profile.Default32KiB,
            40 => Fixed32Scalar8Profile.Default40KiB,
            64 => Fixed32Scalar8Profile.Default64KiB,
            _ => Fixed32Scalar8Profile.Default8KiB
        };
    }

    /// <summary>
    /// Creates the process-level memory `FS32-16` shelf profile.<br/>
    /// `LIBRADEX_MEMORY_FS3216_SHELF_KB` is read once per closed generic index type so sweeps avoid per-insert environment lookups.<br/>
    /// </summary>
    /// <returns>The selected memory-backed `FS32-16` shelf profile.</returns>
    private static Fixed32Scalar16Profile CreateMemoryFixed32Scalar16Profile()
    {
        return ParseMemoryShelfKiB("LIBRADEX_MEMORY_FS3216_SHELF_KB", 8) switch
        {
            4 => Fixed32Scalar16Profile.Default4KiB,
            8 => Fixed32Scalar16Profile.Default8KiB,
            32 => Fixed32Scalar16Profile.Default32KiB,
            40 => Fixed32Scalar16Profile.Default40KiB,
            64 => Fixed32Scalar16Profile.Default64KiB,
            _ => Fixed32Scalar16Profile.Default8KiB
        };
    }

    /// <summary>
    /// Parses one memory shelf-size environment override in KiB.<br/>
    /// Shape-specific overrides stay process-level tuning knobs and intentionally avoid hot-path reads; invalid values fall back to the supplied default.<br/>
    /// </summary>
    /// <param name="environmentVariableName">The environment variable that carries the KiB value.</param>
    /// <param name="defaultKiB">The KiB value to use when the environment variable is absent or invalid.</param>
    /// <returns>The parsed KiB value, or <paramref name="defaultKiB"/>.</returns>
    private static int ParseMemoryShelfKiB(string environmentVariableName, int defaultKiB)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kib)
            ? kib
            : defaultKiB;
    }

    /// <summary>
    /// Gets the index-wide key uniqueness contract selected when the index was opened or created through the public facade.<br/>
    /// `Unique` rejects duplicate keys at insert time; `NonUnique` allows multiple identities to share the same key.<br/>
    /// </summary>
    public IndexKeys KeyContract => keyContract;

    /// <summary>
    /// Gets whether this index allows one identity to be associated with multiple keys.<br/>
    /// `SingleKeyPerIdentity` is enforced by immediate generic-scalar writes and generic staged writer facades before planners may use it as an identity-set proof.<br/>
    /// </summary>
    public IdentityKeyMultiplicity IdentityKeyMultiplicity => identityKeyMultiplicity;

    /// <summary>
    /// Gets the index-global batch manager used to control durability cadence without replacing the index mutation surface.<br/>
    /// When enabled, normal insert calls stage writes until `Commit` or `CommitAndDisable` publishes them; the manager is not a SQL transaction and does not provide rollback semantics.<br/>
    /// </summary>
    internal IndexBatchManager<TKey, TIdentity> BatchManager { get; }

    /// <summary>
    /// Gets the index-global batch controller used to control durability cadence without replacing the index mutation surface.<br/>
    /// This is the preferred short public spelling for handwritten code.<br/>
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

    /// <summary>
    /// Gets allocation-free key-membership operations for this opened index.<br/>
    /// Key checks use ordered point lookup and include null or empty routes when the key family supports them.<br/>
    /// </summary>
    public LibraDexIndexKeys<TKey, TIdentity> Keys => new(this);

    /// <summary>
    /// Gets allocation-conscious identity-membership operations for this opened index.<br/>
    /// A configured inverse is preferred; otherwise batch checks use one bounded identity-only forward walk.<br/>
    /// </summary>
    public LibraDexIndexIdentities<TKey, TIdentity> Identities => new(this);

    /// <summary>
    /// Gets exact key/identity association checks for this opened index.<br/>
    /// Entry checks seek the candidate key and inspect only its identity run rather than requiring an identity inversion.<br/>
    /// </summary>
    public LibraDexIndexEntries<TKey, TIdentity> Entries => new(this);

    /// <summary>
    /// Gets runtime identity-to-key lookup controls for this opened catalog index.<br/>
    /// The returned readonly facade delegates to the owning catalog and does not allocate a second owner object.<br/>
    /// </summary>
    public LibraDexIdentityLookup IdentityLookup => new(this);

    internal LibraDexFileSession Session => session;

    internal LibraDexGenericScalarShape Shape => shape;

    /// <summary>
    /// Gets the open catalog that owns this index handle.<br/>
    /// </summary>
    public Catalog Catalog => catalog;

    /// <summary>
    /// Gets the physical slot of the maintained exact reversed projection, when one belongs to this index.<br/>
    /// An index-owned durability batch registers both slots so catalog-opened mutation handles can join its pending writer state safely.<br/>
    /// </summary>
    internal int? ExactReversedProjectionSlotIndex => exactReversedProjection?.SlotIndex;

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
        sharedBatch.PrepareSharedDurabilityCommit();
    }

    /// <summary>
    /// Inserts the reversed-key companion tuple through the projection index's ordinary immediate write path when no explicit batch owns projection publication.<br/>
    /// This keeps no-ceremony primary writes eligible for writer-context/narrow-topology admission while preserving the explicit batch path for callers that requested a shared publication boundary.<br/>
    /// LibraDex is an identity index over external source data, so a projection failure after primary publication should be handled as stale/incomplete index state rather than database rollback.<br/>
    /// </summary>
    /// <param name="key">The original forward key supplied to the logical index.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    private void InsertExactReversedProjectionImmediate(TKey key, TIdentity identity)
    {
        if (exactReversedProjection is null)
        {
            return;
        }

        _ = exactReversedProjection.Insert(CreateExactReversedProjectionKey(key), identity);
    }

    /// <summary>
    /// Completes a no-batch insert by publishing any maintained projection tuple after the primary tuple has been accepted.<br/>
    /// The primary result is returned unchanged so queued-writer attribution continues to describe the caller-visible index mutation path.<br/>
    /// Projection publication is deliberately separate from public batch publication; callers that need a shared publication boundary should use an explicit batch.<br/>
    /// </summary>
    /// <param name="result">The primary index insert result.<br/></param>
    /// <param name="key">The original forward key supplied to the logical index.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The unchanged primary insert result.</returns>
    internal LibraDexGenericInsertResult CompleteImmediateInsertProjection(
        LibraDexGenericInsertResult result,
        TKey key,
        TIdentity identity)
    {
        if (result.Inserted)
        {
            InsertExactReversedProjectionImmediate(key, identity);
            RecordSingleKeyIdentityInsert(identity, key);
        }

        return result;
    }

    /// <summary>
    /// Starts a typed generic durability batch for bulk mutation.<br/>
    /// Batch inserts use developer-facing generic values and defer lower-level commit requests until the returned batch commits or aborts.<br/>
    /// The batch controls durability cadence for one owner; it is not a transaction-isolation or concurrent-writer domain.<br/>
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
            throw new InvalidOperationException("The index batch is already enabled; use Batch.Commit or Batch.CommitAndDisable to control the active batch.");
        }

        return new LibraDexBatch<TKey, TIdentity>(this, session.BeginDurabilityBatch(writeIntent));
    }

    /// <summary>
    /// Starts a public concurrent-write period for this index.<br/>
    /// Ordinary <see cref="Insert(TKey, TIdentity)"/> calls remain optimized for the default single-owner case, while this writer accepts overlapping caller submissions and uses primitive shelf/router-domain admission where the physical shape supports it.<br/>
    /// The returned writer is not a transaction or durability batch; it is an admission facade for a period where the caller expects multiple threads may submit writes.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this explicit concurrent-writer factory.<br/></param>
    /// <param name="cancellationToken">A token that an unrelated thread may cancel to stop queued or subsequent writer operations.<br/></param>
    /// <returns>A concurrent writer facade for this index.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when this index is not backed by a supported concurrent-writer shape.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when index-level batch mode or a session durability batch is active.<br/></exception>
    public LibraDexQueuedWriter<TKey, TIdentity> BeginConcurrentWriter(
        LibraDexConcurrencyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return BeginQueuedWriter(
            options ?? LibraDexConcurrencyOptions.QueuedWriter,
            cancellationToken);
    }

    /// <summary>
    /// Starts a public concurrent batch for a period where multiple callers may write independent `SS8-8` shelves.<br/>
    /// The batch stages shelf-local insert, exact delete, and rekey work in a writer context, cooperatively publishes after <see cref="LibraDexConcurrencyOptions.MaxActionItems"/> successful mutations, and releases/retries when another writer owns a needed shelf.<br/>
    /// Cooperative publication bounds private shelf retention and permits other admitted writers to make progress; the final <see cref="LibraDexConcurrentBatch{TKey, TIdentity}.Publish"/> call publishes any remaining staged mutations and returns aggregate telemetry for the complete logical batch.<br/>
    /// This first slice is intentionally limited to primary `SS8-8` indexes without maintained projection ownership so projection publication cannot outrun primary batch visibility.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode with a 1,000-mutation cooperative publication bound.<br/></param>
    /// <param name="cancellationToken">A token that an unrelated thread may cancel before the batch begins publication.<br/></param>
    /// <returns>A concurrent batch facade for this index.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when this index is not a supported primary `SS8-8` index.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when index-level batch mode or a session durability batch is active.<br/></exception>
    public LibraDexConcurrentBatch<TKey, TIdentity> BeginConcurrentBatch(
        LibraDexConcurrencyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        LibraDexConcurrencyMode mode = options?.Mode ?? LibraDexConcurrencyMode.QueuedWriter;
        if (mode != LibraDexConcurrencyMode.QueuedWriter)
        {
            throw new NotSupportedException($"The generic concurrent batch requires {nameof(LibraDexConcurrencyMode.QueuedWriter)} mode, not {mode}.");
        }

        if (BatchManager.IsEnabled)
        {
            throw new InvalidOperationException("The generic concurrent batch cannot start while index batch mode is enabled.");
        }

        ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        if (shape is not (LibraDexGenericScalarShape.SS88 or LibraDexGenericScalarShape.FS328))
        {
            throw new NotSupportedException($"The generic concurrent batch currently supports primary SS8-8 and FS32-8 indexes only, not {shape}.");
        }

        if (exactReversedProjection is not null &&
            shape != LibraDexGenericScalarShape.FS328)
        {
            throw new NotSupportedException("The generic concurrent batch currently supports maintained exact reversed projections only for FS32-8 indexes.");
        }

        if (shape == LibraDexGenericScalarShape.FS328)
        {
            return new LibraDexConcurrentBatch<TKey, TIdentity>(this, options, cancellationToken);
        }

        Scalar8Scalar8Index encodedIndex = new(
            session,
            new Scalar8Scalar8IndexHandle(RootRouterOffset, GetScalar8Scalar8Profile()),
            SlotIndex,
            Name,
            ownsSession: false);
        return new LibraDexConcurrentBatch<TKey, TIdentity>(this, encodedIndex, options, cancellationToken);
    }

    /// <summary>
    /// Attempts to expose this typed index's supported concurrent batch through the non-generic runtime bridge.<br/>
    /// Unsupported physical shapes return <see langword="false"/>; active durability-batch conflicts and other invalid runtime states continue to throw because they are not capability misses.<br/>
    /// Null scalar keys are intentionally rejected by the returned bridge in this first slice because typed concurrent batches do not yet expose their dedicated null-key route.<br/>
    /// </summary>
    /// <param name="batch">Receives the runtime bridge when the current physical shape supports concurrent insertion.<br/></param>
    /// <returns><see langword="true"/> when a concurrent batch was opened; otherwise <see langword="false"/>.<br/></returns>
    public bool TryBeginConcurrentInsertBatch(out ILibraDexConcurrentInsertBatch? batch)
    {
        try
        {
            batch = new RuntimeConcurrentInsertBatch(BeginConcurrentBatch());
            return true;
        }
        catch (NotSupportedException)
        {
            batch = null;
            return false;
        }
    }

    /// <summary>
    /// Adapts one typed concurrent batch to metadata-driven runtime key and identity values without reflection.<br/>
    /// </summary>
    private sealed class RuntimeConcurrentInsertBatch : ILibraDexConcurrentInsertBatch
    {
        private LibraDexConcurrentBatch<TKey, TIdentity>? batch;

        /// <summary>
        /// Captures the typed owner and its already opened physical concurrent batch.<br/>
        /// </summary>
        /// <param name="batch">Typed physical batch receiving validated values.<br/></param>
        internal RuntimeConcurrentInsertBatch(LibraDexConcurrentBatch<TKey, TIdentity> batch)
        {
            this.batch = batch;
        }

        /// <inheritdoc/>
        public LibraDexGenericInsertResult Insert(object? key, object identity)
        {
            LibraDexConcurrentBatch<TKey, TIdentity> current = RequireActive();
            if (key is null || key == DBNull.Value)
                throw new NotSupportedException("The generic runtime concurrent batch does not yet expose scalar null-key insertion.");
            return current.Insert(
                RequireObjectKey(key, nameof(key)),
                RequireObjectIdentity(identity, nameof(identity)));
        }

        /// <inheritdoc/>
        public LibraDexConcurrentBatchPublishResult Publish()
        {
            LibraDexConcurrentBatch<TKey, TIdentity> current = RequireActive();
            LibraDexConcurrentBatchPublishResult result = current.Publish();
            batch = null;
            current.Dispose();
            return result;
        }

        /// <inheritdoc/>
        public void Abort()
        {
            LibraDexConcurrentBatch<TKey, TIdentity> current = RequireActive();
            current.Abort();
            batch = null;
            current.Dispose();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            LibraDexConcurrentBatch<TKey, TIdentity>? current = batch;
            batch = null;
            current?.Dispose();
        }

        /// <summary>
        /// Requires the one live typed batch before an operation is forwarded.<br/>
        /// </summary>
        /// <returns>The active typed batch.<br/></returns>
        private LibraDexConcurrentBatch<TKey, TIdentity> RequireActive()
            => batch ?? throw new InvalidOperationException("The runtime concurrent insert batch has already completed.");
    }

    /// <summary>
    /// Starts an internal concurrent-write admission facade for overlapping generic callers on supported fixed-scalar indexes.<br/>
    /// The facade keeps caller code on generic key and identity values while letting the shape-specific path choose independent shelf-local staging, retryable same-shelf contention, or serialized fallback.<br/>
    /// This is an Abraxas-integration bridge, not a public all-shape concurrency contract; unsupported shapes are rejected explicitly.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options; defaults to queued-writer mode for this explicit queued-writer factory.<br/></param>
    /// <param name="cancellationToken">A token that can cancel while writer admission is pending.<br/></param>
    /// <param name="admissionAlreadyHeld">Whether the caller already owns the write-admission lease required by this writer.<br/></param>
    /// <returns>A generic queued writer facade for this index.<br/></returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.<br/></exception>
    /// <exception cref="NotSupportedException">Thrown when this index is not backed by a supported queued-writer shape.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when index-level batch mode is active.<br/></exception>
    internal LibraDexQueuedWriter<TKey, TIdentity> BeginQueuedWriter(
        LibraDexConcurrencyOptions? options = null,
        CancellationToken cancellationToken = default,
        bool admissionAlreadyHeld = false)
    {
        ThrowIfDisposed();
        return BeginQueuedWriterAfterSingleKeyBatchCheck(options, cancellationToken, admissionAlreadyHeld);
    }

    /// <summary>
    /// Starts the queued-writer facade after the caller has already handled single-key-per-identity batch restrictions.<br/>
    /// Public/deferred writer entry points call this only after rejection; immediate mutation internals use it for shelf-local delete/rekey plumbing that remains part of one synchronous operation.<br/>
    /// </summary>
    /// <param name="options">Optional concurrency options for the underlying queued writer.</param>
    /// <param name="cancellationToken">A token that can cancel while writer admission is pending.</param>
    /// <param name="admissionAlreadyHeld">Whether the caller already owns the write-admission lease required by this writer.</param>
    /// <returns>A generic queued writer facade for this index.</returns>
    private LibraDexQueuedWriter<TKey, TIdentity> BeginQueuedWriterAfterSingleKeyBatchCheck(
        LibraDexConcurrencyOptions? options = null,
        CancellationToken cancellationToken = default,
        bool admissionAlreadyHeld = false)
    {
        if (BatchManager.IsEnabled)
        {
            throw new InvalidOperationException("The generic queued writer cannot start while index batch mode is enabled.");
        }

        ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();

        if (shape is LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS328 or
            LibraDexGenericScalarShape.FS3216)
        {
            return new LibraDexQueuedWriter<TKey, TIdentity>(
                this,
                options?.MaxActiveWriters ?? 1,
                options?.MaxQueuedWriters ?? 1024,
                options?.QueueTimeout ?? Timeout.InfiniteTimeSpan,
                options?.MaxActionItems ?? 1000,
                cancellationToken,
                admissionAlreadyHeld);
        }

        if (shape != LibraDexGenericScalarShape.SS88)
        {
            throw new NotSupportedException($"The generic queued writer currently supports SS8-8, SS16-8, SS8-16, SS16-16, FS32-8, and FS32-16 indexes only, not {shape}.");
        }

        Scalar8Scalar8Index encodedIndex = new(
            session,
            new Scalar8Scalar8IndexHandle(RootRouterOffset, GetScalar8Scalar8Profile()),
            SlotIndex,
            Name,
            ownsSession: false);
        return new LibraDexQueuedWriter<TKey, TIdentity>(
            this,
            encodedIndex.BeginQueuedWriter(options),
            options?.MaxActiveWriters ?? 1,
            options?.MaxQueuedWriters ?? 1024,
            options?.QueueTimeout ?? Timeout.InfiniteTimeSpan,
            options?.MaxActionItems ?? 1000,
            cancellationToken,
            admissionAlreadyHeld);
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
        if (SupportsNullKeyRoute() && TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            return Insert(keyState, identity);
        }

        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.Insert(this, key, identity);
        }

        LibraDexBatch<TKey, TIdentity>? activeBatch = BatchManager.ActiveBatch;
        if (activeBatch is not null)
        {
            return activeBatch.Insert(key, identity);
        }

        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity &&
            !CanInsertIdentityAtKey(identity, key))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        return InsertAfterIdentityKeyMultiplicityCheck(key, identity);
    }

    /// <summary>
    /// Inserts one ordinary typed key and identity after the caller has already enforced the identity-key multiplicity contract.<br/>
    /// Direct insert calls use this after <see cref="CanInsertIdentityAtKey(TIdentity, object)"/>; rekey calls use it after allowing the old key as the one association being replaced.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.</param>
    /// <param name="identity">The typed identity value associated with the key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    private LibraDexGenericInsertResult InsertAfterIdentityKeyMultiplicityCheck(TKey key, TIdentity identity)
    {
        if (catalog?.TryGetActiveIdentityGroupBatch(Group, out CatalogIdentityGroupBatchManager? groupBatch) == true)
        {
            return groupBatch.Insert(this, key, identity);
        }

        LibraDexBatch<TKey, TIdentity>? activeBatch = BatchManager.ActiveBatch;
        if (activeBatch is not null)
        {
            return activeBatch.Insert(key, identity);
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (CanUseInternalQueuedWriterForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertScalar8Scalar8UsingAdaptiveAdmission(key, identity), key, identity);
        }

        if (CanUseInternalScalar16Scalar8WriterContextForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertScalar16Scalar8UsingWriterContextOrFallback(key, identity), key, identity);
        }

        if (CanUseInternalScalar8Scalar16WriterContextForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertScalar8Scalar16UsingWriterContextOrFallback(key, identity), key, identity);
        }

        if (CanUseInternalScalar16Scalar16WriterContextForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertScalar16Scalar16UsingWriterContextOrFallback(key, identity), key, identity);
        }

        if (CanUseInternalFixed32Scalar8WriterContextForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertFixed32Scalar8UsingWriterContextOrFallback(key, identity), key, identity);
        }

        if (CanUseInternalFixed32Scalar16WriterContextForPrimaryMutation())
        {
            return CompleteImmediateInsertProjection(InsertFixed32Scalar16UsingWriterContextOrFallback(key, identity), key, identity);
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

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (!CanInsertIdentityAtKey(identity, ScalarNull.Null))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
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

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (!CanInsertIdentityAtKey(identity, keyState))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
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
    /// Counts the physical key/identity tuples currently visible through this generic scalar index.<br/>
    /// The count uses the same primitive count path as condition execution, so fixed scalar shapes count from index metadata, key-state route metadata, and range-reader shelf counts instead of decoding identities.<br/>
    /// </summary>
    /// <returns>The number of stored index tuples.</returns>
    public long Count()
    {
        return CountIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()));
    }

    /// <summary>
    /// Counts physical key/identity tuples matched by a completed condition rooted at this index.<br/>
    /// This is tuple-oriented LibraDex counting: single-index range predicates use shelf/range count metadata, while distinct-identity semantics remain available through condition terminals that explicitly request distinct deduplication.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to count.</param>
    /// <returns>The number of stored tuples matched by the condition.</returns>
    public long Count(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            ResolveOwnIndex,
            ResolveOwnProjectionIndex);
        return LibraDexIdentityExecutionPlanner.Count(criterion, IdentityDeduplication.Preserve);
    }

    /// <summary>
    /// Opens a key/identity cursor for a completed condition rooted at this index.<br/>
    /// This convenience remains single-index scoped: composed cross-index conditions should be executed from the owning identity group so every referenced index can be resolved by name.<br/>
    /// Descending direction streams the matched target-index tuples from highest key to lowest key; the current implementation uses a buffered snapshot until native reverse shelf traversal is connected.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to execute.</param>
    /// <param name="skip">The number of matching index entries to skip.</param>
    /// <param name="take">The optional maximum number of index entries to return.</param>
    /// <param name="direction">The requested target-index key traversal direction.</param>
    /// <returns>A cursor over this index's stored key/identity entries.</returns>
    public LibraDexIndexCursor<TKey, TIdentity> GetCursor(
        LibraDexConditionEndCondition condition,
        int skip = 0,
        int? take = null,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(condition);
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            ResolveOwnIndex,
            ResolveOwnProjectionIndex);
        _ = LibraDexConditionCursorExecutor.TryCreateDirectPrimitiveDeletePlan(
            criterion,
            this,
            skip,
            take,
            out IIdentityPrimitiveMutator? primitiveMutator,
            out LibraDexIdentityPrimitiveRequest? primitiveDeleteRequest);
        return new LibraDexIndexCursor<TKey, TIdentity>(
            LibraDexConditionCursorExecutor.IterateTargetIndexTuples(criterion, this, skip, take, direction),
            this,
            this,
            primitiveMutator,
            primitiveDeleteRequest,
            skip,
            take);
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
        bool oldIsNullKey = TryClassifyNullKeyRouteKey(oldKey, out NullKey oldNullKeyState);
        bool newIsNullKey = TryClassifyNullKeyRouteKey(newKey, out NullKey newNullKeyState);
        if (oldIsNullKey || newIsNullKey)
        {
            if (oldIsNullKey && newIsNullKey)
            {
                return Rekey(typedIdentity, oldNullKeyState, newNullKeyState);
            }

            if (oldIsNullKey)
            {
                return Rekey(typedIdentity, oldNullKeyState, RequireObjectKey(newKey!, nameof(newKey)));
            }

            return Rekey(typedIdentity, RequireObjectKey(oldKey!, nameof(oldKey)), newNullKeyState);
        }

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
            if (!CanInsertIdentityAtKeyReplacingOldKey(typedIdentity, typedNewKey, typedOldKey))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertAfterIdentityKeyMultiplicityCheck(typedNewKey, typedIdentity);
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
        if (TryClassifyNullKeyRouteKey(newKey, out NullKey newNullKeyState))
        {
            long changedToNullKey = 0;
            List<TKey> oldNormalKeys = new();
            using (LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader())
            {
                while (reader.TryReadNext(out TKey key, out TIdentity currentIdentity))
                {
                    if (TupleComponentEquals(currentIdentity, typedIdentity))
                    {
                        oldNormalKeys.Add(key);
                    }
                }
            }

            for (int i = 0; i < oldNormalKeys.Count; i++)
            {
                if (Rekey(typedIdentity, oldNormalKeys[i], newNullKeyState))
                {
                    changedToNullKey++;
                }
            }

            NullKey otherState = newNullKeyState == NullKey.Null ? NullKey.Empty : NullKey.Null;
            if (ContainsNullKeyIdentity(otherState, typedIdentity) &&
                Rekey(typedIdentity, otherState, newNullKeyState))
            {
                changedToNullKey++;
            }

            return changedToNullKey;
        }

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

        if (SupportsScalarNullKeyRoute() &&
            ContainsScalarNullIdentity(typedIdentity) &&
            Rekey(typedIdentity, ScalarNull.Null, typedNewKey))
        {
            changed++;
        }
        else if (SupportsNullKeyRoute() && SupportsStoredNullKeyRoutes())
        {
            if (ContainsNullKeyIdentity(NullKey.Null, typedIdentity) &&
                Rekey(typedIdentity, NullKey.Null, typedNewKey))
            {
                changed++;
            }

            if (ContainsNullKeyIdentity(NullKey.Empty, typedIdentity) &&
                Rekey(typedIdentity, NullKey.Empty, typedNewKey))
            {
                changed++;
            }
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
            foreach (object? key in objectSet)
            {
                if (!TryClassifyNullKeyRouteKey(key, out _))
                {
                    _ = RequireObjectKey(key!, nameof(keys));
                }
            }

            IEqualityComparer<object>? comparer = objectSet is HashSet<object> hashSet ? hashSet.Comparer : null;
            return new LibraDexPreparedObjectSet(typeof(TKey), values: null, objectSet, comparer);
        }

        object[] values = keys.Select(key =>
        {
            if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
            {
                return keyState switch
                {
                    NullKey.Null => null!,
                    NullKey.Empty => Array.Empty<byte>(),
                    _ => keyState
                };
            }

            return (object)RequireObjectKey(key, nameof(keys))!;
        }).ToArray();
        return new LibraDexPreparedObjectSet(typeof(TKey), values);
    }

    IReadOnlyList<object> IIdentityPrimitiveExecutor.ExecuteIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return ExecuteIdentityPrimitive(request);
    }

    IEnumerable<object> IIdentityPrimitiveExecutor.IterateIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return BoxIdentityIterator(IterateIdentityPrimitiveTyped(request));
    }

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
    {
        return IterateIdentityPrimitiveTyped(request);
    }

    long IIdentityPrimitiveExecutor.CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        return CountIdentityPrimitive(request);
    }

    /// <summary>
    /// Executes count as a generic scalar aggregate over the condition-materialized primitive.<br/>
    /// Null-key route counts use existing route-root metadata, while ordinary scalar criteria use the shape-native range readers and count paths.<br/>
    /// </summary>
    /// <param name="request">The aggregate request to execute.<br/></param>
    /// <returns>The aggregate count result and physical plan classification.<br/></returns>
    LibraDexPrimitiveAggregateResult IIdentityPrimitiveAggregateExecutor.ExecuteIdentityPrimitiveAggregate(LibraDexPrimitiveAggregateRequest request)
    {
        if (request.Kind != LibraDexPrimitiveAggregateKind.Count)
        {
            throw new NotSupportedException($"{request.Kind} is not connected to generic scalar aggregation yet.");
        }

        if (request.Scope != AggregateScope.Tuples)
        {
            throw new NotSupportedException($"{request.Scope} aggregate scope is not connected to generic scalar aggregation yet.");
        }

        return LibraDexPrimitiveAggregateResult.ForCount(
            CountIdentityPrimitive(request.PrimitiveRequest),
            ClassifyGenericScalarAggregatePlan(request.PrimitiveRequest));
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
        return IterateTuplePrimitive(request);
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
            LibraDexCriteriaKind.NumericTransform => MaterializeNumericTransformIdentityObjects(request.Values, request.TakeLimit),
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
    private IEnumerable<TIdentity> IterateIdentityPrimitiveTyped(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        switch (request.CriteriaKind)
        {
            case LibraDexCriteriaKind.All:
                foreach (TIdentity identity in IterateAllIdentityObjects(request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Find:
                foreach (TIdentity identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Between:
                foreach (TIdentity identity in IterateIdentityObjects(OpenRangeReader(
                    RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                    RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Before:
                foreach (TIdentity identity in IterateIdentityObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrBefore:
                foreach (TIdentity identity in IterateIdentityObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.After:
                foreach (TIdentity identity in IterateIdentityObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.AtOrAfter:
                foreach (TIdentity identity in IterateIdentityObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.In:
            case LibraDexCriteriaKind.InSet:
                foreach (TIdentity identity in IterateMembershipIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.MultiRange:
                foreach (TIdentity identity in IterateMultiRangeIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.StructuredComponent:
                foreach (TIdentity identity in IterateStructuredComponentIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.GuidPattern:
                foreach (TIdentity identity in IterateGuidPatternIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.BinaryPattern:
                foreach (TIdentity identity in IterateBinaryPatternIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.BinaryTypedSlice:
                foreach (TIdentity identity in IterateBinaryTypedSliceIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.Bitmask:
                foreach (TIdentity identity in IterateBitmaskIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.NumericTransform:
                foreach (TIdentity identity in IterateNumericTransformIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.ScalarNull:
                foreach (TIdentity identity in IterateScalarNullIdentityObjects(request.Values, request.TakeLimit, request.Direction))
                {
                    yield return identity;
                }

                yield break;
            case LibraDexCriteriaKind.KeyState:
                foreach (TIdentity identity in IterateNullKeyIdentityObjects(request.Values, request.TakeLimit, request.Direction))
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
        return BoxIdentityIterator(IterateIdentityUniverseTyped());
    }

    IEnumerable<TIdentity> IIdentityPrimitiveExecutor<TIdentity>.IterateIdentityUniverseTyped()
    {
        return IterateIdentityUniverseTyped();
    }

    /// <summary>
    /// Streams the identity universe visible to this index handle.<br/>
    /// For grouped catalog indexes, the universe is the de-duplicated union of `All` identities across indexes that declare the same identity group in catalog metadata.<br/>
    /// For ungrouped or standalone handles, the method falls back to this index's own `All` primitive because no broader identity source is currently known.<br/>
    /// </summary>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateIdentityUniverseTyped()
    {
        ThrowIfDisposed();
        if (catalog is null || string.IsNullOrWhiteSpace(Group))
        {
            foreach (TIdentity identity in IterateIdentityPrimitiveTyped(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>())))
            {
                yield return identity;
            }

            yield break;
        }

        HashSet<TIdentity> seen = new(LibraDexKeyEquality<TIdentity>.Comparer);
        CatalogIndexInfo[] indexes = catalog.Indexes.IndexSet(Group).List();
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
            LibraDexIdentityPrimitiveRequest allRequest = new(LibraDexCriteriaKind.All, Array.Empty<object?>());
            IEnumerable<TIdentity> candidateIdentities = primitiveExecutor is IIdentityPrimitiveExecutor<TIdentity> typedPrimitiveExecutor
                ? typedPrimitiveExecutor.IterateIdentityPrimitiveTyped(allRequest)
                : CastIdentityIterator(primitiveExecutor.IterateIdentityPrimitive(allRequest));
            foreach (TIdentity identity in candidateIdentities)
            {
                if (seen.Add(identity))
                {
                    yield return identity;
                }
            }
        }
    }

    /// <summary>
    /// Adapts a typed identity stream to the legacy object executor contract.<br/>
    /// Boxing is intentionally confined to callers that explicitly selected the runtime-shaped API; typed condition projections never enter this adapter.<br/>
    /// </summary>
    /// <param name="identities">The typed identities to expose through the compatibility contract.<br/></param>
    /// <returns>A lazy object sequence preserving source order and cardinality.<br/></returns>
    private static IEnumerable<object> BoxIdentityIterator(IEnumerable<TIdentity> identities)
    {
        foreach (TIdentity identity in identities)
        {
            yield return identity!;
        }
    }

    /// <summary>
    /// Validates and adapts a legacy object identity stream when a grouped universe contains an executor that has not implemented the typed contract.<br/>
    /// Built-in scalar executors avoid this fallback; it remains only for compatible runtime-shaped or older internal index implementations.<br/>
    /// </summary>
    /// <param name="identities">The runtime identity sequence to validate.<br/></param>
    /// <returns>A lazy typed sequence over the supplied identities.<br/></returns>
    /// <exception cref="InvalidCastException">Thrown when an emitted identity does not match <typeparamref name="TIdentity"/>.<br/></exception>
    private static IEnumerable<TIdentity> CastIdentityIterator(IEnumerable<object> identities)
    {
        foreach (object? identityObject in identities)
        {
            if (identityObject is not TIdentity identity)
            {
                string actualType = identityObject?.GetType().FullName ?? "null";
                throw new InvalidCastException($"Identity is {actualType}, not {typeof(TIdentity).FullName}.");
            }

            yield return identity;
        }
    }

    private long CountIdentityPrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => CountAllIdentityObjects(),
            LibraDexCriteriaKind.Find => CountOrderedIdentityRange(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values))),
            LibraDexCriteriaKind.Between => CountOrderedIdentityRange(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values))),
            LibraDexCriteriaKind.Before => CountBeforeIdentityRange(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), inclusive: false),
            LibraDexCriteriaKind.AtOrBefore => CountBeforeIdentityRange(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), inclusive: true),
            LibraDexCriteriaKind.After => CountAfterIdentityRange(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), inclusive: false),
            LibraDexCriteriaKind.AtOrAfter => CountAfterIdentityRange(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), inclusive: true),
            LibraDexCriteriaKind.In => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.InSet => CountMembershipIdentityObjects(request.Values),
            LibraDexCriteriaKind.MultiRange => CountMultiRangeIdentityObjects(request.Values),
            LibraDexCriteriaKind.StructuredComponent => CountStructuredComponentIdentityObjects(request.Values),
            LibraDexCriteriaKind.GuidPattern => CountGuidPatternIdentityObjects(request.Values),
            LibraDexCriteriaKind.BinaryPattern => CountBinaryPatternIdentityObjects(request.Values),
            LibraDexCriteriaKind.BinaryTypedSlice => CountBinaryTypedSliceIdentityObjects(request.Values),
            LibraDexCriteriaKind.Bitmask => CountBitmaskIdentityObjects(request.Values),
            LibraDexCriteriaKind.NumericTransform => CountNumericTransformIdentityObjects(request.Values),
            LibraDexCriteriaKind.ScalarNull => CountScalarNullIdentityObjects(request.Values),
            LibraDexCriteriaKind.KeyState => CountNullKeyIdentityObjects(request.Values),
            _ => throw new NotSupportedException($"{request.CriteriaKind} identity count is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Counts one inclusive ordered key extent through the most direct physical count primitive available for the resolved generic scalar shape.<br/>
    /// Shapes without a count-only range primitive keep the existing range-reader fallback so this helper can be widened shape by shape without changing condition semantics.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.<br/></param>
    /// <param name="upperKey">The inclusive upper public key.<br/></param>
    /// <returns>The number of identities in the ordered key extent.<br/></returns>
    private long CountOrderedIdentityRange(TKey lowerKey, TKey upperKey)
    {
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => session.CountScalar8Scalar8IdentityRange(
                RootRouterOffset,
                GetScalar8Scalar8Profile(),
                EncodeKey8(lowerKey),
                EncodeKey8(upperKey)),
            LibraDexGenericScalarShape.SS168 => CountScalar16Scalar8IdentityRange(lowerKey, upperKey),
            LibraDexGenericScalarShape.SS816 => session.CountScalar8Scalar16IdentityRange(
                RootRouterOffset,
                GetScalar8Scalar16Profile(),
                EncodeKey8(lowerKey),
                EncodeKey8(upperKey)),
            LibraDexGenericScalarShape.SS1616 => CountScalar16Scalar16IdentityRange(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS328 => CountFixed32Scalar8IdentityRange(lowerKey, upperKey),
            LibraDexGenericScalarShape.FS3216 => CountFixed32Scalar16IdentityRange(lowerKey, upperKey),
            _ => CountIdentityObjects(OpenRangeReader(lowerKey, upperKey))
        };
    }

    /// <summary>
    /// Counts one inclusive `SS16-8` ordered key extent through the session's count-only routed primitive.<br/>
    /// The helper keeps scalar-16 key encoding local to this generic index layer while letting the file session operate only on encoded lanes.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.<br/></param>
    /// <param name="upperKey">The inclusive upper public key.<br/></param>
    /// <returns>The number of identities in the ordered key extent.<br/></returns>
    private long CountScalar16Scalar8IdentityRange(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.CountScalar16Scalar8IdentityRange(
            RootRouterOffset,
            GetScalar16Scalar8Profile(),
            lowerHigh,
            lowerLow,
            upperHigh,
            upperLow);
    }

    /// <summary>
    /// Counts one inclusive `SS16-16` ordered key extent through the session's count-only routed primitive.<br/>
    /// The helper mirrors the existing scalar-16 range-reader encoding path so public condition semantics remain owned by the generic scalar codec.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.<br/></param>
    /// <param name="upperKey">The inclusive upper public key.<br/></param>
    /// <returns>The number of identities in the ordered key extent.<br/></returns>
    private long CountScalar16Scalar16IdentityRange(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.CountScalar16Scalar16IdentityRange(
            RootRouterOffset,
            GetScalar16Scalar16Profile(),
            lowerHigh,
            lowerLow,
            upperHigh,
            upperLow);
    }

    /// <summary>
    /// Counts one inclusive `FS32-8` ordered key extent through the session's count-only routed primitive.<br/>
    /// The bridge keeps fixed32 public-key encoding in the generic layer and passes only four persisted key lanes to the file-session count path.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.<br/></param>
    /// <param name="upperKey">The inclusive upper public key.<br/></param>
    /// <returns>The number of identities in the ordered key extent.<br/></returns>
    private long CountFixed32Scalar8IdentityRange(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.CountFixed32Scalar8IdentityRange(
            RootRouterOffset,
            GetFixed32Scalar8Profile(),
            lower0,
            lower1,
            lower2,
            lower3,
            upper0,
            upper1,
            upper2,
            upper3);
    }

    /// <summary>
    /// Counts one inclusive `FS32-16` ordered key extent through the session's count-only routed primitive.<br/>
    /// This mirrors the fixed32 range-reader encoding path while avoiding reader materialization for count-only condition execution.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower public key.<br/></param>
    /// <param name="upperKey">The inclusive upper public key.<br/></param>
    /// <returns>The number of identities in the ordered key extent.<br/></returns>
    private long CountFixed32Scalar16IdentityRange(TKey lowerKey, TKey upperKey)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.CountFixed32Scalar16IdentityRange(
            RootRouterOffset,
            GetFixed32Scalar16Profile(),
            lower0,
            lower1,
            lower2,
            lower3,
            upper0,
            upper1,
            upper2,
            upper3);
    }

    /// <summary>
    /// Counts identities before one ordered key boundary by translating the developer-facing condition into an inclusive physical extent.<br/>
    /// Exclusive boundaries use the same predecessor helper as the range reader path so the count-only path preserves existing scalar ordering semantics.<br/>
    /// </summary>
    /// <param name="key">The boundary key.<br/></param>
    /// <param name="inclusive">True when identities at <paramref name="key"/> should be included.<br/></param>
    /// <returns>The number of identities before the boundary.<br/></returns>
    private long CountBeforeIdentityRange(TKey key, bool inclusive)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        if (inclusive)
        {
            return CountOrderedIdentityRange(lowerKey, key);
        }

        return TryGetPreviousKey(key, out TKey upperKey)
            ? CountOrderedIdentityRange(lowerKey, upperKey)
            : 0;
    }

    /// <summary>
    /// Counts identities after one ordered key boundary by translating the developer-facing condition into an inclusive physical extent.<br/>
    /// Exclusive boundaries use the same successor helper as the range reader path so the count-only path preserves existing scalar ordering semantics.<br/>
    /// </summary>
    /// <param name="key">The boundary key.<br/></param>
    /// <param name="inclusive">True when identities at <paramref name="key"/> should be included.<br/></param>
    /// <returns>The number of identities after the boundary.<br/></returns>
    private long CountAfterIdentityRange(TKey key, bool inclusive)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        if (inclusive)
        {
            return CountOrderedIdentityRange(key, upperKey);
        }

        return TryGetNextKey(key, out TKey lowerKey)
            ? CountOrderedIdentityRange(lowerKey, upperKey)
            : 0;
    }

    /// <summary>
    /// Classifies the physical plan used by a generic scalar aggregate primitive.<br/>
    /// Null-key route counts read existing route metadata, direct ordered criteria use shape-native range counts, and residual predicates are classified as key scans.<br/>
    /// </summary>
    /// <param name="request">The primitive request being aggregated.<br/></param>
    /// <returns>The conservative physical plan classification.</returns>
    private static LibraDexPrimitiveAggregatePlanKind ClassifyGenericScalarAggregatePlan(LibraDexIdentityPrimitiveRequest request)
    {
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.ScalarNull or
            LibraDexCriteriaKind.KeyState => LibraDexPrimitiveAggregatePlanKind.Metadata,
            LibraDexCriteriaKind.StructuredComponent or
            LibraDexCriteriaKind.GuidPattern or
            LibraDexCriteriaKind.BinaryPattern or
            LibraDexCriteriaKind.BinaryTypedSlice or
            LibraDexCriteriaKind.Bitmask or
            LibraDexCriteriaKind.NumericTransform => LibraDexPrimitiveAggregatePlanKind.KeyScan,
            _ => LibraDexPrimitiveAggregatePlanKind.RangeSlots
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
        if (session.IsDurabilityBatchActive &&
            !catalog.TryPrepareActiveIndexBatchMutation(SlotIndex))
            ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        if (CanUseInternalExactTupleDeleteForPrimaryMutation() &&
            CanDeleteIdentityPrimitiveUsingQueuedExactTuples(request))
        {
            IReadOnlyList<LibraDexObjectTuple> tuples = ExecuteTuplePrimitive(request);
            long queuedDeleted = 0;
            for (int i = 0; i < tuples.Count; i++)
            {
                LibraDexObjectTuple tuple = tuples[i];
                if (((IIdentityExactTupleMutator)this).DeleteExactTuple(tuple.Key, tuple.Identity))
                {
                    queuedDeleted++;
                }
            }

            return new LibraDexIdentityMutationResult(
                LibraDexCriteriaMutationKind.Delete,
                tuples.Count,
                queuedDeleted,
                new LibraDexQueryDiagnostics(LibraDexExecutionKind.FastPath, RowsScanned: tuples.Count, RowsReturned: queuedDeleted));
        }

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
            LibraDexCriteriaKind.All => MaterializeAllTupleObjects(request.Direction),
            LibraDexCriteriaKind.Find => MaterializeTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                request.Direction)),
            LibraDexCriteriaKind.Between => MaterializeTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)),
                request.Direction)),
            LibraDexCriteriaKind.Before => MaterializeTupleObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction)),
            LibraDexCriteriaKind.AtOrBefore => MaterializeTupleObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction)),
            LibraDexCriteriaKind.After => MaterializeTupleObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction)),
            LibraDexCriteriaKind.AtOrAfter => MaterializeTupleObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction)),
            LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet => MaterializeMembershipTupleObjects(request.Values),
            LibraDexCriteriaKind.MultiRange => MaterializeMultiRangeTupleObjects(request.Values),
            LibraDexCriteriaKind.Bitmask => MaterializeBitmaskTupleObjects(request.Values),
            LibraDexCriteriaKind.NumericTransform => MaterializeNumericTransformTupleObjects(request.Values),
            LibraDexCriteriaKind.ScalarNull => MaterializeScalarNullTupleObjects(request.Values),
            LibraDexCriteriaKind.KeyState => MaterializeNullKeyTupleObjects(request.Values),
            _ => throw new NotSupportedException($"{request.CriteriaKind} tuple execution is not connected to physical readers yet.")
        };
    }

    /// <summary>
    /// Streams key/identity tuples matched by one normalized condition primitive through existing range readers.<br/>
    /// This keeps condition-shaped public cursors on the same physical cursor spine as direct `OpenRangeReader(...)` calls for simple range, boundary, membership, and multirange primitives.<br/>
    /// Complex scan-backed primitives can still fall back to the materialized tuple executor until shape-specific streaming is connected for them.<br/>
    /// </summary>
    /// <param name="request">The normalized primitive request produced by the condition materializer.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexObjectTuple> IterateTuplePrimitive(LibraDexIdentityPrimitiveRequest request)
    {
        ThrowIfDisposed();
        return request.CriteriaKind switch
        {
            LibraDexCriteriaKind.All => IterateAllTupleObjects(request.Direction, request.TakeLimit),
            LibraDexCriteriaKind.Find => IterateTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.Between => IterateTupleObjects(OpenRangeReader(
                RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)),
                RequireObjectKey(RequireCriterionValue(request.Values, 1), nameof(request.Values)),
                request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.Before => IterateTupleObjects(OpenBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.AtOrBefore => IterateTupleObjects(OpenAtOrBeforeRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.After => IterateTupleObjects(OpenAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.AtOrAfter => IterateTupleObjects(OpenAtOrAfterRangeReader(RequireObjectKey(RequireCriterionValue(request.Values, 0), nameof(request.Values)), request.Direction), request.TakeLimit),
            LibraDexCriteriaKind.In or LibraDexCriteriaKind.InSet => IterateMembershipTupleObjects(request.Values, request.TakeLimit, request.Direction),
            LibraDexCriteriaKind.MultiRange => IterateMultiRangeTupleObjects(request.Values, request.TakeLimit, request.Direction),
            LibraDexCriteriaKind.ScalarNull => IterateScalarNullTupleObjects(request),
            LibraDexCriteriaKind.KeyState => IterateNullKeyTupleObjects(request),
            LibraDexCriteriaKind.Bitmask => IterateBitmaskTupleObjects(request),
            LibraDexCriteriaKind.NumericTransform => IterateNumericTransformTupleObjects(request),
            _ => ExecuteTuplePrimitive(request)
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
        else if (SupportsStoredNullKeyRoutes())
        {
            deleted += DeleteNullKeyRouteIdentities(NullKey.Null);
            deleted += DeleteNullKeyRouteIdentities(NullKey.Empty);
        }

        deleted += CanUseInternalExactTupleDeleteForPrimaryMutation()
            ? DeleteIdentityPrimitiveValueTuplesUsingExactTupleBridge(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()))
            : DeleteIdentityPrimitiveRange(GetLowerFullKeyBound(), GetUpperFullKeyBound());
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
            return DeleteMembershipIdentityPrimitive(values);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object?> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues.Cast<object?>(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
            _ => throw new InvalidOperationException("Membership identity deletion requires an enumerable key value or prepared set.")
        };

        return DeleteMembershipIdentityPrimitive(keys);
    }

    private long DeleteMembershipIdentityPrimitive(IEnumerable<object?> keys)
    {
        long deleted = 0;
        foreach (object? keyValue in keys)
        {
            if (TryClassifyNullKeyRouteKey(keyValue, out NullKey keyState))
            {
                deleted += DeleteNullKeyIdentityPrimitive(new object?[] { keyState });
                continue;
            }

            TKey key = RequireObjectKey(keyValue!, nameof(keys));
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
            : CanUseInternalExactTupleDeleteForPrimaryMutation()
                ? DeleteIdentityPrimitiveValueTuplesUsingExactTupleBridge(new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>()))
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

        using Scalar8Scalar8RangePlan plan = session.BuildScalar8Scalar8RangePlan(RootRouterOffset, GetScalar8Scalar8Profile(), lowerEncodedKey, upperEncodedKey);
        long deleted = 0;
        for (int i = 0; i < plan.ShelfCount; i++)
        {
            if (plan.TerminalShelfFlags[i] != 0)
            {
                int removedFromTerminal = session.DeleteScalar8Scalar8TerminalIdentities(
                    plan.TerminalRootOffsets[i],
                    plan.Profile,
                    plan.TerminalScalarKeys[i],
                    encodedIdentity: null);
                deleted += removedFromTerminal;
                continue;
            }

            byte[] shelfBytes = session.IsDurabilityBatchActive
                ? session.ReadScalar8Scalar8ShelfBytesForBatch(plan.ShelfOffsets[i], plan.Profile)
                : plan.Shelves[i];
            Scalar8Scalar8ReadOnly readOnly = new(shelfBytes, plan.Profile);
            int startSlot = session.IsDurabilityBatchActive
                ? readOnly.LowerBoundKey(readOnly.IsDescending ? upperEncodedKey : lowerEncodedKey)
                : plan.StartSlots[i];
            int endSlot = session.IsDurabilityBatchActive ? startSlot : plan.EndSlots[i];
            if (session.IsDurabilityBatchActive)
            {
                while (endSlot < readOnly.ItemCount && (readOnly.IsDescending
                    ? readOnly.ReadKeyAt(endSlot) >= lowerEncodedKey
                    : readOnly.ReadKeyAt(endSlot) <= upperEncodedKey))
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
    /// Rejects ordinary immediate mutation while any explicit session durability batch is active.<br/>
    /// The session batch owns the mutable dirty-shelf overlay, so unrelated no-ceremony callers must not accidentally publish into that batch by observing only their own index-level batch flag.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the owning session already has an active durability batch.<br/></exception>
    private void ThrowIfSessionDurabilityBatchActiveForImmediateMutation()
    {
        if (session.IsDurabilityBatchActive)
        {
            throw new InvalidOperationException("Immediate LibraDex mutation cannot run while another session durability batch is active; publish, abort, or disable the active batch before issuing unrelated no-batch writes.");
        }
    }

    /// <summary>
    /// Rejects queued-writer mutation while any explicit session durability batch is active.<br/>
    /// Queued writers use writer-local staging and serialized publication, while durability batches use the session-owned dirty-shelf overlay; mixing them would make caller ownership ambiguous.<br/>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the owning session already has an active durability batch.<br/></exception>
    internal void ThrowIfSessionDurabilityBatchActiveForConcurrentWriter()
    {
        if (session.IsDurabilityBatchActive)
        {
            throw new InvalidOperationException("The generic queued writer cannot run while a session durability batch is active; publish, abort, or disable the active batch before starting concurrent writer work.");
        }
    }

    /// <summary>
    /// Tests whether an identity may be inserted at the requested key under this index's identity-key multiplicity contract.<br/>
    /// The permissive default returns immediately; the single-key contract scans visible ordinary tuples and key-state routes to ensure the identity is not already associated with a different key in this logical index.<br/>
    /// </summary>
    /// <param name="identity">The identity being inserted.</param>
    /// <param name="candidateKey">The concrete key or key-state route requested for the insert.</param>
    /// <returns><see langword="true"/> when the insert does not violate the identity-key multiplicity contract.</returns>
    private bool CanInsertIdentityAtKey(TIdentity identity, object? candidateKey)
    {
        return identityKeyMultiplicity != IdentityKeyMultiplicity.SingleKeyPerIdentity ||
            !IdentityHasDifferentKey(identity, candidateKey);
    }

    /// <summary>
    /// Tests whether an identity may be inserted at a replacement key while one known old key still contains the same identity.<br/>
    /// Rekey uses this to remain lossless: the replacement tuple is created before the old tuple is deleted, but any third association still blocks the mutation.<br/>
    /// </summary>
    /// <param name="identity">The identity being re-keyed.</param>
    /// <param name="candidateKey">The replacement key or key-state route requested for the rekey.</param>
    /// <param name="oldKey">The old key or key-state route allowed to contain the identity until the replacement is committed.</param>
    /// <returns><see langword="true"/> when the replacement does not violate the identity-key multiplicity contract.</returns>
    private bool CanInsertIdentityAtKeyReplacingOldKey(TIdentity identity, object? candidateKey, object? oldKey)
    {
        return identityKeyMultiplicity != IdentityKeyMultiplicity.SingleKeyPerIdentity ||
            !IdentityHasDifferentKey(identity, candidateKey, oldKey);
    }

    /// <summary>
    /// Tests whether a staged writer may insert an identity at the requested key under this index's identity-key multiplicity contract.<br/>
    /// Batch and queued writer facades call this before accepting a staged write so the persisted contract is enforced before publication.<br/>
    /// Merely staging a tuple does not invalidate the committed identity map; the physical publication boundary performs one invalidation after bytes become reader-visible.<br/>
    /// </summary>
    /// <param name="identity">The identity being inserted.</param>
    /// <param name="candidateKey">The concrete key or key-state route requested for the insert.</param>
    /// <returns><see langword="true"/> when the insert does not violate committed identity-key ownership.</returns>
    internal bool CanStageInsertIdentityAtKey(TIdentity identity, object? candidateKey)
    {
        return CanInsertIdentityAtKey(identity, candidateKey);
    }

    /// <summary>
    /// Tests whether a staged writer may insert a replacement key while the old key still contains the same identity.<br/>
    /// Rekey facades use this to keep replacement-before-delete behavior without allowing a third identity-key association.<br/>
    /// The committed identity map remains valid until an actual publication; successful staged publication invalidates it once at the owning boundary.<br/>
    /// </summary>
    /// <param name="identity">The identity being re-keyed.</param>
    /// <param name="candidateKey">The replacement key or key-state route requested for the rekey.</param>
    /// <param name="oldKey">The old key or key-state route allowed until the replacement is published.</param>
    /// <returns><see langword="true"/> when the replacement does not violate committed identity-key ownership.</returns>
    internal bool CanStageInsertIdentityAtKeyReplacingOldKey(TIdentity identity, object? candidateKey, object? oldKey)
    {
        return CanInsertIdentityAtKeyReplacingOldKey(identity, candidateKey, oldKey);
    }

    /// <summary>
    /// Tests staged identity equality with the same tuple semantics used by stored LibraDex rows.<br/>
    /// This keeps staged byte-array identities content-based instead of reference-based.<br/>
    /// </summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns><see langword="true"/> when both identities compare equal as tuple components.</returns>
    internal bool StagedIdentityEquals(TIdentity left, TIdentity right)
    {
        return TupleComponentEquals(left, right);
    }

    /// <summary>
    /// Tests staged ordinary-key equality with the same tuple semantics used by stored LibraDex rows.<br/>
    /// This keeps staged byte-array keys content-based instead of reference-based.<br/>
    /// </summary>
    /// <param name="left">The first ordinary key.</param>
    /// <param name="right">The second ordinary key.</param>
    /// <returns><see langword="true"/> when both keys compare equal as tuple components.</returns>
    internal bool StagedOrdinaryKeyEquals(TKey left, TKey right)
    {
        return TupleComponentEquals(left, right);
    }

    /// <summary>
    /// Inserts one ordinary tuple through the already checked immediate insert core for queued-writer rekey replacement.<br/>
    /// The queued writer calls this only after its staged identity-key guard has allowed the old key as the association being replaced.<br/>
    /// </summary>
    /// <param name="key">The replacement key to insert.</param>
    /// <param name="identity">The identity associated with the replacement key.</param>
    /// <returns>The insert result plus any route-create and insert commit telemetry.</returns>
    internal LibraDexGenericInsertResult InsertAfterIdentityKeyMultiplicityCheckForQueuedWriter(TKey key, TIdentity identity)
    {
        return InsertAfterIdentityKeyMultiplicityCheck(key, identity);
    }

    /// <summary>
    /// Finds whether an identity is already stored under any key other than the allowed candidate key.<br/>
    /// This is intentionally a correctness guard, not a hot-path optimization: callers opt into it when they need planner-visible single-key identity semantics.<br/>
    /// </summary>
    /// <param name="identity">The identity to search for.</param>
    /// <param name="allowedKey">The primary key or key-state route that is allowed to already contain the identity.</param>
    /// <param name="alternateAllowedKey">The optional second key or key-state route that is also allowed, used for lossless rekey replacement.</param>
    /// <returns><see langword="true"/> when the identity is associated with a different key.</returns>
    private bool IdentityHasDifferentKey(TIdentity identity, object? allowedKey, object? alternateAllowedKey = null)
    {
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity &&
            allowedKey is TKey typedAllowedKey &&
            (alternateAllowedKey is null || alternateAllowedKey is TKey))
        {
            return catalog.GetSingleKeyIdentityMap(this).HasDifferentKey(
                this,
                identity,
                typedAllowedKey,
                alternateAllowedKey is TKey,
                alternateAllowedKey is TKey typedAlternateAllowedKey ? typedAlternateAllowedKey : default!);
        }

        if (catalog is not null && SupportsScalarNullKeyRoute() &&
            !IsAllowedScalarNullKey(allowedKey) &&
            (alternateAllowedKey is null || !IsAllowedScalarNullKey(alternateAllowedKey)) &&
            ContainsOptionalScalarNullIdentity(identity))
        {
            return true;
        }

        if (catalog is not null && SupportsNullKeyRoute())
        {
            if (!IsAllowedNullKey(allowedKey, NullKey.Null) &&
                (alternateAllowedKey is null || !IsAllowedNullKey(alternateAllowedKey, NullKey.Null)) &&
                ContainsOptionalNullKeyIdentity(NullKey.Null, identity))
            {
                return true;
            }

            if (!IsAllowedNullKey(allowedKey, NullKey.Empty) &&
                (alternateAllowedKey is null || !IsAllowedNullKey(alternateAllowedKey, NullKey.Empty)) &&
                ContainsOptionalNullKeyIdentity(NullKey.Empty, identity))
            {
                return true;
            }
        }

        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out TIdentity currentIdentity))
        {
            if (TupleComponentEquals(currentIdentity, identity) &&
                !IsAllowedOrdinaryKey(allowedKey, key) &&
                (alternateAllowedKey is null || !IsAllowedOrdinaryKey(alternateAllowedKey, key)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tests scalar-null route membership when optional route metadata exists, treating absent route metadata as empty.<br/>
    /// Standalone indexes can predate catalog key-state metadata, and the single-key guard should not turn absent optional route metadata into a hard insert failure.<br/>
    /// </summary>
    /// <param name="identity">The identity to test.</param>
    /// <returns><see langword="true"/> when the scalar-null route contains the identity.</returns>
    private bool ContainsOptionalScalarNullIdentity(TIdentity identity)
    {
        try
        {
            return ContainsScalarNullIdentity(identity);
        }
        catch (InvalidDataException ex) when (IsMissingKeyStateRouteMetadata(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Tests null or empty binary key-state membership when optional route metadata exists, treating absent route metadata as empty.<br/>
    /// This mirrors count-all's optional key-state behavior while keeping explicit key-state operations strict elsewhere.<br/>
    /// </summary>
    /// <param name="keyState">The key-state route to inspect.</param>
    /// <param name="identity">The identity to test.</param>
    /// <returns><see langword="true"/> when the selected route contains the identity.</returns>
    private bool ContainsOptionalNullKeyIdentity(NullKey keyState, TIdentity identity)
    {
        try
        {
            return ContainsNullKeyIdentity(keyState, identity);
        }
        catch (InvalidDataException ex) when (IsMissingKeyStateRouteMetadata(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Tests whether an allowed key token represents the scalar-null key-state route.<br/>
    /// This keeps scalar key-state comparisons out of the ordinary typed-key equality path.<br/>
    /// </summary>
    /// <param name="allowedKey">The allowed key token to inspect.</param>
    /// <returns><see langword="true"/> when the token is <see cref="ScalarNull.Null"/>.</returns>
    private static bool IsAllowedScalarNullKey(object? allowedKey)
        => allowedKey is ScalarNull scalarNull && scalarNull == ScalarNull.Null;

    /// <summary>
    /// Tests whether an allowed key token represents one concrete binary null-key route.<br/>
    /// Predicate-only states such as <see cref="NullKey.NullOrEmpty"/> are intentionally not accepted here.<br/>
    /// </summary>
    /// <param name="allowedKey">The allowed key token to inspect.</param>
    /// <param name="keyState">The concrete null-key route being compared.</param>
    /// <returns><see langword="true"/> when both tokens name the same concrete route.</returns>
    private static bool IsAllowedNullKey(object? allowedKey, NullKey keyState)
        => allowedKey is NullKey allowedNullKey && allowedNullKey == keyState;

    /// <summary>
    /// Tests whether an allowed key token is the same ordinary typed key as a stored tuple key.<br/>
    /// This comparison uses LibraDex tuple equality so byte-array keys compare by content instead of reference identity.<br/>
    /// </summary>
    /// <param name="allowedKey">The allowed key token to inspect.</param>
    /// <param name="key">The stored ordinary key to compare.</param>
    /// <returns><see langword="true"/> when both keys are equal under tuple equality.</returns>
    private static bool IsAllowedOrdinaryKey(object? allowedKey, TKey key)
        => allowedKey is TKey allowedOrdinaryKey && TupleComponentEquals(allowedOrdinaryKey, key);

    /// <summary>
    /// Tests one exact typed key/identity tuple for the public concurrent batch facade.<br/>
    /// The batch facade is outside this generic index type, so this narrow bridge keeps rekey semantics aligned with immediate rekey without exposing tuple membership as a broad public API.<br/>
    /// </summary>
    /// <param name="key">The key side of the tuple to test.<br/></param>
    /// <param name="identity">The identity side of the tuple to test.<br/></param>
    /// <returns><see langword="true"/> when the exact tuple exists.</returns>
    internal bool ContainsExactTupleForConcurrentBatch(TKey key, TIdentity identity)
    {
        return ContainsExactTuple(key, identity);
    }

    /// <summary>
    /// Gets whether this index owns a maintained exact reversed projection for concurrent batch maintenance.<br/>
    /// This narrow bridge lets the public concurrent batch avoid reflection or broad projection APIs while keeping projection ownership private to the index.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the index has a maintained exact reversed projection.</returns>
    internal bool HasExactReversedProjectionForConcurrentBatch()
    {
        return exactReversedProjection is not null;
    }

    /// <summary>
    /// Creates the maintained exact reversed projection key for concurrent batch staging.<br/>
    /// The transformation is the same one used by immediate and durability-batch projection maintenance.<br/>
    /// </summary>
    /// <param name="key">The original forward key.<br/></param>
    /// <returns>The reversed projection key.</returns>
    internal TKey CreateExactReversedProjectionKeyForConcurrentBatch(TKey key)
    {
        return CreateExactReversedProjectionKey(key);
    }

    /// <summary>
    /// Stages one exact reversed projection insert through a shared `FS32-8` concurrent batch context.<br/>
    /// The caller owns the context and publication boundary so primary and projection shelves can be flushed together when both remain shelf-local.<br/>
    /// </summary>
    /// <param name="writeContext">The shared writer context for the concurrent batch.<br/></param>
    /// <param name="key">The original forward key.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The staged projection insert result.</returns>
    internal LibraDexGenericInsertResult InsertExactReversedProjectionForConcurrentBatch(
        LibraDexWriteContext writeContext,
        TKey key,
        TIdentity identity)
    {
        if (exactReversedProjection is null)
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        return exactReversedProjection.InsertFixed32Scalar8ForConcurrentBatch(
            writeContext,
            CreateExactReversedProjectionKey(key),
            identity);
    }

    /// <summary>
    /// Stages one exact reversed projection delete through a shared `FS32-8` concurrent batch context.<br/>
    /// The caller owns the context and publication boundary so primary and projection shelves can be flushed together when both remain shelf-local.<br/>
    /// </summary>
    /// <param name="writeContext">The shared writer context for the concurrent batch.<br/></param>
    /// <param name="key">The original forward key.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    /// <returns>The staged projection delete result.</returns>
    internal LibraDexGenericDeleteResult DeleteExactReversedProjectionForConcurrentBatch(
        LibraDexWriteContext writeContext,
        TKey key,
        TIdentity identity)
    {
        if (exactReversedProjection is null)
        {
            return new LibraDexGenericDeleteResult(false, default);
        }

        return exactReversedProjection.DeleteFixed32Scalar8ForConcurrentBatch(
            writeContext,
            CreateExactReversedProjectionKey(key),
            identity);
    }

    /// <summary>
    /// Publishes this index's exact reversed projection insert through its immediate path after a concurrent batch had to publish primary staged work early.<br/>
    /// This fallback is used only for topology-changing projection cases; warmed shelf-local projection work stays in the shared writer context.<br/>
    /// </summary>
    /// <param name="key">The original forward key.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    internal void InsertExactReversedProjectionFallbackForConcurrentBatch(TKey key, TIdentity identity)
    {
        InsertExactReversedProjectionImmediate(key, identity);
    }

    /// <summary>
    /// Publishes this index's exact reversed projection delete through its immediate path after a concurrent batch had to publish primary staged work early.<br/>
    /// This fallback is used only for topology-changing projection cases; warmed shelf-local projection work stays in the shared writer context.<br/>
    /// </summary>
    /// <param name="key">The original forward key.<br/></param>
    /// <param name="identity">The identity associated with the key.<br/></param>
    internal void DeleteExactReversedProjectionFallbackForConcurrentBatch(TKey key, TIdentity identity)
    {
        DeleteExactReversedProjection(key, identity);
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
        if (session.IsDurabilityBatchActive &&
            !catalog.TryPrepareActiveIndexBatchMutation(SlotIndex))
            ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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
            RecordSingleKeyIdentityDelete(identity, key);
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
        if (CanUseInternalQueuedWriterForPrimaryMutation())
        {
            return GetOrCreateInternalQueuedWriter().Delete(key, identity).Deleted;
        }

        ulong encodedKey = EncodeKey8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Scalar8Scalar8RangePlan plan = session.BuildScalar8Scalar8RangePlan(RootRouterOffset, GetScalar8Scalar8Profile(), encodedKey, encodedKey);
        for (int i = 0; i < plan.ShelfCount; i++)
        {
            if (plan.TerminalShelfFlags[i] != 0)
            {
                return session.DeleteScalar8Scalar8TerminalIdentities(
                    plan.TerminalRootOffsets[i],
                    plan.Profile,
                    plan.TerminalScalarKeys[i],
                    encodedIdentity) == 1;
            }

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

    /// <summary>
    /// Gets whether ordinary primary `SS8-8` mutation can use the index-owned queued writer without changing batch or projection semantics.<br/>
    /// Active batches stay on the older explicit durability path; maintained reversed projections publish through a separate immediate projection update after the primary tuple is accepted.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal queued writer is safe for this mutation.</returns>
    private bool CanUseInternalQueuedWriterForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.SS88 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether ordinary primary `SS16-8` mutation can try a writer-context path without changing batch or projection semantics.<br/>
    /// The first `SS16-8` slice is narrower than `SS8-8`: warmed shelf-local insert/delete can stage independently, while route setup, split, transform, and public batch state stay on existing serialized paths.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal `SS16-8` writer context is eligible for this mutation.</returns>
    private bool CanUseInternalScalar16Scalar8WriterContextForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.SS168 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether ordinary primary `SS8-16` mutation can try a writer-context path without changing batch or projection semantics.<br/>
    /// The first `SS8-16` slice is bounded to warmed shelf-local insert/delete; route setup, split, transform, and public batch state stay on existing serialized paths.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal `SS8-16` writer context is eligible for this mutation.</returns>
    private bool CanUseInternalScalar8Scalar16WriterContextForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.SS816 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether ordinary primary `SS16-16` mutation can try a writer-context path without changing batch or projection semantics.<br/>
    /// The first `SS16-16` slice is bounded to warmed shelf-local insert/delete; route setup, split, transform, and public batch state stay on existing serialized paths.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal `SS16-16` writer context is eligible for this mutation.</returns>
    private bool CanUseInternalScalar16Scalar16WriterContextForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.SS1616 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether ordinary primary `FS32-8` mutation can try a writer-context path without changing batch or projection semantics.<br/>
    /// The first `FS32-8` slice is bounded to warmed shelf-local insert; route setup, split, transform, and public batch state stay on existing serialized paths.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal `FS32-8` writer context is eligible for this mutation.</returns>
    private bool CanUseInternalFixed32Scalar8WriterContextForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.FS328 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether ordinary primary `FS32-16` mutation can try a writer-context path without changing batch or projection semantics.<br/>
    /// The first `FS32-16` slice is bounded to warmed shelf-local insert; route setup, split, transform, and public batch state stay on existing serialized paths.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when the internal `FS32-16` writer context is eligible for this mutation.</returns>
    private bool CanUseInternalFixed32Scalar16WriterContextForPrimaryMutation()
    {
        return shape == LibraDexGenericScalarShape.FS3216 &&
            !BatchManager.IsEnabled &&
            !session.IsDurabilityBatchActive &&
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) != true;
    }

    /// <summary>
    /// Gets whether criteria-scoped value-route deletion can be expressed as captured exact tuples for the current shape.<br/>
    /// `SS8-8` uses the queued writer facade; `SS16-8` uses the first widened-key writer-context slice for warmed shelf-local exact deletes with serialized fallback for unsupported shapes.<br/>
    /// </summary>
    /// <returns><see langword="true"/> when exact tuple deletion should replace range rewrite for eligible primitives.</returns>
    private bool CanUseInternalExactTupleDeleteForPrimaryMutation()
    {
        return CanUseInternalQueuedWriterForPrimaryMutation() ||
            CanUseInternalScalar16Scalar8WriterContextForPrimaryMutation() ||
            CanUseInternalScalar8Scalar16WriterContextForPrimaryMutation() ||
            CanUseInternalScalar16Scalar16WriterContextForPrimaryMutation() ||
            CanUseInternalFixed32Scalar8WriterContextForPrimaryMutation() ||
            CanUseInternalFixed32Scalar16WriterContextForPrimaryMutation();
    }

    /// <summary>
    /// Inserts one `SS8-8` tuple through the encoded index's adaptive admission path.<br/>
    /// The encoded wrapper uses direct serialized insertion for the uncontended single-owner case and only admits the queued writer path when concurrent callers overlap on this public index handle.<br/>
    /// This keeps ordinary one-thread writes on the low-overhead path while preserving the existing shelf-local concurrent writer behavior when real overlap occurs.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.<br/></param>
    /// <param name="identity">The typed identity to insert.<br/></param>
    /// <returns>The generic insert result with the path attribution reported by the encoded insert.</returns>
    private LibraDexGenericInsertResult InsertScalar8Scalar8UsingAdaptiveAdmission(TKey key, TIdentity identity)
        => InsertScalar8Scalar8UsingAdaptiveAdmission(
            EncodeKey8(key),
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity));

    /// <summary>
    /// Inserts one already encoded `SS8-8` tuple through the same adaptive admission path used by typed keys.<br/>
    /// Fixed-width span callers use this overload after decoding the borrowed key into its persisted scalar lane.<br/>
    /// </summary>
    /// <param name="encodedKey">The encoded 8-byte key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded 8-byte identity lane.<br/></param>
    /// <returns>The generic insert result with the path attribution reported by the encoded insert.<br/></returns>
    private LibraDexGenericInsertResult InsertScalar8Scalar8UsingAdaptiveAdmission(
        ulong encodedKey,
        ulong encodedIdentity)
    {
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        Scalar8Scalar8EncodedInsertResult result = GetOrCreateInternalScalar8Scalar8Index().InsertEncoded(
            encodedKey,
            encodedIdentity,
            allowDuplicateKeys);
        LibraDexGenericInsertResult genericResult = new(
            result.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted,
            result.CreatedInitialShelfRoute,
            LibraDexOperationDiagnostics.FromDataKernel(result.RouteCreateCommit),
            LibraDexOperationDiagnostics.FromDataKernel(result.InsertCommit))
        {
            QueuedInsertPath = result.QueuedInsertPath
        };
        RecordSingleInsertPublication(genericResult);
        return genericResult;
    }

    /// <summary>
    /// Gets the encoded `SS8-8` wrapper used by default public generic `SS8-8` insertion.<br/>
    /// The wrapper is cached so its adaptive admission lock is shared by all one-shot inserts on this opened public index handle.<br/>
    /// </summary>
    /// <returns>The cached encoded wrapper for the public index.</returns>
    private Scalar8Scalar8Index GetOrCreateInternalScalar8Scalar8Index()
    {
        Scalar8Scalar8Index? encodedIndex = internalScalar8Scalar8Index;
        if (encodedIndex is not null)
        {
            return encodedIndex;
        }

        lock (internalScalar8Scalar8IndexSync)
        {
            encodedIndex = internalScalar8Scalar8Index;
            if (encodedIndex is not null)
            {
                return encodedIndex;
            }

            encodedIndex = new Scalar8Scalar8Index(
                session,
                new Scalar8Scalar8IndexHandle(RootRouterOffset, GetScalar8Scalar8Profile()),
                SlotIndex,
                Name,
                ownsSession: false);
            internalScalar8Scalar8Index = encodedIndex;
            return encodedIndex;
        }
    }

    /// <summary>
    /// Inserts one `SS16-8` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; topology-changing cases fall back to the direct serialized topology path so the first transfer does not broaden route-publication semantics.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.</param>
    /// <param name="identity">The typed identity to insert.</param>
    /// <returns>The generic insert result with writer-context path attribution when the shelf-local path succeeds.</returns>
    private LibraDexGenericInsertResult InsertScalar16Scalar8UsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        return InsertScalar16Scalar8UsingWriterContextOrFallback(keyHigh, keyLow, encodedIdentity);
    }

    /// <summary>
    /// Inserts one already encoded `SS16-8` tuple through the ordinary writer-context and topology-fallback pipeline.<br/>
    /// Fixed-width span callers use this overload after decoding the borrowed key into two persisted scalar lanes.<br/>
    /// </summary>
    /// <param name="keyHigh">The encoded high 8-byte key lane.<br/></param>
    /// <param name="keyLow">The encoded low 8-byte key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded 8-byte identity lane.<br/></param>
    /// <returns>The generic insert result with writer-context or fallback attribution.<br/></returns>
    private LibraDexGenericInsertResult InsertScalar16Scalar8UsingWriterContextOrFallback(
        ulong keyHigh,
        ulong keyLow,
        ulong encodedIdentity)
    {
        Scalar16Scalar8Profile profile = GetScalar16Scalar8Profile();
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        byte rootPrefix = (byte)(keyHigh >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            if (session.TryInsertScalar16Scalar8DirectColdRootRoute(
                RootRouterOffset,
                rootPrefix,
                profile,
                keyHigh,
                keyLow,
                encodedIdentity,
                allowDuplicateKeys,
                out Scalar16Scalar8RoutedInsertResult coldRouteResult))
            {
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    true,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(coldRouteResult.Commit))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }

            return InsertScalar16Scalar8UsingSerializedTopologyFallback(
                profile,
                keyHigh,
                keyLow,
                encodedIdentity,
                allowDuplicateKeys);
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar16Scalar8WriteContext();
            try
            {
                Scalar16Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar8NoSplitForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    encodedIdentity,
                    allowDuplicateKeys,
                    maxRouterHops: Scalar16Scalar8Layout.KeySize + 1);

                if (result.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                {
                    session.AbortScalar16Scalar8WriteContext(writeContext);
                    return new LibraDexGenericInsertResult(false, false, default, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar16Scalar8WriteContext(writeContext);
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    false,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }
            catch (LibraDexWriteContextScalar16Scalar8ShelfOwnershipException ex)
            {
                session.AbortScalar16Scalar8WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortScalar16Scalar8WriteContext(writeContext);
                if (session.TrySplitScalar16Scalar8DirectParentRoute(
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    encodedIdentity,
                    maxRouterHops: Scalar16Scalar8Layout.KeySize + 1,
                    out Scalar16Scalar8RoutedInsertResult parentRouteSplitResult))
                {
                    bool inserted = parentRouteSplitResult.InsertResult == Scalar16Scalar8InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(parentRouteSplitResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                if (session.TryTransformScalar16Scalar8DirectShelf(
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    encodedIdentity,
                    maxRouterHops: Scalar16Scalar8Layout.KeySize + 1,
                    out Scalar16Scalar8RoutedInsertResult shelfTransformResult))
                {
                    bool inserted = shelfTransformResult.InsertResult == Scalar16Scalar8InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(shelfTransformResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                return InsertScalar16Scalar8UsingSerializedTopologyFallback(
                    profile,
                    keyHigh,
                    keyLow,
                    encodedIdentity,
                    allowDuplicateKeys);
            }
        }
    }

    /// <summary>
    /// Inserts one `SS16-8` tuple through the serialized topology path after writer-context staging rejects the route as non-shelf-local.<br/>
    /// Full-shelf splits and transforms are still serialized for topology safety, but the fallback no longer uses session batch state for a one-item direct insert.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-8` shelf profile for the routed index.<br/></param>
    /// <param name="keyHigh">The encoded high key half.<br/></param>
    /// <param name="keyLow">The encoded low key half.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys are accepted by this index.<br/></param>
    /// <returns>The generic insert result with serialized-fallback attribution.</returns>
    private LibraDexGenericInsertResult InsertScalar16Scalar8UsingSerializedTopologyFallback(
        Scalar16Scalar8Profile profile,
        ulong keyHigh,
        ulong keyLow,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        Scalar16Scalar8RoutedInsertResult routedResult = session.InsertScalar16Scalar8DirectSerializedTopologyFallback(
            RootRouterOffset,
            profile,
            keyHigh,
            keyLow,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: Scalar16Scalar8Layout.KeySize + 1);
        bool inserted = routedResult.InsertResult == Scalar16Scalar8InsertResult.Inserted;
        LibraDexGenericInsertResult genericResult = new(
            inserted,
            false,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(routedResult.Commit))
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                : Scalar8Scalar8QueuedInsertPath.None
        };
        if (inserted)
        {
            RecordSingleInsertPublication(genericResult);
        }

        return genericResult;
    }

    /// <summary>
    /// Inserts one `SS8-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; topology-changing cases fall back to the direct serialized topology path for this first wide-identity slice.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.</param>
    /// <param name="identity">The typed identity to insert.</param>
    /// <returns>The generic insert result with writer-context path attribution when the shelf-local path succeeds.</returns>
    private LibraDexGenericInsertResult InsertScalar8Scalar16UsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        ulong encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(key);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar8Scalar16Profile profile = GetScalar8Scalar16Profile();
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        byte rootPrefix = (byte)(encodedKey >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            if (session.TryInsertScalar8Scalar16DirectColdRootRoute(
                RootRouterOffset,
                rootPrefix,
                profile,
                encodedKey,
                identityHigh,
                identityLow,
                allowDuplicateKeys,
                out Scalar8Scalar16RoutedInsertResult coldRouteResult))
            {
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    true,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(coldRouteResult.Commit))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }

            return InsertScalar8Scalar16UsingSerializedTopologyFallback(
                profile,
                encodedKey,
                identityHigh,
                identityLow,
                allowDuplicateKeys);
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar8Scalar16WriteContext();
            try
            {
                Scalar8Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar16NoSplitForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    encodedKey,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys,
                    maxRouterHops: 8);

                if (result.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                {
                    session.AbortScalar8Scalar16WriteContext(writeContext);
                    return new LibraDexGenericInsertResult(false, false, default, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar8Scalar16WriteContext(writeContext);
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    false,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }
            catch (LibraDexWriteContextScalar8Scalar16ShelfOwnershipException ex)
            {
                session.AbortScalar8Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortScalar8Scalar16WriteContext(writeContext);
                if (session.TrySplitScalar8Scalar16DirectParentRoute(
                    RootRouterOffset,
                    profile,
                    encodedKey,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 8,
                    out Scalar8Scalar16RoutedInsertResult parentRouteSplitResult))
                {
                    bool inserted = parentRouteSplitResult.InsertResult == Scalar8Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(parentRouteSplitResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                if (session.TryTransformScalar8Scalar16DirectShelf(
                    RootRouterOffset,
                    profile,
                    encodedKey,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 8,
                    out Scalar8Scalar16RoutedInsertResult shelfTransformResult))
                {
                    bool inserted = shelfTransformResult.InsertResult == Scalar8Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(shelfTransformResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                return InsertScalar8Scalar16UsingSerializedTopologyFallback(
                    profile,
                    encodedKey,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys);
            }
        }
    }

    /// <summary>
    /// Inserts one `SS8-16` tuple through the serialized topology path after writer-context staging rejects the route as non-shelf-local.<br/>
    /// Full-shelf splits and transforms are still serialized for topology safety, but the fallback no longer uses session batch state for a one-item direct insert.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-16` shelf profile for the routed index.<br/></param>
    /// <param name="encodedKey">The encoded scalar key.<br/></param>
    /// <param name="identityHigh">The encoded high identity half.<br/></param>
    /// <param name="identityLow">The encoded low identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys are accepted by this index.<br/></param>
    /// <returns>The generic insert result with serialized-fallback attribution.</returns>
    private LibraDexGenericInsertResult InsertScalar8Scalar16UsingSerializedTopologyFallback(
        Scalar8Scalar16Profile profile,
        ulong encodedKey,
        ulong identityHigh,
        ulong identityLow,
        bool allowDuplicateKeys)
    {
        Scalar8Scalar16RoutedInsertResult routedResult = session.InsertScalar8Scalar16DirectSerializedTopologyFallback(
            RootRouterOffset,
            profile,
            encodedKey,
            identityHigh,
            identityLow,
            allowDuplicateKeys,
            maxRouterHops: 8);
        bool inserted = routedResult.InsertResult == Scalar8Scalar16InsertResult.Inserted;
        LibraDexGenericInsertResult genericResult = new(
            inserted,
            false,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(routedResult.Commit))
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                : Scalar8Scalar8QueuedInsertPath.None
        };
        if (inserted)
        {
            RecordSingleInsertPublication(genericResult);
        }

        return genericResult;
    }

    /// <summary>
    /// Inserts one `SS16-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; topology-changing cases fall back to the direct serialized topology path for this first wide-key/wide-identity slice.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.</param>
    /// <param name="identity">The typed identity to insert.</param>
    /// <returns>The generic insert result with writer-context path attribution when the shelf-local path succeeds.</returns>
    private LibraDexGenericInsertResult InsertScalar16Scalar16UsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar16Scalar16Profile profile = GetScalar16Scalar16Profile();
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        byte rootPrefix = (byte)(keyHigh >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            if (session.TryInsertScalar16Scalar16DirectColdRootRoute(
                RootRouterOffset,
                rootPrefix,
                profile,
                keyHigh,
                keyLow,
                identityHigh,
                identityLow,
                allowDuplicateKeys,
                out Scalar16Scalar16RoutedInsertResult coldRouteResult))
            {
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    true,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(coldRouteResult.Commit))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }

            return InsertScalar16Scalar16UsingSerializedTopologyFallback(
                profile,
                keyHigh,
                keyLow,
                identityHigh,
                identityLow,
                allowDuplicateKeys);
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar16Scalar16WriteContext();
            try
            {
                Scalar16Scalar16RoutedInsertResult result = session.InsertWalkedRoutedScalar16Scalar16NoSplitForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys,
                    maxRouterHops: Scalar16Scalar16Layout.KeySize + 1);

                if (result.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                {
                    session.AbortScalar16Scalar16WriteContext(writeContext);
                    return new LibraDexGenericInsertResult(false, false, default, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar16Scalar16WriteContext(writeContext);
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    false,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }
            catch (LibraDexWriteContextScalar16Scalar16ShelfOwnershipException ex)
            {
                session.AbortScalar16Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortScalar16Scalar16WriteContext(writeContext);
                if (session.TrySplitScalar16Scalar16DirectParentRoute(
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    identityHigh,
                    identityLow,
                    maxRouterHops: Scalar16Scalar16Layout.KeySize + 1,
                    out Scalar16Scalar16RoutedInsertResult parentRouteSplitResult))
                {
                    bool inserted = parentRouteSplitResult.InsertResult == Scalar16Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(parentRouteSplitResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                if (session.TryTransformScalar16Scalar16DirectShelf(
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    identityHigh,
                    identityLow,
                    maxRouterHops: Scalar16Scalar16Layout.KeySize + 1,
                    out Scalar16Scalar16RoutedInsertResult shelfTransformResult))
                {
                    bool inserted = shelfTransformResult.InsertResult == Scalar16Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(shelfTransformResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                return InsertScalar16Scalar16UsingSerializedTopologyFallback(
                    profile,
                    keyHigh,
                    keyLow,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys);
            }
        }
    }

    /// <summary>
    /// Inserts one `SS16-16` tuple through the serialized topology path after writer-context staging rejects the route as non-shelf-local.<br/>
    /// Full-shelf splits and transforms are still serialized for topology safety, but the fallback no longer uses session batch state for a one-item direct insert.<br/>
    /// </summary>
    /// <param name="profile">The `SS16-16` shelf profile for the routed index.<br/></param>
    /// <param name="keyHigh">The encoded high key half.<br/></param>
    /// <param name="keyLow">The encoded low key half.<br/></param>
    /// <param name="identityHigh">The encoded high identity half.<br/></param>
    /// <param name="identityLow">The encoded low identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys are accepted by this index.<br/></param>
    /// <returns>The generic insert result with serialized-fallback attribution.</returns>
    private LibraDexGenericInsertResult InsertScalar16Scalar16UsingSerializedTopologyFallback(
        Scalar16Scalar16Profile profile,
        ulong keyHigh,
        ulong keyLow,
        ulong identityHigh,
        ulong identityLow,
        bool allowDuplicateKeys)
    {
        Scalar16Scalar16RoutedInsertResult routedResult = session.InsertScalar16Scalar16DirectSerializedTopologyFallback(
            RootRouterOffset,
            profile,
            keyHigh,
            keyLow,
            identityHigh,
            identityLow,
            allowDuplicateKeys,
            maxRouterHops: Scalar16Scalar16Layout.KeySize + 1);
        bool inserted = routedResult.InsertResult == Scalar16Scalar16InsertResult.Inserted;
        LibraDexGenericInsertResult genericResult = new(
            inserted,
            false,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(routedResult.Commit))
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                : Scalar8Scalar8QueuedInsertPath.None
        };
        if (inserted)
        {
            RecordSingleInsertPublication(genericResult);
        }

        return genericResult;
    }

    /// <summary>
    /// Inserts one `FS32-8` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; topology-changing cases fall back to the direct serialized topology path for this first 32-byte-key slice.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.</param>
    /// <param name="identity">The typed identity to insert.</param>
    /// <returns>The generic insert result with writer-context path attribution when the shelf-local path succeeds.</returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar8UsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        return InsertFixed32Scalar8UsingWriterContextOrFallback(
            key0,
            key1,
            key2,
            key3,
            encodedIdentity);
    }

    /// <summary>
    /// Inserts one already encoded `FS32-8` tuple through the ordinary writer-context and topology-fallback pipeline.<br/>
    /// Fixed-width span callers use this overload after decoding the borrowed key into four persisted scalar lanes.<br/>
    /// </summary>
    /// <param name="key0">The first encoded 8-byte key lane.<br/></param>
    /// <param name="key1">The second encoded 8-byte key lane.<br/></param>
    /// <param name="key2">The third encoded 8-byte key lane.<br/></param>
    /// <param name="key3">The fourth encoded 8-byte key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded 8-byte identity lane.<br/></param>
    /// <returns>The generic insert result with writer-context or fallback attribution.<br/></returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar8UsingWriterContextOrFallback(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity)
    {
        Fixed32Scalar8Profile profile = GetFixed32Scalar8Profile();
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        byte rootPrefix = (byte)(key0 >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            if (session.TryInsertFixed32Scalar8DirectColdRootRoute(
                RootRouterOffset,
                rootPrefix,
                profile,
                key0,
                key1,
                key2,
                key3,
                encodedIdentity,
                allowDuplicateKeys,
                out Fixed32Scalar8RoutedInsertResult coldRouteResult))
            {
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    true,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(coldRouteResult.Commit))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }

            return InsertFixed32Scalar8UsingSerializedTopologyFallback(
                profile,
                key0,
                key1,
                key2,
                key3,
                encodedIdentity,
                allowDuplicateKeys);
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixed32Scalar8WriteContext();
            try
            {
                Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8NoSplitForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    encodedIdentity,
                    allowDuplicateKeys,
                    maxRouterHops: 32);

                if (result.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                {
                    session.AbortFixed32Scalar8WriteContext(writeContext);
                    return new LibraDexGenericInsertResult(false, false, default, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishFixed32Scalar8WriteContext(writeContext);
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    false,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }
            catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException ex)
            {
                session.AbortFixed32Scalar8WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortFixed32Scalar8WriteContext(writeContext);
                if (session.TrySplitFixed32Scalar8DirectParentRoute(
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    encodedIdentity,
                    maxRouterHops: 32,
                    out Fixed32Scalar8RoutedInsertResult parentRouteSplitResult))
                {
                    bool inserted = parentRouteSplitResult.InsertResult == Fixed32Scalar8InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(parentRouteSplitResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                if (session.TryTransformFixed32Scalar8DirectShelf(
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    encodedIdentity,
                    maxRouterHops: 32,
                    out Fixed32Scalar8RoutedInsertResult shelfTransformResult))
                {
                    bool inserted = shelfTransformResult.InsertResult == Fixed32Scalar8InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(shelfTransformResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                return InsertFixed32Scalar8UsingSerializedTopologyFallback(
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    encodedIdentity,
                    allowDuplicateKeys);
            }
        }
    }

    /// <summary>
    /// Inserts one `FS32-8` tuple through the serialized topology path after writer-context staging rejects the route as non-shelf-local.<br/>
    /// Full-shelf splits and transforms are still serialized for topology safety, but the fallback no longer uses session batch state for a one-item direct insert.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-8` shelf profile for the routed index.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded scalar identity.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys are accepted by this index.<br/></param>
    /// <returns>The generic insert result with serialized-fallback attribution when insertion succeeds.</returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar8UsingSerializedTopologyFallback(
        Fixed32Scalar8Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        bool allowDuplicateKeys)
    {
        Fixed32Scalar8RoutedInsertResult routedResult = session.InsertFixed32Scalar8DirectSerializedTopologyFallback(
            RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            key3,
            encodedIdentity,
            allowDuplicateKeys,
            maxRouterHops: 32);
        bool inserted = routedResult.InsertResult == Fixed32Scalar8InsertResult.Inserted;
        LibraDexGenericInsertResult genericResult = new(
            inserted,
            false,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(routedResult.Commit))
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                : Scalar8Scalar8QueuedInsertPath.None
        };
        if (inserted)
        {
            RecordSingleInsertPublication(genericResult);
        }

        return genericResult;
    }

    /// <summary>
    /// Stages one `FS32-8` tuple in a caller-owned concurrent batch writer context.<br/>
    /// The method accepts only warmed shelf-local inserts; topology-changing cases throw so the batch can publish current staged work and route the single operation through the existing topology-safe fallback.<br/>
    /// </summary>
    /// <param name="writeContext">The shared concurrent batch writer context.<br/></param>
    /// <param name="key">The typed key to insert.<br/></param>
    /// <param name="identity">The typed identity to insert.<br/></param>
    /// <returns>The staged insert result with writer-context attribution when a tuple was added.</returns>
    internal LibraDexGenericInsertResult InsertFixed32Scalar8ForConcurrentBatch(
        LibraDexWriteContext writeContext,
        TKey key,
        TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8NoSplitForWriteContext(
            writeContext,
            RootRouterOffset,
            GetFixed32Scalar8Profile(),
            key0,
            key1,
            key2,
            key3,
            encodedIdentity,
            KeyContract == IndexKeys.NonUnique,
            maxRouterHops: 32);
        bool inserted = result.InsertResult == Fixed32Scalar8InsertResult.Inserted;
        return new LibraDexGenericInsertResult(
            inserted,
            false,
            default,
            default)
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.WriterContext
                : Scalar8Scalar8QueuedInsertPath.None
        };
    }

    /// <summary>
    /// Stages one exact `FS32-8` tuple delete in a caller-owned concurrent batch writer context.<br/>
    /// The method accepts only warmed shelf-local deletes; unsupported route shapes throw so the batch can publish current staged work and route the single operation through the existing fallback.<br/>
    /// </summary>
    /// <param name="writeContext">The shared concurrent batch writer context.<br/></param>
    /// <param name="key">The typed key side of the tuple to delete.<br/></param>
    /// <param name="identity">The typed identity side of the tuple to delete.<br/></param>
    /// <returns>The staged delete result with writer-context attribution when a tuple was removed.</returns>
    internal LibraDexGenericDeleteResult DeleteFixed32Scalar8ForConcurrentBatch(
        LibraDexWriteContext writeContext,
        TKey key,
        TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        bool deleted = session.DeleteWalkedRoutedFixed32Scalar8ExactForWriteContext(
            writeContext,
            RootRouterOffset,
            GetFixed32Scalar8Profile(),
            key0,
            key1,
            key2,
            key3,
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity),
            maxRouterHops: 32);
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
    /// Creates a caller-owned `FS32-8` concurrent batch writer context.<br/>
    /// The context can stage primary and maintained projection shelves together because ownership is claimed by physical shelf offset.<br/>
    /// </summary>
    /// <returns>A new `FS32-8` writer context.</returns>
    internal LibraDexWriteContext BeginFixed32Scalar8ConcurrentBatchContext()
    {
        return session.BeginFixed32Scalar8WriteContext();
    }

    /// <summary>
    /// Publishes a caller-owned `FS32-8` concurrent batch writer context.<br/>
    /// Publication is serialized at the DataKernel boundary while the expensive shelf mutation work has already happened outside that boundary.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to publish.<br/></param>
    /// <returns>The publication telemetry.</returns>
    internal DataKernelCommitTelemetry PublishFixed32Scalar8ConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        return session.PublishFixed32Scalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Aborts a caller-owned `FS32-8` concurrent batch writer context.<br/>
    /// Staged shelf bytes are discarded and physical shelf ownership claims are released without writing to DataKernel.<br/>
    /// </summary>
    /// <param name="writeContext">The writer context to abort.<br/></param>
    internal void AbortFixed32Scalar8ConcurrentBatchContext(LibraDexWriteContext writeContext)
    {
        session.AbortFixed32Scalar8WriteContext(writeContext);
    }

    /// <summary>
    /// Inserts one `FS32-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; topology-changing cases fall back to the direct serialized topology path for this first 32-byte-key/wide-identity slice.<br/>
    /// </summary>
    /// <param name="key">The typed key to insert.</param>
    /// <param name="identity">The typed identity to insert.</param>
    /// <returns>The generic insert result with writer-context path attribution when the shelf-local path succeeds.</returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar16UsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Fixed32Scalar16Profile profile = GetFixed32Scalar16Profile();
        bool allowDuplicateKeys = KeyContract == IndexKeys.NonUnique;
        byte rootPrefix = (byte)(key0 >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            if (session.TryInsertFixed32Scalar16DirectColdRootRoute(
                RootRouterOffset,
                rootPrefix,
                profile,
                key0,
                key1,
                key2,
                key3,
                identityHigh,
                identityLow,
                allowDuplicateKeys,
                out Fixed32Scalar16RoutedInsertResult coldRouteResult))
            {
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    true,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(coldRouteResult.Commit))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }

            return InsertFixed32Scalar16UsingSerializedTopologyFallback(
                profile,
                key0,
                key1,
                key2,
                key3,
                identityHigh,
                identityLow,
                allowDuplicateKeys);
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixed32Scalar16WriteContext();
            try
            {
                Fixed32Scalar16RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar16NoSplitForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys,
                    maxRouterHops: 32);

                if (result.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                {
                    session.AbortFixed32Scalar16WriteContext(writeContext);
                    return new LibraDexGenericInsertResult(false, false, default, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishFixed32Scalar16WriteContext(writeContext);
                LibraDexGenericInsertResult genericResult = new(
                    true,
                    false,
                    default,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
                RecordSingleInsertPublication(genericResult);
                return genericResult;
            }
            catch (LibraDexWriteContextFixed32Scalar16ShelfOwnershipException ex)
            {
                session.AbortFixed32Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortFixed32Scalar16WriteContext(writeContext);
                if (session.TrySplitFixed32Scalar16DirectParentRoute(
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 32,
                    out Fixed32Scalar16RoutedInsertResult parentRouteSplitResult))
                {
                    bool inserted = parentRouteSplitResult.InsertResult == Fixed32Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(parentRouteSplitResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                if (session.TryTransformFixed32Scalar16DirectShelf(
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 32,
                    out Fixed32Scalar16RoutedInsertResult shelfTransformResult))
                {
                    bool inserted = shelfTransformResult.InsertResult == Fixed32Scalar16InsertResult.Inserted;
                    LibraDexGenericInsertResult genericResult = new(
                        inserted,
                        false,
                        default,
                        LibraDexOperationDiagnostics.FromDataKernel(shelfTransformResult.Commit))
                    {
                        QueuedInsertPath = inserted
                            ? Scalar8Scalar8QueuedInsertPath.NarrowTopologyPublisher
                            : Scalar8Scalar8QueuedInsertPath.None
                    };
                    if (inserted)
                    {
                        RecordSingleInsertPublication(genericResult);
                    }

                    return genericResult;
                }

                return InsertFixed32Scalar16UsingSerializedTopologyFallback(
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys);
            }
        }
    }

    /// <summary>
    /// Inserts one `FS32-16` tuple through the serialized topology path after writer-context staging rejects the route as non-shelf-local.<br/>
    /// Full-shelf splits and transforms are still serialized for topology safety, but the fallback no longer uses session batch state for a one-item direct insert.<br/>
    /// </summary>
    /// <param name="profile">The `FS32-16` shelf profile for the routed index.<br/></param>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="identityHigh">The encoded high identity half.<br/></param>
    /// <param name="identityLow">The encoded low identity half.<br/></param>
    /// <param name="allowDuplicateKeys">Whether duplicate keys are accepted by this index.<br/></param>
    /// <returns>The generic insert result with serialized-fallback attribution when insertion succeeds.</returns>
    private LibraDexGenericInsertResult InsertFixed32Scalar16UsingSerializedTopologyFallback(
        Fixed32Scalar16Profile profile,
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong identityHigh,
        ulong identityLow,
        bool allowDuplicateKeys)
    {
        Fixed32Scalar16RoutedInsertResult routedResult = session.InsertFixed32Scalar16DirectSerializedTopologyFallback(
            RootRouterOffset,
            profile,
            key0,
            key1,
            key2,
            key3,
            identityHigh,
            identityLow,
            allowDuplicateKeys,
            maxRouterHops: 32);
        bool inserted = routedResult.InsertResult == Fixed32Scalar16InsertResult.Inserted;
        LibraDexGenericInsertResult genericResult = new(
            inserted,
            false,
            default,
            LibraDexOperationDiagnostics.FromDataKernel(routedResult.Commit))
        {
            QueuedInsertPath = inserted
                ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                : Scalar8Scalar8QueuedInsertPath.None
        };
        if (inserted)
        {
            RecordSingleInsertPublication(genericResult);
        }

        return genericResult;
    }

    /// <summary>
    /// Records stats for one immediately-published queued insert so ordinary direct `Insert` counters remain aligned with the legacy one-item batch path.<br/>
    /// The queued writer publishes inside the insert call, so this helper records both the insert outcome and one publication boundary without requiring a temporary batch object.<br/>
    /// </summary>
    /// <param name="result">The queued insert result to record.</param>
    private void RecordSingleInsertPublication(LibraDexGenericInsertResult result)
    {
        Stats.RecordInsert(result);
        Catalog.Stats.RecordInsert(result);
        LibraDexGenericBatchCommitResult commitResult = new(
            AttemptedInsertCount: 1,
            InsertedCount: result.Inserted ? 1 : 0,
            InitialShelfRouteCreateCount: result.CreatedInitialShelfRoute ? 1 : 0,
            DeferredCommitRequests: result.Inserted ? 1 : 0,
            result.InsertDiagnostics,
            default);
        Stats.RecordCommit(commitResult);
        Catalog.Stats.RecordCommit(commitResult);
    }

    /// <summary>
    /// Gets whether a condition primitive can be safely rewritten as captured value-route tuples followed by exact tuple deletes.<br/>
    /// `All`, scalar-null, and key-state primitives retain their specialized route deletion path because they can include metadata-backed null routes that are not ordinary fixed-scalar value shelves.<br/>
    /// </summary>
    /// <param name="request">The normalized condition primitive request.</param>
    /// <returns><see langword="true"/> when exact tuple deletion can replace the older range rewrite for this primitive.</returns>
    private bool CanDeleteIdentityPrimitiveUsingQueuedExactTuples(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.CriteriaKind == LibraDexCriteriaKind.ScalarNull)
        {
            return RequireScalarNullState(request.Values) == ScalarNull.NonNull;
        }

        return request.CriteriaKind is not LibraDexCriteriaKind.All and
            not LibraDexCriteriaKind.KeyState;
    }

    /// <summary>
    /// Deletes captured value-route tuples through the shape's exact tuple bridge.<br/>
    /// This keeps broad value-route condition deletes on the same no-ceremony mutation path as direct exact deletes while leaving scalar/key-state route mutation to its dedicated route gate.<br/>
    /// </summary>
    /// <param name="request">The primitive request used to capture value-route tuples.</param>
    /// <returns>The number of exact value-route tuples removed.</returns>
    private long DeleteIdentityPrimitiveValueTuplesUsingExactTupleBridge(LibraDexIdentityPrimitiveRequest request)
    {
        IReadOnlyList<LibraDexObjectTuple> tuples = ExecuteTuplePrimitive(request);
        long deleted = 0;
        for (int i = 0; i < tuples.Count; i++)
        {
            LibraDexObjectTuple tuple = tuples[i];
            if (((IIdentityExactTupleMutator)this).DeleteExactTuple(tuple.Key, tuple.Identity))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Gets the lazily-created queued writer used by default primary `SS8-8` mutation paths.<br/>
    /// The writer is cached per opened index handle so direct inserts, direct exact deletes, and condition-driven exact tuple deletion share the same admission facade without caller ceremony.<br/>
    /// </summary>
    /// <returns>The cached queued writer for this index.</returns>
    private LibraDexQueuedWriter<TKey, TIdentity> GetOrCreateInternalQueuedWriter()
    {
        LibraDexQueuedWriter<TKey, TIdentity>? writer = internalQueuedWriter;
        if (writer is not null)
        {
            return writer;
        }

        lock (internalQueuedWriterSync)
        {
            writer = internalQueuedWriter;
            if (writer is not null)
            {
                return writer;
            }

            writer = BeginQueuedWriterAfterSingleKeyBatchCheck(LibraDexConcurrencyOptions.QueuedWriter);
            internalQueuedWriter = writer;
            return writer;
        }
    }

    private bool DeleteExactScalar16Scalar8Tuple(TKey key, TIdentity identity)
    {
        if (CanUseInternalScalar16Scalar8WriterContextForPrimaryMutation())
        {
            return DeleteExactScalar16Scalar8TupleUsingWriterContextOrFallback(key, identity).Deleted;
        }

        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
    }

    /// <summary>
    /// Deletes one exact tuple through the `SS16-8` queued-writer facade and reports the path actually used.<br/>
    /// This keeps facade diagnostics aligned with direct delete behavior instead of inferring writer-context attribution from a Boolean result.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.<br/></param>
    /// <param name="identity">The typed identity side of the tuple to delete.<br/></param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    internal LibraDexGenericDeleteResult DeleteScalar16Scalar8TupleForQueuedWriterFacade(TKey key, TIdentity identity)
    {
        if (!CanUseInternalScalar16Scalar8WriterContextForPrimaryMutation())
        {
            bool deleted = DeleteExactScalar16Scalar8Tuple(key, identity);
            return new LibraDexGenericDeleteResult(
                deleted,
                default)
            {
                QueuedInsertPath = deleted
                    ? Scalar8Scalar8QueuedInsertPath.SerializedFallback
                    : Scalar8Scalar8QueuedInsertPath.None
            };
        }

        return DeleteExactScalar16Scalar8TupleUsingWriterContextOrFallback(key, identity);
    }

    /// <summary>
    /// Deletes one exact tuple through the queued-writer facade and reports the path actually used by the current supported shape.<br/>
    /// Shape-specific dispatch keeps facade diagnostics aligned with direct delete behavior instead of inferring writer-context attribution from a Boolean result.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.<br/></param>
    /// <param name="identity">The typed identity side of the tuple to delete.<br/></param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    internal LibraDexGenericDeleteResult DeleteTupleForQueuedWriterFacade(TKey key, TIdentity identity)
    {
        LibraDexGenericDeleteResult result = shape switch
        {
            LibraDexGenericScalarShape.SS168 => DeleteScalar16Scalar8TupleForQueuedWriterFacade(key, identity),
            LibraDexGenericScalarShape.SS816 => DeleteExactScalar8Scalar16TupleUsingWriterContextOrFallback(key, identity),
            LibraDexGenericScalarShape.SS1616 => DeleteExactScalar16Scalar16TupleUsingWriterContextOrFallback(key, identity),
            LibraDexGenericScalarShape.FS328 => DeleteExactFixed32Scalar8TupleUsingWriterContextOrFallback(key, identity),
            LibraDexGenericScalarShape.FS3216 => DeleteExactFixed32Scalar16TupleUsingWriterContextOrFallback(key, identity),
            _ => throw new NotSupportedException($"The generic queued writer delete currently supports SS16-8, SS8-16, SS16-16, FS32-8, and FS32-16 direct-shape dispatch, not {shape}.")
        };
        if (result.Deleted)
        {
            DeleteExactReversedProjection(key, identity);
            RecordDelete();
        }

        return result;
    }

    /// <summary>
    /// Deletes one exact `SS16-8` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; unsupported route shapes fall back to the existing exact range-reader delete path.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.</param>
    /// <param name="identity">The typed identity side of the tuple to delete.</param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    private LibraDexGenericDeleteResult DeleteExactScalar16Scalar8TupleUsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        Scalar16Scalar8Profile profile = GetScalar16Scalar8Profile();
        byte rootPrefix = (byte)(keyHigh >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            return new LibraDexGenericDeleteResult(false, default)
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
            };
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar16Scalar8WriteContext();
            try
            {
                bool deleted = session.DeleteWalkedRoutedScalar16Scalar8ExactForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    encodedIdentity,
                    maxRouterHops: Scalar16Scalar8Layout.KeySize + 1);
                if (!deleted)
                {
                    session.AbortScalar16Scalar8WriteContext(writeContext);
                    return new LibraDexGenericDeleteResult(false, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar16Scalar8WriteContext(writeContext);
                return new LibraDexGenericDeleteResult(
                    true,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
            }
            catch (LibraDexWriteContextScalar16Scalar8ShelfOwnershipException ex)
            {
                session.AbortScalar16Scalar8WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                session.AbortScalar16Scalar8WriteContext(writeContext);
                using Scalar16Scalar8RangeReader reader = OpenScalar16Scalar8RangeReader(key, key);
                bool deleted = reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
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

    private bool DeleteExactScalar8Scalar16Tuple(TKey key, TIdentity identity)
    {
        if (CanUseInternalScalar8Scalar16WriterContextForPrimaryMutation())
        {
            return DeleteExactScalar8Scalar16TupleUsingWriterContextOrFallback(key, identity).Deleted;
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    /// <summary>
    /// Deletes one exact `SS8-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; unsupported route shapes fall back to the existing exact range-reader delete path.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.</param>
    /// <param name="identity">The typed identity side of the tuple to delete.</param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    private LibraDexGenericDeleteResult DeleteExactScalar8Scalar16TupleUsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        ulong encodedKey = LibraDexGenericScalarCodec<TKey>.Encode8(key);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar8Scalar16Profile profile = GetScalar8Scalar16Profile();
        byte rootPrefix = (byte)(encodedKey >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            return new LibraDexGenericDeleteResult(false, default)
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
            };
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar8Scalar16WriteContext();
            try
            {
                bool deleted = session.DeleteWalkedRoutedScalar8Scalar16ExactForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    encodedKey,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 8);
                if (!deleted)
                {
                    session.AbortScalar8Scalar16WriteContext(writeContext);
                    return new LibraDexGenericDeleteResult(false, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar8Scalar16WriteContext(writeContext);
                return new LibraDexGenericDeleteResult(
                    true,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
            }
            catch (LibraDexWriteContextScalar8Scalar16ShelfOwnershipException ex)
            {
                session.AbortScalar8Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortScalar8Scalar16WriteContext(writeContext);
                using Scalar8Scalar16RangeReader reader = OpenScalar8Scalar16RangeReader(key, key);
                bool deleted = reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
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

    private bool DeleteExactScalar16Scalar16Tuple(TKey key, TIdentity identity)
    {
        if (CanUseInternalScalar16Scalar16WriterContextForPrimaryMutation())
        {
            return DeleteExactScalar16Scalar16TupleUsingWriterContextOrFallback(key, identity).Deleted;
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    /// <summary>
    /// Deletes one exact `SS16-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; unsupported route shapes fall back to the existing exact range-reader delete path.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.</param>
    /// <param name="identity">The typed identity side of the tuple to delete.</param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    private LibraDexGenericDeleteResult DeleteExactScalar16Scalar16TupleUsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong keyHigh, out ulong keyLow);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Scalar16Scalar16Profile profile = GetScalar16Scalar16Profile();
        byte rootPrefix = (byte)(keyHigh >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            return new LibraDexGenericDeleteResult(false, default)
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
            };
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginScalar16Scalar16WriteContext();
            try
            {
                bool deleted = session.DeleteWalkedRoutedScalar16Scalar16ExactForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    keyHigh,
                    keyLow,
                    identityHigh,
                    identityLow,
                    maxRouterHops: Scalar16Scalar16Layout.KeySize + 1);
                if (!deleted)
                {
                    session.AbortScalar16Scalar16WriteContext(writeContext);
                    return new LibraDexGenericDeleteResult(false, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishScalar16Scalar16WriteContext(writeContext);
                return new LibraDexGenericDeleteResult(
                    true,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
            }
            catch (LibraDexWriteContextScalar16Scalar16ShelfOwnershipException ex)
            {
                session.AbortScalar16Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortScalar16Scalar16WriteContext(writeContext);
                using Scalar16Scalar16RangeReader reader = OpenScalar16Scalar16RangeReader(key, key);
                bool deleted = reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
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

    private bool DeleteExactFixed32Scalar8Tuple(TKey key, TIdentity identity)
    {
        if (CanUseInternalFixed32Scalar8WriterContextForPrimaryMutation())
        {
            return DeleteExactFixed32Scalar8TupleUsingWriterContextOrFallback(key, identity).Deleted;
        }

        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        using Fixed32Scalar8RangeReader reader = OpenFixed32Scalar8RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
    }

    /// <summary>
    /// Deletes one exact `FS32-8` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; unsupported route shapes fall back to the existing exact range-reader delete path.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.</param>
    /// <param name="identity">The typed identity side of the tuple to delete.</param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    private LibraDexGenericDeleteResult DeleteExactFixed32Scalar8TupleUsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        Fixed32Scalar8Profile profile = GetFixed32Scalar8Profile();
        byte rootPrefix = (byte)(key0 >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            return new LibraDexGenericDeleteResult(false, default)
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
            };
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixed32Scalar8WriteContext();
            try
            {
                bool deleted = session.DeleteWalkedRoutedFixed32Scalar8ExactForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    encodedIdentity,
                    maxRouterHops: 32);
                if (!deleted)
                {
                    session.AbortFixed32Scalar8WriteContext(writeContext);
                    return new LibraDexGenericDeleteResult(false, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishFixed32Scalar8WriteContext(writeContext);
                return new LibraDexGenericDeleteResult(
                    true,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
            }
            catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException ex)
            {
                session.AbortFixed32Scalar8WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                session.AbortFixed32Scalar8WriteContext(writeContext);
                using Fixed32Scalar8RangeReader reader = OpenFixed32Scalar8RangeReader(key, key);
                bool deleted = reader.DeleteFirstMatchingEncodedIdentity(encodedIdentity);
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

    private bool DeleteExactFixed32Scalar16Tuple(TKey key, TIdentity identity)
    {
        if (CanUseInternalFixed32Scalar16WriterContextForPrimaryMutation())
        {
            return DeleteExactFixed32Scalar16TupleUsingWriterContextOrFallback(key, identity).Deleted;
        }

        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        using Fixed32Scalar16RangeReader reader = OpenFixed32Scalar16RangeReader(key, key);
        return reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
    }

    /// <summary>
    /// Deletes one exact `FS32-16` tuple through a writer-local shelf context when the route is already warmed and shelf-local.<br/>
    /// Same-shelf ownership contention waits and retries inside the method; unsupported route shapes fall back to the existing exact range-reader delete path.<br/>
    /// </summary>
    /// <param name="key">The typed key side of the tuple to delete.</param>
    /// <param name="identity">The typed identity side of the tuple to delete.</param>
    /// <returns>The generic delete result with writer-context or fallback attribution.</returns>
    private LibraDexGenericDeleteResult DeleteExactFixed32Scalar16TupleUsingWriterContextOrFallback(TKey key, TIdentity identity)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(key, out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        LibraDexGenericScalarCodec<TIdentity>.Encode16(identity, out ulong identityHigh, out ulong identityLow);
        Fixed32Scalar16Profile profile = GetFixed32Scalar16Profile();
        byte rootPrefix = (byte)(key0 >> 56);
        if (session.FindRouterTarget(RootRouterOffset, rootPrefix) == 0)
        {
            return new LibraDexGenericDeleteResult(false, default)
            {
                QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
            };
        }

        while (true)
        {
            LibraDexWriteContext writeContext = session.BeginFixed32Scalar16WriteContext();
            try
            {
                bool deleted = session.DeleteWalkedRoutedFixed32Scalar16ExactForWriteContext(
                    writeContext,
                    RootRouterOffset,
                    profile,
                    key0,
                    key1,
                    key2,
                    key3,
                    identityHigh,
                    identityLow,
                    maxRouterHops: 32);
                if (!deleted)
                {
                    session.AbortFixed32Scalar16WriteContext(writeContext);
                    return new LibraDexGenericDeleteResult(false, default)
                    {
                        QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.None
                    };
                }

                DataKernelCommitTelemetry telemetry = session.PublishFixed32Scalar16WriteContext(writeContext);
                return new LibraDexGenericDeleteResult(
                    true,
                    LibraDexOperationDiagnostics.FromDataKernel(telemetry))
                {
                    QueuedInsertPath = Scalar8Scalar8QueuedInsertPath.WriterContext
                };
            }
            catch (LibraDexWriteContextFixed32Scalar16ShelfOwnershipException ex)
            {
                session.AbortFixed32Scalar16WriteContext(writeContext);
                session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                session.AbortFixed32Scalar16WriteContext(writeContext);
                using Fixed32Scalar16RangeReader reader = OpenFixed32Scalar16RangeReader(key, key);
                bool deleted = reader.DeleteFirstMatchingEncodedIdentity(identityHigh, identityLow);
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

    private static bool TupleComponentEquals<TValue>(TValue left, TValue right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        return EqualityComparer<TValue>.Default.Equals(left, right);
    }

    /// <summary>
    /// Attempts to read the one key associated with <paramref name="identity"/> under this index's persisted single-key-per-identity contract.<br/>
    /// The first identity-side use builds one catalog-session map from the authoritative forward index; immediate mutations maintain that map so later reads avoid a forward scan and scalar boxing.<br/>
    /// </summary>
    /// <param name="identity">The identity whose current key should be returned.<br/></param>
    /// <param name="key">The associated key when this method returns <see langword="true"/>.<br/></param>
    /// <returns><see langword="true"/> when the identity currently has one indexed key; otherwise <see langword="false"/>.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when this index was not created with <see cref="IdentityKeyMultiplicity.SingleKeyPerIdentity"/>.<br/></exception>
    public bool TryGetSingleKey(TIdentity identity, out TKey key)
    {
        ThrowIfDisposed();
        if (identityKeyMultiplicity != IdentityKeyMultiplicity.SingleKeyPerIdentity)
        {
            throw new InvalidOperationException(
                $"Index '{Group}/{Name}' must use {nameof(IdentityKeyMultiplicity.SingleKeyPerIdentity)} before identity-to-single-key lookup is valid.");
        }

        return catalog.GetSingleKeyIdentityMap(this).TryGet(this, identity, out key!);
    }

    /// <summary>
    /// Offers a typed selected-key reduction over an already-ready, catalog-owned inverse map.<br/>
    /// Unlike TryGetSingleKey, this never creates or initializes a map and never scans the forward index.<br/>
    /// This internal route is only valid when caller-order reduction is semantically equivalent to key-order reduction.<br/>
    /// </summary>
    /// <param name="identities">Distinct identities to look up.<br/></param>
    /// <param name="visitor">Trusted synchronous reducer; must not mutate this index or its catalog.<br/></param>
    /// <param name="matched">Number of existing keys delivered.<br/></param>
    /// <returns>True when the entire selection used a ready map; false without invoking the visitor otherwise.<br/></returns>
    internal bool TryVisitReadySingleKeys(IEnumerable<TIdentity> identities, Action<TKey> visitor, out long matched)
    {
        ThrowIfDisposed();
        matched = 0;
        return identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity &&
            catalog.TryGetExistingSingleKeyIdentityMap(this, out CatalogSingleKeyIdentityMap<TKey, TIdentity>? map) &&
            map!.TryVisitReadyKeys(identities, visitor, out matched);
    }

    /// <summary>
    /// Populates the shared catalog-session single-key map by walking this index's authoritative forward tuples once.<br/>
    /// This setup path is intentionally internal; normal identity reads call <see cref="TryGetSingleKey(TIdentity, out TKey)"/> and reuse the completed map.<br/>
    /// </summary>
    /// <param name="map">The catalog-owned map receiving current tuples.<br/></param>
    internal void PopulateSingleKeyIdentityMap(CatalogSingleKeyIdentityMap<TKey, TIdentity> map)
    {
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader();
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
            map.AddLoaded(identity, key);
    }

    private void RecordSingleKeyIdentityInsert(TIdentity identity, TKey key)
    {
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            catalog.GetSingleKeyIdentityMap(this).RecordInsert(this, identity, key);
    }

    private void RecordSingleKeyIdentityDelete(TIdentity identity, TKey key)
    {
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            catalog.GetSingleKeyIdentityMap(this).RecordDelete(this, identity, key);
    }

    private void InvalidateSingleKeyIdentityMap()
    {
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity)
            catalog.GetSingleKeyIdentityMap(this).Invalidate();
    }

    /// <summary>
    /// Invalidates the catalog-session identity-to-key acceleration map after a staged writer has made physical mutations reader-visible.<br/>
    /// Staged guards call committed-map checks without invalidating per item; generic durability batches and concurrent writer contexts invoke this once at their real publication boundary.<br/>
    /// Immediate queued-writer inserts continue to update the map directly through the normal completed-insert path and therefore do not require this invalidation.<br/>
    /// </summary>
    internal void InvalidateSingleKeyIdentityMapAfterStagedPublication()
    {
        InvalidateSingleKeyIdentityMap();
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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        bool oldIsNullKey = TryClassifyNullKeyRouteKey(oldKey, out NullKey oldNullKeyState);
        bool newIsNullKey = TryClassifyNullKeyRouteKey(newKey, out NullKey newNullKeyState);
        if (oldIsNullKey || newIsNullKey)
        {
            if (oldIsNullKey && newIsNullKey)
            {
                _ = Rekey(identity, oldNullKeyState, newNullKeyState);
                return;
            }

            if (oldIsNullKey)
            {
                _ = Rekey(identity, oldNullKeyState, newKey);
                return;
            }

            _ = Rekey(identity, oldKey, newNullKeyState);
            return;
        }

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
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKey, oldKey))
            {
                return;
            }

            LibraDexGenericInsertResult insert = InsertAfterIdentityKeyMultiplicityCheck(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("Rekey could not create the replacement tuple; the original tuple was left unchanged.");
            }
        }

        _ = DeleteExactTuple(oldKey, identity);
        RecordSingleKeyIdentityInsert(identity, newKey);
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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKey, oldKeyState))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertAfterIdentityKeyMultiplicityCheck(newKey, identity);
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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, ScalarNull.Null, oldKey))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertScalarNullIdentity(identity);
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
    /// Re-keys one identity from a binary null or empty key-state route to an ordinary binary key.<br/>
    /// The replacement tuple is created before the old key-state identity is removed so a failed replacement cannot lose the existing association.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKeyState">The old binary key state; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <param name="newKey">The replacement ordinary binary key.</param>
    /// <returns><see langword="true"/> when the old key-state identity existed and was removed after the replacement tuple was available.</returns>
    public bool Rekey(TIdentity identity, NullKey oldKeyState, TKey newKey)
    {
        ThrowIfDisposed();
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        EnsureConcreteNullKeyState(oldKeyState, nameof(oldKeyState));
        if (TryClassifyNullKeyRouteKey(newKey, out NullKey newKeyState))
        {
            return Rekey(identity, oldKeyState, newKeyState);
        }

        if (!ContainsNullKeyIdentity(oldKeyState, identity))
        {
            return false;
        }

        if (!ContainsExactTuple(newKey, identity))
        {
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKey, oldKeyState))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertAfterIdentityKeyMultiplicityCheck(newKey, identity);
            if (!insert.Inserted && !ContainsExactTuple(newKey, identity))
            {
                throw new InvalidOperationException("NullKey rekey could not create the replacement tuple; the key-state identity was left unchanged.");
            }
        }

        bool deleted = DeleteNullKeyIdentity(oldKeyState, identity);
        if (deleted)
        {
            RecordRekey();
        }

        return deleted;
    }

    /// <summary>
    /// Re-keys one identity from an ordinary binary key to a binary null or empty key-state route.<br/>
    /// The key-state identity is created before the old ordinary tuple is removed so a failed replacement cannot lose the existing association.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKey">The old ordinary binary key.</param>
    /// <param name="newKeyState">The replacement binary key state; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <returns><see langword="true"/> when the old ordinary tuple existed and was removed after the key-state identity was available.</returns>
    public bool Rekey(TIdentity identity, TKey oldKey, NullKey newKeyState)
    {
        ThrowIfDisposed();
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        EnsureConcreteNullKeyState(newKeyState, nameof(newKeyState));
        if (TryClassifyNullKeyRouteKey(oldKey, out NullKey oldKeyState))
        {
            return Rekey(identity, oldKeyState, newKeyState);
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return false;
        }

        if (!ContainsNullKeyIdentity(newKeyState, identity))
        {
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKeyState, oldKey))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertNullKeyIdentity(newKeyState, identity);
            if (!insert.Inserted && !ContainsNullKeyIdentity(newKeyState, identity))
            {
                throw new InvalidOperationException("NullKey rekey could not create the key-state identity; the original tuple was left unchanged.");
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
    /// Re-keys one identity between binary null and empty key-state routes.<br/>
    /// The replacement route identity is created before the old route identity is removed so the move remains lossless across route classes.<br/>
    /// </summary>
    /// <param name="identity">The identity to re-key.</param>
    /// <param name="oldKeyState">The old binary key state; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <param name="newKeyState">The replacement binary key state; only <see cref="NullKey.Null"/> and <see cref="NullKey.Empty"/> are accepted.</param>
    /// <returns><see langword="true"/> when the old key-state identity existed and was removed after the replacement route was available.</returns>
    public bool Rekey(TIdentity identity, NullKey oldKeyState, NullKey newKeyState)
    {
        ThrowIfDisposed();
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

        EnsureConcreteNullKeyState(oldKeyState, nameof(oldKeyState));
        EnsureConcreteNullKeyState(newKeyState, nameof(newKeyState));
        if (oldKeyState == newKeyState)
        {
            return false;
        }

        if (!ContainsNullKeyIdentity(oldKeyState, identity))
        {
            return false;
        }

        if (!ContainsNullKeyIdentity(newKeyState, identity))
        {
            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKeyState, oldKeyState))
            {
                return false;
            }

            LibraDexGenericInsertResult insert = InsertNullKeyIdentity(newKeyState, identity);
            if (!insert.Inserted && !ContainsNullKeyIdentity(newKeyState, identity))
            {
                throw new InvalidOperationException("NullKey rekey could not create the replacement key-state identity; the original route identity was left unchanged.");
            }
        }

        bool deleted = DeleteNullKeyIdentity(oldKeyState, identity);
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
        if (TryClassifyNullKeyRouteKey(newKey, out NullKey newKeyState))
        {
            if (!ContainsExactTuple(oldKey, identity))
            {
                return new LibraDexGenericInsertResult(false, false, default, default);
            }

            if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKeyState, oldKey))
            {
                return new LibraDexGenericInsertResult(false, false, default, default);
            }

            LibraDexGenericInsertResult keyStateInsert = ContainsNullKeyIdentity(newKeyState, identity)
                ? new LibraDexGenericInsertResult(false, false, default, default)
                : InsertNullKeyIdentity(newKeyState, identity);
            if (!keyStateInsert.Inserted && !ContainsNullKeyIdentity(newKeyState, identity))
            {
                throw new InvalidOperationException("Cursor-local SetKey could not create the key-state replacement tuple; the original tuple was left unchanged.");
            }

            if (DeleteExactTuple(oldKey, identity))
            {
                RecordRekey();
            }

            return keyStateInsert;
        }

        if (TupleComponentEquals(oldKey, newKey))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        if (!ContainsExactTuple(oldKey, identity))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        if (!CanInsertIdentityAtKeyReplacingOldKey(identity, newKey, oldKey))
        {
            return new LibraDexGenericInsertResult(false, false, default, default);
        }

        LibraDexGenericInsertResult insert = ContainsExactTuple(newKey, identity)
            ? new LibraDexGenericInsertResult(false, false, default, default)
            : InsertAfterIdentityKeyMultiplicityCheck(newKey, identity);
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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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

        if (SupportsScalarNullKeyRoute() &&
            ContainsScalarNullIdentity(identity))
        {
            _ = Rekey(identity, ScalarNull.Null, newKey);
        }
        else if (SupportsNullKeyRoute() && SupportsStoredNullKeyRoutes())
        {
            if (ContainsNullKeyIdentity(NullKey.Null, identity))
            {
                _ = Rekey(identity, NullKey.Null, newKey);
            }

            if (ContainsNullKeyIdentity(NullKey.Empty, identity))
            {
                _ = Rekey(identity, NullKey.Empty, newKey);
            }
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

        if (TryClassifyNullKeyRouteKey(key, out NullKey keyState))
        {
            ThrowIfSessionDurabilityBatchActiveForImmediateMutation();
            _ = DeleteNullKeyIdentity(keyState, identity);
            return;
        }

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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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
        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();

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
    /// Opens a generic range reader for an inclusive key range.<br/>
    /// The returned cursor follows the same physical shape selected for this index and allows callers to stream matching rows without allocating a destination array for the whole result set.<br/>
    /// Current key and identity values are decoded only when requested from the cursor, so identity-only analysis can avoid key materialization after positioning.<br/>
    /// Descending direction streams backward over retained shelf-local slot ranges without materializing decoded tuples.<br/>
    /// </summary>
    /// <param name="lowerKey">The inclusive lower key for the range.</param>
    /// <param name="upperKey">The inclusive upper key for the range.</param>
    /// <param name="direction">The requested key traversal direction.</param>
    /// <returns>A cursor positioned before the first matching key/identity row.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the index has already been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the selected physical shape does not have generic cursor support in this slice.</exception>
    public LibraDexRangeReader<TKey, TIdentity> OpenRangeReader(
        TKey lowerKey,
        TKey upperKey,
        QueryDirection direction = QueryDirection.Ascending)
    {
        ThrowIfDisposed();
        if (direction != QueryDirection.Ascending && direction != QueryDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown query direction.");
        }

        DataKernel.CoherentReadLease? coherentRead = session.EnterCoherentRead();
        try
        {
            object reader = shape switch
            {
                LibraDexGenericScalarShape.SS88 => OpenScalar8Scalar8RangeReader(lowerKey, upperKey, direction),
                LibraDexGenericScalarShape.SS168 => OpenScalar16Scalar8RangeReader(lowerKey, upperKey, direction),
                LibraDexGenericScalarShape.SS816 => OpenScalar8Scalar16RangeReader(lowerKey, upperKey, direction),
                LibraDexGenericScalarShape.SS1616 => OpenScalar16Scalar16RangeReader(lowerKey, upperKey, direction),
                LibraDexGenericScalarShape.FS328 => OpenFixed32Scalar8RangeReader(lowerKey, upperKey, direction),
                LibraDexGenericScalarShape.FS3216 => OpenFixed32Scalar16RangeReader(lowerKey, upperKey, direction),
                _ => throw new NotSupportedException($"Generic LibraDex range readers do not support resolved shape {shape}.")
            };
            coherentRead.Pause();
            LibraDexRangeReader<TKey, TIdentity> result = new(
                reader,
                shape,
                direction: direction,
                recordDelete: RecordDelete,
                deleteTuple: DeleteExactTuple,
                rekeyTuple: RekeyForCursor,
                coherentRead: coherentRead);
            coherentRead = null;
            return result;
        }
        finally
        {
            coherentRead?.Dispose();
        }
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAllRangeReader(QueryDirection direction = QueryDirection.Ascending)
    {
        GetFullKeyBounds(out TKey lowerKey, out TKey upperKey);
        return OpenRangeReader(lowerKey, upperKey, direction);
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

    private LibraDexRangeReader<TKey, TIdentity> OpenBeforeRangeReader(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        if (!TryGetPreviousKey(key, out TKey upperKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader(direction);
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey, direction);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrBeforeRangeReader(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        GetFullKeyBounds(out TKey lowerKey, out _);
        return OpenRangeReader(lowerKey, key, direction);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAfterRangeReader(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        if (!TryGetNextKey(key, out TKey lowerKey))
        {
            LibraDexRangeReader<TKey, TIdentity> empty = OpenAllRangeReader(direction);
            empty.ApplyTakeLimit(0);
            return empty;
        }

        return OpenRangeReader(lowerKey, upperKey, direction);
    }

    private LibraDexRangeReader<TKey, TIdentity> OpenAtOrAfterRangeReader(TKey key, QueryDirection direction = QueryDirection.Ascending)
    {
        GetFullKeyBounds(out _, out TKey upperKey);
        return OpenRangeReader(key, upperKey, direction);
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

        if (ownsCatalog)
        {
            catalog.Dispose();
        }
        else if (ownsSession)
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
        return BoxIdentityIterator(IterateIdentityObjects(reader, takeLimit)).ToList();
    }

    /// <summary>
    /// Materializes decoded key/identity tuples from a generic range reader and disposes the reader after consumption.<br/>
    /// The object wrapper is intentionally internal and preserves exact old keys for tuple-level mutation without adding a public projection type.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <returns>The decoded tuples as object key/identity pairs.</returns>
    private static IReadOnlyList<LibraDexObjectTuple> MaterializeTupleObjects(LibraDexRangeReader<TKey, TIdentity> reader)
    {
        return IterateTupleObjects(reader).ToList();
    }

    /// <summary>
    /// Streams decoded key/identity tuples from a generic range reader and disposes the reader after consumption.<br/>
    /// The limit is applied while reading so condition-shaped cursors can skip or stop without forcing a full tuple list first.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <param name="takeLimit">The optional maximum number of tuples to yield.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private static IEnumerable<LibraDexObjectTuple> IterateTupleObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
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
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                yield return new LibraDexObjectTuple(key!, identity!);
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams decoded identities from a concrete generic range reader and disposes the reader when enumeration completes or stops early.<br/>
    /// The limit is applied while reading so callers such as `Exists` can avoid copying more identities than they need.<br/>
    /// This helper is the shared low-level identity-only path for materialized and non-materialized primitive execution.<br/>
    /// </summary>
    /// <param name="reader">The range reader to consume.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private static IEnumerable<TIdentity> IterateIdentityObjects(LibraDexRangeReader<TKey, TIdentity> reader, int? takeLimit = null)
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
        return BoxIdentityIterator(IterateAllIdentityObjects(takeLimit)).ToList();
    }

    /// <summary>
    /// Streams every identity visible through this index in LibraDex index-natural order.<br/>
    /// The stream reads the scalar null route first, then falls through to the ordinary value router while preserving the caller's optional take limit across both route classes.<br/>
    /// </summary>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<TIdentity> IterateAllIdentityObjects(int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        if (direction == QueryDirection.Descending)
        {
            foreach (TIdentity identity in IterateIdentityObjects(OpenAllRangeReader(direction), takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is int limit && returned >= limit)
                    yield break;
            }

            if (SupportsStoredNullKeyRoutes())
            {
                foreach (NullKey state in new[] { NullKey.Empty, NullKey.Null })
                {
                    foreach (TIdentity identity in IterateDescendingKeyStateIdentities(ToKeyStateRoute(state)))
                    {
                        yield return identity;
                        returned++;
                        if (takeLimit is int limit && returned >= limit)
                            yield break;
                    }
                }
            }
            else if (SupportsScalarNullKeyRoute())
            {
                foreach (TIdentity identity in IterateDescendingKeyStateIdentities(KeyStateRoute.Null))
                {
                    yield return identity;
                    returned++;
                    if (takeLimit is int limit && returned >= limit)
                        yield break;
                }
            }
            yield break;
        }
        if (direction != QueryDirection.Ascending)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (SupportsScalarNullKeyRoute())
        {
            foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects(takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
        else if (SupportsStoredNullKeyRoutes())
        {
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Null, takeLimit))
            {
                yield return identity;
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }

            int? remainingForEmpty = takeLimit is null ? null : takeLimit.Value - returned;
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Empty, remainingForEmpty))
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
        foreach (TIdentity identity in IterateIdentityObjects(OpenAllRangeReader(), remaining))
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
        long count = CountOrdinaryIdentityObjects();
        if (catalog is not null && SupportsScalarNullKeyRoute())
        {
            count += CountOptionalScalarNullRouteIdentityObjects();
        }
        else if (catalog is not null && SupportsStoredNullKeyRoutes())
        {
            count += CountOptionalNullKeyRouteIdentityObjects(NullKey.Null);
            count += CountOptionalNullKeyRouteIdentityObjects(NullKey.Empty);
        }

        return count;
    }

    /// <summary>
    /// Counts ordinary non-key-state identities through the fastest physical count path available for the resolved shape.<br/>
    /// Shapes with a dedicated route-count primitive use it directly so count-all does not retain cursor shelf images or allocate reader extent arrays.<br/>
    /// Shapes without a dedicated primitive continue to use range-reader shelf counts, which still avoid identity decoding.<br/>
    /// </summary>
    /// <returns>The number of ordinary non-null/non-empty key tuples in this index.</returns>
    private long CountOrdinaryIdentityObjects()
    {
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => session.CountScalar8Scalar8Identities(RootRouterOffset, GetScalar8Scalar8Profile()),
            LibraDexGenericScalarShape.SS168 => session.CountScalar16Scalar8Identities(RootRouterOffset, GetScalar16Scalar8Profile()),
            LibraDexGenericScalarShape.SS816 => session.CountScalar8Scalar16Identities(RootRouterOffset, GetScalar8Scalar16Profile()),
            LibraDexGenericScalarShape.SS1616 => session.CountScalar16Scalar16Identities(RootRouterOffset, GetScalar16Scalar16Profile()),
            LibraDexGenericScalarShape.FS328 => session.CountFixed32Scalar8Identities(RootRouterOffset, GetFixed32Scalar8Profile()),
            LibraDexGenericScalarShape.FS3216 => session.CountFixed32Scalar16Identities(RootRouterOffset, GetFixed32Scalar16Profile()),
            _ => CountIdentityObjects(OpenAllRangeReader())
        };
    }

    /// <summary>
    /// Counts the scalar-null route when the index metadata contains that optional route, otherwise returns zero.<br/>
    /// Standalone generic indexes can predate catalog key-state metadata, and count-all should still report ordinary shelf rows instead of failing before returning them.<br/>
    /// Explicit ScalarNull criteria continue to use <see cref="CountScalarNullRouteIdentityObjects"/> so missing route metadata remains visible when the caller asks for that route specifically.<br/>
    /// </summary>
    /// <returns>The scalar-null route identity count, or zero when the optional route metadata is absent.</returns>
    private long CountOptionalScalarNullRouteIdentityObjects()
    {
        try
        {
            return CountScalarNullRouteIdentityObjects();
        }
        catch (InvalidDataException ex) when (IsMissingKeyStateRouteMetadata(ex))
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts one binary key-state route when optional route metadata is present, otherwise returns zero.<br/>
    /// This keeps count-all compatible with standalone fixed-byte indexes that have no catalog key-state route metadata while preserving strict failures for direct NullKey criteria.<br/>
    /// </summary>
    /// <param name="keyState">The concrete null or empty binary key-state route to count.</param>
    /// <returns>The selected route identity count, or zero when the optional route metadata is absent.</returns>
    private long CountOptionalNullKeyRouteIdentityObjects(NullKey keyState)
    {
        try
        {
            return CountNullKeyRouteIdentityObjects(keyState);
        }
        catch (InvalidDataException ex) when (IsMissingKeyStateRouteMetadata(ex))
        {
            return 0;
        }
    }

    /// <summary>
    /// Identifies the file-session signal used when an index has no catalog metadata for optional key-state routes.<br/>
    /// The check is intentionally narrow so count-all does not mask unrelated storage corruption or route read failures.<br/>
    /// </summary>
    /// <param name="ex">The storage exception raised while reading a key-state route.</param>
    /// <returns><see langword="true"/> when the exception represents absent optional key-state route metadata.</returns>
    private static bool IsMissingKeyStateRouteMetadata(InvalidDataException ex)
    {
        return string.Equals(
            ex.Message,
            "The requested index slot does not have decodable catalog metadata for key-state routes.",
            StringComparison.Ordinal);
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
        return BoxIdentityIterator(IterateScalarNullIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities for a scalar-null presence primitive without first materializing the whole route.<br/>
    /// Null-route identities are stored as encoded identities only, so this method decodes scalar-8 and scalar-16 identity lanes directly from the compact route shelf.<br/>
    /// Non-null scalar state intentionally falls through to the normal all-range reader instead of synthesizing a complement against the null route.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="ScalarNull"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<TIdentity> IterateScalarNullIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        ScalarNull state = RequireScalarNullState(values);
        if (state == ScalarNull.NonNull)
        {
            foreach (TIdentity identity in IterateIdentityObjects(OpenAllRangeReader(direction), takeLimit))
            {
                yield return identity;
            }

            yield break;
        }

        IEnumerable<TIdentity> nullIdentities = direction == QueryDirection.Descending
            ? IterateDescendingKeyStateIdentities(KeyStateRoute.Null)
            : IterateScalarNullRouteIdentityObjects(takeLimit);
        int returned = 0;
        foreach (TIdentity identity in nullIdentities)
        {
            if (takeLimit is int limit && returned >= limit)
                yield break;
            yield return identity;
            returned++;
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
        Catalog.Stats.RecordInsert(result);
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
    private IEnumerable<TIdentity> IterateScalarNullRouteIdentityObjects(int? takeLimit = null)
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
                foreach (ulong encodedIdentity in session.IterateScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null))
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity)!;
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
                foreach (var encodedIdentity in session.IterateScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null))
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(encodedIdentity.High, encodedIdentity.Low)!;
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
            LibraDexGenericScalarShape.FS328 => session.CountScalar8KeyStateIdentities(SlotIndex, KeyStateRoute.Null),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => session.CountScalar16KeyStateIdentities(SlotIndex, KeyStateRoute.Null),
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
            .Select(static identity => new LibraDexObjectTuple(null, identity!))
            .ToList();
    }

    /// <summary>
    /// Materializes every physical tuple visible through this index, including metadata-backed null and empty key routes.<br/>
    /// Target-owned mutation uses this capture before changing tuples, so its <c>All</c> meaning must match identity retrieval rather than only the ordinary value router.<br/>
    /// </summary>
    /// <param name="direction">The requested ordinary value-route traversal direction.<br/></param>
    /// <returns>Null/empty route tuples followed by ordinary value-route tuples.<br/></returns>
    private IReadOnlyList<LibraDexObjectTuple> MaterializeAllTupleObjects(QueryDirection direction)
    {
        List<LibraDexObjectTuple> tuples = new();
        if (catalog is not null && SupportsScalarNullKeyRoute())
        {
            tuples.AddRange(IterateScalarNullRouteIdentityObjects().Select(static identity => new LibraDexObjectTuple(null, identity!)));
        }
        else if (catalog is not null && SupportsStoredNullKeyRoutes())
        {
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Null).Select(static identity => new LibraDexObjectTuple(null, identity!)));
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Empty).Select(static identity => new LibraDexObjectTuple(Array.Empty<byte>(), identity!)));
        }

        tuples.AddRange(MaterializeTupleObjects(OpenAllRangeReader(direction)));
        return tuples;
    }

    /// <summary>
    /// Streams all tuple routes without capturing the complete index in a list.<br/>
    /// Preserves the existing null, empty, then directed ordinary-value route order.<br/>
    /// Readers are opened only when reached and disposed on completion, early disposal, or consumer failure.<br/>
    /// Only yielded values cross the object compatibility boundary; Take(0) opens no reader.<br/>
    /// </summary>
    /// <param name="direction">Ordinary-value traversal direction.<br/></param>
    /// <param name="takeLimit">Optional nonnegative limit across all routes.<br/></param>
    /// <returns>A deferred tuple sequence; no full-result collection is retained.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateAllTupleObjects(QueryDirection direction, int? takeLimit)
    {
        if (takeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        if (takeLimit == 0)
            yield break;

        ThrowIfDisposed();
        int remaining = takeLimit ?? int.MaxValue;
        if (catalog is not null && SupportsScalarNullKeyRoute())
        {
            foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects())
            {
                yield return new LibraDexObjectTuple(null, identity!);
                if (--remaining == 0) yield break;
            }
        }
        else if (catalog is not null && SupportsStoredNullKeyRoutes())
        {
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Null))
            {
                yield return new LibraDexObjectTuple(null, identity!);
                if (--remaining == 0) yield break;
            }
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Empty))
            {
                yield return new LibraDexObjectTuple(Array.Empty<byte>(), identity!);
                if (--remaining == 0) yield break;
            }
        }

        using var reader = OpenAllRangeReader(direction);
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            yield return new LibraDexObjectTuple(key!, identity!);
            if (--remaining == 0) yield break;
        }
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
        return BoxIdentityIterator(IterateNullKeyIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities for a null or empty binary key-state primitive without materializing the route first.<br/>
    /// `NullKey.NullOrEmpty` streams null before empty, matching the index-natural all-scan ordering contract.<br/>
    /// </summary>
    /// <param name="values">The condition primitive operands; operand zero must be <see cref="NullKey"/>.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only sequence of decoded identities.</returns>
    private IEnumerable<TIdentity> IterateNullKeyIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        NullKey keyState = RequireNullKeyState(values);
        if (keyState != NullKey.NullOrEmpty)
        {
            IEnumerable<TIdentity> source = direction == QueryDirection.Descending
                ? IterateDescendingKeyStateIdentities(ToKeyStateRoute(keyState))
                : IterateNullKeyRouteIdentityObjects(keyState, takeLimit);
            int yielded = 0;
            foreach (TIdentity identity in source)
            {
                if (takeLimit is int limit && yielded >= limit)
                    yield break;
                yield return identity;
                yielded++;
            }

            yield break;
        }

        int returned = 0;
        NullKey first = direction == QueryDirection.Descending ? NullKey.Empty : NullKey.Null;
        NullKey second = direction == QueryDirection.Descending ? NullKey.Null : NullKey.Empty;
        foreach (TIdentity identity in IterateNullKeyIdentityObjects(new object?[] { first }, takeLimit, direction))
        {
            yield return identity;
            returned++;
            if (takeLimit is not null && returned >= takeLimit.Value)
            {
                yield break;
            }
        }

        int? remaining = takeLimit is null ? null : takeLimit.Value - returned;
        foreach (TIdentity identity in IterateNullKeyIdentityObjects(new object?[] { second }, remaining, direction))
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
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Null).Select(static identity => new LibraDexObjectTuple(null, identity!)));
        }

        if (keyState is NullKey.Empty or NullKey.NullOrEmpty)
        {
            tuples.AddRange(IterateNullKeyRouteIdentityObjects(NullKey.Empty).Select(static identity => new LibraDexObjectTuple(Array.Empty<byte>(), identity!)));
        }

        return tuples;
    }

    private LibraDexGenericInsertResult InsertNullKeyIdentity(NullKey keyState, TIdentity identity)
    {
        EnsureConcreteNullKeyState(keyState, nameof(keyState));
        bool inserted = shape switch
        {
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => InsertScalar8NullKeyRouteIdentity(keyState, identity),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => InsertScalar16NullKeyRouteIdentity(keyState, identity),
            _ => throw new NotSupportedException($"NullKey route inserts do not support resolved shape {shape}.")
        };

        LibraDexGenericInsertResult result = new(inserted, false, default, default);
        Stats.RecordInsert(result);
        Catalog.Stats.RecordInsert(result);
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
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.DeleteScalar8KeyStateIdentity(
                SlotIndex,
                route,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
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
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.ContainsScalar8KeyStateIdentity(
                SlotIndex,
                route,
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
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
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => DeleteScalar8NullKeyRouteIdentities(ToKeyStateRoute(keyState)),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
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

    private IEnumerable<TIdentity> IterateNullKeyRouteIdentityObjects(NullKey keyState, int? takeLimit = null)
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
            case LibraDexGenericScalarShape.SS88:
            case LibraDexGenericScalarShape.SS168:
            case LibraDexGenericScalarShape.FS328:
                int returned8 = 0;
                foreach (ulong encodedIdentity in session.IterateScalar8KeyStateIdentities(SlotIndex, route))
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(encodedIdentity)!;
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
                foreach (var encodedIdentity in session.IterateScalar16KeyStateIdentities(SlotIndex, route))
                {
                    yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(encodedIdentity.High, encodedIdentity.Low)!;
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
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328 => session.CountScalar8KeyStateIdentities(SlotIndex, route),
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS3216 => session.CountScalar16KeyStateIdentities(SlotIndex, route),
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
            case NullKey state:
                keyState = state;
                return true;
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

    private bool SupportsStoredNullKeyRoutes()
    {
        return shape is
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.SS816 or
            LibraDexGenericScalarShape.SS1616 or
            LibraDexGenericScalarShape.FS328 or
            LibraDexGenericScalarShape.FS3216;
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
            return MaterializeMembershipIdentityObjects(values, takeLimit);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object?> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues.Cast<object?>(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
            _ => throw new InvalidOperationException("Membership identity execution requires an enumerable key value or prepared set.")
        };

        return MaterializeMembershipIdentityObjects(keys, takeLimit);
    }

    private IReadOnlyList<object> MaterializeMembershipIdentityObjects(IEnumerable<object?> keys, int? takeLimit = null)
    {
        return BoxIdentityIterator(IterateMembershipIdentityObjects(keys, takeLimit)).ToList();
    }

    private IReadOnlyList<LibraDexObjectTuple> MaterializeMembershipTupleObjects(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return MaterializeMembershipTupleObjects(values);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object?> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues.Cast<object?>(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
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
    private IReadOnlyList<LibraDexObjectTuple> MaterializeMembershipTupleObjects(IEnumerable<object?> keys)
    {
        return IterateMembershipTupleObjects(keys).ToList();
    }

    /// <summary>
    /// Streams tuple rows for a non-generic membership primitive by opening one equality reader per key.<br/>
    /// The method preserves supplied key order and leaves duplicate tuple handling to the caller because physical tuple streams may intentionally contain duplicate identities under different keys.<br/>
    /// </summary>
    /// <param name="keys">The membership keys to read.</param>
    /// <param name="takeLimit">The optional maximum number of tuples to yield across all keys.</param>
    /// <param name="direction">The traversal direction used to enumerate matching tuples.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexObjectTuple> IterateMembershipTupleObjects(IEnumerable<object?> keys, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        foreach (object? keyValue in OrderMembershipKeys(keys, direction))
        {
            if (TryClassifyNullKeyRouteKey(keyValue, out NullKey keyState))
            {
                IEnumerable<LibraDexObjectTuple> nullTuples = MaterializeNullKeyTupleObjects(new object?[] { keyState });
                if (direction == QueryDirection.Descending)
                    nullTuples = nullTuples.Reverse();
                foreach (LibraDexObjectTuple tuple in nullTuples)
                {
                    yield return tuple;
                    returned++;
                    if (takeLimit is not null && returned >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                continue;
            }

            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue!, nameof(keys)),
                RequireObjectKey(keyValue!, nameof(keys)), direction);
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                yield return new LibraDexObjectTuple(key!, identity!);
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams identities for a membership primitive by opening one equality range reader for each supplied key.<br/>
    /// The method preserves the caller's key order and applies one shared take limit across all matching key runs.<br/>
    /// It intentionally leaves duplicate identities intact because duplicate handling belongs to the projection options above this primitive layer.<br/>
    /// </summary>
    /// <param name="values">The condition operand values, either inline keys, an enumerable of keys, or a prepared set.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateMembershipIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        if (values.Count > 1)
        {
            return IterateMembershipIdentityObjects(values, takeLimit, direction);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object?> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues.Cast<object?>(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
            _ => throw new InvalidOperationException("Membership identity iteration requires an enumerable key value or prepared set.")
        };

        return IterateMembershipIdentityObjects(keys, takeLimit, direction);
    }

    /// <summary>
    /// Streams identities for already-normalized membership keys through one equality reader per key.<br/>
    /// The supplied key enumerable is consumed lazily so prepared or generated sets do not need to become an intermediate array at this layer.<br/>
    /// The limit is cumulative across the whole membership request, matching the materialized primitive behavior.<br/>
    /// </summary>
    /// <param name="keys">The membership keys to read.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all keys.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateMembershipIdentityObjects(IEnumerable<object?> keys, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        foreach (object? keyValue in OrderMembershipKeys(keys, direction))
        {
            if (TryClassifyNullKeyRouteKey(keyValue, out NullKey keyState))
            {
                IEnumerable<TIdentity> nullIdentities = direction == QueryDirection.Descending
                    ? IterateDescendingKeyStateIdentities(ToKeyStateRoute(keyState))
                    : IterateNullKeyIdentityObjects(new object?[] { keyState }, takeLimit.HasValue ? takeLimit.Value - returned : null);
                foreach (TIdentity identity in nullIdentities)
                {
                    yield return identity;
                    returned++;
                    if (takeLimit is not null && returned >= takeLimit.Value)
                    {
                        yield break;
                    }
                }

                continue;
            }

            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(keyValue!, nameof(keys)),
                RequireObjectKey(keyValue!, nameof(keys)), direction);
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

    /// <summary>
    /// Counts identities selected by a membership criterion operand.<br/>
    /// The count path treats membership operands as set membership, so repeated operand keys do not count the same physical key extent more than once.<br/>
    /// Ordinary keys fan out through <see cref="CountOrderedIdentityRange(TKey, TKey)"/> so equality membership can reuse the shape-native count-only range primitives.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list captured from the condition builder.<br/></param>
    /// <returns>The count of identities whose key is contained in the membership set.<br/></returns>
    private long CountMembershipIdentityObjects(IReadOnlyList<object?> values)
    {
        if (values.Count > 1)
        {
            return CountMembershipIdentityObjects((IEnumerable<object?>)values);
        }

        object value = RequireCriterionValue(values, 0);
        IEnumerable<object?> keys = value switch
        {
            LibraDexPreparedObjectSet prepared => prepared.Source,
            IEnumerable<object> objectValues => objectValues.Cast<object?>(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
            _ => throw new InvalidOperationException("Membership identity count requires an enumerable key value or prepared set.")
        };

        return CountMembershipIdentityObjects(keys);
    }

    /// <summary>
    /// Counts identities selected by an enumerable membership operand using one direct count extent per distinct key.<br/>
    /// Null-key route states are deduplicated separately from ordinary encoded keys because they live outside the ordered key routes.<br/>
    /// </summary>
    /// <param name="keys">The captured membership keys.<br/></param>
    /// <returns>The count of identities whose key is contained in the membership set.<br/></returns>
    private long CountMembershipIdentityObjects(IEnumerable<object?> keys)
    {
        long count = 0;
        HashSet<NullKey> seenNullKeys = new();
        HashSet<TKey> seenKeys = new(LibraDexKeyEquality<TKey>.Comparer);
        foreach (object? keyValue in keys)
        {
            if (TryClassifyNullKeyRouteKey(keyValue, out NullKey keyState))
            {
                if (!seenNullKeys.Add(keyState))
                {
                    continue;
                }

                count += CountNullKeyIdentityObjects(new object?[] { keyState });
                continue;
            }

            TKey key = RequireObjectKey(keyValue!, nameof(keys));
            if (!seenKeys.Add(key))
            {
                continue;
            }

            count += CountOrderedIdentityRange(key, key);
        }

        return count;
    }

    private IReadOnlyList<object> MaterializeMultiRangeIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return BoxIdentityIterator(IterateMultiRangeIdentityObjects(values, takeLimit)).ToList();
    }

    private IReadOnlyList<LibraDexObjectTuple> MaterializeMultiRangeTupleObjects(IReadOnlyList<object?> values)
    {
        return IterateMultiRangeTupleObjects(values).ToList();
    }

    /// <summary>
    /// Streams tuples for a condition-derived multi-range primitive by opening one inclusive range reader per requested extent.<br/>
    /// The primitive preserves exact range output semantics; duplicate handling remains above this layer.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one range array.</param>
    /// <param name="takeLimit">The optional maximum number of tuples to yield across all ranges.</param>
    /// <param name="direction">The traversal direction used to enumerate matching tuples.</param>
    /// <returns>A forward-only tuple sequence.</returns>
    private IEnumerable<LibraDexObjectTuple> IterateMultiRangeTupleObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexIdentityKeyRange[] ranges = OrderMultiRanges(RequireIdentityKeyRanges(values), direction);
        int returned = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)), direction);
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                yield return new LibraDexObjectTuple(key!, identity!);
                returned++;
                if (takeLimit is not null && returned >= takeLimit.Value)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Streams identities for a condition-derived multi-range primitive by opening one inclusive range reader per requested extent.<br/>
    /// The primitive exists because date condition permutations such as year membership and year/month tuple sets naturally produce multiple ordered extents over one index.<br/>
    /// Duplicate identity handling remains above this layer so the physical primitive can preserve exact range output semantics.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one range array.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield across all ranges.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateMultiRangeIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexIdentityKeyRange[] ranges = OrderMultiRanges(RequireIdentityKeyRanges(values), direction);
        int returned = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = OpenRangeReader(
                RequireObjectKey(ranges[i].LowerKey, nameof(values)),
                RequireObjectKey(ranges[i].UpperKey, nameof(values)), direction);
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
    /// Counts identities selected by a condition-derived multi-range operand.<br/>
    /// Overlapping inclusive ranges are sorted and merged before counting so OR-style range criteria do not double count the same physical key extent.<br/>
    /// Each merged extent is dispatched through <see cref="CountOrderedIdentityRange(TKey, TKey)"/> to preserve the shape-native count fast path.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one range array.<br/></param>
    /// <returns>The count of identities whose key falls inside any requested range.<br/></returns>
    private long CountMultiRangeIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexIdentityKeyRange[] ranges = RequireIdentityKeyRanges(values);
        if (ranges.Length == 0)
        {
            return 0;
        }

        CountKeyRange[] typedRanges = new CountKeyRange[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            TKey lowerKey = RequireObjectKey(ranges[i].LowerKey, nameof(values));
            TKey upperKey = RequireObjectKey(ranges[i].UpperKey, nameof(values));
            if (CompareCountKeys(lowerKey, upperKey) > 0)
            {
                throw new ArgumentException("Multi-range identity count requires each lower key to be less than or equal to its upper key.", nameof(values));
            }

            typedRanges[i] = new CountKeyRange(lowerKey, upperKey);
        }

        Array.Sort(typedRanges, (left, right) =>
        {
            int lowerComparison = CompareCountKeys(left.LowerKey, right.LowerKey);
            return lowerComparison != 0
                ? lowerComparison
                : CompareCountKeys(left.UpperKey, right.UpperKey);
        });

        long count = 0;
        TKey currentLower = typedRanges[0].LowerKey;
        TKey currentUpper = typedRanges[0].UpperKey;
        for (int i = 1; i < typedRanges.Length; i++)
        {
            if (CompareCountKeys(typedRanges[i].LowerKey, currentUpper) <= 0)
            {
                if (CompareCountKeys(typedRanges[i].UpperKey, currentUpper) > 0)
                {
                    currentUpper = typedRanges[i].UpperKey;
                }

                continue;
            }

            count += CountOrderedIdentityRange(currentLower, currentUpper);
            currentLower = typedRanges[i].LowerKey;
            currentUpper = typedRanges[i].UpperKey;
        }

        count += CountOrderedIdentityRange(currentLower, currentUpper);
        return count;
    }

    /// <summary>
    /// Compares two public keys according to this index's encoded physical key order.<br/>
    /// Count range merging uses encoded order instead of CLR comparer order so DateTime-like encodings and fixed32 byte-array keys match routed shelf traversal semantics.<br/>
    /// </summary>
    /// <param name="left">The first public key.<br/></param>
    /// <param name="right">The second public key.<br/></param>
    /// <returns>A negative value when <paramref name="left"/> sorts before <paramref name="right"/>, zero when equal, or a positive value when after.<br/></returns>
    private int CompareCountKeys(TKey left, TKey right)
    {
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 or LibraDexGenericScalarShape.SS816 => EncodeKey8(left).CompareTo(EncodeKey8(right)),
            LibraDexGenericScalarShape.SS168 or LibraDexGenericScalarShape.SS1616 => CompareScalar16CountKeys(left, right),
            LibraDexGenericScalarShape.FS328 or LibraDexGenericScalarShape.FS3216 => CompareFixed32CountKeys(left, right),
            _ => throw new NotSupportedException($"Generic LibraDex criteria counts do not support resolved shape {shape}.")
        };
    }

    /// <summary>
    /// Compares two scalar16 public keys according to their encoded high/low key lanes.<br/>
    /// The method is kept separate from the range reader so count fan-out can sort and merge ranges before opening physical readers.<br/>
    /// </summary>
    /// <param name="left">The first public key.<br/></param>
    /// <param name="right">The second public key.<br/></param>
    /// <returns>The encoded scalar16 ordering comparison.<br/></returns>
    private static int CompareScalar16CountKeys(TKey left, TKey right)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(left, out ulong leftHigh, out ulong leftLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(right, out ulong rightHigh, out ulong rightLow);
        int highComparison = leftHigh.CompareTo(rightHigh);
        return highComparison != 0
            ? highComparison
            : leftLow.CompareTo(rightLow);
    }

    /// <summary>
    /// Compares two fixed32 public keys according to their encoded four-lane big-endian key order.<br/>
    /// This avoids CLR array reference ordering and matches the route order used by fixed32 shelves.<br/>
    /// </summary>
    /// <param name="left">The first public key.<br/></param>
    /// <param name="right">The second public key.<br/></param>
    /// <returns>The encoded fixed32 ordering comparison.<br/></returns>
    private static int CompareFixed32CountKeys(TKey left, TKey right)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(left, out ulong left0, out ulong left1, out ulong left2, out ulong left3);
        LibraDexGenericScalarCodec<TKey>.Encode32(right, out ulong right0, out ulong right1, out ulong right2, out ulong right3);
        int comparison = left0.CompareTo(right0);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left1.CompareTo(right1);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left2.CompareTo(right2);
        return comparison != 0
            ? comparison
            : left3.CompareTo(right3);
    }

    /// <summary>
    /// Stores one typed inclusive key range while count planning sorts and merges multi-range operands.<br/>
    /// The struct intentionally stores public keys so the final count dispatch still uses the normal shape-specific bridge path.<br/>
    /// </summary>
    /// <param name="LowerKey">The inclusive lower public key.<br/></param>
    /// <param name="UpperKey">The inclusive upper public key.<br/></param>
    private readonly record struct CountKeyRange(TKey LowerKey, TKey UpperKey);

    private IReadOnlyList<object> MaterializeStructuredComponentIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return BoxIdentityIterator(IterateStructuredComponentIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded structured date/time key satisfies a condition-derived component predicate.<br/>
    /// The predicate is evaluated against the sortable 64-bit key value, so component-only date branches avoid CLR date reconstruction and avoid text conversion while remaining visibly distinct from ordered range seeks.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled structured component predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateStructuredComponentIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenStructuredComponentRangeReader(direction);
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
        return BoxIdentityIterator(IterateGuidPatternIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded GUID key satisfies a condition-derived canonical nibble predicate.<br/>
    /// The predicate reads the encoded 16-byte key directly, so GUID prefix/suffix/contains/pattern branches avoid per-row Guid.ToString conversion and remain separate from maintained text projections.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled GUID predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateGuidPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(direction);
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
        return BoxIdentityIterator(IterateBinaryPatternIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded fixed-width byte-array key satisfies a condition-derived binary byte predicate.<br/>
    /// The predicate reads the key lanes into a stack buffer, so raw binary prefix, suffix, contains, and slice branches avoid decoding every candidate key to an owned byte array.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled binary predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateBinaryPatternIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(direction);
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
        return BoxIdentityIterator(IterateBinaryTypedSliceIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose encoded fixed-width byte-array key satisfies a condition-derived typed binary-slice predicate.<br/>
    /// The predicate reads only the requested slice from a stack-sized key buffer, so typed binary conditions avoid whole-key decoding while preserving Abraxas-compatible slice semantics.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled typed binary-slice predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateBinaryTypedSliceIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(direction);
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
        return BoxIdentityIterator(IterateBitmaskIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose decoded scalar key satisfies a condition-derived bitmask predicate.<br/>
    /// Arbitrary bitmask predicates are not contiguous in ordered key space, so this first bridge performs an explicit compact index scan rather than pretending a range seek exists.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled bitmask predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateBitmaskIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
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
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(direction);
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
    /// Materializes identities matched by one numeric transform predicate into a caller-owned list.<br/>
    /// </summary>
    /// <param name="values">The primitive values containing the compiled predicate.<br/></param>
    /// <param name="takeLimit">The optional maximum number of identities.<br/></param>
    /// <returns>The matched identity objects in physical key order.<br/></returns>
    private IReadOnlyList<object> MaterializeNumericTransformIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null)
    {
        return BoxIdentityIterator(IterateNumericTransformIdentityObjects(values, takeLimit)).ToList();
    }

    /// <summary>
    /// Streams identities whose decoded Decimal, Single, or Double key satisfies a planner-visible native numeric transform and comparison.<br/>
    /// The transform is intentionally evaluated over the compact key scan until a future planner can safely derive equivalent inverse key ranges.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list containing one compiled numeric transform predicate.</param>
    /// <param name="takeLimit">The optional maximum number of identities to yield.</param>
    /// <param name="direction">The traversal direction used to enumerate matching identities.</param>
    /// <returns>A forward-only identity sequence.</returns>
    private IEnumerable<TIdentity> IterateNumericTransformIdentityObjects(IReadOnlyList<object?> values, int? takeLimit = null, QueryDirection direction = QueryDirection.Ascending)
    {
        if (takeLimit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(takeLimit), takeLimit, "Take cannot be negative.");
        }

        if (takeLimit == 0)
        {
            yield break;
        }

        LibraDexNumericTransformPredicate predicate = RequireNumericTransformPredicate(values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(direction);
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

    /// <summary>
    /// Counts physical tuples whose decoded key satisfies one numeric transform predicate without materializing identities.<br/>
    /// </summary>
    /// <param name="values">The primitive values containing the compiled predicate.<br/></param>
    /// <returns>The matching physical tuple count.<br/></returns>
    private long CountNumericTransformIdentityObjects(IReadOnlyList<object?> values)
    {
        LibraDexNumericTransformPredicate predicate = RequireNumericTransformPredicate(values);
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

    /// <summary>
    /// Captures exact key/identity tuples matched by one numeric transform predicate for result shaping or mutation.<br/>
    /// </summary>
    /// <param name="values">The primitive values containing the compiled predicate.<br/></param>
    /// <returns>The matching exact tuples in physical key order.<br/></returns>
    private IReadOnlyList<LibraDexObjectTuple> MaterializeNumericTransformTupleObjects(IReadOnlyList<object?> values)
    {
        LibraDexNumericTransformPredicate predicate = RequireNumericTransformPredicate(values);
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

    /// <summary>
    /// Extracts the compiled numeric transform predicate from a normalized primitive request.<br/>
    /// </summary>
    /// <param name="values">The primitive operand list.<br/></param>
    /// <returns>The compiled numeric transform predicate.<br/></returns>
    private static LibraDexNumericTransformPredicate RequireNumericTransformPredicate(IReadOnlyList<object?> values)
    {
        object value = RequireCriterionValue(values, 0);
        return value is LibraDexNumericTransformPredicate predicate
            ? predicate
            : throw new InvalidOperationException("Numeric transform identity execution requires a compiled numeric transform predicate.");
    }

    /// <summary>
    /// Opens the valid CLR key domain for an Abraxas-compatible structured date/time component predicate.<br/>
    /// The ordinary all-reader uses raw encoded bounds, which are not always decodable as DateTime-like CLR values; this helper keeps component scans inside valid typed min/max keys while still reading encoded keys from the cursor.<br/>
    /// </summary>
    /// <returns>A range reader over the full valid structured date/time key domain.</returns>
    private LibraDexRangeReader<TKey, TIdentity> OpenStructuredComponentRangeReader(QueryDirection direction = QueryDirection.Ascending)
    {
        Type keyType = typeof(TKey);
        if (keyType == typeof(DateTime))
        {
            return OpenRangeReader(
                RequireObjectKey(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), nameof(keyType)),
                RequireObjectKey(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), nameof(keyType)), direction);
        }

        if (keyType == typeof(DateTimeOffset))
        {
            return OpenRangeReader(
                RequireObjectKey(DateTimeOffset.MinValue.ToUniversalTime(), nameof(keyType)),
                RequireObjectKey(DateTimeOffset.MaxValue.ToUniversalTime(), nameof(keyType)), direction);
        }

        if (keyType == typeof(DateOnly))
        {
            return OpenRangeReader(
                RequireObjectKey(DateOnly.MinValue, nameof(keyType)),
                RequireObjectKey(DateOnly.MaxValue, nameof(keyType)), direction);
        }

        if (keyType == typeof(TimeOnly))
        {
            return OpenRangeReader(
                RequireObjectKey(TimeOnly.MinValue, nameof(keyType)),
                RequireObjectKey(TimeOnly.MaxValue, nameof(keyType)), direction);
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
                new Scalar8Scalar8IndexHandle(RootRouterOffset, GetScalar8Scalar8Profile()),
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

    private Scalar8Scalar8RangeReader OpenScalar8Scalar8RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        return session.OpenScalar8Scalar8RangeReader(RootRouterOffset, GetScalar8Scalar8Profile(), lowerEncodedKey, upperEncodedKey, direction);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        Scalar16Scalar8Profile profile = GetScalar16Scalar8Profile();
        if (profile.Descending)
        {
            using Scalar16Scalar8RangeReader reader = session.OpenScalar16Scalar8RangeReader(
                RootRouterOffset, profile, lowerHigh, lowerLow, upperHigh, upperLow, QueryDirection.Descending);
            int copied = 0;
            while (reader.MovePrevious())
            {
                if (copied >= identities.Length)
                    throw new ArgumentException("The identity output span is too small for the requested SS16-8 range.", nameof(identities));
                identities[copied++] = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
            }
            return new LibraDexGenericRangeReadResult(
                copied, UsedPooledScratch: true, UsedEncodedIdentityScratch: false,
                CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }

        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar8IdentityRange(
                RootRouterOffset,
                profile,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: Scalar16Scalar8Layout.KeySize + 1,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, profile.ShelfExtentSize),
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

    private Scalar16Scalar8RangeReader OpenScalar16Scalar8RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar8RangeReader(RootRouterOffset, GetScalar16Scalar8Profile(), lowerHigh, lowerLow, upperHigh, upperLow, direction);
    }

    private LibraDexGenericRangeReadResult ReadScalar8Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        Scalar8Scalar16Profile profile = GetScalar8Scalar16Profile();
        if (profile.Descending)
        {
            using Scalar8Scalar16RangeReader reader = session.OpenScalar8Scalar16RangeReader(
                RootRouterOffset, profile, lowerEncodedKey, upperEncodedKey, QueryDirection.Descending);
            int copied = 0;
            while (reader.MovePrevious())
            {
                if (copied >= identities.Length)
                    throw new ArgumentException("The identity output span is too small for the requested SS8-16 range.", nameof(identities));
                identities[copied++] = LibraDexGenericScalarCodec<TIdentity>.Decode16(
                    reader.CurrentEncodedIdentityHigh, reader.CurrentEncodedIdentityLow);
            }
            return new LibraDexGenericRangeReadResult(
                copied, UsedPooledScratch: true, UsedEncodedIdentityScratch: false,
                CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }

        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar8Scalar16IdentityRange(
                RootRouterOffset,
                profile,
                lowerEncodedKey,
                upperEncodedKey,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, profile.ShelfExtentSize),
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

    private Scalar8Scalar16RangeReader OpenScalar8Scalar16RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        ulong lowerEncodedKey = EncodeKey8(lowerKey);
        ulong upperEncodedKey = EncodeKey8(upperKey);
        return session.OpenScalar8Scalar16RangeReader(RootRouterOffset, GetScalar8Scalar16Profile(), lowerEncodedKey, upperEncodedKey, direction);
    }

    private LibraDexGenericRangeReadResult ReadScalar16Scalar16Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        Scalar16Scalar16Profile profile = GetScalar16Scalar16Profile();
        if (profile.Descending)
        {
            using Scalar16Scalar16RangeReader reader = session.OpenScalar16Scalar16RangeReader(
                RootRouterOffset, profile, lowerHigh, lowerLow, upperHigh, upperLow, QueryDirection.Descending);
            int copied = 0;
            while (reader.MovePrevious())
            {
                if (copied >= identities.Length)
                    throw new ArgumentException("The identity output span is too small for the requested SS16-16 range.", nameof(identities));
                identities[copied++] = LibraDexGenericScalarCodec<TIdentity>.Decode16(
                    reader.CurrentEncodedIdentityHigh, reader.CurrentEncodedIdentityLow);
            }
            return new LibraDexGenericRangeReadResult(
                copied, UsedPooledScratch: true, UsedEncodedIdentityScratch: false,
                CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }

        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadScalar16Scalar16IdentityRange(
                RootRouterOffset,
                profile,
                lowerHigh,
                lowerLow,
                upperHigh,
                upperLow,
                maxRouterHops: 8,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, profile.ShelfExtentSize),
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

    private Scalar16Scalar16RangeReader OpenScalar16Scalar16RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        LibraDexGenericScalarCodec<TKey>.Encode16(lowerKey, out ulong lowerHigh, out ulong lowerLow);
        LibraDexGenericScalarCodec<TKey>.Encode16(upperKey, out ulong upperHigh, out ulong upperLow);
        return session.OpenScalar16Scalar16RangeReader(RootRouterOffset, GetScalar16Scalar16Profile(), lowerHigh, lowerLow, upperHigh, upperLow, direction);
    }

    private LibraDexGenericRangeReadResult ReadFixed32Scalar8Range(
        TKey lowerKey,
        TKey upperKey,
        Span<TIdentity> identities)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        Fixed32Scalar8Profile profile = GetFixed32Scalar8Profile();
        if (profile.Descending)
        {
            using Fixed32Scalar8RangeReader reader = session.OpenFixed32Scalar8RangeReader(
                RootRouterOffset, profile, lower0, lower1, lower2, lower3,
                upper0, upper1, upper2, upper3, QueryDirection.Descending);
            int copied = 0;
            while (reader.MovePrevious())
            {
                if (copied >= identities.Length)
                    throw new ArgumentException("The identity output span is too small for the requested FS32-8 range.", nameof(identities));
                identities[copied++] = LibraDexGenericScalarCodec<TIdentity>.Decode8(reader.CurrentEncodedIdentity);
            }
            return new LibraDexGenericRangeReadResult(
                copied, UsedPooledScratch: true, UsedEncodedIdentityScratch: false,
                CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }

        ulong[] encodedIdentities = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar8IdentityRange(
                RootRouterOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: Fixed32Scalar8Layout.KeySize + 1,
                encodedIdentities.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, profile.ShelfExtentSize),
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

    private Fixed32Scalar8RangeReader OpenFixed32Scalar8RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar8RangeReader(RootRouterOffset, GetFixed32Scalar8Profile(), lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, direction);
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
        Fixed32Scalar16Profile profile = GetFixed32Scalar16Profile();
        if (profile.Descending)
        {
            using Fixed32Scalar16RangeReader reader = OpenFixed32Scalar16RangeReader(lowerKey, upperKey, QueryDirection.Descending);
            int copied = 0;
            while (copied < identities.Length && reader.TryReadPreviousEncodedTuple(
                out _, out _, out _, out _, out ulong identityHigh, out ulong identityLow))
                identities[copied++] = LibraDexGenericScalarCodec<TIdentity>.Decode16(identityHigh, identityLow);
            return new LibraDexGenericRangeReadResult(copied, UsedPooledScratch: false, UsedEncodedIdentityScratch: false, CoalescingEnabled: false, RangeScratchShelfCapacity: 1);
        }

        ulong[] identityHighs = ArrayPool<ulong>.Shared.Rent(identities.Length);
        ulong[] identityLows = ArrayPool<ulong>.Shared.Rent(identities.Length);
        byte[] shelfScratch = ArrayPool<byte>.Shared.Rent(profile.ShelfExtentSize);
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        try
        {
            int count = session.ReadFixed32Scalar16IdentityRange(
                RootRouterOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 33,
                identityHighs.AsSpan(0, identities.Length),
                identityLows.AsSpan(0, identities.Length),
                shelfScratch.AsSpan(0, profile.ShelfExtentSize),
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

    private Fixed32Scalar16RangeReader OpenFixed32Scalar16RangeReader(TKey lowerKey, TKey upperKey, QueryDirection direction = QueryDirection.Ascending)
    {
        LibraDexGenericScalarCodec<TKey>.Encode32(lowerKey, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3);
        LibraDexGenericScalarCodec<TKey>.Encode32(upperKey, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        return session.OpenFixed32Scalar16RangeReader(RootRouterOffset, GetFixed32Scalar16Profile(), lower0, lower1, lower2, lower3, upper0, upper1, upper2, upper3, direction);
    }

    /// <summary>
    /// Determines whether a completed condition rooted at this opened index matches at least one identity.<br/>
    /// This convenience is intentionally single-index scoped: composed cross-index conditions should be executed from the owning identity group so every referenced index can be resolved by name.<br/>
    /// Execution uses the condition planner's stop-first existence path and does not materialize a result collection.<br/>
    /// </summary>
    /// <param name="condition">The completed condition to test.</param>
    /// <returns><see langword="true"/> when the condition matches at least one identity; otherwise, <see langword="false"/>.</returns>
    public bool Exists(LibraDexConditionEndCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        IIdentityCriterion criterion = condition.MaterializeWithProjectionBridge(
            ResolveOwnIndex,
            ResolveOwnProjectionIndex);
        return LibraDexIdentityExecutionPlanner.Exists(criterion, IdentityDeduplication.Preserve);
    }

    /// <summary>
    /// Inserts one borrowed fixed-width binary key through the same physical admission paths used by ordinary typed mutation.<br/>
    /// Immediate 8-, 16-, and 32-byte keys are decoded directly into persisted scalar lanes; an explicit batch, single-key identity contract, or maintained projection copies deliberately because that owner must retain a CLR key beyond this call.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed before this method returns.<br/></param>
    /// <param name="identity">The runtime identity matching <typeparamref name="TIdentity"/>.<br/></param>
    /// <returns>The ordinary insert result and telemetry.<br/></returns>
    LibraDexGenericInsertResult IFixedBinaryKeyIndex<TIdentity>.Insert(ReadOnlySpan<byte> key, TIdentity identity)
    {
        ThrowIfDisposed();
        ValidateBorrowedFixedBinaryKey(key);
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity ||
            exactReversedProjection is not null ||
            BatchManager.ActiveBatch is not null ||
            catalog?.TryGetActiveIdentityGroupBatch(Group, out _) == true ||
            shape is not (
                LibraDexGenericScalarShape.SS88 or
                LibraDexGenericScalarShape.SS168 or
                LibraDexGenericScalarShape.FS328))
        {
            return Insert((TKey)(object)key.ToArray(), identity);
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => InsertScalar8Scalar8UsingAdaptiveAdmission(
                BinaryPrimitives.ReadUInt64BigEndian(key),
                encodedIdentity),
            LibraDexGenericScalarShape.SS168 => InsertScalar16Scalar8UsingWriterContextOrFallback(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key[8..]),
                encodedIdentity),
            _ => InsertFixed32Scalar8UsingWriterContextOrFallback(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(24, 8)),
                encodedIdentity)
        };
    }

    /// <summary>
    /// Deletes one borrowed fixed-width binary key and runtime identity tuple without allocating a standalone key array on supported immediate scalar-identity paths.<br/>
    /// Writer-context topology fallback may copy deliberately only after the borrowed-lane path proves that a typed range-reader fallback is required.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed before this method returns.<br/></param>
    /// <param name="identity">The runtime identity matching <typeparamref name="TIdentity"/>.<br/></param>
    /// <returns><see langword="true"/> when one exact tuple was removed.<br/></returns>
    bool IFixedBinaryKeyIndex<TIdentity>.Delete(ReadOnlySpan<byte> key, TIdentity identity)
    {
        ThrowIfDisposed();
        ValidateBorrowedFixedBinaryKey(key);
        if (identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity ||
            exactReversedProjection is not null ||
            shape is not (
                LibraDexGenericScalarShape.SS88 or
                LibraDexGenericScalarShape.SS168 or
                LibraDexGenericScalarShape.FS328))
        {
            return ((IIndex)this).Delete(key.ToArray(), identity!);
        }

        ThrowIfSessionDurabilityBatchActiveForImmediateMutation();
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        bool deleted = shape switch
        {
            LibraDexGenericScalarShape.SS88 => DeleteBorrowedScalar8Scalar8(
                BinaryPrimitives.ReadUInt64BigEndian(key),
                encodedIdentity),
            LibraDexGenericScalarShape.SS168 => DeleteBorrowedScalar16Scalar8(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key[8..]),
                encodedIdentity,
                key,
                identity),
            _ => DeleteBorrowedFixed32Scalar8(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(24, 8)),
                encodedIdentity,
                key,
                identity)
        };
        if (deleted)
            RecordDelete();
        return deleted;
    }

    /// <summary>
    /// Tests one borrowed fixed-width binary key through the existing count-only routed primitives.<br/>
    /// The borrowed bytes are decoded into scalar lanes and are never retained or copied on supported scalar-identity shapes.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed before this method returns.<br/></param>
    /// <returns><see langword="true"/> when at least one tuple uses the key; otherwise <see langword="false"/>.<br/></returns>
    bool IFixedBinaryKeyIndex<TIdentity>.ContainsKey(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        ValidateBorrowedFixedBinaryKey(key);
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => ContainsBorrowedScalar8Key(
                BinaryPrimitives.ReadUInt64BigEndian(key)),
            LibraDexGenericScalarShape.SS168 => ContainsBorrowedScalar16Key(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key[8..])),
            LibraDexGenericScalarShape.FS328 => ContainsBorrowedFixed32Key(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(24, 8))),
            _ => Keys.Exists((TKey)(object)key.ToArray())
        };
    }

    /// <summary>
    /// Tests one exact borrowed fixed-width key and identity tuple without materializing a CLR key array on supported scalar-identity shapes.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed before this method returns.<br/></param>
    /// <param name="identity">The runtime identity matching <typeparamref name="TIdentity"/>.<br/></param>
    /// <returns><see langword="true"/> when the exact tuple exists; otherwise <see langword="false"/>.<br/></returns>
    bool IFixedBinaryKeyIndex<TIdentity>.ContainsTuple(ReadOnlySpan<byte> key, TIdentity identity)
        => ContainsBorrowedFixedBinaryIdentity(key, identity, requireDifferentIdentity: false);

    /// <summary>
    /// Tests whether one borrowed fixed-width key is owned by any identity other than the supplied identity without materializing a CLR key array on supported scalar-identity shapes.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed before this method returns.<br/></param>
    /// <param name="identity">The runtime identity allowed to retain the key.<br/></param>
    /// <returns><see langword="true"/> when a different identity owns the key; otherwise <see langword="false"/>.<br/></returns>
    bool IFixedBinaryKeyIndex<TIdentity>.ContainsOtherIdentity(ReadOnlySpan<byte> key, TIdentity identity)
        => ContainsBorrowedFixedBinaryIdentity(key, identity, requireDifferentIdentity: true);

    /// <summary>
    /// Tests whether one typed key is owned by any identity other than the supplied identity without boxing or materializing a result collection on fixed-scalar shapes.<br/>
    /// Variable or otherwise unsupported shapes retain a correct typed exact-lookup fallback.<br/>
    /// </summary>
    /// <param name="key">The typed key whose exact route is inspected.<br/></param>
    /// <param name="identity">The identity permitted to retain the key.<br/></param>
    /// <returns><see langword="true"/> when a different identity owns the key; otherwise <see langword="false"/>.<br/></returns>
    internal bool ContainsOtherIdentity(TKey key, TIdentity identity)
    {
        ThrowIfDisposed();
        if (key is null)
            throw new ArgumentNullException(nameof(key));
        if (identity is null)
            throw new ArgumentNullException(nameof(identity));

        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        switch (shape)
        {
            case LibraDexGenericScalarShape.SS88:
                return ContainsBorrowedScalar8Identity(
                    EncodeKey8(key),
                    encodedIdentity,
                    requireDifferentIdentity: true);

            case LibraDexGenericScalarShape.SS168:
                LibraDexGenericScalarCodec<TKey>.Encode16(key, out ulong high, out ulong low);
                return ContainsBorrowedScalar16Identity(
                    high,
                    low,
                    encodedIdentity,
                    requireDifferentIdentity: true);

            case LibraDexGenericScalarShape.FS328:
                LibraDexGenericScalarCodec<TKey>.Encode32(
                    key,
                    out ulong part0,
                    out ulong part1,
                    out ulong part2,
                    out ulong part3);
                return ContainsBorrowedFixed32Identity(
                    part0,
                    part1,
                    part2,
                    part3,
                    encodedIdentity,
                    requireDifferentIdentity: true);
        }

        IReadOnlyList<TIdentity> identities = Identities.GetByKey(key);
        EqualityComparer<TIdentity> comparer = EqualityComparer<TIdentity>.Default;
        for (int i = 0; i < identities.Count; i++)
        {
            if (!comparer.Equals(identities[i], identity))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scans one exact borrowed fixed-width key route for either the supplied encoded identity or any different encoded identity.<br/>
    /// Unsupported identity shapes deliberately materialize the key through the ordinary public route.<br/>
    /// </summary>
    /// <param name="key">The fixed-width key bytes consumed synchronously.<br/></param>
    /// <param name="identity">The runtime identity used for comparison.<br/></param>
    /// <param name="requireDifferentIdentity"><see langword="true"/> to seek another owner; <see langword="false"/> to seek the exact tuple.<br/></param>
    /// <returns><see langword="true"/> when the requested ownership relation exists.<br/></returns>
    private bool ContainsBorrowedFixedBinaryIdentity(
        ReadOnlySpan<byte> key,
        TIdentity identity,
        bool requireDifferentIdentity)
    {
        ThrowIfDisposed();
        ValidateBorrowedFixedBinaryKey(key);
        if (shape is not (
            LibraDexGenericScalarShape.SS88 or
            LibraDexGenericScalarShape.SS168 or
            LibraDexGenericScalarShape.FS328))
        {
            IReadOnlyList<LibraDexIndexEntry<TKey, TIdentity>> entries =
                Entries.GetByKey((TKey)(object)key.ToArray());
            for (int index = 0; index < entries.Count; index++)
            {
                TIdentity existing = entries[index].Identity;
                if (requireDifferentIdentity ? !EqualityComparer<TIdentity>.Default.Equals(existing, identity) : EqualityComparer<TIdentity>.Default.Equals(existing, identity))
                    return true;
            }

            return false;
        }

        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        return shape switch
        {
            LibraDexGenericScalarShape.SS88 => ContainsBorrowedScalar8Identity(
                BinaryPrimitives.ReadUInt64BigEndian(key),
                encodedIdentity,
                requireDifferentIdentity),
            LibraDexGenericScalarShape.SS168 => ContainsBorrowedScalar16Identity(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key[8..]),
                encodedIdentity,
                requireDifferentIdentity),
            _ => ContainsBorrowedFixed32Identity(
                BinaryPrimitives.ReadUInt64BigEndian(key[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(key.Slice(24, 8)),
                encodedIdentity,
                requireDifferentIdentity)
        };
    }

    /// <summary>
    /// Scans one exact encoded `SS8-8` key route for the requested identity relation.<br/>
    /// </summary>
    private bool ContainsBorrowedScalar8Identity(ulong key, ulong identity, bool requireDifferentIdentity)
    {
        using Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            key,
            key);
        while (reader.TryReadNextEncodedIdentity(out ulong existing))
        {
            if (requireDifferentIdentity ? existing != identity : existing == identity)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scans one exact encoded `SS16-8` key route for the requested identity relation.<br/>
    /// </summary>
    private bool ContainsBorrowedScalar16Identity(
        ulong high,
        ulong low,
        ulong identity,
        bool requireDifferentIdentity)
    {
        using Scalar16Scalar8RangeReader reader = session.OpenScalar16Scalar8RangeReader(
            RootRouterOffset,
            GetScalar16Scalar8Profile(),
            high,
            low,
            high,
            low);
        while (reader.TryReadNextEncodedIdentity(out ulong existing))
        {
            if (requireDifferentIdentity ? existing != identity : existing == identity)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scans one exact encoded `FS32-8` key route for the requested identity relation.<br/>
    /// </summary>
    private bool ContainsBorrowedFixed32Identity(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong identity,
        bool requireDifferentIdentity)
    {
        using Fixed32Scalar8RangeReader reader = session.OpenFixed32Scalar8RangeReader(
            RootRouterOffset,
            GetFixed32Scalar8Profile(),
            key0,
            key1,
            key2,
            key3,
            key0,
            key1,
            key2,
            key3);
        while (reader.TryReadNextEncodedIdentity(out ulong existing))
        {
            if (requireDifferentIdentity ? existing != identity : existing == identity)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a borrowed key matches this generic index's fixed-width byte-array contract.<br/>
    /// </summary>
    /// <param name="key">The borrowed key to validate.<br/></param>
    private void ValidateBorrowedFixedBinaryKey(ReadOnlySpan<byte> key)
    {
        int width = FixedKeyByteWidth
            ?? throw new NotSupportedException($"Index '{Name}' is not a fixed-width byte-array key index.");
        if (key.Length != width)
        {
            throw new ArgumentException(
                $"Index '{Name}' requires exactly {width} fixed binary key bytes, not {key.Length}.",
                nameof(key));
        }
    }

    /// <summary>
    /// Tests one encoded 8-byte key through the session's count-only `SS8-8` primitive.<br/>
    /// </summary>
    /// <param name="key">The encoded key lane.<br/></param>
    /// <returns><see langword="true"/> when at least one identity uses the key.<br/></returns>
    private bool ContainsBorrowedScalar8Key(ulong key)
        => session.CountScalar8Scalar8IdentityRange(
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            key,
            key) != 0;

    /// <summary>
    /// Tests one encoded 16-byte key through the session's count-only `SS16-8` primitive.<br/>
    /// </summary>
    /// <param name="high">The high encoded key lane.<br/></param>
    /// <param name="low">The low encoded key lane.<br/></param>
    /// <returns><see langword="true"/> when at least one identity uses the key.<br/></returns>
    private bool ContainsBorrowedScalar16Key(ulong high, ulong low)
        => session.CountScalar16Scalar8IdentityRange(
            RootRouterOffset,
            GetScalar16Scalar8Profile(),
            high,
            low,
            high,
            low) != 0;

    /// <summary>
    /// Tests one encoded 32-byte key through the session's count-only `FS32-8` primitive.<br/>
    /// </summary>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <returns><see langword="true"/> when at least one identity uses the key.<br/></returns>
    private bool ContainsBorrowedFixed32Key(ulong key0, ulong key1, ulong key2, ulong key3)
        => session.CountFixed32Scalar8IdentityRange(
            RootRouterOffset,
            GetFixed32Scalar8Profile(),
            key0,
            key1,
            key2,
            key3,
            key0,
            key1,
            key2,
            key3) != 0;

    /// <summary>
    /// Deletes one encoded `SS8-8` tuple through the immediate classified route walker.<br/>
    /// </summary>
    /// <param name="key">The encoded key lane.<br/></param>
    /// <param name="identity">The encoded identity lane.<br/></param>
    /// <returns><see langword="true"/> when one tuple was removed.<br/></returns>
    private bool DeleteBorrowedScalar8Scalar8(ulong key, ulong identity)
        => session.DeleteWalkedRoutedScalar8Scalar8Exact(
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            key,
            identity,
            maxRouterHops: 8,
            Scalar8Scalar8RouteReadPolicy.PreferArenaCache,
            out _);

    /// <summary>
    /// Deletes one encoded `SS16-8` tuple through a borrowed writer context, copying only if topology fallback requires a typed range reader.<br/>
    /// </summary>
    /// <param name="keyHigh">The encoded high key lane.<br/></param>
    /// <param name="keyLow">The encoded low key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded identity lane.<br/></param>
    /// <param name="borrowedKey">The borrowed key retained only for a synchronous exceptional fallback.<br/></param>
    /// <param name="identity">The typed identity used by the existing fallback.<br/></param>
    /// <returns><see langword="true"/> when one tuple was removed.<br/></returns>
    private bool DeleteBorrowedScalar16Scalar8(
        ulong keyHigh,
        ulong keyLow,
        ulong encodedIdentity,
        ReadOnlySpan<byte> borrowedKey,
        TIdentity identity)
    {
        LibraDexWriteContext writeContext = session.BeginScalar16Scalar8WriteContext();
        try
        {
            bool deleted = session.DeleteWalkedRoutedScalar16Scalar8ExactForWriteContext(
                writeContext,
                RootRouterOffset,
                GetScalar16Scalar8Profile(),
                keyHigh,
                keyLow,
                encodedIdentity,
                maxRouterHops: Scalar16Scalar8Layout.KeySize + 1);
            if (!deleted)
            {
                session.AbortScalar16Scalar8WriteContext(writeContext);
                return false;
            }

            _ = session.PublishScalar16Scalar8WriteContext(writeContext);
            return true;
        }
        catch (LibraDexWriteContextScalar16Scalar8ShelfOwnershipException ex)
        {
            session.AbortScalar16Scalar8WriteContext(writeContext);
            session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            return DeleteBorrowedScalar16Scalar8(keyHigh, keyLow, encodedIdentity, borrowedKey, identity);
        }
        catch (InvalidOperationException)
        {
            session.AbortScalar16Scalar8WriteContext(writeContext);
            return DeleteExactScalar16Scalar8Tuple((TKey)(object)borrowedKey.ToArray(), identity);
        }
    }

    /// <summary>
    /// Deletes one encoded `FS32-8` tuple through a borrowed writer context, copying only if topology fallback requires a typed range reader.<br/>
    /// </summary>
    /// <param name="key0">The first encoded key lane.<br/></param>
    /// <param name="key1">The second encoded key lane.<br/></param>
    /// <param name="key2">The third encoded key lane.<br/></param>
    /// <param name="key3">The fourth encoded key lane.<br/></param>
    /// <param name="encodedIdentity">The encoded identity lane.<br/></param>
    /// <param name="borrowedKey">The borrowed key retained only for a synchronous exceptional fallback.<br/></param>
    /// <param name="identity">The typed identity used by the existing fallback.<br/></param>
    /// <returns><see langword="true"/> when one tuple was removed.<br/></returns>
    private bool DeleteBorrowedFixed32Scalar8(
        ulong key0,
        ulong key1,
        ulong key2,
        ulong key3,
        ulong encodedIdentity,
        ReadOnlySpan<byte> borrowedKey,
        TIdentity identity)
    {
        LibraDexWriteContext writeContext = session.BeginFixed32Scalar8WriteContext();
        try
        {
            bool deleted = session.DeleteWalkedRoutedFixed32Scalar8ExactForWriteContext(
                writeContext,
                RootRouterOffset,
                GetFixed32Scalar8Profile(),
                key0,
                key1,
                key2,
                key3,
                encodedIdentity,
                maxRouterHops: 32);
            if (!deleted)
            {
                session.AbortFixed32Scalar8WriteContext(writeContext);
                return false;
            }

            _ = session.PublishFixed32Scalar8WriteContext(writeContext);
            return true;
        }
        catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException ex)
        {
            session.AbortFixed32Scalar8WriteContext(writeContext);
            session.WaitForWriteContextShelfRelease(ex.ShelfOffset, CancellationToken.None);
            return DeleteBorrowedFixed32Scalar8(
                key0,
                key1,
                key2,
                key3,
                encodedIdentity,
                borrowedKey,
                identity);
        }
        catch (InvalidOperationException)
        {
            session.AbortFixed32Scalar8WriteContext(writeContext);
            return DeleteExactFixed32Scalar8Tuple((TKey)(object)borrowedKey.ToArray(), identity);
        }
    }

    bool ILibraDexIdentityInverseLookup.TryIdentityExistsFromInverse(object identity, out bool exists)
    {
        if (catalog is not null)
            return catalog.TryIdentityExistsFromInverse(this, identity, out exists);

        exists = false;
        return false;
    }

    /// <summary>
    /// Builds this empty `SS8-8` index from tuples already arranged in the index's stable key-then-identity order.<br/>
    /// The method encodes each tuple once, validates global ordering and index contracts before allocation, packs unreachable shelves and routers directly, and publishes the completed generation through one stable-root rewrite.<br/>
    /// This first native-builder slice accepts only an empty scalar-8/scalar-8 index and does not build an identity inversion; configured lazy identity lookup remains lazy.<br/>
    /// </summary>
    /// <param name="tuples">Typed tuples sorted by this index's encoded key order and then encoded identity order.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before durable child publication begins.<br/></param>
    /// <returns>Tuple, topology, timing, and durability diagnostics for the completed build.<br/></returns>
    /// <exception cref="NotSupportedException">Thrown when the physical shape is not `SS8-8`, a maintained reversed projection exists, or a null key/identity is supplied.<br/></exception>
    /// <exception cref="InvalidOperationException">Thrown when the index is not empty or an index uniqueness/multiplicity contract is violated.<br/></exception>
    /// <exception cref="ArgumentException">Thrown when tuples are inverted or repeated.<br/></exception>
    public LibraDexSortedBuildDiagnostics BuildFromSorted(
        ReadOnlySpan<LibraDexSortedTuple<TKey, TIdentity>> tuples,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureNativeSortedBuildSupported();

        var encoded = new Scalar8Scalar8SortedTuple[tuples.Length];
        for (int i = 0; i < tuples.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraDexSortedTuple<TKey, TIdentity> tuple = tuples[i];
            if (tuple.Key is null)
                throw new NotSupportedException("The native SS8-8 sorted builder does not yet populate null key-state routes.");
            if (tuple.Identity is null)
                throw new NotSupportedException("The native SS8-8 sorted builder does not accept a null identity.");
            encoded[i] = new Scalar8Scalar8SortedTuple(
                EncodeKey8(tuple.Key),
                LibraDexGenericScalarCodec<TIdentity>.Encode8(tuple.Identity));
        }

        return BuildFromSortedEncoded(
            new Scalar8Scalar8SortedArraySource(encoded),
            identityMultiplicityAlreadyValidated: false,
            cancellationToken);
    }

    /// <summary>
    /// Gets whether this index can use the current shape-specific native sorted builder before an owner starts producing spill runs.<br/>
    /// The check is intentionally internal because the public method remains the authoritative exception-reporting contract.<br/>
    /// </summary>
    internal bool SupportsScalar8Scalar8SortedBuild
    {
        get
        {
            ThrowIfDisposed();
            return shape == LibraDexGenericScalarShape.SS88 && exactReversedProjection is null;
        }
    }

    /// <summary>
    /// Builds this empty index from a stable seekable encoded source while preserving the public native-builder validation and diagnostics contract.<br/>
    /// Authoritative owners may suppress the otherwise unbounded identity set only after proving one extracted tuple per identity by construction.<br/>
    /// </summary>
    /// <param name="tuples">Stable encoded tuple source in key-then-identity order.<br/></param>
    /// <param name="identityMultiplicityAlreadyValidated">Whether the source lifecycle already proved one key per identity.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before durable child publication begins.<br/></param>
    /// <returns>Tuple, topology, timing, and durability diagnostics for the completed build.<br/></returns>
    internal LibraDexSortedBuildDiagnostics BuildFromSortedEncoded(
        IScalar8Scalar8SortedTupleSource tuples,
        bool identityMultiplicityAlreadyValidated,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(tuples);
        EnsureNativeSortedBuildSupported();
        Scalar8Scalar8SortedBuildResult result = session.BuildScalar8Scalar8FromSorted(
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            tuples,
            keyContract == IndexKeys.NonUnique,
            identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity,
            identityMultiplicityAlreadyValidated,
            cancellationToken);
        return new LibraDexSortedBuildDiagnostics(
            result.TupleCount,
            result.OrdinaryShelfCount,
            result.TerminalRootCount,
            result.TerminalShelfCount,
            result.RouterCount,
            result.ReachableTopologyBytes,
            result.GrowthCanaryLimitBytes,
            result.StorageBuildTime,
            result.RootPublicationTime,
            LibraDexOperationDiagnostics.FromDataKernel(result.ChildStorageCommit),
            LibraDexOperationDiagnostics.FromDataKernel(result.RootPublicationCommit));
    }

    /// <summary>
    /// Rejects physical shapes and maintained projections that the first native sorted-builder contract cannot populate atomically.<br/>
    /// </summary>
    private void EnsureNativeSortedBuildSupported()
    {
        if (shape != LibraDexGenericScalarShape.SS88)
            throw new NotSupportedException("The native sorted builder currently supports only scalar-8 keys with scalar-8 identities.");
        if (exactReversedProjection is not null)
            throw new NotSupportedException("The native sorted builder does not yet populate a maintained exact reversed projection.");
    }

    /// <summary>
    /// Adapts an authoritative object tuple stream to the typed native-builder surface for shape-aware internal owners such as closed-file compaction.<br/>
    /// Unsupported physical shapes are rejected before enumeration; null key-state rows return <see langword="false"/> before storage mutation so the caller may rerun the source through its compatibility path.<br/>
    /// </summary>
    bool ILibraDexNativeSortedBuild.TryBuildFromSortedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        tupleCount = 0;
        if (shape != LibraDexGenericScalarShape.SS88 || exactReversedProjection is not null)
            return false;

        var typed = new List<LibraDexSortedTuple<TKey, TIdentity>>();
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tuple.Key is not TKey key || tuple.Identity is not TIdentity identity)
                return false;
            typed.Add(new LibraDexSortedTuple<TKey, TIdentity>(key, identity));
        }

        _ = BuildFromSorted(typed.ToArray(), cancellationToken);
        tupleCount = typed.Count;
        return true;
    }

    /// <summary>
    /// Adapts an unordered legacy object-tuple stream to the same encoded array and native topology builder used by the public sorted path.<br/>
    /// This recovery-only bridge performs one bounded-to-index managed materialization, orders encoded tuples by key and identity, and then applies the complete native uniqueness and multiplicity validation before storage allocation.<br/>
    /// </summary>
    /// <param name="tuples">Authoritative source tuples whose traversal order is not trusted.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before durable child publication begins.<br/></param>
    /// <param name="tupleCount">Receives the number of tuples built when the method returns <see langword="true"/>.<br/></param>
    /// <returns><see langword="true"/> when this is a supported non-null `SS8-8` shape; otherwise <see langword="false"/> before storage mutation.<br/></returns>
    bool ILibraDexNativeSortedBuild.TryBuildFromUnorderedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        tupleCount = 0;
        if (shape != LibraDexGenericScalarShape.SS88 || exactReversedProjection is not null)
            return false;

        var encoded = new List<Scalar8Scalar8SortedTuple>();
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tuple.Key is not TKey key || tuple.Identity is not TIdentity identity)
                return false;
            encoded.Add(new Scalar8Scalar8SortedTuple(
                EncodeKey8(key),
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)));
        }

        encoded.Sort(static (left, right) =>
        {
            int keyComparison = left.Key.CompareTo(right.Key);
            return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
        });
        _ = BuildFromSortedEncoded(
            new Scalar8Scalar8SortedArraySource(encoded.ToArray()),
            identityMultiplicityAlreadyValidated: false,
            cancellationToken);
        tupleCount = encoded.Count;
        return true;
    }

    /// <summary>
    /// Replaces this live `SS8-8` stable-root generation from an authoritative unordered object stream.<br/>
    /// All tuples are typed, encoded, sorted, and validated before candidate storage is built; publication retains the existing root offset so current owners immediately observe the replacement.<br/>
    /// Unsupported shapes, maintained reversed projections, or null tuple members return <see langword="false"/> before storage mutation.<br/>
    /// </summary>
    /// <param name="tuples">Authoritative unordered tuples to replace the current generation.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before replacement publication.<br/></param>
    /// <param name="tupleCount">Receives the number of replacement tuples when successful.<br/></param>
    /// <returns><see langword="true"/> when native replacement completed; otherwise <see langword="false"/> before mutation.<br/></returns>
    bool ILibraDexNativeSortedBuild.TryReplaceFromUnorderedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        tupleCount = 0;
        if (shape != LibraDexGenericScalarShape.SS88 || exactReversedProjection is not null)
            return false;

        var encoded = new List<Scalar8Scalar8SortedTuple>();
        foreach (LibraDexObjectTuple tuple in tuples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tuple.Key is not TKey key || tuple.Identity is not TIdentity identity)
                return false;
            encoded.Add(new Scalar8Scalar8SortedTuple(
                EncodeKey8(key),
                LibraDexGenericScalarCodec<TIdentity>.Encode8(identity)));
        }

        encoded.Sort(static (left, right) =>
        {
            int keyComparison = left.Key.CompareTo(right.Key);
            return keyComparison != 0 ? keyComparison : left.Identity.CompareTo(right.Identity);
        });
        Scalar8Scalar8SortedBuildResult result = session.ReplaceScalar8Scalar8StableRootFromSorted(
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            new Scalar8Scalar8SortedArraySource(encoded.ToArray()),
            keyContract == IndexKeys.NonUnique,
            identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity,
            identityMultiplicityAlreadyValidated: false,
            cancellationToken);
        tupleCount = result.TupleCount;
        return true;
    }

    /// <summary>
    /// Opens a forward-only reader over every tuple in this index's stable key and identity order.<br/>
    /// The returned reader exposes source-aware skipping and borrowed cursor state without first materializing the index contents.<br/>
    /// Callers own the returned reader and must dispose it.<br/>
    /// </summary>
    /// <param name="direction">Direction in which to traverse the complete ordered index.<br/></param>
    /// <returns>A range reader positioned before the first tuple in the requested direction.<br/></returns>
    public LibraDexRangeReader<TKey, TIdentity> OpenReader(QueryDirection direction = QueryDirection.Ascending)
        => OpenAllRangeReader(direction);

    /// <summary>
    /// Captures this live index's slot, root, profile, and physical contracts for one later group-level detached replacement.<br/>
    /// Creating the request performs no storage mutation; the stable encoded source must remain available until the replacement call returns.<br/>
    /// </summary>
    /// <param name="tuples">Stable seekable encoded tuple source in key-then-identity order.<br/></param>
    /// <param name="identityMultiplicityAlreadyValidated">Whether authoritative extraction already proved one key per identity.<br/></param>
    /// <returns>A session-owned detached replacement request that is not independently publishable.<br/></returns>
    internal Scalar8Scalar8DetachedBuildRequest CreateDetachedSortedBuildRequest(
        IScalar8Scalar8SortedTupleSource tuples,
        bool identityMultiplicityAlreadyValidated)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(tuples);
        EnsureNativeSortedBuildSupported();
        return new Scalar8Scalar8DetachedBuildRequest(
            SlotIndex,
            RootRouterOffset,
            GetScalar8Scalar8Profile(),
            tuples,
            keyContract == IndexKeys.NonUnique,
            identityKeyMultiplicity == IdentityKeyMultiplicity.SingleKeyPerIdentity,
            identityMultiplicityAlreadyValidated);
    }

    /// <summary>
    /// Encodes caller-owned typed tuples once and captures a detached replacement request for this live `SS8-8` index.<br/>
    /// This convenience overload keeps directed tests and small in-memory replacements strongly typed; bounded production feeds should use the seekable encoded-source overload.<br/>
    /// </summary>
    /// <param name="tuples">Typed tuples arranged in this index's encoded key-then-identity order.<br/></param>
    /// <returns>A detached replacement request retaining its encoded tuple array until group publication returns.<br/></returns>
    internal Scalar8Scalar8DetachedBuildRequest CreateDetachedSortedBuildRequest(
        ReadOnlySpan<LibraDexSortedTuple<TKey, TIdentity>> tuples)
    {
        ThrowIfDisposed();
        EnsureNativeSortedBuildSupported();
        var encoded = new Scalar8Scalar8SortedTuple[tuples.Length];
        for (int i = 0; i < tuples.Length; i++)
        {
            LibraDexSortedTuple<TKey, TIdentity> tuple = tuples[i];
            if (tuple.Key is null)
                throw new NotSupportedException("Detached SS8-8 replacement does not populate null key-state routes.");
            if (tuple.Identity is null)
                throw new NotSupportedException("Detached SS8-8 replacement does not accept a null identity.");
            encoded[i] = new Scalar8Scalar8SortedTuple(
                EncodeKey8(tuple.Key),
                LibraDexGenericScalarCodec<TIdentity>.Encode8(tuple.Identity));
        }

        return CreateDetachedSortedBuildRequest(
            new Scalar8Scalar8SortedArraySource(encoded),
            identityMultiplicityAlreadyValidated: false);
    }

    /// <summary>
    /// Builds and publishes a complete group of detached `SS8-8` replacements through this index's owning session.<br/>
    /// Every request must have been captured from an index in the same catalog; slot and root ownership are revalidated before allocation and again by the one directory image being replaced.<br/>
    /// </summary>
    /// <param name="requests">Detached sibling requests to prepare and publish together.<br/></param>
    /// <param name="cancellationToken">Cancellation observed until all detached generations pass pre-publication validation.<br/></param>
    /// <returns>Per-index build evidence and one directory-publication diagnostic.<br/></returns>
    internal Scalar8Scalar8DetachedBuildGroupResult ReplaceFromSortedEncoded(
        ReadOnlySpan<Scalar8Scalar8DetachedBuildRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureNativeSortedBuildSupported();
        return session.ReplaceScalar8Scalar8FromSorted(requests, cancellationToken);
    }

    /// <summary>
    /// Atomically reserves one identity for a staged writer after validating the committed `SingleKeyPerIdentity` association.<br/>
    /// The catalog-session map supplies one reservation domain for every handle opened over the same logical index, preventing independent unpublished batches from accepting conflicting keys while unrelated identities remain fully concurrent.<br/>
    /// </summary>
    /// <param name="owner">Stable writer-facade token retaining the reservation through publication or abort.<br/></param>
    /// <param name="identity">Identity being reserved.<br/></param>
    /// <param name="candidateKey">Candidate key requested by the staged operation.<br/></param>
    /// <param name="hasAlternateAllowedKey">Whether rekey semantics permit one known old key during replacement.<br/></param>
    /// <param name="alternateAllowedKey">The optional old key permitted until replacement publication completes.<br/></param>
    /// <returns><see langword="true"/> when the reservation and committed identity-key contract both permit the operation.<br/></returns>
    internal bool TryReserveStagedIdentity(
        object owner,
        TIdentity identity,
        TKey candidateKey,
        bool hasAlternateAllowedKey,
        TKey alternateAllowedKey)
    {
        return catalog.GetSingleKeyIdentityMap(this).TryReserve(
            this,
            owner,
            identity,
            candidateKey,
            hasAlternateAllowedKey,
            alternateAllowedKey);
    }

    /// <summary>
    /// Releases one unused staged identity reservation after the corresponding physical insert did not succeed.<br/>
    /// The catalog map verifies owner identity, so cleanup cannot remove another facade's later reservation.<br/>
    /// </summary>
    /// <param name="owner">Writer-facade token that acquired the reservation.<br/></param>
    /// <param name="identity">Identity whose unused reservation should be released.<br/></param>
    internal void ReleaseStagedIdentityReservation(object owner, TIdentity identity)
    {
        catalog.GetSingleKeyIdentityMap(this).Release(owner, identity);
    }

    /// <summary>
    /// Releases all identity reservations retained by one staged writer facade after publication or abort.<br/>
    /// Physical publication invalidation remains independent so intermediate context publications may refresh committed state without exposing still-owned unpublished identities to competing writers.<br/>
    /// </summary>
    /// <param name="owner">Writer-facade token whose lifetime has completed.<br/></param>
    internal void ReleaseAllStagedIdentityReservations(object owner)
    {
        catalog.GetSingleKeyIdentityMap(this).ReleaseAll(owner);
    }

    /// <summary>
    /// Streams one metadata key-state run in descending encoded identity order.<br/>
    /// Key-state shelves currently expose a forward stream, so only that equal-key run is copied; ordinary keys retain native reverse reader traversal.<br/>
    /// </summary>
    /// <param name="route">The null or empty key-state route to read.<br/></param>
    /// <returns>Decoded identities from highest to lowest encoded identity.<br/></returns>
    private IEnumerable<TIdentity> IterateDescendingKeyStateIdentities(KeyStateRoute route)
    {
        if (shape is LibraDexGenericScalarShape.SS88 or LibraDexGenericScalarShape.SS168 or LibraDexGenericScalarShape.FS328)
        {
            ulong[] identities = session.ReadScalar8KeyStateIdentities(SlotIndex, route);
            for (int i = identities.Length - 1; i >= 0; i--)
                yield return LibraDexGenericScalarCodec<TIdentity>.Decode8(identities[i])!;
            yield break;
        }

        (ulong[] highs, ulong[] lows) = session.ReadScalar16KeyStateIdentities(SlotIndex, route);
        for (int i = highs.Length - 1; i >= 0; i--)
            yield return LibraDexGenericScalarCodec<TIdentity>.Decode16(highs[i], lows[i])!;
    }

    /// <summary>
    /// Orders only membership key descriptors for descending plan-natural reads.<br/>
    /// The physical equality reader still streams each equal-key identity run in reverse tuple order.<br/>
    /// </summary>
    /// <param name="keys">The supplied membership keys.<br/></param>
    /// <param name="direction">The requested tuple direction.<br/></param>
    /// <returns>Keys in requested traversal order.<br/></returns>
    private IEnumerable<object?> OrderMembershipKeys(IEnumerable<object?> keys, QueryDirection direction)
    {
        if (direction != QueryDirection.Descending)
            return keys;
        List<object?> ordered = keys.ToList();
        ordered.Sort((left, right) =>
        {
            bool leftState = TryClassifyNullKeyRouteKey(left, out NullKey leftKeyState);
            bool rightState = TryClassifyNullKeyRouteKey(right, out NullKey rightKeyState);
            if (leftState || rightState)
            {
                if (!leftState)
                    return -1;
                if (!rightState)
                    return 1;
                return rightKeyState.CompareTo(leftKeyState);
            }
            return CompareCountKeys(RequireObjectKey(right!, nameof(keys)), RequireObjectKey(left!, nameof(keys)));
        });
        return ordered;
    }

    /// <summary>
    /// Sorts only multirange descriptors by their upper bound before reverse physical range reading.<br/>
    /// Source range arrays remain unchanged, and tuple results are never buffered for this ordering step.<br/>
    /// </summary>
    /// <param name="ranges">The requested inclusive key extents.<br/></param>
    /// <param name="direction">The requested tuple direction.<br/></param>
    /// <returns>Range descriptors in requested traversal order.<br/></returns>
    private LibraDexIdentityKeyRange[] OrderMultiRanges(LibraDexIdentityKeyRange[] ranges, QueryDirection direction)
    {
        if (direction != QueryDirection.Descending)
            return ranges;
        LibraDexIdentityKeyRange[] ordered = (LibraDexIdentityKeyRange[])ranges.Clone();
        Array.Sort(ordered, (left, right) => CompareCountKeys(
            RequireObjectKey(right.UpperKey, nameof(ranges)),
            RequireObjectKey(left.UpperKey, nameof(ranges))));
        return ordered;
    }

    /// <summary>Streams scalar-null or non-null tuple rows without materializing the complete matching route.<br/>
    /// A null request reads the identity-keyed null route; a non-null request uses the existing directed ordinary range reader.<br/>
    /// The optional Take limit is applied before opening or advancing a physical reader, and early disposal releases the reader through the underlying iterator.<br/></summary>
    /// <param name="request">Normalized scalar-null primitive with its state, direction, and optional Take limit.<br/></param>
    /// <returns>A deferred sequence of matching key and identity tuples.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateScalarNullTupleObjects(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        ScalarNull state = RequireScalarNullState(request.Values);
        if (state == ScalarNull.NonNull)
        {
            foreach (LibraDexObjectTuple tuple in IterateTupleObjects(OpenAllRangeReader(request.Direction), request.TakeLimit))
                yield return tuple;
            yield break;
        }

        foreach (TIdentity identity in IterateScalarNullRouteIdentityObjects(request.TakeLimit))
            yield return new LibraDexObjectTuple(null, identity!);
    }

    /// <summary>Streams null and empty binary key-state tuple rows in their established null-before-empty order.<br/>
    /// The optional Take limit spans both routes, so stopping in the null route never opens the empty route and early disposal retains no full-result list.<br/>
    /// Null keys remain null and empty keys remain empty byte arrays in the caller-visible tuple representation.<br/></summary>
    /// <param name="request">Normalized key-state primitive with its selected state and optional Take limit.<br/></param>
    /// <returns>A deferred sequence of matching key and identity tuples.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateNullKeyTupleObjects(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        NullKey state = RequireNullKeyState(request.Values);
        int remaining = request.TakeLimit ?? int.MaxValue;
        if (state is NullKey.Null or NullKey.NullOrEmpty)
        {
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Null, remaining))
            {
                yield return new LibraDexObjectTuple(null, identity!);
                if (--remaining == 0)
                    yield break;
            }
        }

        if (state is NullKey.Empty or NullKey.NullOrEmpty)
        {
            foreach (TIdentity identity in IterateNullKeyRouteIdentityObjects(NullKey.Empty, remaining))
            {
                yield return new LibraDexObjectTuple(Array.Empty<byte>(), identity!);
                if (--remaining == 0)
                    yield break;
            }
        }
    }

    /// <summary>Streams matching bitmask tuples directly from the directed physical key reader.<br/>
    /// The compiled predicate is evaluated per key, and Take stops the scan without buffering later matches.<br/></summary>
    /// <param name="request">Normalized bitmask predicate, direction, and optional Take limit.<br/></param>
    /// <returns>A deferred sequence of matching key and identity tuples.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateBitmaskTupleObjects(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        LibraDexBitmaskPredicate predicate = RequireBitmaskPredicate(request.Values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(request.Direction);
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (!predicate.Matches(key!))
                continue;
            yield return new LibraDexObjectTuple(key!, identity!);
            if (request.TakeLimit is not null && ++returned >= request.TakeLimit.Value)
                yield break;
        }
    }

    /// <summary>Streams matching numeric-transform tuples directly from the directed physical key reader.<br/>
    /// A transform still scans keys because an inverse key range is not generally available; only result buffering is removed.<br/></summary>
    /// <param name="request">Normalized numeric transform predicate, direction, and optional Take limit.<br/></param>
    /// <returns>A deferred sequence of matching key and identity tuples.<br/></returns>
    private IEnumerable<LibraDexObjectTuple> IterateNumericTransformTupleObjects(LibraDexIdentityPrimitiveRequest request)
    {
        if (request.TakeLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.TakeLimit, "Take cannot be negative.");
        if (request.TakeLimit == 0)
            yield break;

        LibraDexNumericTransformPredicate predicate = RequireNumericTransformPredicate(request.Values);
        int returned = 0;
        using LibraDexRangeReader<TKey, TIdentity> reader = OpenAllRangeReader(request.Direction);
        while (reader.TryReadNext(out TKey key, out TIdentity identity))
        {
            if (!predicate.Matches(key!))
                continue;
            yield return new LibraDexObjectTuple(key!, identity!);
            if (request.TakeLimit is not null && ++returned >= request.TakeLimit.Value)
                yield break;
        }
    }
}

/// <summary>
/// Tracks accepted staged identity-key associations for a single generic writer facade.<br/>
/// The guard combines committed-index checks from the owning index with staged reservations so unpublished batch or queued-writer inserts cannot create a second key for the same identity.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the owning index.</typeparam>
/// <typeparam name="TIdentity">The public identity type associated with the owning index.</typeparam>
internal sealed class LibraDexStagedIdentityKeyGuard<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly object reservationOwner = new();
    private readonly Dictionary<LibraDexIdentityMapKey<TIdentity>, TKey> entries = [];

    internal LibraDexStagedIdentityKeyGuard(LibraDexIndex<TKey, TIdentity> index)
    {
        this.index = index;
    }

    /// <summary>
    /// Tests whether the identity may be staged at a new key.<br/>
    /// This checks both committed rows and already accepted staged rows owned by this guard.<br/>
    /// </summary>
    /// <param name="identity">The identity requested for staging.</param>
    /// <param name="key">The ordinary key requested for staging.</param>
    /// <param name="newlyTracked">Receives whether this call newly reserved the identity for the current staged operation.</param>
    /// <returns><see langword="true"/> when the staged insert does not violate `SingleKeyPerIdentity`.</returns>
    internal bool CanInsert(TIdentity identity, TKey key, out bool newlyTracked)
    {
        newlyTracked = false;
        LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
        if (entries.TryGetValue(mapKey, out TKey? stagedKey))
            return index.StagedOrdinaryKeyEquals(stagedKey, key);
        if (!index.TryReserveStagedIdentity(
            reservationOwner,
            identity,
            key,
            hasAlternateAllowedKey: false,
            alternateAllowedKey: default!))
        {
            return false;
        }

        entries.Add(mapKey, key);
        newlyTracked = true;
        return true;
    }

    /// <summary>
    /// Tests whether the identity may be staged at a replacement key while one old key is being replaced.<br/>
    /// Rekey uses this to preserve replacement-before-delete semantics without accepting a third staged or committed association.<br/>
    /// </summary>
    /// <param name="identity">The identity requested for staging.</param>
    /// <param name="newKey">The replacement ordinary key requested for staging.</param>
    /// <param name="oldKey">The old ordinary key allowed during the staged replacement.</param>
    /// <param name="newlyTracked">Receives whether this call newly reserved the identity for the current staged operation.</param>
    /// <returns><see langword="true"/> when the staged replacement does not violate `SingleKeyPerIdentity`.</returns>
    internal bool CanReplace(TIdentity identity, TKey newKey, TKey oldKey, out bool newlyTracked)
    {
        newlyTracked = false;
        LibraDexIdentityMapKey<TIdentity> mapKey = new(identity);
        if (entries.TryGetValue(mapKey, out TKey? stagedKey))
        {
            return index.StagedOrdinaryKeyEquals(stagedKey, newKey) ||
                index.StagedOrdinaryKeyEquals(stagedKey, oldKey);
        }

        if (!index.TryReserveStagedIdentity(
            reservationOwner,
            identity,
            newKey,
            hasAlternateAllowedKey: true,
            alternateAllowedKey: oldKey))
        {
            return false;
        }

        entries.Add(mapKey, newKey);
        newlyTracked = true;
        return true;
    }

    /// <summary>
    /// Records an accepted staged association.<br/>
    /// Only successful inserts are recorded so failed unique-key conflicts or no-op duplicate inserts do not reserve a key they did not publish.<br/>
    /// </summary>
    /// <param name="identity">The accepted staged identity.</param>
    /// <param name="key">The accepted staged key.</param>
    internal void RecordInserted(TIdentity identity, TKey key)
    {
        entries[new LibraDexIdentityMapKey<TIdentity>(identity)] = key;
    }

    /// <summary>
    /// Finds whether the identity already has a staged key other than the allowed key set.<br/>
    /// The typed dictionary key reuses LibraDex tuple equality and hashing, including content semantics for byte arrays, so batch checks remain amortized constant time without scalar boxing.<br/>
    /// </summary>
    /// <param name="identity">The identity being inspected.</param>
    /// <param name="newlyTracked">Whether this call newly reserved the identity and must therefore release that reservation.</param>
    /// <returns><see langword="true"/> when a conflicting staged key exists.</returns>
    internal void CancelUnusedReservation(TIdentity identity, bool newlyTracked)
    {
        if (!newlyTracked)
            return;

        entries.Remove(new LibraDexIdentityMapKey<TIdentity>(identity));
        index.ReleaseStagedIdentityReservation(reservationOwner, identity);
    }

    /// <summary>
    /// Releases every identity reservation retained by this staged facade after its publication or abort boundary.<br/>
    /// Intermediate physical context publications deliberately do not call this method because the facade may still own unpublished mutations or retry state for the same identities.<br/>
    /// </summary>
    internal void ReleaseAllReservations()
    {
        index.ReleaseAllStagedIdentityReservations(reservationOwner);
        entries.Clear();
    }

    /// <summary>
    /// Releases one identity after an immediate queued-writer operation reaches its publication outcome.<br/>
    /// Concurrent batches retain reservations until their facade-level publication or abort boundary; queued writers call this per operation because their physical mutation is already reader-visible before returning.<br/>
    /// </summary>
    /// <param name="identity">Identity whose immediate operation has completed.<br/></param>
    internal void ReleaseReservation(TIdentity identity)
    {
        entries.Remove(new LibraDexIdentityMapKey<TIdentity>(identity));
        index.ReleaseStagedIdentityReservation(reservationOwner, identity);
    }
}

/// <summary>
/// Provides a writer-context-backed concurrent batch for generic `SS8-8` indexes.<br/>
/// The batch is optimized for bulk periods where each caller can stage many shelf-local insert, delete, and rekey operations before one publication boundary, while still releasing owned shelves before retrying a contended operation.<br/>
/// It is not a SQL transaction: replacement tuples may be published before old tuple deletion during rekey, and fallback topology work may publish before the final batch boundary.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the owning index.<br/></typeparam>
/// <typeparam name="TIdentity">The public identity type associated with the owning index.<br/></typeparam>
public sealed class LibraDexConcurrentBatch<TKey, TIdentity> : IDisposable
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly Scalar8Scalar8Index? encodedIndex;
    private readonly LibraDexQueuedWriter<TKey, TIdentity> fallbackWriter;
    private readonly int? maximumActiveWriters;
    private readonly int maximumQueuedWriters;
    private readonly TimeSpan queueTimeout;
    private readonly int maximumActionItems;
    private readonly CancellationToken cancellationToken;
    private readonly object admissionSync = new();
    private readonly object singleKeySync = new();
    private LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedIdentityKeyGuard;
    private Scalar8Scalar8Writer? writer;
    private LibraDexWriteContext? fixed32Scalar8Writer;
    private long attemptedInsertCount;
    private long insertedCount;
    private long attemptedDeleteCount;
    private long deletedCount;
    private long attemptedRekeyCount;
    private long changedRekeyCount;
    private long publishedContextCount;
    private long ownershipConflictCount;
    private long conflictPublicationCount;
    private long emptyContextAbortCount;
    private long topologyFallbackCount;
    private long currentStagedMutationCount;
    private long maximumStagedMutationCount;
    private long stagedMutationCountBeforeConflictPublication;
    private DataKernelCommitTelemetry lastPublishTelemetry;
    private LibraDexWriteAdmissionLease? admission;
    private bool completed;

    internal LibraDexConcurrentBatch(
        LibraDexIndex<TKey, TIdentity> index,
        Scalar8Scalar8Index encodedIndex,
        LibraDexConcurrencyOptions? options,
        CancellationToken cancellationToken)
    {
        maximumActionItems = options?.MaxActionItems ?? LibraDexConcurrencyOptions.QueuedWriter.MaxActionItems;
        if (maximumActionItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                maximumActionItems,
                $"{nameof(LibraDexConcurrencyOptions.MaxActionItems)} must be greater than zero.");
        }

        this.index = index;
        this.encodedIndex = encodedIndex;
        maximumActiveWriters = options?.MaxActiveWriters;
        maximumQueuedWriters = options?.MaxQueuedWriters ?? 1024;
        queueTimeout = options?.QueueTimeout ?? Timeout.InfiniteTimeSpan;
        this.cancellationToken = cancellationToken;
        fallbackWriter = index.BeginQueuedWriter(
            options ?? LibraDexConcurrencyOptions.QueuedWriter,
            cancellationToken,
            admissionAlreadyHeld: true);
    }

    internal LibraDexConcurrentBatch(
        LibraDexIndex<TKey, TIdentity> index,
        LibraDexConcurrencyOptions? options,
        CancellationToken cancellationToken)
    {
        maximumActionItems = options?.MaxActionItems ?? LibraDexConcurrencyOptions.QueuedWriter.MaxActionItems;
        if (maximumActionItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                maximumActionItems,
                $"{nameof(LibraDexConcurrencyOptions.MaxActionItems)} must be greater than zero.");
        }

        this.index = index;
        maximumActiveWriters = options?.MaxActiveWriters;
        maximumQueuedWriters = options?.MaxQueuedWriters ?? 1024;
        queueTimeout = options?.QueueTimeout ?? Timeout.InfiniteTimeSpan;
        this.cancellationToken = cancellationToken;
        fallbackWriter = index.BeginQueuedWriter(
            options ?? LibraDexConcurrencyOptions.QueuedWriter,
            cancellationToken,
            admissionAlreadyHeld: true);
    }

    /// <summary>
    /// Inserts one key/identity tuple into this concurrent batch.<br/>
    /// Shelf-local inserts are staged in the batch writer context; topology-changing inserts publish any current staged context and then use the normal concurrent writer fallback for that one operation.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic insert result; writer-context results become visible when the batch publishes.<br/></returns>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        ThrowIfCompleted();
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        EnsureAdmission();
        attemptedInsertCount++;
        LibraDexGenericInsertResult result;
        lock (singleKeySync)
        {
            LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedGuard = GetStagedIdentityKeyGuard();
            bool newlyTracked = false;
            if (stagedGuard is not null &&
                !stagedGuard.CanInsert(identity, key, out newlyTracked))
            {
                return new LibraDexGenericInsertResult(false, false, default, default);
            }

            try
            {
                result = InsertCore(key, identity);
            }
            catch
            {
                stagedGuard?.CancelUnusedReservation(identity, newlyTracked);
                throw;
            }

            if (result.Inserted)
            {
                stagedGuard?.RecordInserted(identity, key);
            }
            else
            {
                stagedGuard?.CancelUnusedReservation(identity, newlyTracked);
            }
        }

        if (result.Inserted)
        {
            insertedCount++;
            RecordStagedMutation(result.QueuedInsertPath);
        }

        return result;
    }

    /// <summary>
    /// Deletes one exact key/identity tuple through this concurrent batch.<br/>
    /// Shelf-local deletes are staged in the batch writer context; unsupported delete shapes publish any current staged context and then use the normal concurrent writer fallback for that one operation.<br/>
    /// </summary>
    /// <param name="key">The typed key value to delete.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic delete result; writer-context results become visible when the batch publishes.<br/></returns>
    public LibraDexGenericDeleteResult Delete(TKey key, TIdentity identity)
    {
        ThrowIfCompleted();
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        EnsureAdmission();
        attemptedDeleteCount++;
        LibraDexGenericDeleteResult result = DeleteCore(key, identity);
        if (result.Deleted)
        {
            deletedCount++;
            RecordStagedMutation(result.QueuedInsertPath);
        }

        return result;
    }

    /// <summary>
    /// Moves one identity from an old key to a new key through this concurrent batch.<br/>
    /// Rekey is modeled as replacement insert followed by old tuple delete, matching LibraDex identity-index semantics rather than database transaction semantics.<br/>
    /// </summary>
    /// <param name="identity">The typed identity to move between keys.<br/></param>
    /// <param name="oldKey">The old typed key value.<br/></param>
    /// <param name="newKey">The replacement typed key value.<br/></param>
    /// <returns>The generic rekey result with replacement and removal leg results.<br/></returns>
    public LibraDexGenericRekeyResult Rekey(
        TIdentity identity,
        TKey oldKey,
        TKey newKey)
    {
        ThrowIfCompleted();
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        EnsureAdmission();
        attemptedRekeyCount++;
        if (EqualityComparer<TKey>.Default.Equals(oldKey, newKey))
        {
            return new LibraDexGenericRekeyResult(false, default, default);
        }

        if (!index.ContainsExactTupleForConcurrentBatch(oldKey, identity))
        {
            return new LibraDexGenericRekeyResult(false, default, default);
        }

        LibraDexGenericInsertResult replacement;
        LibraDexGenericDeleteResult removal;
        lock (singleKeySync)
        {
            LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedGuard = GetStagedIdentityKeyGuard();
            bool newlyTracked = false;
            if (stagedGuard is not null &&
                !stagedGuard.CanReplace(identity, newKey, oldKey, out newlyTracked))
            {
                return new LibraDexGenericRekeyResult(false, default, default);
            }

            try
            {
                replacement = InsertCore(newKey, identity);
            }
            catch
            {
                stagedGuard?.CancelUnusedReservation(identity, newlyTracked);
                throw;
            }

            if (replacement.Inserted)
            {
                stagedGuard?.RecordInserted(identity, newKey);
            }

            removal = DeleteCore(oldKey, identity);
        }

        if (replacement.Inserted)
        {
            insertedCount++;
            RecordStagedMutation(replacement.QueuedInsertPath);
        }

        if (removal.Deleted)
        {
            deletedCount++;
            changedRekeyCount++;
            RecordStagedMutation(removal.QueuedInsertPath);
        }

        return new LibraDexGenericRekeyResult(removal.Deleted, replacement, removal);
    }

    /// <summary>
    /// Publishes any staged writer-context work and closes this concurrent batch.<br/>
    /// Fallback topology operations may already have published before this call; this method publishes the final shelf-local context and reports aggregate batch counters.<br/>
    /// </summary>
    /// <returns>The concurrent batch publication result.</returns>
    public LibraDexConcurrentBatchPublishResult Publish()
    {
        ThrowIfCompleted();
        ThrowIfCancellationRequestedBeforePublication();
        PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.Final);
        stagedIdentityKeyGuard?.ReleaseAllReservations();
        completed = true;
        admission?.Dispose();
        admission = null;
        return new LibraDexConcurrentBatchPublishResult(
            attemptedInsertCount,
            insertedCount,
            attemptedDeleteCount,
            deletedCount,
            attemptedRekeyCount,
            changedRekeyCount,
            publishedContextCount,
            LibraDexOperationDiagnostics.FromDataKernel(lastPublishTelemetry))
        {
            OwnershipConflictCount = ownershipConflictCount,
            ConflictPublicationCount = conflictPublicationCount,
            EmptyContextAbortCount = emptyContextAbortCount,
            TopologyFallbackCount = topologyFallbackCount,
            MaximumStagedMutationCount = maximumStagedMutationCount,
            StagedMutationCountBeforeConflictPublication = stagedMutationCountBeforeConflictPublication
        };
    }

    /// <summary>
    /// Aborts any unpublished writer-context work and closes this concurrent batch.<br/>
    /// Fallback topology operations already published through the normal concurrent writer cannot be rolled back by this abort.<br/>
    /// </summary>
    public void Abort()
    {
        ThrowIfCompleted();
        AbortCurrentWriter();
        stagedIdentityKeyGuard?.ReleaseAllReservations();
        completed = true;
        admission?.Dispose();
        admission = null;
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

    private LibraDexGenericInsertResult InsertCore(TKey key, TIdentity identity)
    {
        return encodedIndex is not null
            ? InsertScalar8Scalar8Core(key, identity)
            : InsertFixed32Scalar8Core(key, identity);
    }

    /// <summary>
    /// Gets the staged identity-key guard required by `SingleKeyPerIdentity`, or <see langword="null"/> for normal multi-key identity indexes.<br/>
    /// Concurrent batches lock around the guard and physical mutation so overlapping callers cannot reserve conflicting keys for one identity.<br/>
    /// </summary>
    /// <returns>The staged guard when the owning index requires one, otherwise <see langword="null"/>.</returns>
    private LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? GetStagedIdentityKeyGuard()
    {
        if (index.IdentityKeyMultiplicity != IdentityKeyMultiplicity.SingleKeyPerIdentity)
        {
            return null;
        }

        stagedIdentityKeyGuard ??= new LibraDexStagedIdentityKeyGuard<TKey, TIdentity>(index);
        return stagedIdentityKeyGuard;
    }

    private LibraDexGenericInsertResult InsertScalar8Scalar8Core(TKey key, TIdentity identity)
    {
        bool allowDuplicateKeys = index.KeyContract == IndexKeys.NonUnique;
        ulong encodedKey = index.EncodeKey8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        while (true)
        {
            Scalar8Scalar8Writer current = GetOrCreateWriter();
            try
            {
                Scalar8Scalar8EncodedInsertResult staged = current.InsertEncoded(encodedKey, encodedIdentity, allowDuplicateKeys);
                return new LibraDexGenericInsertResult(
                    staged.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted,
                    staged.CreatedInitialShelfRoute,
                    default,
                    default)
                {
                    QueuedInsertPath = staged.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted
                        ? Scalar8Scalar8QueuedInsertPath.WriterContext
                        : Scalar8Scalar8QueuedInsertPath.None
                };
            }
            catch (LibraDexWriteContextShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                topologyFallbackCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                return fallbackWriter.InsertAfterExternalIdentityReservation(key, identity);
            }
        }
    }

    private LibraDexGenericDeleteResult DeleteCore(TKey key, TIdentity identity)
    {
        return encodedIndex is not null
            ? DeleteScalar8Scalar8Core(key, identity)
            : DeleteFixed32Scalar8Core(key, identity);
    }

    private LibraDexGenericDeleteResult DeleteScalar8Scalar8Core(TKey key, TIdentity identity)
    {
        ulong encodedKey = index.EncodeKey8(key);
        ulong encodedIdentity = LibraDexGenericScalarCodec<TIdentity>.Encode8(identity);
        while (true)
        {
            Scalar8Scalar8Writer current = GetOrCreateWriter();
            try
            {
                bool deleted = current.DeleteEncoded(encodedKey, encodedIdentity);
                return new LibraDexGenericDeleteResult(
                    deleted,
                    default)
                {
                    QueuedInsertPath = deleted
                        ? Scalar8Scalar8QueuedInsertPath.WriterContext
                        : Scalar8Scalar8QueuedInsertPath.None
                };
            }
            catch (LibraDexWriteContextShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (LibraDexWriteContextTerminalIdentityShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                topologyFallbackCount++;
                PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                return fallbackWriter.Delete(key, identity);
            }
        }
    }

    private Scalar8Scalar8Writer GetOrCreateWriter()
    {
        writer ??= encodedIndex!.BeginWriter();
        return writer;
    }

    private LibraDexGenericInsertResult InsertFixed32Scalar8Core(TKey key, TIdentity identity)
    {
        while (true)
        {
            LibraDexWriteContext current = GetOrCreateFixed32Scalar8Writer();
            try
            {
                LibraDexGenericInsertResult primary = index.InsertFixed32Scalar8ForConcurrentBatch(current, key, identity);
                if (primary.Inserted &&
                    index.HasExactReversedProjectionForConcurrentBatch())
                {
                    try
                    {
                        _ = index.InsertExactReversedProjectionForConcurrentBatch(current, key, identity);
                    }
                    catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException)
                    {
                        ownershipConflictCount++;
                        PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                        index.InsertExactReversedProjectionFallbackForConcurrentBatch(key, identity);
                    }
                    catch (InvalidOperationException)
                    {
                        topologyFallbackCount++;
                        PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                        index.InsertExactReversedProjectionFallbackForConcurrentBatch(key, identity);
                    }
                }

                return primary;
            }
            catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                topologyFallbackCount++;
                PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                return fallbackWriter.InsertAfterExternalIdentityReservation(key, identity);
            }
        }
    }

    private LibraDexGenericDeleteResult DeleteFixed32Scalar8Core(TKey key, TIdentity identity)
    {
        while (true)
        {
            LibraDexWriteContext current = GetOrCreateFixed32Scalar8Writer();
            try
            {
                LibraDexGenericDeleteResult primary = index.DeleteFixed32Scalar8ForConcurrentBatch(current, key, identity);
                if (primary.Deleted &&
                    index.HasExactReversedProjectionForConcurrentBatch())
                {
                    try
                    {
                        _ = index.DeleteExactReversedProjectionForConcurrentBatch(current, key, identity);
                    }
                    catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException)
                    {
                        ownershipConflictCount++;
                        PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                        index.DeleteExactReversedProjectionFallbackForConcurrentBatch(key, identity);
                    }
                    catch (InvalidOperationException)
                    {
                        topologyFallbackCount++;
                        PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                        index.DeleteExactReversedProjectionFallbackForConcurrentBatch(key, identity);
                    }
                }

                return primary;
            }
            catch (LibraDexWriteContextFixed32Scalar8ShelfOwnershipException ex)
            {
                ownershipConflictCount++;
                PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.OwnershipConflict);
                index.Session.WaitForWriteContextShelfRelease(ex.ShelfOffset, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                topologyFallbackCount++;
                PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause.TopologyFallback);
                return fallbackWriter.Delete(key, identity);
            }
        }
    }

    private LibraDexWriteContext GetOrCreateFixed32Scalar8Writer()
    {
        fixed32Scalar8Writer ??= index.BeginFixed32Scalar8ConcurrentBatchContext();
        return fixed32Scalar8Writer;
    }

    private void PublishCurrentWriter(ConcurrentBatchPublicationCause cause)
    {
        if (encodedIndex is null)
        {
            PublishFixed32Scalar8CurrentWriter(cause);
            return;
        }

        Scalar8Scalar8Writer? current = writer;
        if (current is null)
        {
            return;
        }

        RecordContextPublication(cause);
        lastPublishTelemetry = current.Publish();
        index.InvalidateSingleKeyIdentityMapAfterStagedPublication();
        publishedContextCount++;
        writer = null;
    }

    /// <summary>
    /// Publishes useful `SS8-8` staged mutations or aborts a context that contains only read/claim state.<br/>
    /// The decision comes from context-owned dirty shelf state, not completed-operation counters, so an admission exception cannot cause accepted staged bytes to be discarded.<br/>
    /// Fixed-32 contexts retain unconditional publication because their shared primary/projection mutation boundary is not represented by the `SS8-8` dirty signal.<br/>
    /// </summary>
    /// <param name="cause">The reason the current context is being completed.<br/></param>
    private void PublishOrAbortEmptyCurrentWriter(ConcurrentBatchPublicationCause cause)
    {
        if (encodedIndex is not null &&
            writer is Scalar8Scalar8Writer current &&
            !current.HasStagedMutations)
        {
            current.Abort();
            writer = null;
            emptyContextAbortCount++;
            return;
        }

        PublishCurrentWriter(cause);
    }

    private void PublishFixed32Scalar8CurrentWriter(ConcurrentBatchPublicationCause cause)
    {
        LibraDexWriteContext? current = fixed32Scalar8Writer;
        if (current is null)
        {
            return;
        }

        RecordContextPublication(cause);
        lastPublishTelemetry = index.PublishFixed32Scalar8ConcurrentBatchContext(current);
        index.InvalidateSingleKeyIdentityMapAfterStagedPublication();
        publishedContextCount++;
        fixed32Scalar8Writer = null;
    }

    private void RecordStagedMutation(Scalar8Scalar8QueuedInsertPath path)
    {
        if (path != Scalar8Scalar8QueuedInsertPath.WriterContext)
        {
            return;
        }

        currentStagedMutationCount++;
        maximumStagedMutationCount = Math.Max(maximumStagedMutationCount, currentStagedMutationCount);
        if (currentStagedMutationCount >= maximumActionItems)
        {
            PublishCurrentWriter(ConcurrentBatchPublicationCause.CooperativeLimit);
        }
    }

    private void RecordContextPublication(ConcurrentBatchPublicationCause cause)
    {
        maximumStagedMutationCount = Math.Max(maximumStagedMutationCount, currentStagedMutationCount);
        if (cause == ConcurrentBatchPublicationCause.OwnershipConflict)
        {
            conflictPublicationCount++;
            stagedMutationCountBeforeConflictPublication += currentStagedMutationCount;
        }

        currentStagedMutationCount = 0;
    }

    private void AbortCurrentWriter()
    {
        if (encodedIndex is null)
        {
            AbortFixed32Scalar8CurrentWriter();
            return;
        }

        Scalar8Scalar8Writer? current = writer;
        if (current is null)
        {
            return;
        }

        current.Abort();
        writer = null;
    }

    private void AbortFixed32Scalar8CurrentWriter()
    {
        LibraDexWriteContext? current = fixed32Scalar8Writer;
        if (current is null)
        {
            return;
        }

        index.AbortFixed32Scalar8ConcurrentBatchContext(current);
        fixed32Scalar8Writer = null;
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException("The LibraDex concurrent batch has already completed.");
        }
    }

    /// <summary>
    /// Acquires one session admission slot on the batch's first operation and retains it through publish or abort.<br/>
    /// Holding the lease keeps a batch with unpublished shelf ownership inside the configured active-writer budget.<br/>
    /// </summary>
    private void EnsureAdmission()
    {
        lock (admissionSync)
        {
            if (admission is not null)
            {
                ThrowIfCancellationRequestedBeforePublication();
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            admission = index.Session.EnterConcurrentWriteAdmission(
                maximumActiveWriters,
                maximumQueuedWriters,
                queueTimeout,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                admission.Dispose();
                admission = null;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Aborts unpublished writer-local state when cancellation is observed before publication begins.<br/>
    /// Once final publication starts, cancellation no longer changes the outcome and the publish operation wins.<br/>
    /// </summary>
    private void ThrowIfCancellationRequestedBeforePublication()
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        AbortCurrentWriter();
        stagedIdentityKeyGuard?.ReleaseAllReservations();
        completed = true;
        admission?.Dispose();
        admission = null;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private enum ConcurrentBatchPublicationCause : byte
    {
        Final = 0,
        OwnershipConflict = 1,
        TopologyFallback = 2,
        CooperativeLimit = 3
    }

}

/// <summary>
/// Provides an internal concurrent-write admission facade for supported generic LibraDex indexes.<br/>
/// The facade accepts developer-facing generic key and identity values, delegates `SS8-8` overlap handling to the encoded writer, and lets widened fixed-scalar shapes use the same direct writer-context path as ordinary inserts/deletes.<br/>
/// It is deliberately limited by <see cref="LibraDexIndex{TKey, TIdentity}.BeginQueuedWriter"/> so broader shape support has to be added explicitly.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the owning index.</typeparam>
/// <typeparam name="TIdentity">The public identity type associated with the owning index.</typeparam>
public sealed class LibraDexQueuedWriter<TKey, TIdentity>
{
    private readonly LibraDexIndex<TKey, TIdentity> index;
    private readonly Scalar8Scalar8QueuedWriter? encodedWriter;
    private readonly int? maximumActiveWriters;
    private readonly int maximumQueuedWriters;
    private readonly TimeSpan queueTimeout;
    private readonly int maximumActionItems;
    private readonly CancellationToken cancellationToken;
    private readonly bool admissionAlreadyHeld;
    private readonly object singleKeySync = new();
    private LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedIdentityKeyGuard;

    internal LibraDexQueuedWriter(
        LibraDexIndex<TKey, TIdentity> index,
        int? maximumActiveWriters,
        int maximumQueuedWriters,
        TimeSpan queueTimeout,
        int maximumActionItems,
        CancellationToken cancellationToken,
        bool admissionAlreadyHeld)
    {
        if (maximumActionItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActionItems));
        }

        this.index = index;
        this.maximumActiveWriters = maximumActiveWriters;
        this.maximumQueuedWriters = maximumQueuedWriters;
        this.queueTimeout = queueTimeout;
        this.maximumActionItems = maximumActionItems;
        this.cancellationToken = cancellationToken;
        this.admissionAlreadyHeld = admissionAlreadyHeld;
    }

    internal LibraDexQueuedWriter(
        LibraDexIndex<TKey, TIdentity> index,
        Scalar8Scalar8QueuedWriter encodedWriter,
        int? maximumActiveWriters,
        int maximumQueuedWriters,
        TimeSpan queueTimeout,
        int maximumActionItems,
        CancellationToken cancellationToken,
        bool admissionAlreadyHeld)
    {
        if (maximumActionItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActionItems));
        }

        this.index = index;
        this.encodedWriter = encodedWriter;
        this.maximumActiveWriters = maximumActiveWriters;
        this.maximumQueuedWriters = maximumQueuedWriters;
        this.queueTimeout = queueTimeout;
        this.maximumActionItems = maximumActionItems;
        this.cancellationToken = cancellationToken;
        this.admissionAlreadyHeld = admissionAlreadyHeld;
    }

    /// <summary>
    /// Inserts one typed key and identity through the queued generic writer facade.<br/>
    /// Supported shelf-local writes use writer-context staging, same-shelf conflicts wait and retry, publication is serialized, and unsupported topology shapes fall back to the existing serialized insert path.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic insert result with queued writer path attribution.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        cancellationToken.ThrowIfCancellationRequested();
        using LibraDexWriteAdmissionLease? operationAdmission = EnterOperationAdmission();

        LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedGuard = GetStagedIdentityKeyGuard();
        if (stagedGuard is null)
        {
            return InsertAfterStagedIdentityKeyGuard(key, identity);
        }

        lock (singleKeySync)
        {
            if (!stagedGuard.CanInsert(identity, key, out _))
            {
                return new LibraDexGenericInsertResult(false, false, default, default);
            }

            try
            {
                LibraDexGenericInsertResult completed = InsertAfterStagedIdentityKeyGuard(key, identity);
                if (completed.Inserted)
                {
                    stagedGuard.RecordInserted(identity, key);
                }

                return completed;
            }
            finally
            {
                stagedGuard.ReleaseReservation(identity);
            }
        }
    }

    /// <summary>
    /// Deletes one exact typed key/identity tuple through the queued generic writer facade.<br/>
    /// Supported shelf-local deletes use writer-context staging, same-shelf conflicts wait and retry, and unsupported terminal or linked-chain shapes fall back to serialized exact-delete mutation.<br/>
    /// </summary>
    /// <param name="key">The typed key value to delete.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic delete result with queued writer path attribution.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    public LibraDexGenericDeleteResult Delete(TKey key, TIdentity identity)
    {
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        cancellationToken.ThrowIfCancellationRequested();
        using LibraDexWriteAdmissionLease? operationAdmission = EnterOperationAdmission();
        return DeleteAfterAdmission(key, identity);
    }

    /// <summary>
    /// Deletes one exact tuple after the caller has entered the session admission domain.<br/>
    /// Rekey uses this helper so its replacement and removal legs share one permit instead of recursively queuing.<br/>
    /// </summary>
    /// <param name="key">The typed key value to delete.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic delete result with queued writer path attribution.<br/></returns>
    private LibraDexGenericDeleteResult DeleteAfterAdmission(TKey key, TIdentity identity)
    {
        if (encodedWriter is null)
        {
            return index.DeleteTupleForQueuedWriterFacade(key, identity);
        }

        bool deleted = encodedWriter.DeleteEncoded(
            index.EncodeKey8(key),
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity),
            out DataKernelCommitTelemetry telemetry,
            out Scalar8Scalar8QueuedInsertPath queuedPath,
            cancellationToken);
        return new LibraDexGenericDeleteResult(
            deleted,
            LibraDexOperationDiagnostics.FromDataKernel(telemetry))
        {
            QueuedInsertPath = queuedPath
        };
    }

    /// <summary>
    /// Inserts one tuple after the queued writer's staged identity-key guard has already accepted it.<br/>
    /// Direct queued-writer paths use the generic index's checked insert core, while encoded `SS8-8` paths use the queued writer and then maintain projections.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic insert result with queued writer path attribution when available.<br/></returns>
    private LibraDexGenericInsertResult InsertAfterStagedIdentityKeyGuard(TKey key, TIdentity identity)
    {
        if (encodedWriter is null)
        {
            return index.InsertAfterIdentityKeyMultiplicityCheckForQueuedWriter(key, identity);
        }

        bool allowDuplicateKeys = index.KeyContract == IndexKeys.NonUnique;
        Scalar8Scalar8EncodedInsertResult result = encodedWriter.InsertEncoded(
            index.EncodeKey8(key),
            LibraDexGenericScalarCodec<TIdentity>.Encode8(identity),
            allowDuplicateKeys,
            cancellationToken);
        LibraDexGenericInsertResult genericResult = new(
            result.Outcome == Scalar8Scalar8EncodedInsertOutcome.Inserted,
            result.CreatedInitialShelfRoute,
            LibraDexOperationDiagnostics.FromDataKernel(result.RouteCreateCommit),
            LibraDexOperationDiagnostics.FromDataKernel(result.InsertCommit))
        {
            QueuedInsertPath = result.QueuedInsertPath
        };
        return index.CompleteImmediateInsertProjection(genericResult, key, identity);
    }

    /// <summary>
    /// Moves one identity from an old key to a new key through the queued generic writer facade.<br/>
    /// The replacement tuple is submitted first and the old tuple is deleted only after replacement insert succeeds or is already present, matching the existing direct rekey loss-avoidance rule.<br/>
    /// This is not a rollback transaction: callers should treat partial indexing failure through Abraxas' eventual-indexed/stale-index state model.<br/>
    /// </summary>
    /// <param name="identity">The typed identity to move between keys.<br/></param>
    /// <param name="oldKey">The old typed key value.<br/></param>
    /// <param name="newKey">The replacement typed key value.<br/></param>
    /// <returns>The queued rekey result, including the replacement insert and old tuple delete legs.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when the replacement tuple cannot be made available before old tuple deletion.<br/></exception>
    /// <exception cref="InvalidDataException">Thrown when the resolved route graph or shelf bytes are invalid.<br/></exception>
    public LibraDexGenericRekeyResult Rekey(
        TIdentity identity,
        TKey oldKey,
        TKey newKey)
    {
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        cancellationToken.ThrowIfCancellationRequested();
        using LibraDexWriteAdmissionLease? operationAdmission = EnterOperationAdmission();

        lock (singleKeySync)
        {
            if (EqualityComparer<TKey>.Default.Equals(oldKey, newKey))
            {
                return new LibraDexGenericRekeyResult(
                    Changed: false,
                    Replacement: default,
                    Removal: default);
            }

            LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? stagedGuard = GetStagedIdentityKeyGuard();
            bool newlyTracked = false;
            if (stagedGuard is not null &&
                !stagedGuard.CanReplace(identity, newKey, oldKey, out newlyTracked))
            {
                return new LibraDexGenericRekeyResult(false, default, default);
            }

            try
            {
                if (encodedWriter is null)
                {
                    LibraDexGenericInsertResult directReplacement = InsertAfterStagedIdentityKeyGuard(newKey, identity);
                    if (directReplacement.Inserted)
                    {
                        stagedGuard?.RecordInserted(identity, newKey);
                    }

                    if (!directReplacement.Inserted)
                    {
                        bool directReplacementPresent = false;
                        using (LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(newKey, newKey))
                        {
                            while (reader.TryReadNext(out _, out TIdentity currentIdentity))
                            {
                                if (EqualityComparer<TIdentity>.Default.Equals(currentIdentity, identity))
                                {
                                    directReplacementPresent = true;
                                    break;
                                }
                            }
                        }

                        if (!directReplacementPresent)
                        {
                            throw new InvalidOperationException("Queued rekey could not create the replacement tuple; the original tuple was left unchanged.");
                        }
                    }

                    LibraDexGenericDeleteResult directRemoval = DeleteAfterAdmission(oldKey, identity);
                    return new LibraDexGenericRekeyResult(
                        directRemoval.Deleted,
                        directReplacement,
                        directRemoval);
                }

                LibraDexGenericInsertResult replacement = InsertAfterStagedIdentityKeyGuard(newKey, identity);
                if (replacement.Inserted)
                {
                    stagedGuard?.RecordInserted(identity, newKey);
                }

                if (!replacement.Inserted)
                {
                    bool replacementPresent = false;
                    using (LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(newKey, newKey))
                    {
                        while (reader.TryReadNext(out _, out TIdentity currentIdentity))
                        {
                            if (EqualityComparer<TIdentity>.Default.Equals(currentIdentity, identity))
                            {
                                replacementPresent = true;
                                break;
                            }
                        }
                    }

                    if (!replacementPresent)
                    {
                        throw new InvalidOperationException("Queued rekey could not create the replacement tuple; the original tuple was left unchanged.");
                    }
                }

                LibraDexGenericDeleteResult removal = DeleteAfterAdmission(oldKey, identity);
                return new LibraDexGenericRekeyResult(
                    removal.Deleted,
                    replacement,
                    removal);
            }
            finally
            {
                stagedGuard?.ReleaseReservation(identity);
            }
        }
    }

    /// <summary>
    /// Gets the staged identity-key guard required by `SingleKeyPerIdentity`, or <see langword="null"/> for normal multi-key identity indexes.<br/>
    /// Queued writers lock around the guard and physical mutation so overlapping callers cannot reserve conflicting keys for one identity.<br/>
    /// </summary>
    /// <returns>The staged guard when the owning index requires one, otherwise <see langword="null"/>.</returns>
    private LibraDexStagedIdentityKeyGuard<TKey, TIdentity>? GetStagedIdentityKeyGuard()
    {
        if (index.IdentityKeyMultiplicity != IdentityKeyMultiplicity.SingleKeyPerIdentity)
        {
            return null;
        }

        stagedIdentityKeyGuard ??= new LibraDexStagedIdentityKeyGuard<TKey, TIdentity>(index);
        return stagedIdentityKeyGuard;
    }

    /// <summary>
    /// Enters one session admission slot for a direct queued-writer operation unless an owning action or concurrent batch already supplied that slot.<br/>
    /// This keeps the convenient one-call API inside the same bounded queue while avoiding recursive admission for action-scoped and batch-scoped operations.<br/>
    /// </summary>
    /// <returns>A lease for this operation, or <see langword="null"/> when admission is already owned by the caller.<br/></returns>
    private LibraDexWriteAdmissionLease? EnterOperationAdmission()
    {
        if (admissionAlreadyHeld)
        {
            return null;
        }

        return index.Session.EnterConcurrentWriteAdmission(
            maximumActiveWriters,
            maximumQueuedWriters,
            queueTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Starts one explicitly bounded threaded write action through this concurrent writer.<br/>
    /// The action acquires one session admission slot for its lifetime, so a worker loop pays queue admission once rather than on every tuple.<br/>
    /// The writer-wide cancellation token stops all actions; the optional action token can independently stop only this action.<br/>
    /// </summary>
    /// <param name="actionCancellationToken">An optional token used to cancel this action independently of the writer-wide token.<br/></param>
    /// <returns>A bounded action facade that must be disposed to release its admission slot.<br/></returns>
    /// <exception cref="InvalidOperationException">Thrown when called from a writer that already inherits an owning action or batch admission slot.<br/></exception>
    public LibraDexConcurrentWriteAction<TKey, TIdentity> BeginAction(
        CancellationToken actionCancellationToken = default)
    {
        if (admissionAlreadyHeld)
        {
            throw new InvalidOperationException("A LibraDex concurrent write action cannot be nested inside an already admitted action or batch.");
        }

        CancellationTokenSource? linkedCancellation = null;
        CancellationToken effectiveCancellation;
        if (!actionCancellationToken.CanBeCanceled)
        {
            effectiveCancellation = cancellationToken;
        }
        else if (!cancellationToken.CanBeCanceled)
        {
            effectiveCancellation = actionCancellationToken;
        }
        else
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                actionCancellationToken);
            effectiveCancellation = linkedCancellation.Token;
        }

        LibraDexWriteAdmissionLease? admission = null;
        try
        {
            admission = index.Session.EnterConcurrentWriteAdmission(
                maximumActiveWriters,
                maximumQueuedWriters,
                queueTimeout,
                effectiveCancellation);
            LibraDexQueuedWriter<TKey, TIdentity> admittedWriter = encodedWriter is null
                ? new LibraDexQueuedWriter<TKey, TIdentity>(
                    index,
                    maximumActiveWriters,
                    maximumQueuedWriters,
                    queueTimeout,
                    maximumActionItems,
                    effectiveCancellation,
                    admissionAlreadyHeld: true)
                : new LibraDexQueuedWriter<TKey, TIdentity>(
                    index,
                    encodedWriter,
                    maximumActiveWriters,
                    maximumQueuedWriters,
                    queueTimeout,
                    maximumActionItems,
                    effectiveCancellation,
                    admissionAlreadyHeld: true);
            return new LibraDexConcurrentWriteAction<TKey, TIdentity>(
                admittedWriter,
                admission,
                maximumActionItems,
                linkedCancellation);
        }
        catch
        {
            admission?.Dispose();
            linkedCancellation?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts one bounded write action without blocking a caller thread while session admission is saturated.<br/>
    /// The returned action has the same cancellation, automatic rotation, and disposal contract as <see cref="BeginAction(CancellationToken)"/>.<br/>
    /// </summary>
    /// <param name="actionCancellationToken">An optional token used to cancel this action independently of the writer-wide token.<br/></param>
    /// <returns>A value task that completes with an admitted action.<br/></returns>
    public async ValueTask<LibraDexConcurrentWriteAction<TKey, TIdentity>> BeginActionAsync(
        CancellationToken actionCancellationToken = default)
    {
        if (admissionAlreadyHeld)
        {
            throw new InvalidOperationException("A LibraDex concurrent write action cannot be nested inside an already admitted action or batch.");
        }

        CancellationTokenSource? linkedCancellation = null;
        CancellationToken effectiveCancellation;
        if (!actionCancellationToken.CanBeCanceled)
        {
            effectiveCancellation = cancellationToken;
        }
        else if (!cancellationToken.CanBeCanceled)
        {
            effectiveCancellation = actionCancellationToken;
        }
        else
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                actionCancellationToken);
            effectiveCancellation = linkedCancellation.Token;
        }

        LibraDexWriteAdmissionLease? admission = null;
        try
        {
            admission = await index.Session.EnterConcurrentWriteAdmissionAsync(
                maximumActiveWriters,
                maximumQueuedWriters,
                queueTimeout,
                effectiveCancellation).ConfigureAwait(false);
            LibraDexQueuedWriter<TKey, TIdentity> admittedWriter = encodedWriter is null
                ? new LibraDexQueuedWriter<TKey, TIdentity>(
                    index,
                    maximumActiveWriters,
                    maximumQueuedWriters,
                    queueTimeout,
                    maximumActionItems,
                    effectiveCancellation,
                    admissionAlreadyHeld: true)
                : new LibraDexQueuedWriter<TKey, TIdentity>(
                    index,
                    encodedWriter,
                    maximumActiveWriters,
                    maximumQueuedWriters,
                    queueTimeout,
                    maximumActionItems,
                    effectiveCancellation,
                    admissionAlreadyHeld: true);
            return new LibraDexConcurrentWriteAction<TKey, TIdentity>(
                admittedWriter,
                admission,
                maximumActionItems,
                linkedCancellation);
        }
        catch
        {
            admission?.Dispose();
            linkedCancellation?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inserts a sequence through automatically rotated bounded actions.<br/>
    /// This is the low-friction path for a producer loop: LibraDex owns queue admission, action sizing, cancellation, and lease cleanup.<br/>
    /// </summary>
    /// <param name="items">The typed key/identity tuples to insert.<br/></param>
    /// <param name="actionCancellationToken">An optional token used to stop this producer independently.<br/></param>
    /// <returns>The number of tuples newly inserted.<br/></returns>
    public long InsertAll(
        IEnumerable<(TKey Key, TIdentity Identity)> items,
        CancellationToken actionCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        using LibraDexConcurrentWriteAction<TKey, TIdentity> action = BeginAction(actionCancellationToken);
        long inserted = 0;
        foreach ((TKey key, TIdentity identity) in items)
        {
            if (action.Insert(key, identity).Inserted)
            {
                inserted++;
            }
        }

        return inserted;
    }

    /// <summary>
    /// Gets the current file-session admission counters shared by this writer, concurrent batches, and other indexes in the same catalog session.<br/>
    /// </summary>
    public LibraDexWriteAdmissionDiagnostics GetAdmissionDiagnostics()
        => index.Session.GetConcurrentWriteAdmissionDiagnostics();

    /// <summary>
    /// Reenters admission after an action reaches its cooperative item budget.<br/>
    /// </summary>
    internal LibraDexWriteAdmissionLease ReenterActionAdmission()
        => index.Session.EnterConcurrentWriteAdmission(
            maximumActiveWriters,
            maximumQueuedWriters,
            queueTimeout,
            cancellationToken);

    /// <summary>
    /// Executes one queued-writer insert after an owning concurrent batch has already acquired the shared `SingleKeyPerIdentity` reservation.<br/>
    /// This path is limited to the batch's topology fallback: it preserves queued publication and admission behavior while avoiding a second facade-local guard that would otherwise contend with its own outer reservation.<br/>
    /// Callers without an existing reservation must use <see cref="Insert(TKey, TIdentity)"/> so committed and staged identity ownership is validated atomically.<br/>
    /// </summary>
    /// <param name="key">The key already approved by the owning batch reservation.<br/></param>
    /// <param name="identity">The identity already reserved by the owning batch.<br/></param>
    /// <returns>The queued insert result after physical publication.<br/></returns>
    internal LibraDexGenericInsertResult InsertAfterExternalIdentityReservation(TKey key, TIdentity identity)
    {
        index.ThrowIfSessionDurabilityBatchActiveForConcurrentWriter();
        cancellationToken.ThrowIfCancellationRequested();
        using LibraDexWriteAdmissionLease? operationAdmission = EnterOperationAdmission();
        return InsertAfterStagedIdentityKeyGuard(key, identity);
    }
}

/// <summary>
/// Represents one CPU-budgeted concurrent writer action, normally one worker's write loop.<br/>
/// The action holds one session admission slot until disposal and delegates tuple operations to the existing shape-specific concurrent writer.<br/>
/// </summary>
/// <typeparam name="TKey">The public key type accepted by the owning index.<br/></typeparam>
/// <typeparam name="TIdentity">The public identity type associated with the owning index.<br/></typeparam>
public sealed class LibraDexConcurrentWriteAction<TKey, TIdentity> : IDisposable
{
    private readonly LibraDexQueuedWriter<TKey, TIdentity> writer;
    private readonly CancellationTokenSource? linkedCancellation;
    private readonly int maximumActionItems;
    private LibraDexWriteAdmissionLease? admission;
    private int actionItems;

    internal LibraDexConcurrentWriteAction(
        LibraDexQueuedWriter<TKey, TIdentity> writer,
        LibraDexWriteAdmissionLease admission,
        int maximumActionItems,
        CancellationTokenSource? linkedCancellation)
    {
        this.writer = writer;
        this.admission = admission;
        this.maximumActionItems = maximumActionItems;
        this.linkedCancellation = linkedCancellation;
    }

    /// <summary>
    /// Inserts one tuple while this action owns a session admission slot.<br/>
    /// </summary>
    /// <param name="key">The typed key value to insert.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic insert result with queued writer path attribution.<br/></returns>
    public LibraDexGenericInsertResult Insert(TKey key, TIdentity identity)
    {
        PrepareOperation();
        return writer.Insert(key, identity);
    }

    /// <summary>
    /// Deletes one exact tuple while this action owns a session admission slot.<br/>
    /// </summary>
    /// <param name="key">The typed key value to delete.<br/></param>
    /// <param name="identity">The typed identity value associated with the key.<br/></param>
    /// <returns>The generic delete result with queued writer path attribution.<br/></returns>
    public LibraDexGenericDeleteResult Delete(TKey key, TIdentity identity)
    {
        PrepareOperation();
        return writer.Delete(key, identity);
    }

    /// <summary>
    /// Rekeys one identity while this action owns a session admission slot.<br/>
    /// </summary>
    /// <param name="identity">The typed identity to move.<br/></param>
    /// <param name="oldKey">The old typed key.<br/></param>
    /// <param name="newKey">The replacement typed key.<br/></param>
    /// <returns>The generic rekey result for the replacement and removal legs.<br/></returns>
    public LibraDexGenericRekeyResult Rekey(TIdentity identity, TKey oldKey, TKey newKey)
    {
        PrepareOperation();
        return writer.Rekey(identity, oldKey, newKey);
    }

    /// <summary>
    /// Releases this worker action's session admission slot.<br/>
    /// Disposal is idempotent and does not cancel or roll back operations that already published.<br/>
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref admission, null)?.Dispose();
        linkedCancellation?.Dispose();
    }

    /// <summary>
    /// Rejects tuple operations after this action has released its admission slot.<br/>
    /// </summary>
    private void PrepareOperation()
    {
        if (Volatile.Read(ref admission) is null)
        {
            throw new ObjectDisposedException(nameof(LibraDexConcurrentWriteAction<TKey, TIdentity>));
        }

        actionItems++;
        if (actionItems <= maximumActionItems)
        {
            return;
        }

        LibraDexWriteAdmissionLease current = Interlocked.Exchange(ref admission, null)!;
        current.Dispose();
        admission = writer.ReenterActionAdmission();
        actionItems = 1;
    }
}

/// <summary>
/// Carries one typed key and identity to a native sorted index builder.<br/>
/// Callers sort values according to the target index's normal key ordering and then identity ordering before invoking the builder.<br/>
/// </summary>
/// <typeparam name="TKey">Public key type accepted by the target index.<br/></typeparam>
/// <typeparam name="TIdentity">Public identity type accepted by the target index.<br/></typeparam>
/// <param name="Key">Typed key value.<br/></param>
/// <param name="Identity">Typed identity associated with the key.<br/></param>
public readonly record struct LibraDexSortedTuple<TKey, TIdentity>(TKey Key, TIdentity Identity);

/// <summary>
/// Reports topology, elapsed time, and durability boundaries for one native sorted index build.<br/>
/// Child storage is committed while unreachable; root publication is the separate operation that makes the completed generation visible.<br/>
/// </summary>
/// <param name="TupleCount">Number of accepted tuples.<br/></param>
/// <param name="OrdinaryShelfCount">Number of densely packed ordinary shelves.<br/></param>
/// <param name="TerminalRootCount">Number of exact-key terminal roots.<br/></param>
/// <param name="TerminalShelfCount">Number of densely packed terminal identity shelves.<br/></param>
/// <param name="RouterCount">Number of non-root child routers.<br/></param>
/// <param name="ReachableTopologyBytes">Exact bytes reachable through the completed generation, including its root router.<br/></param>
/// <param name="GrowthCanaryLimitBytes">Conservative pre-publication topology-growth ceiling applied to this tuple count.<br/></param>
/// <param name="StorageBuildTime">Elapsed child construction and child durability time.<br/></param>
/// <param name="RootPublicationTime">Elapsed stable-root rewrite and publication time.<br/></param>
/// <param name="ChildStorage">Storage diagnostics for the unreachable child commit.<br/></param>
/// <param name="RootPublication">Storage diagnostics for the stable-root publication commit.<br/></param>
public readonly record struct LibraDexSortedBuildDiagnostics(
    int TupleCount,
    int OrdinaryShelfCount,
    int TerminalRootCount,
    int TerminalShelfCount,
    int RouterCount,
    long ReachableTopologyBytes,
    long GrowthCanaryLimitBytes,
    TimeSpan StorageBuildTime,
    TimeSpan RootPublicationTime,
    LibraDexOperationDiagnostics ChildStorage,
    LibraDexOperationDiagnostics RootPublication)
{
    /// <summary>
    /// Gets the complete measured native-build duration.<br/>
    /// </summary>
    public TimeSpan TotalTime => StorageBuildTime + RootPublicationTime;

    /// <summary>
    /// Gets exact reachable topology bytes per accepted tuple, or zero for an empty index.<br/>
    /// </summary>
    public double BytesPerTuple => TupleCount == 0 ? 0 : (double)ReachableTopologyBytes / TupleCount;

    /// <summary>
    /// Gets the fraction of the conservative growth-canary budget consumed by the completed topology.<br/>
    /// Values near one warrant investigation even though publication was allowed; ordinary packed builds should remain substantially lower.<br/>
    /// </summary>
    public double GrowthCanaryUtilization =>
        GrowthCanaryLimitBytes == 0 ? 0 : (double)ReachableTopologyBytes / GrowthCanaryLimitBytes;
}

/// <summary>
/// Provides a shape-aware internal bridge from authoritative stable object-tuple streams to a native sorted builder.<br/>
/// Implementations return <see langword="false"/> only before storage mutation so callers may safely replay the source through a compatibility insertion path.<br/>
/// </summary>
internal interface ILibraDexNativeSortedBuild
{
    /// <summary>
    /// Attempts to build the empty destination directly from its authoritative stable tuple stream.<br/>
    /// </summary>
    /// <param name="tuples">Source tuples in destination key-then-identity order.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before native child publication.<br/></param>
    /// <param name="tupleCount">Receives the number of tuples built when the method returns <see langword="true"/>.<br/></param>
    /// <returns><see langword="true"/> when native construction completed; <see langword="false"/> when the shape or tuple state requires compatibility insertion.<br/></returns>
    bool TryBuildFromSortedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount);

    /// <summary>
    /// Attempts recovery-only native construction after sorting an authoritative unordered source by the destination's encoded key-and-identity contract.<br/>
    /// </summary>
    /// <param name="tuples">Authoritative source tuples whose existing physical traversal order is not trusted.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before native child publication.<br/></param>
    /// <param name="tupleCount">Receives the number of tuples built when the method returns <see langword="true"/>.<br/></param>
    /// <returns><see langword="true"/> when recovery completed; <see langword="false"/> before storage mutation when shape or tuple state requires compatibility insertion.<br/></returns>
    bool TryBuildFromUnorderedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount);

    /// <summary>
    /// Attempts atomic stable-root replacement from an authoritative unordered source.<br/>
    /// Implementations return <see langword="false"/> only before storage mutation when the shape or tuple state is unsupported.<br/>
    /// </summary>
    /// <param name="tuples">Authoritative replacement tuples.<br/></param>
    /// <param name="cancellationToken">Cancellation observed before replacement publication.<br/></param>
    /// <param name="tupleCount">Receives the published replacement tuple count.<br/></param>
    /// <returns><see langword="true"/> when replacement completed; otherwise <see langword="false"/> before mutation.<br/></returns>
    bool TryReplaceFromUnorderedObjects(
        IEnumerable<LibraDexObjectTuple> tuples,
        CancellationToken cancellationToken,
        out long tupleCount);
}
