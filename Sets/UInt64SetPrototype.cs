using System.Buffers;
using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Provides the harness-gated UInt64 ordered presence-set proof over the existing DataKernel backings.<br/>
/// The prototype owns a direct key-only topology and deliberately remains outside the public catalog surface until correctness, reopen, churn, and benchmark gates pass.<br/>
/// </summary>
internal sealed class UInt64SetPrototype : IDisposable
{
    private const int MaximumRouterLevels = sizeof(ulong);
    private const int TraversalPageCount = MaximumRouterLevels + 1;
    private readonly DataKernel _kernel;
    private readonly object _mutationSync = new();
    private readonly string? _path;
    private readonly UInt64SetPrototypeKind _kind;
    private readonly byte[] _mutationRouterPage = GC.AllocateUninitializedArray<byte>(UInt64SetPrototypeRootLayout.PageSize);
    private readonly byte[] _mutationTargetPage = GC.AllocateUninitializedArray<byte>(UInt64SetPrototypeRootLayout.PageSize);
    private readonly byte[] _rootRouterCache = GC.AllocateUninitializedArray<byte>(UInt64SetPrototypeRootLayout.PageSize);
    private long _rootRouterOffset;
    private long _count;
    private long _generation;
    private bool _rootRouterCacheValid;
    private bool _disposed;

    private UInt64SetPrototype(
        DataKernel kernel,
        string? path,
        long rootRouterOffset,
        ulong count,
        ulong generation,
        UInt64SetPrototypeKind kind)
    {
        _kernel = kernel;
        _path = path;
        _rootRouterOffset = rootRouterOffset;
        _count = unchecked((long)count);
        _generation = unchecked((long)generation);
        _kind = kind;
    }

    /// <summary>
    /// Gets the exact number of distinct keys currently present.<br/>
    /// The value is maintained with each successful logical mutation and does not enumerate the topology.<br/>
    /// </summary>
    internal ulong Count => unchecked((ulong)Interlocked.Read(ref _count));

    /// <summary>
    /// Gets the successful logical mutation generation.<br/>
    /// Duplicate additions and removal misses do not advance this value.<br/>
    /// </summary>
    internal ulong Generation => unchecked((ulong)Interlocked.Read(ref _generation));

    /// <summary>
    /// Gets whether this prototype uses the process-local memory arena or a reopenable file.<br/>
    /// </summary>
    internal DataKernelBackingKind BackingKind => _kernel.BackingKind;

    /// <summary>
    /// Gets the current file length for a file-backed prototype.<br/>
    /// Memory-backed prototypes return zero because they do not own a durable file image.<br/>
    /// </summary>
    internal long FileLength => _path is null ? 0 : new FileInfo(_path).Length;

    /// <summary>
    /// Captures exact file-allocation accounting for prototype lifecycle validation.<br/>
    /// Memory-backed prototypes return the default snapshot because they do not use the durable extent allocator.<br/>
    /// </summary>
    /// <returns>The allocator's current materialized, occupied, and reusable extent accounting.<br/></returns>
    internal FileAllocationStorageSnapshot GetFileAllocationStorageSnapshot() =>
        _kernel.GetFileAllocationStorageSnapshot();

    /// <summary>
    /// Creates an empty process-local UInt64 ordered presence set.<br/>
    /// Memory mode uses the same root, router, and shelf byte formats as file mode but cannot be reopened after disposal.<br/>
    /// </summary>
    /// <returns>An initialized memory-backed prototype.<br/></returns>
    internal static UInt64SetPrototype CreateMemory()
        => CreateMemory(UInt64SetPrototypeKind.Presence);

    /// <summary>
    /// Creates an empty process-local UInt64 routed presence set.<br/>
    /// The backing shares root, router, allocator, and publication mechanics with the sorted set while selecting the end-filled routed leaf format.<br/>
    /// </summary>
    /// <returns>An initialized memory-backed routed prototype.<br/></returns>
    internal static UInt64SetPrototype CreateRoutedMemory()
        => CreateMemory(UInt64SetPrototypeKind.RoutedPresence);

