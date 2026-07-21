using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Provides the low-friction public entry point for opening LibraDex index families.<br/>
/// The type lives directly under the `LibraDex` namespace so fully qualified calls and stack traces read as `LibraDex.Indexes.SS88.*` rather than repeating the project name.<br/>
/// </summary>
internal static class Indexes
{
    /// <summary>
    /// Creates a new generic fixed-scalar index and routes it to the matching current physical shelf shape.<br/>
    /// Numeric scalar types default to 8-byte storage, `Guid` defaults to 16-byte storage, and `byte[]` requires explicit width enum values so the index shape is fixed at creation time.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
    /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
    /// <param name="slotIndex">The fixed index-directory slot to create.</param>
    /// <param name="name">The fixed index-directory name to store for the created index.</param>
    /// <param name="keyWidth">Required only when <typeparamref name="TKey"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="identityWidth">Required only when <typeparamref name="TIdentity"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
    /// <param name="developerMetadata">Optional superblock developer metadata.</param>
    /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
    /// <returns>A typed runtime wrapper over the created generic index.</returns>
    internal static LibraDexIndex<TKey, TIdentity> Create<TKey, TIdentity>(
        string? path = null,
        DataKernelBackingKind backingKind = DataKernelBackingKind.File,
        int slotIndex = 0,
        string name = "primary",
        LibraDexScalarWidth? keyWidth = null,
        LibraDexScalarWidth? identityWidth = null,
        DataKernelOptions? options = null,
        SuperblockDeveloperMetadata? developerMetadata = null,
        DataKernelTelemetryOptions? telemetryOptions = null)
    {
        DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
        DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
        SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata();
        LibraDexFileSession session = backingKind switch
        {
            DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry),
            DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
            _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
        };