    /// <summary>
    /// Creates an empty process-local UInt64 presence set for one persisted leaf policy.<br/>
    /// </summary>
    /// <param name="kind">The sorted or routed presence kind.<br/></param>
    /// <returns>An initialized memory-backed prototype.<br/></returns>
    private static UInt64SetPrototype CreateMemory(UInt64SetPrototypeKind kind)
    {
        DataKernel kernel = DataKernel.OpenMemory(CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            return Initialize(kernel, path: null, kind);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new file-backed UInt64 ordered presence set.<br/>
    /// Creation fails when the target exists so a harness cannot accidentally overwrite an unrelated durable artifact.<br/>
    /// </summary>
    /// <param name="path">The new prototype file path.<br/></param>
    /// <returns>An initialized file-backed prototype.<br/></returns>
    internal static UInt64SetPrototype Create(string path)
        => Create(path, UInt64SetPrototypeKind.Presence);

    /// <summary>
    /// Creates a new file-backed UInt64 routed presence set.<br/>
    /// Creation fails when the target exists and persists a distinct root kind so sorted and routed handles cannot be interchanged accidentally.<br/>
    /// </summary>
    /// <param name="path">The new routed-set file path.<br/></param>
    /// <returns>An initialized file-backed routed prototype.<br/></returns>
    internal static UInt64SetPrototype CreateRouted(string path)
        => Create(path, UInt64SetPrototypeKind.RoutedPresence);

    /// <summary>
    /// Creates one file-backed UInt64 presence set for the requested persisted leaf policy.<br/>
    /// </summary>
    /// <param name="path">The new prototype file path.<br/></param>
    /// <param name="kind">The sorted or routed presence kind.<br/></param>
    /// <returns>An initialized file-backed prototype.<br/></returns>
    private static UInt64SetPrototype Create(string path, UInt64SetPrototypeKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        DataKernel kernel = DataKernel.Open(fullPath, FileMode.CreateNew, CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            return Initialize(kernel, fullPath, kind);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing file-backed UInt64 ordered presence-set prototype.<br/>
    /// The method validates the root descriptor before restoring the allocator and exposing any topology reads.<br/>
    /// </summary>
    /// <param name="path">The existing prototype file path.<br/></param>
    /// <returns>The reopened set prototype.<br/></returns>
    internal static UInt64SetPrototype Open(string path)
        => Open(path, UInt64SetPrototypeKind.Presence);

    /// <summary>
    /// Opens an existing file-backed UInt64 routed presence set.<br/>
    /// Root-kind validation rejects sorted or counted set files before any topology is exposed.<br/>
    /// </summary>
    /// <param name="path">The existing routed-set file path.<br/></param>
    /// <returns>The reopened routed prototype.<br/></returns>
    internal static UInt64SetPrototype OpenRouted(string path)
        => Open(path, UInt64SetPrototypeKind.RoutedPresence);

    /// <summary>
    /// Opens one file-backed UInt64 presence set and requires an exact persisted collection kind.<br/>
    /// </summary>
    /// <param name="path">The existing prototype file path.<br/></param>
    /// <param name="expectedKind">The sorted or routed presence kind required by the caller.<br/></param>
    /// <returns>The reopened set prototype.<br/></returns>
    private static UInt64SetPrototype Open(string path, UInt64SetPrototypeKind expectedKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        DataKernel kernel = DataKernel.Open(fullPath, FileMode.Open, CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            byte[] rootBytes = new byte[UInt64SetPrototypeRootLayout.Size];
            kernel.Read(0, rootBytes);
            if (!UInt64SetPrototypeRootLayout.IsValid(rootBytes) ||
                UInt64SetPrototypeRootLayout.ReadKind(rootBytes) != expectedKind)
            {
                throw new InvalidDataException($"The file is not a valid UInt64 {GetKindDisplayName(expectedKind)} presence-set prototype.");
            }

            long allocationDirectoryOffset = UInt64SetPrototypeRootLayout.ReadAllocationDirectoryOffset(rootBytes);
            long rootRouterOffset = UInt64SetPrototypeRootLayout.ReadRootRouterOffset(rootBytes);
            if (allocationDirectoryOffset <= 0 || rootRouterOffset <= 0)
                throw new InvalidDataException("The UInt64 ordered-set root does not reference initialized allocator and router offsets.");

            kernel.ConfigureFileExtentAllocator(allocationDirectoryOffset);
            UInt64SetPrototype set = new(
                kernel,
                fullPath,
                rootRouterOffset,
                UInt64SetPrototypeRootLayout.ReadDistinctCount(rootBytes),
                UInt64SetPrototypeRootLayout.ReadGeneration(rootBytes),
                expectedKind);
            set.ValidateRootRouter();
            return set;
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tests whether one UInt64 key is present through a direct router walk and one sparse-shelf binary search.<br/>
    /// The method allocates no result collection and holds a coherent read only for the bounded traversal.<br/>
    /// </summary>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns><see langword="true"/> when the key is present.<br/></returns>
    internal bool Contains(ulong key)
    {
        ThrowIfDisposed();
        using DataKernel.CoherentReadLease read = _kernel.EnterCoherentRead();
        byte[] page = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        try
        {
            long offset = _rootRouterOffset;
            for (int hop = 0; hop <= MaximumRouterLevels; hop++)
            {
                Span<byte> bytes = page.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(offset, bytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (magic == RouterLayout.Magic)
                {
                    RouterReader router = ValidateDirectRouter(bytes, offset);
                    long target = router.GetDirectTarget(GetKeyByte(key, router.KeyDepth));
                    if (target == 0)
                        return false;
                    offset = target;
                    continue;
                }

                if (magic != LeafMagic || !IsValidLeaf(bytes))
                    throw new InvalidDataException($"UInt64 set route target at offset {offset:N0} is not a valid router or sparse shelf.");

                return FindLeafKey(bytes, key) >= 0;
            }

            throw new InvalidDataException("The UInt64 set route exceeded the maximum eight-byte key depth.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }
    }

    /// <summary>
    /// Adds one UInt64 key when it is not already present.<br/>
    /// The returned boolean has ordinary set semantics: one caller wins insertion and later duplicate attempts return false without advancing count or generation.<br/>
    /// </summary>
    /// <param name="key">The key to add.<br/></param>
    /// <returns><see langword="true"/> when the key became newly present.<br/></returns>
    internal bool TryAdd(ulong key)
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            long routerOffset = _rootRouterOffset;
            for (int hop = 0; hop <= MaximumRouterLevels; hop++)
            {
                Span<byte> routerBytes = _mutationRouterPage;
                ReadMutationRouter(routerOffset, routerBytes);
                RouterReader router = ValidateDirectRouter(routerBytes, routerOffset);
                byte prefix = GetKeyByte(key, router.KeyDepth);
                long targetOffset = router.GetDirectTarget(prefix);
                if (targetOffset == 0)
                {
                    PublishNewLeaf(routerOffset, routerBytes, prefix, key);
                    return true;
                }

                Span<byte> targetBytes = _mutationTargetPage;
                _kernel.Read(targetOffset, targetBytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                if (magic == RouterLayout.Magic)
                {
                    routerOffset = targetOffset;
                    continue;
                }

                if (magic != LeafMagic || !IsValidLeaf(targetBytes))
                    throw new InvalidDataException($"UInt64 set route target at offset {targetOffset:N0} is not a valid router or sparse shelf.");

                int insertIndex = FindLeafKey(targetBytes, key);
                int itemCount = ReadLeafCount(targetBytes);
                if (insertIndex >= 0)
                    return false;

                if (itemCount < LeafCapacity)
                {
                    InsertAbsentLeaf(targetBytes, key);
                    PublishExistingLeafInsert(targetOffset, targetBytes);
                    return true;
                }

                PublishFullLeafInsert(
                    routerOffset,
                    routerBytes,
                    router.KeyDepth,
                    targetOffset,
                    targetBytes,
                    key);
                return true;
            }

            throw new InvalidDataException("The UInt64 set route exceeded the maximum eight-byte key depth.");
        }
    }

    /// <summary>
    /// Adds a caller-provided batch through one sorted union build and one authoritative-root publication.<br/>
    /// Immediate <see cref="TryAdd(ulong)"/> semantics remain unchanged; this explicit route deliberately trades one bounded pooled union buffer for coalesced routing, shelf construction, root accounting, and commit work.<br/>
    /// Duplicate values within the input or already present in the set are ignored according to ordinary presence-set semantics.<br/>
    /// </summary>
    /// <param name="keys">The input keys; caller order and repeated values are allowed.<br/></param>
    /// <returns>The number of keys that became newly present in the completed batch publication.<br/></returns>
    internal int AddMany(ReadOnlySpan<ulong> keys)
    {
        ThrowIfDisposed();
        if (IsRouted)
            throw new NotSupportedException("The first UInt64 routed-set slice exposes immediate end-fill mutations; a routed bulk-build contract has not been promoted.");
        if (keys.IsEmpty)
            return 0;

        lock (_mutationSync)
        {
            ulong capturedCount = Count;
            if (capturedCount > int.MaxValue)
                throw new InvalidOperationException("The current UInt64 set is too large for the first pooled sorted-union batch route.");

            int existingCount = checked((int)capturedCount);
            int combinedCount = checked(existingCount + keys.Length);
            ulong[] combinedArray = ArrayPool<ulong>.Shared.Rent(combinedCount);
            try
            {
                Span<ulong> combined = combinedArray.AsSpan(0, combinedCount);
                if (existingCount != 0)
                    _ = CopyTo(combined.Slice(0, existingCount));
                keys.CopyTo(combined.Slice(existingCount));
                combined.Sort();

                int distinctCount = CompactSortedDistinct(combined);
                int addedCount = checked(distinctCount - existingCount);
                if (addedCount == 0)
                    return 0;

                HashSet<long> retiredPages = CollectNonRootTopologyPages();
                retiredPages.Add(_rootRouterOffset);
                ulong nextGeneration = checked(Generation + 1);
                long replacementRootOffset;
                try
                {
                    replacementRootOffset = BuildSortedRouter(combined.Slice(0, distinctCount), keyDepth: 0);
                }
                catch
                {
                    _kernel.DiscardPending();
                    throw;
                }

                _kernel.EnterExclusiveStoragePublicationBlockingReads();
                try
                {
                    Span<byte> replacementRootBytes = stackalloc byte[sizeof(long)];
                    BinaryPrimitives.WriteInt64LittleEndian(replacementRootBytes, replacementRootOffset);
                    _kernel.StageWriteAt(UInt64SetPrototypeRootLayout.RootRouterOffsetOffset, replacementRootBytes);
                    StagePresenceRootMutation(checked((ulong)distinctCount), nextGeneration);
                    foreach (long retiredPageOffset in retiredPages)
                        _kernel.StageExtentRetirement(retiredPageOffset, UInt64SetPrototypeRootLayout.PageSize);
                    _kernel.Commit();

                    _rootRouterOffset = replacementRootOffset;
                    _kernel.Read(_rootRouterOffset, _rootRouterCache);
                    _rootRouterCacheValid = true;
                    Interlocked.Exchange(ref _count, distinctCount);
                    Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
                    return addedCount;
                }
                catch
                {
                    _kernel.DiscardPending();
                    throw;
                }
                finally
                {
                    _kernel.ExitExclusiveStoragePublication();
                }
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(combinedArray);
            }
        }
    }

    /// <summary>
    /// Removes one present UInt64 key.<br/>
    /// A shelf that becomes empty is disconnected through its parent routes and retired as one topology mutation; non-empty shelves close their packed gap in place.<br/>
    /// </summary>
    /// <param name="key">The key to remove.<br/></param>
    /// <returns><see langword="true"/> when a present key was removed.<br/></returns>
    internal bool Remove(ulong key)
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            long routerOffset = _rootRouterOffset;
            for (int hop = 0; hop <= MaximumRouterLevels; hop++)
            {
                Span<byte> routerBytes = _mutationRouterPage;
                ReadMutationRouter(routerOffset, routerBytes);
                RouterReader router = ValidateDirectRouter(routerBytes, routerOffset);
                long targetOffset = router.GetDirectTarget(GetKeyByte(key, router.KeyDepth));
                if (targetOffset == 0)
                    return false;

                Span<byte> targetBytes = _mutationTargetPage;
                _kernel.Read(targetOffset, targetBytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                if (magic == RouterLayout.Magic)
                {
                    routerOffset = targetOffset;
                    continue;
                }

                if (magic != LeafMagic || !IsValidLeaf(targetBytes))
                    throw new InvalidDataException($"UInt64 set route target at offset {targetOffset:N0} is not a valid router or sparse shelf.");

                int removeIndex = FindLeafKey(targetBytes, key);
                int itemCount = ReadLeafCount(targetBytes);
                if (removeIndex < 0)
                    return false;

                RemoveLeafAt(targetBytes, removeIndex);
                PublishRemove(routerOffset, routerBytes, targetOffset, targetBytes, itemCount == 1, removeIndex);
                return true;
            }

            throw new InvalidDataException("The UInt64 set route exceeded the maximum eight-byte key depth.");
        }
    }

    /// <summary>
    /// Copies every present key into a caller-owned span in exact natural or reverse order.<br/>
    /// Traversal retains at most eight router pages plus one target page and skips adjacent aliases without allocating a visited or deduplication set.<br/>
    /// </summary>
    /// <param name="destination">The caller-owned output span, which must fit the captured count.<br/></param>
    /// <param name="descending">Whether to emit keys in descending rather than ascending order.<br/></param>
    /// <returns>The number of keys copied.<br/></returns>
    internal int CopyTo(Span<ulong> destination, bool descending = false)
    {
        ThrowIfDisposed();
        if (IsRouted && descending)
            throw new NotSupportedException("A routed set exposes router-major physical traversal only; use LibraDexSortedSet<TKey> when reverse total order is required.");
        using DataKernel.CoherentReadLease read = _kernel.EnterCoherentRead();
        ulong capturedCount = Count;
        if (capturedCount > int.MaxValue || destination.Length < (int)capturedCount)
            throw new ArgumentException("The destination span is smaller than the UInt64 set's captured distinct-key count.", nameof(destination));

        byte[] pages = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize * TraversalPageCount);
        Span<long> offsets = stackalloc long[MaximumRouterLevels];
        Span<int> nextRoutes = stackalloc int[MaximumRouterLevels];
        Span<long> priorTargets = stackalloc long[MaximumRouterLevels];
        priorTargets.Fill(long.MinValue);
        try
        {
            int level = 0;
            offsets[0] = _rootRouterOffset;
            nextRoutes[0] = descending ? RouterLayout.MaxOneByteRouteCount - 1 : 0;
            ReadAndValidateTraversalRouter(pages, level, offsets[level]);
            int written = 0;

            while (level >= 0)
            {
                int routeIndex = nextRoutes[level];
                if ((!descending && routeIndex >= RouterLayout.MaxOneByteRouteCount) ||
                    (descending && routeIndex < 0))
                {
                    level--;
                    continue;
                }

                nextRoutes[level] = descending ? routeIndex - 1 : routeIndex + 1;
                ReadOnlySpan<byte> routerBytes = pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize);
                RouterReader router = new(routerBytes);
                long targetOffset = router.GetDirectTarget(checked((byte)routeIndex));
                if (targetOffset == 0 || targetOffset == priorTargets[level])
                    continue;
                priorTargets[level] = targetOffset;

                Span<byte> targetBytes = pages.AsSpan(MaximumRouterLevels * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(targetOffset, targetBytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                if (magic == RouterLayout.Magic)
                {
                    if (level + 1 >= MaximumRouterLevels)
                        throw new InvalidDataException("The UInt64 set traversal exceeded the maximum router depth.");

                    level++;
                    offsets[level] = targetOffset;
                    nextRoutes[level] = descending ? RouterLayout.MaxOneByteRouteCount - 1 : 0;
                    priorTargets[level] = long.MinValue;
                    targetBytes.CopyTo(pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize));
                    RouterReader child = new(pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize));
                    if (!child.IsValid || !child.HasDirectIndex)
                        throw new InvalidDataException($"UInt64 set router at offset {targetOffset:N0} is invalid or not direct.");
                    continue;
                }

                if (magic != LeafMagic || !IsValidLeaf(targetBytes))
                    throw new InvalidDataException($"UInt64 set traversal target at offset {targetOffset:N0} is invalid.");

                int itemCount = ReadLeafCount(targetBytes);
                if (descending)
                {
                    for (int i = itemCount - 1; i >= 0; i--)
                        destination[written++] = ReadLeafKey(targetBytes, i);
                }
                else
                {
                    for (int i = 0; i < itemCount; i++)
                        destination[written++] = ReadLeafKey(targetBytes, i);
                }
            }

            if ((ulong)written != capturedCount)
                throw new InvalidDataException($"UInt64 set traversal emitted {written:N0} keys but the root count is {capturedCount:N0}.");
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pages);
        }
    }

    /// <summary>
    /// Clears the complete set through one root-topology publication and retires every non-root page.<br/>
    /// This is the fast topology reset path; it never removes keys one at a time.<br/>
    /// </summary>
    internal void Clear()
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            if (Count == 0)
                return;

            HashSet<long> pages = CollectNonRootTopologyPages();
            byte[] rootRouter = new byte[RouterLayout.Size];
            new RouterWriter(rootRouter).InitializeRoot(allocationClassId: 0);
            ulong nextGeneration = checked(Generation + 1);

            _kernel.EnterExclusiveStoragePublicationBlockingReads();
            try
            {
                _kernel.StageWriteAt(_rootRouterOffset, rootRouter);
                foreach (long offset in pages)
                    _kernel.StageExtentRetirement(offset, UInt64SetPrototypeRootLayout.PageSize);
                StagePresenceRootMutation(0, nextGeneration);
                _kernel.Commit();
                rootRouter.CopyTo(_rootRouterCache, 0);
                _rootRouterCacheValid = true;
                Interlocked.Exchange(ref _count, 0);
                Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
            }
            catch
            {
                _kernel.DiscardPending();
                throw;
            }
            finally
            {
                _kernel.ExitExclusiveStoragePublication();
            }
        }
    }

    /// <summary>
    /// Releases the underlying memory arena or file handle.<br/>
    /// The prototype performs no implicit compaction and leaves a file-backed image reopenable at its current authoritative root.<br/>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _kernel.Dispose();
    }

    /// <summary>
    /// Initializes the root descriptor, allocator directory, and allocator-owned root router for either backing kind.<br/>
    /// The two-step publication configures file extent reuse before the first structural page reservation.<br/>
    /// </summary>
    /// <param name="kernel">The newly opened empty DataKernel.<br/></param>
    /// <param name="path">The canonical file path, or null for memory backing.<br/></param>
    /// <param name="kind">The sorted or routed presence-set format to initialize.<br/></param>
    /// <returns>The initialized prototype owner.<br/></returns>
    private static UInt64SetPrototype Initialize(DataKernel kernel, string? path, UInt64SetPrototypeKind kind)
    {
        if (kind is not UInt64SetPrototypeKind.Presence and not UInt64SetPrototypeKind.RoutedPresence)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A UInt64 presence set must use the sorted or routed presence format.");

        RawDataReservation root = kernel.Reserve(UInt64SetPrototypeRootLayout.Size);
        RawDataReservation allocationDirectory = kernel.Reserve(FileAllocationDirectoryLayout.Size);
        if (root.Extent.Offset != 0 || allocationDirectory.Extent.Offset != UInt64SetPrototypeRootLayout.Size)
            throw new InvalidDataException("The UInt64 set prototype requires root and allocator pages at deterministic initial offsets.");

        UInt64SetPrototypeRootLayout.Initialize(
            root.Span,
            kind,
            counterBits: 0,
            allocationDirectory.Extent.Offset,
            saturationCeiling: 0);
        FileAllocationDirectoryLayout.Initialize(allocationDirectory.Span);
        byte[] allocationDirectoryBytes = allocationDirectory.Span.ToArray();
        kernel.Commit();
        if (kernel.BackingKind == DataKernelBackingKind.File)
            kernel.ConfigureNewFileExtentAllocator(allocationDirectory.Extent.Offset, allocationDirectoryBytes);

        RawDataReservation rootRouter = kernel.Reserve(RouterLayout.Size);
        new RouterWriter(rootRouter.Span).InitializeRoot(allocationClassId: 0);
        Span<byte> rootRouterOffsetBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(rootRouterOffsetBytes, rootRouter.Extent.Offset);
        kernel.StageWriteAt(UInt64SetPrototypeRootLayout.RootRouterOffsetOffset, rootRouterOffsetBytes);
        kernel.Commit();

        return new UInt64SetPrototype(kernel, path, rootRouter.Extent.Offset, count: 0, generation: 0, kind);
    }

    /// <summary>
    /// Creates the direct DataKernel policy used by both prototype backings.<br/>
    /// Offset zero belongs to the set root, so no additional reserved prefix is configured.<br/>
    /// </summary>
    /// <returns>The backing-neutral DataKernel policy.<br/></returns>
    private static DataKernelOptions CreateDataKernelOptions() => new(
        AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
        ReservedPrefixBytes: 0,
        FlushToDiskOnCommit: false,
        MaxCommitGapCoalesceBytes: 512);

    /// <summary>
    /// Validates the reopened authoritative root router before the set becomes observable.<br/>
    /// </summary>
    private void ValidateRootRouter()
    {
        byte[] bytes = new byte[RouterLayout.Size];
        _kernel.Read(_rootRouterOffset, bytes);
        RouterReader router = ValidateDirectRouter(bytes, _rootRouterOffset);
        if (router.KeyDepth != 0)
            throw new InvalidDataException("The UInt64 set root router must use key depth zero.");
    }

    /// <summary>
    /// Publishes one new one-key shelf into a previously unset route.<br/>
    /// The shelf reservation is payload; the parent route and root count are publication state.<br/>
    /// </summary>
    /// <param name="routerOffset">The owning direct-router offset.<br/></param>
    /// <param name="routerBytes">The caller-owned complete router image.<br/></param>
    /// <param name="prefix">The unset direct route ordinal.<br/></param>
    /// <param name="key">The first key for the new shelf.<br/></param>
    private void PublishNewLeaf(long routerOffset, Span<byte> routerBytes, byte prefix, ulong key)
    {
        ulong nextCount = checked(Count + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation shelf = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            Span<ulong> keySpan = stackalloc ulong[1];
            keySpan[0] = key;
            InitializeLeaf(shelf.Span, keySpan);
            new RouterWriter(routerBytes).WriteRouteTarget(prefix, shelf.Extent.Offset);
            _kernel.StageWriteAt(routerOffset, routerBytes);
            StagePresenceRootMutation(nextCount, nextGeneration);
            _kernel.Commit();
            UpdateRootRouterCacheAfterPublication(routerOffset, routerBytes);
            Interlocked.Exchange(ref _count, unchecked((long)nextCount));
            Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
        }
        catch
        {
            _kernel.DiscardPending();
            throw;
        }
        finally
        {
            _kernel.ExitExclusiveStoragePublication();
        }
    }

    /// <summary>
    /// Publishes one already-prepared non-structural sparse-shelf insertion and its root cardinality change.<br/>
    /// </summary>
    /// <param name="shelfOffset">The existing shelf offset.<br/></param>
    /// <param name="shelfBytes">The complete post-insert shelf image.<br/></param>
    private void PublishExistingLeafInsert(long shelfOffset, ReadOnlySpan<byte> shelfBytes)
    {
        ulong nextCount = checked(Count + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            if (IsRouted)
            {
                int itemCount = UInt64RoutedSetSparseLayout.ReadItemCount(shelfBytes);
                int appendedIndex = itemCount - 1;
                int fingerprintOffset = UInt64RoutedSetSparseLayout.GetFingerprintStorageOffset(appendedIndex);
                _kernel.StageWriteAt(
                    shelfOffset + fingerprintOffset,
                    shelfBytes.Slice(fingerprintOffset, UInt64RoutedSetSparseLayout.FingerprintSize));
                _kernel.StageWriteAt(
                    shelfOffset + UInt64RoutedSetSparseLayout.KeysOffset + (appendedIndex * UInt64RoutedSetSparseLayout.KeySize),
                    shelfBytes.Slice(
                        UInt64RoutedSetSparseLayout.KeysOffset + (appendedIndex * UInt64RoutedSetSparseLayout.KeySize),
                        UInt64RoutedSetSparseLayout.KeySize));
                _kernel.StageWriteAt(
                    shelfOffset + UInt64RoutedSetSparseLayout.ItemCountOffset,
                    shelfBytes.Slice(UInt64RoutedSetSparseLayout.ItemCountOffset, sizeof(ushort)));
            }
            else
            {
                _kernel.StageWriteAt(shelfOffset, shelfBytes);
            }
            StagePresenceRootMutation(nextCount, nextGeneration);
            _kernel.Commit();
            Interlocked.Exchange(ref _count, unchecked((long)nextCount));
            Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
        }
        catch
        {
            _kernel.DiscardPending();
            throw;
        }
        finally
        {
            _kernel.ExitExclusiveStoragePublication();
        }
    }

    /// <summary>
    /// Publishes insertion into a full sparse shelf by either splitting at the parent byte or converting the shelf into deeper routers.<br/>
    /// The 509-key merged sequence is stack-bounded and no result-sized managed collection is created.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The direct router that selected the full shelf.<br/></param>
    /// <param name="parentRouterBytes">The caller-owned complete parent-router image.<br/></param>
    /// <param name="parentDepth">The key-byte depth selected by the parent router.<br/></param>
    /// <param name="shelfOffset">The full sparse-shelf offset.<br/></param>
    /// <param name="shelfBytes">The complete full shelf bytes.<br/></param>
    /// <param name="key">The absent incoming key.<br/></param>
    private void PublishFullLeafInsert(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        ushort parentDepth,
        long shelfOffset,
        ReadOnlySpan<byte> shelfBytes,
        ulong key)
    {
        Span<ulong> keys = stackalloc ulong[LeafCapacity + 1];
        int existingCount = ReadLeafCount(shelfBytes);
        for (int source = 0; source < existingCount; source++)
            keys[source] = ReadLeafKey(shelfBytes, source);
        keys[existingCount] = key;

        if (IsRouted)
        {
            keys.Sort();
        }
        else
        {
            int insertion = keys.Slice(0, existingCount).BinarySearch(key);
            if (insertion >= 0)
                throw new InvalidDataException("The UInt64 sorted-set full-shelf insertion received a key already present in the shelf.");
            insertion = ~insertion;
            keys.Slice(insertion, existingCount - insertion).CopyTo(keys.Slice(insertion + 1));
            keys[insertion] = key;
        }

        byte firstPrefix = GetKeyByte(keys[0], parentDepth);
        byte lastPrefix = GetKeyByte(keys[^1], parentDepth);
        if (firstPrefix != lastPrefix)
        {
            int splitIndex = FindByteBoundarySplit(keys, parentDepth);
            PublishLeafSplit(parentRouterOffset, parentRouterBytes, shelfOffset, keys, splitIndex, parentDepth);
            return;
        }

        PublishLeafTransform(parentRouterOffset, parentRouterBytes, shelfOffset, keys, parentDepth, firstPrefix);
    }

    /// <summary>
    /// Publishes two copy-on-write replacement shelves and redirects every adjacent parent alias at one byte boundary.<br/>
    /// The prior shelf becomes reusable only after the parent publication is durable and coherent readers are excluded.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The owning router offset.<br/></param>
    /// <param name="parentRouterBytes">The complete writable parent-router image.<br/></param>
    /// <param name="oldShelfOffset">The full shelf being replaced.<br/></param>
    /// <param name="keys">The complete sorted 509-key sequence.<br/></param>
    /// <param name="splitIndex">The first key ordinal assigned to the right shelf.<br/></param>
    /// <param name="parentDepth">The parent route key-byte depth.<br/></param>
    private void PublishLeafSplit(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long oldShelfOffset,
        ReadOnlySpan<ulong> keys,
        int splitIndex,
        ushort parentDepth)
    {
        byte rightPrefix = GetKeyByte(keys[splitIndex], parentDepth);
        ulong nextCount = checked(Count + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation left = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            RawDataReservation right = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            InitializeLeaf(left.Span, keys.Slice(0, splitIndex));
            InitializeLeaf(right.Span, keys.Slice(splitIndex));

            RouterReader parent = ValidateDirectRouter(parentRouterBytes, parentRouterOffset);
            RouterWriter writer = new(parentRouterBytes);
            bool foundOwner = false;
            for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
            {
                if (parent.GetDirectTarget(checked((byte)i)) != oldShelfOffset)
                    continue;

                writer.WriteRouteTarget(i, i < rightPrefix ? left.Extent.Offset : right.Extent.Offset);
                foundOwner = true;
            }

            if (!foundOwner)
                throw new InvalidDataException("The UInt64 set split could not find the parent routes owning the full shelf.");

            _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
            _kernel.StageExtentRetirement(oldShelfOffset, UInt64SetPrototypeRootLayout.PageSize);
            StagePresenceRootMutation(nextCount, nextGeneration);
            _kernel.Commit();
            UpdateRootRouterCacheAfterPublication(parentRouterOffset, parentRouterBytes);
            Interlocked.Exchange(ref _count, unchecked((long)nextCount));
            Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
        }
        catch
        {
            _kernel.DiscardPending();
            throw;
        }
        finally
        {
            _kernel.ExitExclusiveStoragePublication();
        }
    }

    /// <summary>
    /// Converts one full same-byte shelf into a same-extent router chain and two final sparse child shelves.<br/>
    /// Parent aliases outside the actual shared byte are cleared so no future key can skip a required routing depth.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The parent router selecting the full shelf.<br/></param>
    /// <param name="parentRouterBytes">The complete writable parent-router image.<br/></param>
    /// <param name="shelfOffset">The shelf offset reused by the first new router.<br/></param>
    /// <param name="keys">The complete sorted 509-key sequence.<br/></param>
    /// <param name="parentDepth">The key-byte depth selected by the parent router.<br/></param>
    /// <param name="sharedParentPrefix">The one parent-depth byte actually present in the full shelf.<br/></param>
    private void PublishLeafTransform(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long shelfOffset,
        ReadOnlySpan<ulong> keys,
        ushort parentDepth,
        byte sharedParentPrefix)
    {
        int firstChildDepth = parentDepth + 1;
        int splitDepth = firstChildDepth;
        while (splitDepth < sizeof(ulong) && GetKeyByte(keys[0], splitDepth) == GetKeyByte(keys[^1], splitDepth))
            splitDepth++;
        if (splitDepth >= sizeof(ulong))
            throw new InvalidDataException("A full UInt64 set shelf contains indistinguishable distinct keys.");

        int splitIndex = FindByteBoundarySplit(keys, splitDepth);
        byte rightPrefix = GetKeyByte(keys[splitIndex], splitDepth);
        ulong nextCount = checked(Count + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation left = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            RawDataReservation right = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            InitializeLeaf(left.Span, keys.Slice(0, splitIndex));
            InitializeLeaf(right.Span, keys.Slice(splitIndex));

            byte[] firstRouterBytes = ArrayPool<byte>.Shared.Rent(RouterLayout.Size);
            long[] targetArray = ArrayPool<long>.Shared.Rent(RouterLayout.MaxOneByteRouteCount);
            try
            {
                Span<long> targets = targetArray.AsSpan(0, RouterLayout.MaxOneByteRouteCount);
                for (int i = 0; i < targets.Length; i++)
                    targets[i] = i < rightPrefix ? left.Extent.Offset : right.Extent.Offset;

                long nextRouterOffset;
                if (splitDepth == firstChildDepth)
                {
                    Span<byte> firstRouter = firstRouterBytes.AsSpan(0, RouterLayout.Size);
                    new RouterWriter(firstRouter).InitializeExpandedOneByte(checked((ushort)splitDepth), allocationClassId: 0, targets);
                    nextRouterOffset = shelfOffset;
                }
                else
                {
                    RawDataReservation finalRouter = _kernel.Reserve(RouterLayout.Size);
                    new RouterWriter(finalRouter.Span).InitializeExpandedOneByte(checked((ushort)splitDepth), allocationClassId: 0, targets);
                    nextRouterOffset = finalRouter.Extent.Offset;
                }

                for (int depth = splitDepth - 1; depth >= firstChildDepth; depth--)
                {
                    targets.Clear();
                    targets[GetKeyByte(keys[0], depth)] = nextRouterOffset;
                    if (depth == firstChildDepth)
                    {
                        Span<byte> firstRouter = firstRouterBytes.AsSpan(0, RouterLayout.Size);
                        new RouterWriter(firstRouter).InitializeExpandedOneByte(checked((ushort)depth), allocationClassId: 0, targets);
                        nextRouterOffset = shelfOffset;
                    }
                    else
                    {
                        RawDataReservation intermediate = _kernel.Reserve(RouterLayout.Size);
                        new RouterWriter(intermediate.Span).InitializeExpandedOneByte(checked((ushort)depth), allocationClassId: 0, targets);
                        nextRouterOffset = intermediate.Extent.Offset;
                    }
                }

                RouterReader parent = ValidateDirectRouter(parentRouterBytes, parentRouterOffset);
                RouterWriter parentWriter = new(parentRouterBytes);
                bool retainedOwner = false;
                for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
                {
                    if (parent.GetDirectTarget(checked((byte)i)) != shelfOffset)
                        continue;

                    if (i == sharedParentPrefix)
                    {
                        retainedOwner = true;
                        continue;
                    }

                    parentWriter.WriteRouteTarget(i, 0);
                }

                if (!retainedOwner)
                    throw new InvalidDataException("The UInt64 set transform could not retain the parent's exact shared-byte owner.");

                _kernel.StageWriteAt(shelfOffset, firstRouterBytes.AsSpan(0, RouterLayout.Size));
                _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
                StagePresenceRootMutation(nextCount, nextGeneration);
                _kernel.Commit();
                UpdateRootRouterCacheAfterPublication(parentRouterOffset, parentRouterBytes);
                Interlocked.Exchange(ref _count, unchecked((long)nextCount));
                Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
            }
            finally
            {
                ArrayPool<long>.Shared.Return(targetArray);
                ArrayPool<byte>.Shared.Return(firstRouterBytes);
            }
        }
        catch
        {
            _kernel.DiscardPending();
            throw;
        }
        finally
        {
            _kernel.ExitExclusiveStoragePublication();
        }
    }

    /// <summary>
    /// Publishes one successful removal, optionally disconnecting and retiring an empty shelf.<br/>
    /// </summary>
    /// <param name="parentRouterOffset">The owning direct-router offset.<br/></param>
    /// <param name="parentRouterBytes">The complete writable parent-router image.<br/></param>
    /// <param name="shelfOffset">The mutated sparse-shelf offset.<br/></param>
    /// <param name="shelfBytes">The post-removal shelf image.<br/></param>
    /// <param name="becameEmpty">Whether the removed key was the shelf's final live key.<br/></param>
    /// <param name="removedIndex">The removed key's pre-mutation physical ordinal.<br/></param>
    private void PublishRemove(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long shelfOffset,
        ReadOnlySpan<byte> shelfBytes,
        bool becameEmpty,
        int removedIndex)
    {
        ulong nextCount = checked(Count - 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            if (becameEmpty)
            {
                RouterReader parent = ValidateDirectRouter(parentRouterBytes, parentRouterOffset);
                RouterWriter writer = new(parentRouterBytes);
                for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
                {
                    if (parent.GetDirectTarget(checked((byte)i)) == shelfOffset)
                        writer.WriteRouteTarget(i, 0);
                }

                _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
                _kernel.StageExtentRetirement(shelfOffset, UInt64SetPrototypeRootLayout.PageSize);
            }
            else
            {
                if (IsRouted)
                {
                    int itemCount = UInt64RoutedSetSparseLayout.ReadItemCount(shelfBytes);
                    int releasedIndex = itemCount;
                    if (removedIndex < itemCount)
                    {
                        int movedFingerprintOffset = UInt64RoutedSetSparseLayout.GetFingerprintStorageOffset(removedIndex);
                        _kernel.StageWriteAt(
                            shelfOffset + movedFingerprintOffset,
                            shelfBytes.Slice(movedFingerprintOffset, UInt64RoutedSetSparseLayout.FingerprintSize));
                        _kernel.StageWriteAt(
                            shelfOffset + UInt64RoutedSetSparseLayout.KeysOffset + (removedIndex * UInt64RoutedSetSparseLayout.KeySize),
                            shelfBytes.Slice(
                                UInt64RoutedSetSparseLayout.KeysOffset + (removedIndex * UInt64RoutedSetSparseLayout.KeySize),
                                UInt64RoutedSetSparseLayout.KeySize));
                    }
                    int releasedFingerprintOffset = UInt64RoutedSetSparseLayout.GetFingerprintStorageOffset(releasedIndex);
                    _kernel.StageWriteAt(
                        shelfOffset + releasedFingerprintOffset,
                        shelfBytes.Slice(releasedFingerprintOffset, UInt64RoutedSetSparseLayout.FingerprintSize));
                    _kernel.StageWriteAt(
                        shelfOffset + UInt64RoutedSetSparseLayout.KeysOffset + (releasedIndex * UInt64RoutedSetSparseLayout.KeySize),
                        shelfBytes.Slice(
                            UInt64RoutedSetSparseLayout.KeysOffset + (releasedIndex * UInt64RoutedSetSparseLayout.KeySize),
                            UInt64RoutedSetSparseLayout.KeySize));
                    _kernel.StageWriteAt(
                        shelfOffset + UInt64RoutedSetSparseLayout.ItemCountOffset,
                        shelfBytes.Slice(UInt64RoutedSetSparseLayout.ItemCountOffset, sizeof(ushort)));
                }
                else
                {
                    _kernel.StageWriteAt(shelfOffset, shelfBytes);
                }
            }

            StagePresenceRootMutation(nextCount, nextGeneration);
            _kernel.Commit();
            UpdateRootRouterCacheAfterPublication(parentRouterOffset, parentRouterBytes);
            Interlocked.Exchange(ref _count, unchecked((long)nextCount));
            Interlocked.Exchange(ref _generation, unchecked((long)nextGeneration));
        }
        catch
        {
            _kernel.DiscardPending();
            throw;
        }
        finally
        {
            _kernel.ExitExclusiveStoragePublication();
        }
    }

    /// <summary>
    /// Stages the compact mutable presence-root range for the current logical publication.<br/>
    /// </summary>
    /// <param name="count">The post-publication exact distinct-key count.<br/></param>
    /// <param name="generation">The post-publication logical generation.<br/></param>
    private void StagePresenceRootMutation(ulong count, ulong generation)
    {
        Span<byte> bytes = stackalloc byte[32];
        UInt64SetPrototypeRootLayout.WritePresenceMutation(bytes, count, generation);
        _kernel.StageWriteAt(UInt64SetPrototypeRootLayout.DistinctCountOffset, bytes);
    }

    /// <summary>
    /// Reads one mutation router while retaining the authoritative root-router image in owner-local memory.<br/>
    /// The cache is accessed only while the set mutation lock is held and is refreshed only after a successful publication, so failed mutations cannot leak speculative route bytes into later operations.<br/>
    /// </summary>
    /// <param name="routerOffset">The router offset selected by the current route walk.<br/></param>
    /// <param name="destination">The complete page-sized mutation buffer to fill.<br/></param>
    private void ReadMutationRouter(long routerOffset, Span<byte> destination)
    {
        if (routerOffset == _rootRouterOffset && _rootRouterCacheValid)
        {
            _rootRouterCache.AsSpan().CopyTo(destination);
            return;
        }

        _kernel.Read(routerOffset, destination);
        if (routerOffset == _rootRouterOffset)
        {
            destination.CopyTo(_rootRouterCache);
            _rootRouterCacheValid = true;
        }
    }

    /// <summary>
    /// Refreshes the owner-local root-router cache after the matching router bytes have been committed successfully.<br/>
    /// Non-root publications are ignored because caching every routed page would trade the set's low retained-memory advantage for uncertain random-write speed.<br/>
    /// </summary>
    /// <param name="routerOffset">The router offset included in the completed publication.<br/></param>
    /// <param name="publishedBytes">The complete committed router image.<br/></param>
    private void UpdateRootRouterCacheAfterPublication(long routerOffset, ReadOnlySpan<byte> publishedBytes)
    {
        if (routerOffset != _rootRouterOffset)
            return;

        publishedBytes.CopyTo(_rootRouterCache);
        _rootRouterCacheValid = true;
    }

    /// <summary>
    /// Compacts one sorted UInt64 span in place so every retained prefix position contains exactly one distinct key.<br/>
    /// The method allocates no side structure and therefore keeps duplicate-heavy batch preparation proportional to the caller-provided pooled union buffer.<br/>
    /// </summary>
    /// <param name="sortedKeys">The non-empty sorted span to compact.<br/></param>
    /// <returns>The number of distinct keys retained at the beginning of <paramref name="sortedKeys"/>.<br/></returns>
    private static int CompactSortedDistinct(Span<ulong> sortedKeys)
    {
        if (sortedKeys.IsEmpty)
            return 0;

        int writeIndex = 1;
        ulong prior = sortedKeys[0];
        for (int readIndex = 1; readIndex < sortedKeys.Length; readIndex++)
        {
            ulong candidate = sortedKeys[readIndex];
            if (candidate == prior)
                continue;

            sortedKeys[writeIndex++] = candidate;
            prior = candidate;
        }

        return writeIndex;
    }

    /// <summary>
    /// Builds one direct-router subtree from a strictly ascending UInt64 key span without publishing it into the set root.<br/>
    /// Consecutive byte buckets share a packed sparse shelf until its fixed capacity would be exceeded; an oversized single bucket recursively consumes the next key byte.<br/>
    /// The result preserves direct routing and adjacent aliases while avoiding the one-shelf-per-prefix expansion that would waste hundreds of megabytes on sparse full-domain keys.<br/>
    /// </summary>
    /// <param name="sortedDistinctKeys">The non-empty strictly ascending keys owned by this router.<br/></param>
    /// <param name="keyDepth">The big-endian UInt64 byte selected by this router.<br/></param>
    /// <returns>The reserved offset of the fully initialized router page.<br/></returns>
    private long BuildSortedRouter(ReadOnlySpan<ulong> sortedDistinctKeys, int keyDepth)
    {
        if (sortedDistinctKeys.IsEmpty)
            throw new ArgumentException("A sorted UInt64 set router cannot be built from an empty key span.", nameof(sortedDistinctKeys));
        if ((uint)keyDepth >= sizeof(ulong))
            throw new InvalidDataException("A sorted UInt64 set build exceeded the eight-byte key depth.");

        long[] targetArray = ArrayPool<long>.Shared.Rent(RouterLayout.MaxOneByteRouteCount);
        Span<long> targets = targetArray.AsSpan(0, RouterLayout.MaxOneByteRouteCount);
        targets.Clear();
        try
        {
            int index = 0;
            while (index < sortedDistinctKeys.Length)
            {
                byte firstPrefix = GetKeyByte(sortedDistinctKeys[index], keyDepth);
                int firstBucketEnd = FindPrefixBucketEnd(sortedDistinctKeys, index, keyDepth, firstPrefix);
                int firstBucketCount = firstBucketEnd - index;
                if (firstBucketCount > UInt64SetPrototypeSparseLayout.Capacity)
                {
                    if (keyDepth + 1 >= sizeof(ulong))
                        throw new InvalidDataException("A terminal UInt64 byte bucket exceeds sparse-shelf capacity despite containing distinct full-width keys.");

                    targets[firstPrefix] = BuildSortedRouter(sortedDistinctKeys.Slice(index, firstBucketCount), keyDepth + 1);
                    index = firstBucketEnd;
                    continue;
                }

                int leafStart = index;
                int leafEnd = firstBucketEnd;
                byte lastPrefix = firstPrefix;
                while (leafEnd < sortedDistinctKeys.Length)
                {
                    byte nextPrefix = GetKeyByte(sortedDistinctKeys[leafEnd], keyDepth);
                    int nextBucketEnd = FindPrefixBucketEnd(sortedDistinctKeys, leafEnd, keyDepth, nextPrefix);
                    int nextBucketCount = nextBucketEnd - leafEnd;
                    if (nextBucketCount > UInt64SetPrototypeSparseLayout.Capacity ||
                        nextBucketEnd - leafStart > UInt64SetPrototypeSparseLayout.Capacity)
                    {
                        break;
                    }

                    leafEnd = nextBucketEnd;
                    lastPrefix = nextPrefix;
                }

                RawDataReservation leaf = _kernel.Reserve(UInt64SetPrototypeSparseLayout.Size);
                UInt64SetPrototypeSparseLayout.Initialize(leaf.Span, sortedDistinctKeys.Slice(leafStart, leafEnd - leafStart));
                for (int route = firstPrefix; route <= lastPrefix; route++)
                    targets[route] = leaf.Extent.Offset;
                index = leafEnd;
            }

            RawDataReservation router = _kernel.Reserve(RouterLayout.Size);
            new RouterWriter(router.Span).InitializeExpandedOneByte(checked((ushort)keyDepth), allocationClassId: 0, targetArray.AsSpan(0, RouterLayout.MaxOneByteRouteCount));
            return router.Extent.Offset;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(targetArray, clearArray: true);
        }
    }

    /// <summary>
    /// Finds the exclusive end of one equal-prefix-byte bucket in an ascending UInt64 sequence.<br/>
    /// The linear advance is aggregate O(n) across one router build because each key participates in exactly one bucket-boundary walk at that depth.<br/>
    /// </summary>
    /// <param name="keys">The sorted keys owned by the current router.<br/></param>
    /// <param name="startIndex">The first key in the bucket.<br/></param>
    /// <param name="keyDepth">The big-endian UInt64 byte selected by the router.<br/></param>
    /// <param name="prefix">The expected byte shared by the bucket.<br/></param>
    /// <returns>The exclusive bucket end.<br/></returns>
    private static int FindPrefixBucketEnd(ReadOnlySpan<ulong> keys, int startIndex, int keyDepth, byte prefix)
    {
        int end = startIndex + 1;
        while (end < keys.Length && GetKeyByte(keys[end], keyDepth) == prefix)
            end++;
        return end;
    }

    /// <summary>
    /// Finds a valid key-byte transition nearest the sequence median.<br/>
    /// The returned ordinal is always nonzero, below sequence length, and leaves no side larger than sparse-shelf capacity.<br/>
    /// </summary>
    /// <param name="keys">The sorted full-shelf-plus-incoming sequence.<br/></param>
    /// <param name="keyDepth">The big-endian key byte that must distinguish the replacement shelves.<br/></param>
    /// <returns>The first ordinal assigned to the right shelf.<br/></returns>
    private int FindByteBoundarySplit(ReadOnlySpan<ulong> keys, int keyDepth)
    {
        int middle = keys.Length / 2;
        int selected = -1;
        int selectedDistance = int.MaxValue;
        for (int i = 1; i < keys.Length; i++)
        {
            if (GetKeyByte(keys[i - 1], keyDepth) == GetKeyByte(keys[i], keyDepth))
                continue;

            int distance = Math.Abs(i - middle);
            if (distance < selectedDistance)
            {
                selected = i;
                selectedDistance = distance;
            }
        }

        if (selected <= 0 || selected >= keys.Length ||
            selected > LeafCapacity ||
            keys.Length - selected > LeafCapacity)
        {
            throw new InvalidDataException("The UInt64 set could not find a capacity-safe byte-boundary shelf split.");
        }

        return selected;
    }

    /// <summary>
    /// Extracts one big-endian route byte from a UInt64 key without allocating an encoded key buffer.<br/>
    /// </summary>
    /// <param name="key">The UInt64 key.<br/></param>
    /// <param name="depth">The zero-based big-endian byte depth from zero through seven.<br/></param>
    /// <returns>The selected route byte.<br/></returns>
    private static byte GetKeyByte(ulong key, int depth)
    {
        if ((uint)depth >= sizeof(ulong))
            throw new ArgumentOutOfRangeException(nameof(depth));
        return checked((byte)((key >> ((sizeof(ulong) - depth - 1) * 8)) & byte.MaxValue));
    }

    /// <summary>
    /// Gets whether this owner uses the route-ordered, physically end-filled leaf policy.<br/>
    /// The persisted root kind fixes this decision for the owner's complete lifetime and reopen contract.<br/>
    /// </summary>
    private bool IsRouted => _kind == UInt64SetPrototypeKind.RoutedPresence;

    /// <summary>
    /// Gets the persisted leaf magic selected by this set owner's immutable root kind.<br/>
    /// </summary>
    private uint LeafMagic => IsRouted ? UInt64RoutedSetSparseLayout.Magic : UInt64SetPrototypeSparseLayout.Magic;

    /// <summary>
    /// Gets the fixed live-key capacity of this owner's selected leaf format.<br/>
    /// Routed leaves gain two entries by omitting sorted minimum and maximum fields that they cannot use safely.<br/>
    /// </summary>
    private int LeafCapacity => IsRouted ? UInt64RoutedSetSparseLayout.Capacity : UInt64SetPrototypeSparseLayout.Capacity;

    /// <summary>
    /// Validates one candidate leaf against the exact persisted format selected by this set owner.<br/>
    /// </summary>
    /// <param name="source">The complete candidate leaf page.<br/></param>
    /// <returns><see langword="true"/> when the selected leaf header is valid.<br/></returns>
    private bool IsValidLeaf(ReadOnlySpan<byte> source) =>
        IsRouted ? UInt64RoutedSetSparseLayout.IsValid(source) : UInt64SetPrototypeSparseLayout.IsValid(source);

    /// <summary>
    /// Reads the live key count from this owner's selected leaf format.<br/>
    /// </summary>
    /// <param name="source">The validated leaf bytes.<br/></param>
    /// <returns>The live physical key count.<br/></returns>
    private int ReadLeafCount(ReadOnlySpan<byte> source) =>
        IsRouted ? UInt64RoutedSetSparseLayout.ReadItemCount(source) : UInt64SetPrototypeSparseLayout.ReadItemCount(source);

    /// <summary>
    /// Reads one key from the selected leaf's physical ordinal.<br/>
    /// Sorted leaves decode big-endian order bytes; routed leaves decode little-endian equality payload bytes.<br/>
    /// </summary>
    /// <param name="source">The validated leaf bytes.<br/></param>
    /// <param name="index">The zero-based live physical ordinal.<br/></param>
    /// <returns>The decoded UInt64 key.<br/></returns>
    private ulong ReadLeafKey(ReadOnlySpan<byte> source, int index) =>
        IsRouted ? UInt64RoutedSetSparseLayout.ReadKeyAt(source, index) : UInt64SetPrototypeSparseLayout.ReadKeyAt(source, index);

    /// <summary>
    /// Finds one exact key using the lookup discipline selected by the persisted set kind.<br/>
    /// Sorted leaves use lower-bound binary search; routed leaves use vectorized equality scanning over one bounded 4 KiB page.<br/>
    /// </summary>
    /// <param name="source">The validated leaf bytes.<br/></param>
    /// <param name="key">The exact key to locate.<br/></param>
    /// <returns>The live physical ordinal, or minus one when absent.<br/></returns>
    private int FindLeafKey(ReadOnlySpan<byte> source, ulong key)
    {
        if (IsRouted)
            return UInt64RoutedSetSparseLayout.IndexOf(source, key);

        int index = UInt64SetPrototypeSparseLayout.LowerBound(source, key);
        return index < UInt64SetPrototypeSparseLayout.ReadItemCount(source) &&
               UInt64SetPrototypeSparseLayout.ReadKeyAt(source, index) == key
            ? index
            : -1;
    }

    /// <summary>
    /// Inserts one key that the caller has already proven absent into the selected non-full leaf format.<br/>
    /// Routed leaves append; sorted leaves recompute lower bound and preserve exact physical ordering.<br/>
    /// </summary>
    /// <param name="target">The complete writable leaf image.<br/></param>
    /// <param name="key">The absent key to insert.<br/></param>
    private void InsertAbsentLeaf(Span<byte> target, ulong key)
    {
        if (IsRouted)
        {
            UInt64RoutedSetSparseLayout.AppendAbsent(target, key);
            return;
        }

        int insertIndex = UInt64SetPrototypeSparseLayout.LowerBound(target, key);
        UInt64SetPrototypeSparseLayout.InsertAbsent(target, insertIndex, key);
    }

    /// <summary>
    /// Removes one known live physical key ordinal using the selected leaf-local reuse policy.<br/>
    /// Routed leaves move only the final key; sorted leaves close the ordered packed suffix.<br/>
    /// </summary>
    /// <param name="target">The complete writable leaf image.<br/></param>
    /// <param name="removeIndex">The zero-based live physical ordinal to remove.<br/></param>
    private void RemoveLeafAt(Span<byte> target, int removeIndex)
    {
        if (IsRouted)
            UInt64RoutedSetSparseLayout.RemoveAt(target, removeIndex);
        else
            UInt64SetPrototypeSparseLayout.RemoveAt(target, removeIndex);
    }

    /// <summary>
    /// Initializes one reserved page in this owner's selected leaf format.<br/>
    /// The caller supplies keys belonging to one canonical router region; sorted callers additionally supply strict ascending order.<br/>
    /// </summary>
    /// <param name="target">The complete reserved page.<br/></param>
    /// <param name="keys">The distinct keys owned by the new leaf.<br/></param>
    private void InitializeLeaf(Span<byte> target, ReadOnlySpan<ulong> keys)
    {
        if (IsRouted)
            UInt64RoutedSetSparseLayout.Initialize(target, keys);
        else
            UInt64SetPrototypeSparseLayout.Initialize(target, keys);
    }

    /// <summary>
    /// Produces a stable diagnostic name for one supported presence-set root kind.<br/>
    /// </summary>
    /// <param name="kind">The persisted presence-set kind.<br/></param>
    /// <returns>The user-facing physical policy name.<br/></returns>
    private static string GetKindDisplayName(UInt64SetPrototypeKind kind) => kind switch
    {
        UInt64SetPrototypeKind.Presence => "sorted",
        UInt64SetPrototypeKind.RoutedPresence => "routed",
        _ => kind.ToString()
    };

    /// <summary>
    /// Validates one direct router page and returns a zero-allocation reader overlay.<br/>
    /// </summary>
    /// <param name="bytes">The complete router page bytes.<br/></param>
    /// <param name="offset">The backing offset used in corruption diagnostics.<br/></param>
    /// <returns>A validated direct-router reader.<br/></returns>
    private static RouterReader ValidateDirectRouter(ReadOnlySpan<byte> bytes, long offset)
    {
        RouterReader router = new(bytes);
        if (!router.IsValid || !router.HasDirectIndex || router.PrefixByteCount != 1 || router.KeyDepth >= sizeof(ulong))
            throw new InvalidDataException($"UInt64 set router at offset {offset:N0} is invalid or incompatible with direct one-byte routing.");
        return router;
    }

    /// <summary>
    /// Reads and validates one traversal router into its level-owned pooled page slice.<br/>
    /// </summary>
    /// <param name="pages">The pooled contiguous traversal page array.<br/></param>
    /// <param name="level">The zero-based router stack level.<br/></param>
    /// <param name="offset">The router backing offset.<br/></param>
    private void ReadAndValidateTraversalRouter(byte[] pages, int level, long offset)
    {
        Span<byte> bytes = pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize);
        _kernel.Read(offset, bytes);
        _ = ValidateDirectRouter(bytes, offset);
    }

    /// <summary>
    /// Captures the reachable router/leaf shape together with the memory arena's exact page accounting.<br/>
    /// This diagnostic performs a coherent maintenance traversal after benchmark timing has stopped; it does not participate in ordinary set lookup or mutation.<br/>
    /// Adjacent aliases are collapsed at each direct router, while a defensive visited set proves that every non-root topology page has exactly one logical owner.<br/>
    /// </summary>
    /// <returns>The current topology cardinality, occupancy, and memory-arena accounting.<br/></returns>
    internal UInt64SetTopologyStorageSnapshot GetTopologyStorageSnapshot()
    {
        ThrowIfDisposed();
        using DataKernel.CoherentReadLease read = _kernel.EnterCoherentRead();
        HashSet<long> pages = [];
        Stack<long> routers = new();
        routers.Push(_rootRouterOffset);
        byte[] routerPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        byte[] targetPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        int routerPageCount = 0;
        int leafPageCount = 0;
        ulong leafItemCount = 0;
        try
        {
            while (routers.Count != 0)
            {
                long routerOffset = routers.Pop();
                routerPageCount++;
                Span<byte> routerBytes = routerPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(routerOffset, routerBytes);
                RouterReader router = ValidateDirectRouter(routerBytes, routerOffset);
                long priorTarget = long.MinValue;
                for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
                {
                    long target = router.GetDirectTarget(checked((byte)i));
                    if (target == 0 || target == priorTarget)
                        continue;
                    priorTarget = target;
                    if (!pages.Add(target))
                        throw new InvalidDataException($"UInt64 set topology target {target:N0} has more than one logical owner.");

                    Span<byte> targetBytes = targetPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                    _kernel.Read(target, targetBytes);
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                    if (magic == RouterLayout.Magic)
                    {
                        routers.Push(target);
                    }
                    else if (magic == LeafMagic && IsValidLeaf(targetBytes))
                    {
                        leafPageCount++;
                        leafItemCount = checked(leafItemCount + (uint)ReadLeafCount(targetBytes));
                    }
                    else
                    {
                        throw new InvalidDataException($"UInt64 set topology diagnostic found an unknown page format at offset {target:N0}.");
                    }
                }
            }

            if (leafItemCount != Count)
                throw new InvalidDataException($"UInt64 set topology diagnostic counted {leafItemCount:N0} leaf items but the root records {Count:N0}.");

            DataKernelMemoryDiagnostics memory = _kernel.GetMemoryDiagnostics();
            return new UInt64SetTopologyStorageSnapshot(
                RouterPageCount: routerPageCount,
                LeafPageCount: leafPageCount,
                LeafItemCount: leafItemCount,
                LeafCapacity: LeafCapacity,
                MemoryPageCount: memory.PageCount,
                MemoryArenaBytes: memory.ArenaBytes,
                MemoryLiveRangeCount: memory.LiveRangeCount,
                MemoryLiveBytes: memory.LiveBytes,
                MemoryFreeExtentCount: memory.FreeExtentCount,
                MemoryFreeBytes: memory.FreeBytes,
                MemoryEndOffset: memory.EndOffset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(targetPage);
            ArrayPool<byte>.Shared.Return(routerPage);
        }
    }

    /// <summary>
    /// Collects each reachable non-root topology page once for the clear fast path.<br/>
    /// This maintenance-only traversal may use a managed set defensively; ordinary ordered reads rely solely on adjacent-alias invariants.<br/>
    /// </summary>
    /// <returns>The unique non-root page offsets to retire after empty-root publication.<br/></returns>
    private HashSet<long> CollectNonRootTopologyPages()
    {
        HashSet<long> pages = [];
        Stack<long> routers = new();
        routers.Push(_rootRouterOffset);
        byte[] page = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        try
        {
            while (routers.Count != 0)
            {
                long routerOffset = routers.Pop();
                Span<byte> bytes = page.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(routerOffset, bytes);
                RouterReader router = ValidateDirectRouter(bytes, routerOffset);
                long priorTarget = long.MinValue;
                for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
                {
                    long target = router.GetDirectTarget(checked((byte)i));
                    if (target == 0 || target == priorTarget)
                        continue;
                    priorTarget = target;
                    if (!pages.Add(target))
                        throw new InvalidDataException($"UInt64 set topology target {target:N0} has more than one logical owner.");

                    _kernel.Read(target, magicBytes);
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
                    if (magic == RouterLayout.Magic)
                        routers.Push(target);
                    else if (magic != LeafMagic)
                        throw new InvalidDataException($"UInt64 set clear found an unknown page format at offset {target:N0}.");
                }
            }

            return pages;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }
    }

    /// <summary>
    /// Throws when an operation is attempted after the prototype releases its backing owner.<br/>
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// Describes the exact reachable topology and volatile-arena shape of one UInt64 set at a diagnostic instant.<br/>
/// The snapshot is internal test evidence rather than a public collection contract; file-backed sets report zero for memory-arena fields.<br/>
/// </summary>
/// <param name="RouterPageCount">The number of reachable direct-router pages, including the root router.<br/></param>
/// <param name="LeafPageCount">The number of reachable set leaf pages.<br/></param>
/// <param name="LeafItemCount">The total live keys observed across reachable leaves.<br/></param>
/// <param name="LeafCapacity">The fixed key capacity of the selected leaf format.<br/></param>
/// <param name="MemoryPageCount">The number of managed arena pages currently allocated.<br/></param>
/// <param name="MemoryArenaBytes">The total bytes held by managed arena pages.<br/></param>
/// <param name="MemoryLiveRangeCount">The number of committed live ranges tracked by the arena.<br/></param>
/// <param name="MemoryLiveBytes">The total committed live bytes tracked by the arena.<br/></param>
/// <param name="MemoryFreeExtentCount">The number of reusable released extents tracked by the arena.<br/></param>
/// <param name="MemoryFreeBytes">The total reusable released bytes tracked by the arena.<br/></param>
/// <param name="MemoryEndOffset">The highest arena end offset reached by append allocation.<br/></param>
internal readonly record struct UInt64SetTopologyStorageSnapshot(
    int RouterPageCount,
    int LeafPageCount,
    ulong LeafItemCount,
    int LeafCapacity,
    int MemoryPageCount,
    long MemoryArenaBytes,
    int MemoryLiveRangeCount,
    long MemoryLiveBytes,
    int MemoryFreeExtentCount,
    long MemoryFreeBytes,
    long MemoryEndOffset);