        return CreateGenericIndexInSession<TKey, TIdentity>(session, slotIndex, name, ownsSession: true, keyWidth, identityWidth);
    }

    /// <summary>
    /// Opens an existing file-backed generic fixed-scalar index from a fixed index-directory slot.<br/>
    /// The caller's generic types and any required `byte[]` width options select the runtime physical shape used to operate on the existing root router.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="path">The existing `.lbdx` file path.</param>
    /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
    /// <param name="keyWidth">Required only when <typeparamref name="TKey"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="identityWidth">Required only when <typeparamref name="TIdentity"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
    /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
    /// <returns>A typed runtime wrapper over the opened generic index.</returns>
    internal static LibraDexIndex<TKey, TIdentity> Open<TKey, TIdentity>(
        string path,
        int slotIndex = 0,
        LibraDexScalarWidth? keyWidth = null,
        LibraDexScalarWidth? identityWidth = null,
        DataKernelOptions? options = null,
        DataKernelTelemetryOptions? telemetryOptions = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file-backed generic LibraDex index requires a path.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The generic LibraDex index file does not exist.", path);
        }

        DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
        DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
        LibraDexFileSession session = LibraDexFileSession.Open(path, effectiveOptions, effectiveTelemetry);
        try
        {
            if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
            {
                throw new InvalidDataException("The requested generic LibraDex index slot is not active.");
            }

            LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
            return new LibraDexIndex<TKey, TIdentity>(session, slotIndex, slot.Name, slot.RootRouterOffset, shape, ownsSession: true);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing generic fixed-scalar index or creates it when it is missing.<br/>
    /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
    /// </summary>
    /// <typeparam name="TKey">The public key type.</typeparam>
    /// <typeparam name="TIdentity">The public identity type.</typeparam>
    /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
    /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
    /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
    /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
    /// <param name="keyWidth">Required only when <typeparamref name="TKey"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="identityWidth">Required only when <typeparamref name="TIdentity"/> is `byte[]`; ignored otherwise.</param>
    /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
    /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
    /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
    /// <returns>A typed runtime wrapper over the opened or created generic index.</returns>
    internal static LibraDexIndex<TKey, TIdentity> CreateOrOpen<TKey, TIdentity>(
        string? path = null,
        DataKernelBackingKind backingKind = DataKernelBackingKind.File,
        int slotIndex = 0,
        string name = "primary",
        LibraDexScalarWidth? keyWidth = null,
        LibraDexScalarWidth? identityWidth = null,
        DataKernelOptions? options = null,
        SuperblockDeveloperMetadata? developerMetadata = null,
        DataKernelTelemetryOptions? telemetryOptions = null)
    {
        if (backingKind == DataKernelBackingKind.Memory)
        {
            return Create<TKey, TIdentity>(path, backingKind, slotIndex, name, keyWidth, identityWidth, options, developerMetadata, telemetryOptions);
        }

        string requiredPath = RequireGenericPath(path);
        DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
        DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
        if (!File.Exists(requiredPath))
        {
            return Create<TKey, TIdentity>(requiredPath, DataKernelBackingKind.File, slotIndex, name, keyWidth, identityWidth, effectiveOptions, developerMetadata, effectiveTelemetry);
        }

        LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
        try
        {
            if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
            {
                LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
                return new LibraDexIndex<TKey, TIdentity>(session, slotIndex, slot.Name, slot.RootRouterOffset, shape, ownsSession: true);
            }

            return CreateGenericIndexInSession<TKey, TIdentity>(session, slotIndex, name, ownsSession: true, keyWidth, identityWidth);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static LibraDexIndex<TKey, TIdentity> CreateGenericIndexInSession<TKey, TIdentity>(
        LibraDexFileSession session,
        int slotIndex,
        string name,
        bool ownsSession,
        LibraDexScalarWidth? keyWidth,
        LibraDexScalarWidth? identityWidth)
    {
        try
        {
            if (TryFindSlot(session, slotIndex, out _))
            {
                throw new InvalidOperationException("The requested generic LibraDex index slot is already active.");
            }

            LibraDexGenericScalarShape shape = ResolveGenericShape<TKey, TIdentity>(keyWidth, identityWidth);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateGenericSlot(slotIndex, name));
            return new LibraDexIndex<TKey, TIdentity>(session, slotIndex, name, root.Offset, shape, ownsSession);
        }
        catch
        {
            if (ownsSession)
            {
                session.Dispose();
            }

            throw;
        }
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

    private static LibraDexFileSession CreateFileSession(
        string? path,
        DataKernelOptions options,
        SuperblockDeveloperMetadata developerMetadata,
        DataKernelTelemetryOptions telemetryOptions)
    {
        string requiredPath = RequireGenericPath(path);
        if (File.Exists(requiredPath))
        {
            throw new IOException($"The generic LibraDex index file already exists: {requiredPath}");
        }

        string? directory = Path.GetDirectoryName(requiredPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return LibraDexFileSession.Initialize(requiredPath, options, developerMetadata, telemetryOptions);
    }

    private static string RequireGenericPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file-backed generic LibraDex index requires a path.", nameof(path));
        }

        return path;
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

    private static DataKernelOptions CreateDefaultOptions()
    {
        return new DataKernelOptions(
            AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
    }

    private static SuperblockDeveloperMetadata CreateDefaultDeveloperMetadata()
    {
        return new SuperblockDeveloperMetadata(
            DevIdentity: "LibraDex",
            DevCustomText: "generic index",
            DevGuid: Guid.NewGuid(),
            DevDate1UtcTicks: DateTimeOffset.UtcNow.UtcDateTime.Ticks,
            DevDate2UtcTicks: 0,
            DevNumber: 0);
    }

    private static SuperblockDeveloperMetadata CreateDefaultDeveloperMetadata(string customText)
    {
        return new SuperblockDeveloperMetadata(
            DevIdentity: "LibraDex",
            DevCustomText: customText,
            DevGuid: Guid.NewGuid(),
            DevDate1UtcTicks: DateTimeOffset.UtcNow.UtcDateTime.Ticks,
            DevDate2UtcTicks: 0,
            DevNumber: 0);
    }

    private static LibraDexFileSession CreateFileSession(
        string? path,
        DataKernelOptions options,
        SuperblockDeveloperMetadata developerMetadata,
        DataKernelTelemetryOptions telemetryOptions,
        string shapeName)
    {
        string requiredPath = RequirePath(path, shapeName);
        if (File.Exists(requiredPath))
        {
            throw new IOException($"The {shapeName} index file already exists: {requiredPath}");
        }

        string? directory = Path.GetDirectoryName(requiredPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return LibraDexFileSession.Initialize(requiredPath, options, developerMetadata, telemetryOptions);
    }

    private static string RequireExistingPath(string path, string shapeName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"A file-backed {shapeName} index requires a path.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {shapeName} index file does not exist.", path);
        }

        return path;
    }

    private static string RequirePath(string? path, string shapeName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"A file-backed {shapeName} index requires a path.", nameof(path));
        }

        return path;
    }

    private static IndexDirectorySlotSnapshot CreateSlot(int slotIndex, string name)
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

    private static void ValidateMaxKeyLength(int maxKeyLength, string parameterName, string shapeName)
    {
        if (maxKeyLength <= 0 || maxKeyLength > 1024)
        {
            throw new ArgumentOutOfRangeException(parameterName, maxKeyLength, $"The public {shapeName} maximum key length must be from 1 to 1024 bytes.");
        }
    }

    private static void ValidateMaxIdentityLength(int maxIdentityLength, string parameterName, string shapeName)
    {
        if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
        {
            throw new ArgumentOutOfRangeException(parameterName, maxIdentityLength, $"The public {shapeName} maximum identity length must be from 1 to 1024 bytes.");
        }
    }

    private static VarLenOptimizerMaintenancePolicy DefaultOptimizerPolicy()
    {
        return VarLenOptimizerMaintenancePolicy.Automatic(
            depthThreshold: 3,
            shelfItemThreshold: 32,
            hitThreshold: 12);
    }

    /// <summary>
    /// Provides factory methods for the raw-byte `SV8` index shape.<br/>
    /// The shape stores encoded 8-byte scalar keys and varlen identity bytes while keeping text/path/blob codecs outside this first public API layer.<br/>
    /// </summary>
    internal static class SV8
    {
        /// <summary>
        /// Creates a new routed raw-byte `SV8` index.<br/>
        /// Root-prefix shelves are created lazily by the public batch/insert path, so creation only publishes the root router and directory slot.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="readCacheMaxBytes">The optional per-index immutable-shelf cache ceiling; zero means no limit.</param>
        /// <returns>A runtime wrapper over the created routed raw-byte `SV8` index.</returns>
        internal static Scalar8VarIdentityIndex Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV8");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata("SV8 index");
            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry, "SV8"),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
        }

        /// <summary>
        /// Opens an existing file-backed routed raw-byte `SV8` index from a fixed index-directory slot.<br/>
        /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
        /// </summary>
        /// <param name="path">The existing `.lbdx` file path.</param>
        /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="readCacheMaxBytes">The optional per-index immutable-shelf cache ceiling; zero means no limit.</param>
        /// <returns>A runtime wrapper over the opened routed raw-byte `SV8` index.</returns>
        internal static Scalar8VarIdentityIndex Open(
            string path,
            int slotIndex = 0,
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV8");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            string requiredPath = RequireExistingPath(path, "SV8");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    throw new InvalidDataException("The requested SV8 index slot is not active.");
                }

                return new Scalar8VarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxIdentityLength, ownsSession: true, readCacheMaxBytes);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing routed raw-byte `SV8` index or creates it when it is missing.<br/>
        /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
        /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
        /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="readCacheMaxBytes">The optional per-index immutable-shelf cache ceiling; zero means no limit.</param>
        /// <returns>A runtime wrapper over the opened or created routed raw-byte `SV8` index.</returns>
        internal static Scalar8VarIdentityIndex CreateOrOpen(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV8");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            if (backingKind == DataKernelBackingKind.Memory)
            {
                return Create(path, backingKind, slotIndex, name, maxIdentityLength, options, developerMetadata, telemetryOptions, readCacheMaxBytes);
            }

            string requiredPath = RequirePath(path, "SV8");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            if (!File.Exists(requiredPath))
            {
                return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, maxIdentityLength, effectiveOptions, developerMetadata, effectiveTelemetry, readCacheMaxBytes);
            }

            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    return new Scalar8VarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxIdentityLength, ownsSession: true, readCacheMaxBytes);
                }

                return CreateIndexInSession(session, slotIndex, name, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private static Scalar8VarIdentityIndex CreateIndexInSession(
            LibraDexFileSession session,
            int slotIndex,
            string name,
            int maxIdentityLength,
            bool ownsSession,
            long readCacheMaxBytes)
        {
            try
            {
                if (TryFindSlot(session, slotIndex, out _))
                {
                    throw new InvalidOperationException("The requested SV8 index slot is already active.");
                }

                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateSlot(slotIndex, name));
                return new Scalar8VarIdentityIndex(session, slotIndex, name, root.Offset, maxIdentityLength, ownsSession, readCacheMaxBytes);
            }
            catch
            {
                if (ownsSession)
                {
                    session.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Validates the optional per-index immutable-shelf read-cache ceiling.<br/>
        /// Zero preserves the design-intent default of retaining routed shelves without an arbitrary hidden cap.<br/>
        /// </summary>
        /// <param name="readCacheMaxBytes">The requested retained-byte ceiling, or zero for no limit.<br/></param>
        private static void ValidateReadCacheMaxBytes(long readCacheMaxBytes)
        {
            if (readCacheMaxBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(readCacheMaxBytes), readCacheMaxBytes, "The SV8 per-index read-cache limit cannot be negative.");
            }
        }
    }

    /// <summary>
    /// Provides factory methods for the raw-byte `SV16` index shape.<br/>
    /// The shape stores encoded 16-byte scalar keys and varlen identity bytes while keeping text/path/blob codecs outside this first public API layer.<br/>
    /// </summary>
    internal static class SV16
    {
        /// <summary>
        /// Creates a new routed raw-byte `SV16` index.<br/>
        /// Root-prefix shelves are created lazily by the public batch/insert path, so creation only publishes the root router and directory slot.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the created routed raw-byte `SV16` index.</returns>
        internal static Scalar16VarIdentityIndex Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV16");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata("SV16 index");
            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry, "SV16"),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
        }

        /// <summary>
        /// Opens an existing file-backed routed raw-byte `SV16` index from a fixed index-directory slot.<br/>
        /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
        /// </summary>
        /// <param name="path">The existing `.lbdx` file path.</param>
        /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="readCacheMaxBytes">The optional per-index immutable-shelf cache ceiling; zero means no limit.</param>
        /// <returns>A runtime wrapper over the opened routed raw-byte `SV16` index.</returns>
        internal static Scalar16VarIdentityIndex Open(
            string path,
            int slotIndex = 0,
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV16");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            string requiredPath = RequireExistingPath(path, "SV16");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    throw new InvalidDataException("The requested SV16 index slot is not active.");
                }

                return new Scalar16VarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing routed raw-byte `SV16` index or creates it when it is missing.<br/>
        /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
        /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
        /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="readCacheMaxBytes">The optional per-index immutable-shelf cache ceiling; zero means no limit.</param>
        /// <returns>A runtime wrapper over the opened or created routed raw-byte `SV16` index.</returns>
        internal static Scalar16VarIdentityIndex CreateOrOpen(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            long readCacheMaxBytes = 0)
        {
            ValidateMaxIdentityLength(maxIdentityLength, nameof(maxIdentityLength), "SV16");
            ValidateReadCacheMaxBytes(readCacheMaxBytes);
            if (backingKind == DataKernelBackingKind.Memory)
            {
                return Create(path, backingKind, slotIndex, name, maxIdentityLength, options, developerMetadata, telemetryOptions, readCacheMaxBytes);
            }

            string requiredPath = RequirePath(path, "SV16");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            if (!File.Exists(requiredPath))
            {
                return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, maxIdentityLength, effectiveOptions, developerMetadata, effectiveTelemetry, readCacheMaxBytes);
            }

            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    return new Scalar16VarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
                }

                return CreateIndexInSession(session, slotIndex, name, maxIdentityLength, ownsSession: true, readCacheMaxBytes: readCacheMaxBytes);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private static Scalar16VarIdentityIndex CreateIndexInSession(
            LibraDexFileSession session,
            int slotIndex,
            string name,
            int maxIdentityLength,
            bool ownsSession,
            long readCacheMaxBytes)
        {
            try
            {
                if (TryFindSlot(session, slotIndex, out _))
                {
                    throw new InvalidOperationException("The requested SV16 index slot is already active.");
                }

                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateSlot(slotIndex, name));
                return new Scalar16VarIdentityIndex(session, slotIndex, name, root.Offset, maxIdentityLength, ownsSession, readCacheMaxBytes);
            }
            catch
            {
                if (ownsSession)
                {
                    session.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Validates the optional per-index `SV16` read-cache ceiling.<br/>
        /// Zero deliberately means no limit, while positive values bound runtime retention without becoming persisted index metadata.<br/>
        /// </summary>
        /// <param name="readCacheMaxBytes">The requested retained-byte limit, or zero for no limit.<br/></param>
        private static void ValidateReadCacheMaxBytes(long readCacheMaxBytes)
        {
            if (readCacheMaxBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(readCacheMaxBytes), readCacheMaxBytes, "The SV16 per-index read-cache limit cannot be negative.");
            }
        }
    }

    /// <summary>
    /// Provides factory methods for the raw-byte `VS8` index shape.<br/>
    /// The shape stores varlen key bytes and encoded 8-byte scalar identities while keeping text/path/blob codecs outside this first public API layer.<br/>
    /// </summary>
    internal static class VS8
    {
        /// <summary>
        /// Creates a new routed raw-byte `VS8` index.<br/>
        /// Root-prefix shelves are created lazily by the public batch/insert path, so creation only publishes the root router and directory slot.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the created routed raw-byte `VS8` index.</returns>
        internal static VarKeyScalar8Index Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS8");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata("VS8 index");
            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry, "VS8"),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, maxKeyLength, ownsSession: true);
        }

        /// <summary>
        /// Opens an existing file-backed routed raw-byte `VS8` index from a fixed index-directory slot.<br/>
        /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
        /// </summary>
        /// <param name="path">The existing `.lbdx` file path.</param>
        /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened routed raw-byte `VS8` index.</returns>
        internal static VarKeyScalar8Index Open(
            string path,
            int slotIndex = 0,
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS8");
            string requiredPath = RequireExistingPath(path, "VS8");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    throw new InvalidDataException("The requested VS8 index slot is not active.");
                }

                VarKeyScalar8IndexHandle handle = new(slot.RootRouterOffset, maxKeyLength, OptimizerRouteFanout: 16, DefaultOptimizerPolicy());
                handle.Validate();
                return new VarKeyScalar8Index(session, handle, slotIndex, slot.Name, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing routed raw-byte `VS8` index or creates it when it is missing.<br/>
        /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
        /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
        /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened or created routed raw-byte `VS8` index.</returns>
        internal static VarKeyScalar8Index CreateOrOpen(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS8");
            if (backingKind == DataKernelBackingKind.Memory)
            {
                return Create(path, backingKind, slotIndex, name, maxKeyLength, options, developerMetadata, telemetryOptions);
            }

            string requiredPath = RequirePath(path, "VS8");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            if (!File.Exists(requiredPath))
            {
                return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, maxKeyLength, effectiveOptions, developerMetadata, effectiveTelemetry);
            }

            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    VarKeyScalar8IndexHandle handle = new(slot.RootRouterOffset, maxKeyLength, OptimizerRouteFanout: 16, DefaultOptimizerPolicy());
                    handle.Validate();
                    return new VarKeyScalar8Index(session, handle, slotIndex, slot.Name, ownsSession: true);
                }

                return CreateIndexInSession(session, slotIndex, name, maxKeyLength, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private static VarKeyScalar8Index CreateIndexInSession(LibraDexFileSession session, int slotIndex, string name, int maxKeyLength, bool ownsSession)
        {
            try
            {
                if (TryFindSlot(session, slotIndex, out _))
                {
                    throw new InvalidOperationException("The requested VS8 index slot is already active.");
                }

                (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateSlot(slotIndex, name), maxKeyLength, optimizerRouteFanout: 16, DefaultOptimizerPolicy());
                return new VarKeyScalar8Index(session, handle, slotIndex, name, ownsSession);
            }
            catch
            {
                if (ownsSession)
                {
                    session.Dispose();
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Provides factory methods for the raw-byte `VS16` index shape.<br/>
    /// The shape stores varlen key bytes and encoded 16-byte scalar identities while keeping text/path/blob codecs outside this first public API layer.<br/>
    /// </summary>
    internal static class VS16
    {
        /// <summary>
        /// Creates a new routed raw-byte `VS16` index.<br/>
        /// Root-prefix shelves are created lazily by the public batch/insert path, so creation only publishes the root router and directory slot.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the created routed raw-byte `VS16` index.</returns>
        internal static VarKeyScalar16Index Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS16");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata("VS16 index");
            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry, "VS16"),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, maxKeyLength, ownsSession: true);
        }

        /// <summary>
        /// Opens an existing file-backed routed raw-byte `VS16` index from a fixed index-directory slot.<br/>
        /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
        /// </summary>
        /// <param name="path">The existing `.lbdx` file path.</param>
        /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened routed raw-byte `VS16` index.</returns>
        internal static VarKeyScalar16Index Open(
            string path,
            int slotIndex = 0,
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS16");
            string requiredPath = RequireExistingPath(path, "VS16");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    throw new InvalidDataException("The requested VS16 index slot is not active.");
                }

                VarKeyScalar16IndexHandle handle = new(slot.RootRouterOffset, maxKeyLength, OptimizerRouteFanout: 16, DefaultOptimizerPolicy());
                handle.Validate();
                return new VarKeyScalar16Index(session, handle, slotIndex, slot.Name, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing routed raw-byte `VS16` index or creates it when it is missing.<br/>
        /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
        /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
        /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened or created routed raw-byte `VS16` index.</returns>
        internal static VarKeyScalar16Index CreateOrOpen(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxKeyLength(maxKeyLength, nameof(maxKeyLength), "VS16");
            if (backingKind == DataKernelBackingKind.Memory)
            {
                return Create(path, backingKind, slotIndex, name, maxKeyLength, options, developerMetadata, telemetryOptions);
            }

            string requiredPath = RequirePath(path, "VS16");
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            if (!File.Exists(requiredPath))
            {
                return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, maxKeyLength, effectiveOptions, developerMetadata, effectiveTelemetry);
            }

            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    VarKeyScalar16IndexHandle handle = new(slot.RootRouterOffset, maxKeyLength, OptimizerRouteFanout: 16, DefaultOptimizerPolicy());
                    handle.Validate();
                    return new VarKeyScalar16Index(session, handle, slotIndex, slot.Name, ownsSession: true);
                }

                return CreateIndexInSession(session, slotIndex, name, maxKeyLength, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private static VarKeyScalar16Index CreateIndexInSession(LibraDexFileSession session, int slotIndex, string name, int maxKeyLength, bool ownsSession)
        {
            try
            {
                if (TryFindSlot(session, slotIndex, out _))
                {
                    throw new InvalidOperationException("The requested VS16 index slot is already active.");
                }

                (VarKeyScalar16IndexHandle handle, _, _) = session.CreateVarKeyScalar16RootRouterIndex(CreateSlot(slotIndex, name), maxKeyLength, optimizerRouteFanout: 16, DefaultOptimizerPolicy());
                return new VarKeyScalar16Index(session, handle, slotIndex, name, ownsSession);
            }
            catch
            {
                if (ownsSession)
                {
                    session.Dispose();
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Provides factory methods for the raw-byte `VV` index shape.<br/>
    /// The shape stores varlen key bytes and varlen identity bytes while keeping text/blob/projection codecs outside this first public API layer.<br/>
    /// </summary>
    internal static class VV
    {
        /// <summary>
        /// Creates a new routed raw-byte `VV` index.<br/>
        /// File-backed creation fails if the target file already exists, while memory-backed creation returns a temporary process-local index that cannot be reopened after disposal.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the created routed raw-byte `VV` index.</returns>
        /// <exception cref="ArgumentException">Thrown when a file-backed path is missing or memory-backed open semantics are requested.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the maximum key or identity length is outside the current public limits.</exception>
        /// <exception cref="IOException">Thrown when the file-backed target already exists.</exception>
        internal static VarKeyVarIdentityIndex Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxLengths(maxKeyLength, maxIdentityLength);
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata();

            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, maxKeyLength, maxIdentityLength, ownsSession: true);
        }

        /// <summary>
        /// Opens an existing file-backed routed raw-byte `VV` index from a fixed index-directory slot.<br/>
        /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
        /// </summary>
        /// <param name="path">The existing `.lbdx` file path.</param>
        /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened routed raw-byte `VV` index.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the maximum key or identity length is outside the current public limits.</exception>
        /// <exception cref="FileNotFoundException">Thrown when the target file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown when the slot is missing or inactive.</exception>
        internal static VarKeyVarIdentityIndex Open(
            string path,
            int slotIndex = 0,
            int maxKeyLength = 1024,
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxLengths(maxKeyLength, maxIdentityLength);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file-backed VV index requires a path.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The VV index file does not exist.", path);
            }

            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            LibraDexFileSession session = LibraDexFileSession.Open(path, effectiveOptions, effectiveTelemetry);
            try
            {
                if (!TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    throw new InvalidDataException("The requested VV index slot is not active.");
                }

                return new VarKeyVarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxKeyLength, maxIdentityLength, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens an existing routed raw-byte `VV` index or creates it when it is missing.<br/>
        /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
        /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
        /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
        /// <param name="maxKeyLength">The maximum raw key length accepted by the public wrapper.</param>
        /// <param name="maxIdentityLength">The maximum raw identity length accepted by the public wrapper.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <returns>A runtime wrapper over the opened or created routed raw-byte `VV` index.</returns>
        /// <exception cref="ArgumentException">Thrown when a file-backed path is missing.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the maximum key or identity length is outside the current public limits.</exception>
        internal static VarKeyVarIdentityIndex CreateOrOpen(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            int maxKeyLength = 1024,
            int maxIdentityLength = 1024,
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null)
        {
            ValidateMaxLengths(maxKeyLength, maxIdentityLength);
            if (backingKind == DataKernelBackingKind.Memory)
            {
                return Create(path, backingKind, slotIndex, name, maxKeyLength, maxIdentityLength, options, developerMetadata, telemetryOptions);
            }

            string requiredPath = RequirePath(path);
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            if (!File.Exists(requiredPath))
            {
                return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, maxKeyLength, maxIdentityLength, effectiveOptions, developerMetadata, effectiveTelemetry);
            }

            LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
            try
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    return new VarKeyVarIdentityIndex(session, slotIndex, slot.Name, slot.RootRouterOffset, maxKeyLength, maxIdentityLength, ownsSession: true);
                }

                return CreateIndexInSession(session, slotIndex, name, maxKeyLength, maxIdentityLength, ownsSession: true);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private static VarKeyVarIdentityIndex CreateIndexInSession(
            LibraDexFileSession session,
            int slotIndex,
            string name,
            int maxKeyLength,
            int maxIdentityLength,
            bool ownsSession)
        {
            try
            {
                if (TryFindSlot(session, slotIndex, out _))
                {
                    throw new InvalidOperationException("The requested VV index slot is already active.");
                }

                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateSlot(slotIndex, name));
                return new VarKeyVarIdentityIndex(session, slotIndex, name, root.Offset, maxKeyLength, maxIdentityLength, ownsSession);
            }
            catch
            {
                if (ownsSession)
                {
                    session.Dispose();
                }

                throw;
            }
        }

        private static LibraDexFileSession CreateFileSession(
            string? path,
            DataKernelOptions options,
            SuperblockDeveloperMetadata developerMetadata,
            DataKernelTelemetryOptions telemetryOptions)
        {
            string requiredPath = RequirePath(path);
            if (File.Exists(requiredPath))
            {
                throw new IOException($"The VV index file already exists: {requiredPath}");
            }

            string? directory = Path.GetDirectoryName(requiredPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return LibraDexFileSession.Initialize(requiredPath, options, developerMetadata, telemetryOptions);
        }

        private static string RequirePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A file-backed VV index requires a path.", nameof(path));
            }

            return path;
        }

        private static IndexDirectorySlotSnapshot CreateSlot(int slotIndex, string name)
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

        private static void ValidateMaxLengths(int maxKeyLength, int maxIdentityLength)
        {
            if (maxKeyLength <= 0 || maxKeyLength > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maxKeyLength), maxKeyLength, "The public VV maximum key length must be from 1 to 1024 bytes.");
            }

            if (maxIdentityLength <= 0 || maxIdentityLength > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maxIdentityLength), maxIdentityLength, "The public VV maximum identity length must be from 1 to 1024 bytes.");
            }
        }

        private static DataKernelOptions CreateDefaultOptions()
        {
            return new DataKernelOptions(
                AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
                ReservedPrefixBytes: 0,
                FlushToDiskOnCommit: false,
                MaxCommitGapCoalesceBytes: 512);
        }

        private static SuperblockDeveloperMetadata CreateDefaultDeveloperMetadata()
        {
            return new SuperblockDeveloperMetadata(
                DevIdentity: "LibraDex",
                DevCustomText: "VV index",
                DevGuid: Guid.NewGuid(),
                DevDate1UtcTicks: DateTimeOffset.UtcNow.UtcDateTime.Ticks,
                DevDate2UtcTicks: 0,
                DevNumber: 0);
        }
    }

    /// <summary>
    /// Provides factory methods for the first encoded `SS8-8` index shape.<br/>
    /// Factory methods choose file-backed or memory-backed sessions, resolve the fixed index-directory slot, and return a concrete `Scalar8Scalar8Index` wrapper.<br/>
    /// </summary>
    internal static class SS88
    {
        /// <summary>
        /// Creates a new encoded `SS8-8` index.<br/>
        /// File-backed creation fails if the target file already exists, while memory-backed creation returns a temporary process-local index that cannot be reopened after disposal.<br/>
        /// </summary>
        /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
        /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
        /// <param name="slotIndex">The fixed index-directory slot to create.</param>
        /// <param name="name">The fixed index-directory name to store for the created index.</param>
        /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
        /// <param name="developerMetadata">Optional superblock developer metadata.</param>
        /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
        /// <param name="shelfExtentSize">The supported fixed shelf extent size to persist for the created `SS8-8` profile.</param>
        /// <returns>A runtime wrapper over the created encoded `SS8-8` index.</returns>
        /// <exception cref="ArgumentException">Thrown when a file-backed path is missing or memory-backed open semantics are requested.</exception>
        /// <exception cref="IOException">Thrown when the file-backed target already exists.</exception>
        internal static Scalar8Scalar8Index Create(
            string? path = null,
            DataKernelBackingKind backingKind = DataKernelBackingKind.File,
            int slotIndex = 0,
            string name = "primary",
            DataKernelOptions? options = null,
            SuperblockDeveloperMetadata? developerMetadata = null,
            DataKernelTelemetryOptions? telemetryOptions = null,
            int shelfExtentSize = 32 * 1024)
        {
            DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
            DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
            SuperblockDeveloperMetadata effectiveMetadata = developerMetadata ?? CreateDefaultDeveloperMetadata();

            LibraDexFileSession session = backingKind switch
            {
                DataKernelBackingKind.File => CreateFileSession(path, effectiveOptions, effectiveMetadata, effectiveTelemetry),
                DataKernelBackingKind.Memory => LibraDexFileSession.InitializeMemory(effectiveOptions, effectiveMetadata, effectiveTelemetry),
                _ => throw new ArgumentOutOfRangeException(nameof(backingKind), backingKind, "Unsupported LibraDex backing kind.")
            };

            return CreateIndexInSession(session, slotIndex, name, ownsSession: true, shelfExtentSize);
        }

            /// <summary>
            /// Opens an existing file-backed encoded `SS8-8` index from a fixed index-directory slot.<br/>
            /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
            /// </summary>
            /// <param name="path">The existing `.lbdx` file path.</param>
            /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
            /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
            /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
            /// <param name="shelfExtentSize">The supported fixed shelf extent size to persist when creating a missing `SS8-8` profile.</param>
            /// <returns>A runtime wrapper over the opened encoded `SS8-8` index.</returns>
            /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
            /// <exception cref="FileNotFoundException">Thrown when the target file does not exist.</exception>
            /// <exception cref="InvalidDataException">Thrown when the slot is missing, inactive, or not an `SS8-8` profile.</exception>
            internal static Scalar8Scalar8Index Open(
                string path,
                int slotIndex = 0,
                DataKernelOptions? options = null,
                DataKernelTelemetryOptions? telemetryOptions = null)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("A file-backed SS8-8 index requires a path.", nameof(path));
                }

                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("The SS8-8 index file does not exist.", path);
                }

                DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
                DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
                LibraDexFileSession session = LibraDexFileSession.Open(path, effectiveOptions, effectiveTelemetry);
                try
                {
                    Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(slotIndex);
                    string name = FindSlotName(session, slotIndex);
                    return new Scalar8Scalar8Index(session, handle, slotIndex, name, ownsSession: true);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// Opens an existing encoded `SS8-8` index or creates it when it is missing.<br/>
            /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
            /// </summary>
            /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
            /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
            /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
            /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
            /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
            /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
            /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
            /// <returns>A runtime wrapper over the opened or created encoded `SS8-8` index.</returns>
            /// <exception cref="ArgumentException">Thrown when a file-backed path is missing.</exception>
            /// <exception cref="InvalidDataException">Thrown when an existing active slot is not an `SS8-8` profile.</exception>
            internal static Scalar8Scalar8Index CreateOrOpen(
                string? path = null,
                DataKernelBackingKind backingKind = DataKernelBackingKind.File,
                int slotIndex = 0,
                string name = "primary",
                DataKernelOptions? options = null,
                SuperblockDeveloperMetadata? developerMetadata = null,
                DataKernelTelemetryOptions? telemetryOptions = null,
                int shelfExtentSize = 32 * 1024)
            {
                if (backingKind == DataKernelBackingKind.Memory)
                {
                    return Create(path, backingKind, slotIndex, name, options, developerMetadata, telemetryOptions, shelfExtentSize);
                }

                string requiredPath = RequirePath(path);
                DataKernelOptions effectiveOptions = options ?? CreateDefaultOptions();
                DataKernelTelemetryOptions effectiveTelemetry = telemetryOptions ?? DataKernelTelemetryOptions.EnabledOptions;
                if (!File.Exists(requiredPath))
                {
                    return Create(requiredPath, DataKernelBackingKind.File, slotIndex, name, effectiveOptions, developerMetadata, effectiveTelemetry, shelfExtentSize);
                }

                LibraDexFileSession session = LibraDexFileSession.Open(requiredPath, effectiveOptions, effectiveTelemetry);
                try
                {
                    if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                    {
                        Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(slot);
                        return new Scalar8Scalar8Index(session, handle, slotIndex, slot.Name, ownsSession: true);
                    }

                    return CreateIndexInSession(session, slotIndex, name, ownsSession: true, shelfExtentSize);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            private static Scalar8Scalar8Index CreateIndexInSession(
                LibraDexFileSession session,
                int slotIndex,
                string name,
                bool ownsSession,
                int shelfExtentSize)
            {
                try
                {
                    if (TryFindSlot(session, slotIndex, out _))
                    {
                        throw new InvalidOperationException("The requested SS8-8 index slot is already active.");
                    }

                    IndexDirectorySlotSnapshot slot = CreateSlot(slotIndex, name, shelfExtentSize);
                    (Scalar8Scalar8IndexHandle handle, _, _) = session.CreateScalar8Scalar8RootRouterIndex(slot);
                    return new Scalar8Scalar8Index(session, handle, slotIndex, name, ownsSession);
                }
                catch
                {
                    if (ownsSession)
                    {
                        session.Dispose();
                    }

                    throw;
                }
            }

            private static LibraDexFileSession CreateFileSession(
                string? path,
                DataKernelOptions options,
                SuperblockDeveloperMetadata developerMetadata,
                DataKernelTelemetryOptions telemetryOptions)
            {
                string requiredPath = RequirePath(path);
                if (File.Exists(requiredPath))
                {
                    throw new IOException($"The SS8-8 index file already exists: {requiredPath}");
                }

                string? directory = Path.GetDirectoryName(requiredPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return LibraDexFileSession.Initialize(requiredPath, options, developerMetadata, telemetryOptions);
            }

            private static string RequirePath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("A file-backed SS8-8 index requires a path.", nameof(path));
                }

                return path;
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

            private static string FindSlotName(LibraDexFileSession session, int slotIndex)
            {
                if (TryFindSlot(session, slotIndex, out IndexDirectorySlotSnapshot slot))
                {
                    return slot.Name;
                }

                return string.Empty;
            }

            private static IndexDirectorySlotSnapshot CreateSlot(int slotIndex, string name, int shelfExtentSize)
            {
                if ((uint)slotIndex >= IndexDirectoryLayout.SlotCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Index directory slot is outside the fixed directory.");
                }

                ushort allocationClassId = GetAllocationClassIdForShelfExtentSize(shelfExtentSize);
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
                    AllocationClassId: allocationClassId,
                    Name: name);
            }

            /// <summary>
            /// Maps a supported public `SS8-8` shelf extent to the persisted allocation-class identifier used by the index directory.<br/>
            /// Key, identity, and router profile identifiers stay stable because this subshape changes fixed shelf size rather than tuple encoding or route semantics.<br/>
            /// </summary>
            /// <param name="shelfExtentSize">The requested fixed shelf extent size in bytes.</param>
            /// <returns>The allocation-class identifier for the supported public shelf extent.</returns>
            /// <exception cref="ArgumentOutOfRangeException">Thrown when the requested extent is not a supported public `SS8-8` profile.</exception>
            private static ushort GetAllocationClassIdForShelfExtentSize(int shelfExtentSize)
            {
                Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.FromSupportedShelfExtentSize(shelfExtentSize);
                return profile.ShelfExtentSize switch
                {
                    16 * 1024 => 3,
                    24 * 1024 => 2,
                    32 * 1024 => 1,
                    48 * 1024 => 4,
                    64 * 1024 => 5,
                    _ => throw new ArgumentOutOfRangeException(nameof(shelfExtentSize), shelfExtentSize, "Supported SS8-8 shelf-size sweep extents are 16384, 24576, 32768, 49152, and 65536 bytes.")
                };
            }

            private static DataKernelOptions CreateDefaultOptions()
            {
                return new DataKernelOptions(
                    AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
                    ReservedPrefixBytes: 0,
                    FlushToDiskOnCommit: false,
                    MaxCommitGapCoalesceBytes: 512);
            }

            private static SuperblockDeveloperMetadata CreateDefaultDeveloperMetadata()
            {
                return new SuperblockDeveloperMetadata(
                    DevIdentity: "LibraDex",
                    DevCustomText: "SS8-8 index",
                    DevGuid: Guid.NewGuid(),
                    DevDate1UtcTicks: DateTimeOffset.UtcNow.UtcDateTime.Ticks,
                    DevDate2UtcTicks: 0,
                    DevNumber: 0);
            }

            /// <summary>
            /// Provides typed factory methods for unsigned scalar-8 key and unsigned scalar-8 identity indexes over the encoded `SS8-8` core.<br/>
            /// The nested type keeps the codec choice explicit while preserving the short `Create`, `Open`, and `CreateOrOpen` factory names.<br/>
            /// </summary>
            internal static class Unsigned
            {
                /// <summary>
                /// Creates a new unsigned scalar `SS8-8` index.<br/>
                /// File-backed creation fails if the target file already exists, while memory-backed creation returns a temporary process-local index that cannot be reopened after disposal.<br/>
                /// </summary>
                /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
                /// <param name="backingKind">Whether to create a file-backed or memory-backed index.</param>
                /// <param name="slotIndex">The fixed index-directory slot to create.</param>
                /// <param name="name">The fixed index-directory name to store for the created index.</param>
                /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
                /// <param name="developerMetadata">Optional superblock developer metadata.</param>
                /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
                /// <param name="shelfExtentSize">The supported fixed shelf extent size to persist for the created `SS8-8` profile.</param>
                /// <returns>A typed runtime wrapper over the created unsigned scalar `SS8-8` index.</returns>
                /// <exception cref="ArgumentException">Thrown when a file-backed path is missing or memory-backed open semantics are requested.</exception>
                /// <exception cref="IOException">Thrown when the file-backed target already exists.</exception>
                internal static UnsignedScalar8Scalar8Index Create(
                    string? path = null,
                    DataKernelBackingKind backingKind = DataKernelBackingKind.File,
                    int slotIndex = 0,
                    string name = "primary",
                    DataKernelOptions? options = null,
                    SuperblockDeveloperMetadata? developerMetadata = null,
                    DataKernelTelemetryOptions? telemetryOptions = null,
                    int shelfExtentSize = 32 * 1024)
                {
                    return new UnsignedScalar8Scalar8Index(SS88.Create(
                        path,
                        backingKind,
                        slotIndex,
                        name,
                        options,
                        developerMetadata,
                        telemetryOptions,
                        shelfExtentSize));
                }

                /// <summary>
                /// Opens an existing file-backed unsigned scalar `SS8-8` index from a fixed index-directory slot.<br/>
                /// Memory-backed indexes are intentionally excluded because they have no durable reopen boundary.<br/>
                /// </summary>
                /// <param name="path">The existing `.lbdx` file path.</param>
                /// <param name="slotIndex">The fixed index-directory slot to resolve.</param>
                /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
                /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
                /// <param name="shelfExtentSize">The supported fixed shelf extent size to persist when creating a missing `SS8-8` profile.</param>
                /// <returns>A typed runtime wrapper over the opened unsigned scalar `SS8-8` index.</returns>
                /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
                /// <exception cref="FileNotFoundException">Thrown when the target file does not exist.</exception>
                /// <exception cref="InvalidDataException">Thrown when the slot is missing, inactive, or not an `SS8-8` profile.</exception>
                internal static UnsignedScalar8Scalar8Index Open(
                    string path,
                    int slotIndex = 0,
                    DataKernelOptions? options = null,
                    DataKernelTelemetryOptions? telemetryOptions = null)
                {
                    return new UnsignedScalar8Scalar8Index(SS88.Open(
                        path,
                        slotIndex,
                        options,
                        telemetryOptions));
                }

                /// <summary>
                /// Opens an existing unsigned scalar `SS8-8` index or creates it when it is missing.<br/>
                /// File-backed mode opens the existing file when present and creates the requested slot if that file has no active slot there; memory-backed mode always creates a new temporary index.<br/>
                /// </summary>
                /// <param name="path">The `.lbdx` file path for file-backed indexes; ignored for memory-backed indexes.</param>
                /// <param name="backingKind">Whether to use file-backed or memory-backed storage.</param>
                /// <param name="slotIndex">The fixed index-directory slot to open or create.</param>
                /// <param name="name">The fixed index-directory name to store when a new slot is created.</param>
                /// <param name="options">Optional DataKernel policy; defaults to a facade policy with the superblock at file offset zero.</param>
                /// <param name="developerMetadata">Optional superblock developer metadata for newly created sessions.</param>
                /// <param name="telemetryOptions">Optional telemetry policy; enabled by default for early validation.</param>
                /// <returns>A typed runtime wrapper over the opened or created unsigned scalar `SS8-8` index.</returns>
                /// <exception cref="ArgumentException">Thrown when a file-backed path is missing.</exception>
                /// <exception cref="InvalidDataException">Thrown when an existing active slot is not an `SS8-8` profile.</exception>
                internal static UnsignedScalar8Scalar8Index CreateOrOpen(
                    string? path = null,
                    DataKernelBackingKind backingKind = DataKernelBackingKind.File,
                    int slotIndex = 0,
                    string name = "primary",
                    DataKernelOptions? options = null,
                    SuperblockDeveloperMetadata? developerMetadata = null,
                    DataKernelTelemetryOptions? telemetryOptions = null,
                    int shelfExtentSize = 32 * 1024)
                {
                    return new UnsignedScalar8Scalar8Index(SS88.CreateOrOpen(
                        path,
                        backingKind,
                        slotIndex,
                        name,
                        options,
                        developerMetadata,
                        telemetryOptions,
                        shelfExtentSize));
                }
            }
    }
}
