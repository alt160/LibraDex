using System.Buffers;
using System.Buffers.Binary;
using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

/// <summary>
/// Owns the UInt64 exact-or-saturating counted-set topology shared by the public generic handle.<br/>
/// The implementation reuses LibraDex direct routers, coherent publication, and extent allocation while storing only ordered keys plus the selected packed counters.<br/>
/// </summary>
internal sealed class UInt64CountedSetPrototype : IDisposable
{
    private const int MaximumRouterLevels = sizeof(ulong);
    private const int TraversalPageCount = MaximumRouterLevels + 1;
    private readonly DataKernel _kernel;
    private readonly object _mutationSync = new();
    private readonly string? _path;
    private readonly UInt64SetPrototypeKind _kind;
    private readonly byte _counterBits;
    private readonly ulong _saturationCeiling;
    private long _distinctCount;
    private long _occurrenceCount;
    private long _generation;
    private long _rootRouterOffset;
    private bool _disposed;

    private UInt64CountedSetPrototype(
        DataKernel kernel,
        string? path,
        UInt64SetPrototypeKind kind,
        byte counterBits,
        ulong saturationCeiling,
        long rootRouterOffset,
        ulong distinctCount,
        ulong occurrenceCount,
        ulong generation)
    {
        _kernel = kernel;
        _path = path;
        _kind = kind;
        _counterBits = counterBits;
        _saturationCeiling = saturationCeiling;
        _rootRouterOffset = rootRouterOffset;
        _distinctCount = unchecked((long)distinctCount);
        _occurrenceCount = unchecked((long)occurrenceCount);
        _generation = unchecked((long)generation);
    }

    /// <summary>
    /// Gets the exact number of distinct keys with a positive retained counter.<br/>
    /// </summary>
    internal ulong DistinctCount => unchecked((ulong)Interlocked.Read(ref _distinctCount));

    /// <summary>
    /// Gets the exact sum of stored counters.<br/>
    /// Exact mode represents all accepted occurrences; saturating mode represents the sum after per-key caps.<br/>
    /// </summary>
    internal ulong RetainedOccurrenceCount => unchecked((ulong)Interlocked.Read(ref _occurrenceCount));

    /// <summary>
    /// Gets the successful logical mutation generation.<br/>
    /// Inputs received after a key has reached its saturation ceiling are no-ops and do not advance this value.<br/>
    /// </summary>
    internal ulong Generation => unchecked((ulong)Interlocked.Read(ref _generation));

    /// <summary>
    /// Gets whether the stored counters stop at a caller-selected ceiling.<br/>
    /// </summary>
    internal bool IsSaturating => _kind == UInt64SetPrototypeKind.Saturating;

    /// <summary>
    /// Gets the saturation ceiling, or <see cref="ulong.MaxValue"/> for exact mode.<br/>
    /// </summary>
    internal ulong MaximumStoredCount => IsSaturating ? _saturationCeiling : ulong.MaxValue;

    /// <summary>
    /// Gets the selected physical counter width.<br/>
    /// </summary>
    internal byte CounterBits => _counterBits;

    /// <summary>
    /// Gets the backing kind for public diagnostics and harness validation.<br/>
    /// </summary>
    internal DataKernelBackingKind BackingKind => _kernel.BackingKind;

    /// <summary>
    /// Gets the current durable file length, or zero for memory backing.<br/>
    /// </summary>
    internal long FileLength => _path is null ? 0 : new FileInfo(_path).Length;

    /// <summary>
    /// Creates an empty memory-backed exact or saturating UInt64 counted set.<br/>
    /// </summary>
    /// <param name="saturationCeiling">Zero selects exact mode; a value of two or greater selects saturating mode.<br/></param>
    /// <returns>The initialized memory-backed counted set.<br/></returns>
    internal static UInt64CountedSetPrototype CreateMemory(ulong saturationCeiling)
    {
        ResolvePolicy(saturationCeiling, out UInt64SetPrototypeKind kind, out byte counterBits);
        DataKernel kernel = DataKernel.OpenMemory(CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            return Initialize(kernel, path: null, kind, counterBits, saturationCeiling);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an empty file-backed exact or saturating UInt64 counted set.<br/>
    /// </summary>
    /// <param name="path">The new durable set path.<br/></param>
    /// <param name="saturationCeiling">Zero selects exact mode; a value of two or greater selects saturating mode.<br/></param>
    /// <returns>The initialized file-backed counted set.<br/></returns>
    internal static UInt64CountedSetPrototype Create(string path, ulong saturationCeiling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ResolvePolicy(saturationCeiling, out UInt64SetPrototypeKind kind, out byte counterBits);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        DataKernel kernel = DataKernel.Open(fullPath, FileMode.CreateNew, CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            return Initialize(kernel, fullPath, kind, counterBits, saturationCeiling);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing file-backed UInt64 counted set and restores its persisted counter policy.<br/>
    /// </summary>
    /// <param name="path">The existing durable set path.<br/></param>
    /// <returns>The reopened counted set.<br/></returns>
    internal static UInt64CountedSetPrototype Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        DataKernel kernel = DataKernel.Open(fullPath, FileMode.Open, CreateDataKernelOptions(), DataKernelTelemetryOptions.EnabledOptions);
        try
        {
            byte[] root = new byte[UInt64SetPrototypeRootLayout.Size];
            kernel.Read(0, root);
            if (!UInt64SetPrototypeRootLayout.IsValid(root))
                throw new InvalidDataException("The file is not a valid UInt64 counted-set image.");

            UInt64SetPrototypeKind kind = UInt64SetPrototypeRootLayout.ReadKind(root);
            byte counterBits = UInt64SetPrototypeRootLayout.ReadCounterBits(root);
            ulong saturationCeiling = UInt64SetPrototypeRootLayout.ReadSaturationCeiling(root);
            ValidatePolicy(kind, counterBits, saturationCeiling);
            long allocationDirectoryOffset = UInt64SetPrototypeRootLayout.ReadAllocationDirectoryOffset(root);
            long rootRouterOffset = UInt64SetPrototypeRootLayout.ReadRootRouterOffset(root);
            if (allocationDirectoryOffset <= 0 || rootRouterOffset <= 0)
                throw new InvalidDataException("The UInt64 counted-set root does not reference initialized allocator and router offsets.");

            kernel.ConfigureFileExtentAllocator(allocationDirectoryOffset);
            UInt64CountedSetPrototype set = new(
                kernel,
                fullPath,
                kind,
                counterBits,
                saturationCeiling,
                rootRouterOffset,
                UInt64SetPrototypeRootLayout.ReadDistinctCount(root),
                UInt64SetPrototypeRootLayout.ReadOccurrenceCount(root),
                UInt64SetPrototypeRootLayout.ReadGeneration(root));
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
    /// Reads the stored exact or capped count for one key.<br/>
    /// </summary>
    /// <param name="key">The key to locate.<br/></param>
    /// <returns>The positive stored count, or zero when absent.<br/></returns>
    internal ulong GetCount(ulong key)
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
                    offset = router.GetDirectTarget(GetKeyByte(key, router.KeyDepth));
                    if (offset == 0)
                        return 0;
                    continue;
                }

                ValidateLeaf(bytes, offset);
                int item = UInt64CountedSetSparseLayout.LowerBound(bytes, key);
                return item < UInt64CountedSetSparseLayout.ReadItemCount(bytes) &&
                       UInt64CountedSetSparseLayout.ReadKeyAt(bytes, item) == key
                    ? UInt64CountedSetSparseLayout.ReadCountAt(bytes, item)
                    : 0;
            }

            throw new InvalidDataException("The UInt64 counted-set route exceeded the maximum key depth.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
        }
    }

    /// <summary>
    /// Adds one occurrence and returns the new stored exact or capped count.<br/>
    /// Saturating inputs received after the ceiling is reached are no-ops; exact mode throws rather than wrapping UInt64 overflow.<br/>
    /// </summary>
    /// <param name="key">The key whose occurrence count should increase.<br/></param>
    /// <returns>The post-operation stored count.<br/></returns>
    internal ulong AddOccurrence(ulong key)
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            byte[] routerPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
            byte[] targetPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
            try
            {
                long routerOffset = _rootRouterOffset;
                for (int hop = 0; hop <= MaximumRouterLevels; hop++)
                {
                    Span<byte> routerBytes = routerPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                    _kernel.Read(routerOffset, routerBytes);
                    RouterReader router = ValidateDirectRouter(routerBytes, routerOffset);
                    byte prefix = GetKeyByte(key, router.KeyDepth);
                    long targetOffset = router.GetDirectTarget(prefix);
                    if (targetOffset == 0)
                    {
                        PublishNewLeaf(routerOffset, routerBytes, prefix, key);
                        return 1;
                    }

                    Span<byte> targetBytes = targetPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                    _kernel.Read(targetOffset, targetBytes);
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                    if (magic == RouterLayout.Magic)
                    {
                        routerOffset = targetOffset;
                        continue;
                    }

                    ValidateLeaf(targetBytes, targetOffset);
                    int item = UInt64CountedSetSparseLayout.LowerBound(targetBytes, key);
                    int itemCount = UInt64CountedSetSparseLayout.ReadItemCount(targetBytes);
                    if (item < itemCount && UInt64CountedSetSparseLayout.ReadKeyAt(targetBytes, item) == key)
                    {
                        ulong prior = UInt64CountedSetSparseLayout.ReadCountAt(targetBytes, item);
                        if (IsSaturating && prior >= _saturationCeiling)
                            return prior;
                        ulong next = checked(prior + 1);
                        UInt64CountedSetSparseLayout.WriteCountAt(targetBytes, item, next);
                        PublishLeafMutation(targetOffset, targetBytes, DistinctCount, checked(RetainedOccurrenceCount + 1));
                        return next;
                    }

                    if (itemCount < UInt64CountedSetSparseLayout.GetCapacity(_counterBits))
                    {
                        UInt64CountedSetSparseLayout.InsertAbsent(targetBytes, item, key, count: 1);
                        PublishLeafMutation(targetOffset, targetBytes, checked(DistinctCount + 1), checked(RetainedOccurrenceCount + 1));
                        return 1;
                    }

                    PublishFullLeafInsert(routerOffset, routerBytes, router.KeyDepth, targetOffset, targetBytes, item, key);
                    return 1;
                }

                throw new InvalidDataException("The UInt64 counted-set route exceeded the maximum key depth.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(targetPage);
                ArrayPool<byte>.Shared.Return(routerPage);
            }
        }
    }

    /// <summary>
    /// Removes one retained occurrence and returns the new stored count.<br/>
    /// Removing the final occurrence removes the key and may retire an empty leaf; an absent key remains a no-op.<br/>
    /// </summary>
    /// <param name="key">The key whose retained count should decrease.<br/></param>
    /// <returns>The post-operation stored count, or zero when absent or removed.<br/></returns>
    internal ulong RemoveOccurrence(ulong key)
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            return RemoveCore(key, removeAll: false, out _);
        }
    }

    /// <summary>
    /// Removes one key and its complete stored exact or capped count.<br/>
    /// </summary>
    /// <param name="key">The key to remove.<br/></param>
    /// <returns>The prior stored count, or zero when absent.<br/></returns>
    internal ulong Remove(ulong key)
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            _ = RemoveCore(key, removeAll: true, out ulong removed);
            return removed;
        }
    }

    /// <summary>
    /// Performs one decrement or whole-key removal while the caller owns the mutation gate.<br/>
    /// </summary>
    /// <param name="key">The key to mutate.<br/></param>
    /// <param name="removeAll">Whether to remove the complete stored count rather than one retained occurrence.<br/></param>
    /// <param name="removedCount">Receives the prior stored count when the key exists.<br/></param>
    /// <returns>The post-operation stored count.<br/></returns>
    private ulong RemoveCore(ulong key, bool removeAll, out ulong removedCount)
    {
        byte[] routerPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        byte[] targetPage = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize);
        try
        {
            long routerOffset = _rootRouterOffset;
            for (int hop = 0; hop <= MaximumRouterLevels; hop++)
            {
                Span<byte> routerBytes = routerPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(routerOffset, routerBytes);
                RouterReader router = ValidateDirectRouter(routerBytes, routerOffset);
                long targetOffset = router.GetDirectTarget(GetKeyByte(key, router.KeyDepth));
                if (targetOffset == 0)
                {
                    removedCount = 0;
                    return 0;
                }

                Span<byte> targetBytes = targetPage.AsSpan(0, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(targetOffset, targetBytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                if (magic == RouterLayout.Magic)
                {
                    routerOffset = targetOffset;
                    continue;
                }

                ValidateLeaf(targetBytes, targetOffset);
                int item = UInt64CountedSetSparseLayout.LowerBound(targetBytes, key);
                int itemCount = UInt64CountedSetSparseLayout.ReadItemCount(targetBytes);
                if (item >= itemCount || UInt64CountedSetSparseLayout.ReadKeyAt(targetBytes, item) != key)
                {
                    removedCount = 0;
                    return 0;
                }

                ulong prior = UInt64CountedSetSparseLayout.ReadCountAt(targetBytes, item);
                removedCount = prior;
                if (!removeAll && prior > 1)
                {
                    ulong next = prior - 1;
                    UInt64CountedSetSparseLayout.WriteCountAt(targetBytes, item, next);
                    PublishLeafMutation(targetOffset, targetBytes, DistinctCount, RetainedOccurrenceCount - 1);
                    return next;
                }

                UInt64CountedSetSparseLayout.RemoveAt(targetBytes, item);
                PublishRemove(
                    routerOffset,
                    routerBytes,
                    targetOffset,
                    targetBytes,
                    itemCount == 1,
                    DistinctCount - 1,
                    RetainedOccurrenceCount - prior);
                return 0;
            }

            throw new InvalidDataException("The UInt64 counted-set removal exceeded the maximum key depth.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(targetPage);
            ArrayPool<byte>.Shared.Return(routerPage);
        }
    }

    /// <summary>
    /// Copies every key/counter pair into a caller-owned span in ascending or descending natural key order.<br/>
    /// Traversal uses a fixed eight-router stack and one target page without a result-side sort or deduplication set.<br/>
    /// </summary>
    /// <param name="destination">The destination span, which must fit the captured distinct count.<br/></param>
    /// <param name="descending">Whether to emit descending rather than ascending order.<br/></param>
    /// <returns>The number of entries copied.<br/></returns>
    internal int CopyTo(Span<UInt64CountedSetPrototypeEntry> destination, bool descending = false)
    {
        ThrowIfDisposed();
        using DataKernel.CoherentReadLease read = _kernel.EnterCoherentRead();
        ulong capturedCount = DistinctCount;
        if (capturedCount > int.MaxValue || destination.Length < (int)capturedCount)
            throw new ArgumentException("The destination is smaller than the counted set's captured distinct count.", nameof(destination));

        byte[] pages = ArrayPool<byte>.Shared.Rent(UInt64SetPrototypeRootLayout.PageSize * TraversalPageCount);
        Span<int> nextRoutes = stackalloc int[MaximumRouterLevels];
        Span<long> priorTargets = stackalloc long[MaximumRouterLevels];
        priorTargets.Fill(long.MinValue);
        try
        {
            int level = 0;
            nextRoutes[0] = descending ? RouterLayout.MaxOneByteRouteCount - 1 : 0;
            ReadAndValidateTraversalRouter(pages, level, _rootRouterOffset);
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
                long targetOffset = new RouterReader(routerBytes).GetDirectTarget(checked((byte)routeIndex));
                if (targetOffset == 0 || targetOffset == priorTargets[level])
                    continue;
                priorTargets[level] = targetOffset;

                Span<byte> targetBytes = pages.AsSpan(MaximumRouterLevels * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize);
                _kernel.Read(targetOffset, targetBytes);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(targetBytes);
                if (magic == RouterLayout.Magic)
                {
                    if (level + 1 >= MaximumRouterLevels)
                        throw new InvalidDataException("The UInt64 counted-set traversal exceeded maximum router depth.");
                    level++;
                    nextRoutes[level] = descending ? RouterLayout.MaxOneByteRouteCount - 1 : 0;
                    priorTargets[level] = long.MinValue;
                    targetBytes.CopyTo(pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize));
                    _ = ValidateDirectRouter(pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize), targetOffset);
                    continue;
                }

                ValidateLeaf(targetBytes, targetOffset);
                int itemCount = UInt64CountedSetSparseLayout.ReadItemCount(targetBytes);
                if (descending)
                {
                    for (int i = itemCount - 1; i >= 0; i--)
                        destination[written++] = new(UInt64CountedSetSparseLayout.ReadKeyAt(targetBytes, i), UInt64CountedSetSparseLayout.ReadCountAt(targetBytes, i));
                }
                else
                {
                    for (int i = 0; i < itemCount; i++)
                        destination[written++] = new(UInt64CountedSetSparseLayout.ReadKeyAt(targetBytes, i), UInt64CountedSetSparseLayout.ReadCountAt(targetBytes, i));
                }
            }

            if ((ulong)written != capturedCount)
                throw new InvalidDataException($"UInt64 counted-set traversal emitted {written:N0} entries but the root reports {capturedCount:N0}.");
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pages);
        }
    }

    /// <summary>
    /// Clears the complete counted set through one empty-root publication and retires every reachable non-root page.<br/>
    /// </summary>
    internal void Clear()
    {
        ThrowIfDisposed();
        lock (_mutationSync)
        {
            if (DistinctCount == 0)
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
                StageRootMutation(0, 0, nextGeneration);
                _kernel.Commit();
                SetVolatileRoot(0, 0, nextGeneration);
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
    /// Initializes the counted root, allocator directory, and allocator-owned root router for either backing kind.<br/>
    /// </summary>
    private static UInt64CountedSetPrototype Initialize(
        DataKernel kernel,
        string? path,
        UInt64SetPrototypeKind kind,
        byte counterBits,
        ulong saturationCeiling)
    {
        RawDataReservation root = kernel.Reserve(UInt64SetPrototypeRootLayout.Size);
        RawDataReservation allocationDirectory = kernel.Reserve(FileAllocationDirectoryLayout.Size);
        if (root.Extent.Offset != 0 || allocationDirectory.Extent.Offset != UInt64SetPrototypeRootLayout.Size)
            throw new InvalidDataException("The UInt64 counted set requires deterministic root and allocator offsets.");

        UInt64SetPrototypeRootLayout.Initialize(root.Span, kind, counterBits, allocationDirectory.Extent.Offset, saturationCeiling);
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
        return new UInt64CountedSetPrototype(kernel, path, kind, counterBits, saturationCeiling, rootRouter.Extent.Offset, 0, 0, 0);
    }

    /// <summary>
    /// Creates the shared DataKernel policy used by counted-set memory and file backings.<br/>
    /// </summary>
    private static DataKernelOptions CreateDataKernelOptions() => new(
        AppendBufferSize: DataKernelOptions.DefaultAppendBufferSize,
        ReservedPrefixBytes: 0,
        FlushToDiskOnCommit: false,
        MaxCommitGapCoalesceBytes: 512);

    /// <summary>
    /// Validates the authoritative root router after initialization or reopen.<br/>
    /// </summary>
    private void ValidateRootRouter()
    {
        byte[] bytes = new byte[RouterLayout.Size];
        _kernel.Read(_rootRouterOffset, bytes);
        RouterReader router = ValidateDirectRouter(bytes, _rootRouterOffset);
        if (router.KeyDepth != 0)
            throw new InvalidDataException("The UInt64 counted-set root router must use key depth zero.");
    }

    /// <summary>
    /// Publishes one new key/count leaf into a previously unset route.<br/>
    /// </summary>
    private void PublishNewLeaf(long routerOffset, Span<byte> routerBytes, byte prefix, ulong key)
    {
        ulong nextDistinct = checked(DistinctCount + 1);
        ulong nextOccurrences = checked(RetainedOccurrenceCount + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation leaf = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            Span<ulong> keys = stackalloc ulong[1];
            Span<ulong> counts = stackalloc ulong[1];
            keys[0] = key;
            counts[0] = 1;
            UInt64CountedSetSparseLayout.Initialize(leaf.Span, _kind, _counterBits, keys, counts);
            new RouterWriter(routerBytes).WriteRouteTarget(prefix, leaf.Extent.Offset);
            _kernel.StageWriteAt(routerOffset, routerBytes);
            StageRootMutation(nextDistinct, nextOccurrences, nextGeneration);
            _kernel.Commit();
            SetVolatileRoot(nextDistinct, nextOccurrences, nextGeneration);
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
    /// Publishes one prepared non-structural leaf mutation and its authoritative root counters.<br/>
    /// </summary>
    private void PublishLeafMutation(long leafOffset, ReadOnlySpan<byte> leafBytes, ulong nextDistinct, ulong nextOccurrences)
    {
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            _kernel.StageWriteAt(leafOffset, leafBytes);
            StageRootMutation(nextDistinct, nextOccurrences, nextGeneration);
            _kernel.Commit();
            SetVolatileRoot(nextDistinct, nextOccurrences, nextGeneration);
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
    /// Merges one absent key into a full counted leaf and selects a parent split or deeper same-extent router transform.<br/>
    /// </summary>
    private void PublishFullLeafInsert(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        ushort parentDepth,
        long leafOffset,
        ReadOnlySpan<byte> leafBytes,
        int insertIndex,
        ulong key)
    {
        int priorCount = UInt64CountedSetSparseLayout.ReadItemCount(leafBytes);
        Span<ulong> keys = stackalloc ulong[UInt64CountedSetSparseLayout.Capacity2 + 1];
        Span<ulong> counts = stackalloc ulong[UInt64CountedSetSparseLayout.Capacity2 + 1];
        Span<ulong> liveKeys = keys.Slice(0, priorCount + 1);
        Span<ulong> liveCounts = counts.Slice(0, priorCount + 1);
        for (int source = 0, target = 0; target < liveKeys.Length; target++)
        {
            if (target == insertIndex)
            {
                liveKeys[target] = key;
                liveCounts[target] = 1;
            }
            else
            {
                liveKeys[target] = UInt64CountedSetSparseLayout.ReadKeyAt(leafBytes, source);
                liveCounts[target] = UInt64CountedSetSparseLayout.ReadCountAt(leafBytes, source++);
            }
        }

        byte firstPrefix = GetKeyByte(liveKeys[0], parentDepth);
        byte lastPrefix = GetKeyByte(liveKeys[^1], parentDepth);
        if (firstPrefix != lastPrefix)
        {
            int splitIndex = FindByteBoundarySplit(liveKeys, parentDepth, UInt64CountedSetSparseLayout.GetCapacity(_counterBits));
            PublishLeafSplit(parentRouterOffset, parentRouterBytes, leafOffset, liveKeys, liveCounts, splitIndex, parentDepth);
            return;
        }

        PublishLeafTransform(parentRouterOffset, parentRouterBytes, leafOffset, liveKeys, liveCounts, parentDepth, firstPrefix);
    }

    /// <summary>
    /// Publishes two replacement counted leaves at one parent-byte boundary and retires the old full leaf.<br/>
    /// </summary>
    private void PublishLeafSplit(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long oldLeafOffset,
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<ulong> counts,
        int splitIndex,
        ushort parentDepth)
    {
        byte rightPrefix = GetKeyByte(keys[splitIndex], parentDepth);
        ulong nextDistinct = checked(DistinctCount + 1);
        ulong nextOccurrences = checked(RetainedOccurrenceCount + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation left = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            RawDataReservation right = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            UInt64CountedSetSparseLayout.Initialize(left.Span, _kind, _counterBits, keys.Slice(0, splitIndex), counts.Slice(0, splitIndex));
            UInt64CountedSetSparseLayout.Initialize(right.Span, _kind, _counterBits, keys.Slice(splitIndex), counts.Slice(splitIndex));

            RouterReader parent = ValidateDirectRouter(parentRouterBytes, parentRouterOffset);
            RouterWriter writer = new(parentRouterBytes);
            bool foundOwner = false;
            for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
            {
                if (parent.GetDirectTarget(checked((byte)i)) != oldLeafOffset)
                    continue;
                writer.WriteRouteTarget(i, i < rightPrefix ? left.Extent.Offset : right.Extent.Offset);
                foundOwner = true;
            }
            if (!foundOwner)
                throw new InvalidDataException("The UInt64 counted-set split could not find its parent route owner.");

            _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
            _kernel.StageExtentRetirement(oldLeafOffset, UInt64SetPrototypeRootLayout.PageSize);
            StageRootMutation(nextDistinct, nextOccurrences, nextGeneration);
            _kernel.Commit();
            SetVolatileRoot(nextDistinct, nextOccurrences, nextGeneration);
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
    /// Converts one full same-byte counted leaf into a same-extent router chain and two final counted child leaves.<br/>
    /// Parent aliases outside the actual shared byte are cleared so future inputs cannot skip a required key depth.<br/>
    /// </summary>
    private void PublishLeafTransform(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long leafOffset,
        ReadOnlySpan<ulong> keys,
        ReadOnlySpan<ulong> counts,
        ushort parentDepth,
        byte sharedParentPrefix)
    {
        int firstChildDepth = parentDepth + 1;
        int splitDepth = firstChildDepth;
        while (splitDepth < sizeof(ulong) && GetKeyByte(keys[0], splitDepth) == GetKeyByte(keys[^1], splitDepth))
            splitDepth++;
        if (splitDepth >= sizeof(ulong))
            throw new InvalidDataException("A full UInt64 counted-set leaf contains indistinguishable distinct keys.");

        int splitIndex = FindByteBoundarySplit(keys, splitDepth, UInt64CountedSetSparseLayout.GetCapacity(_counterBits));
        byte rightPrefix = GetKeyByte(keys[splitIndex], splitDepth);
        ulong nextDistinct = checked(DistinctCount + 1);
        ulong nextOccurrences = checked(RetainedOccurrenceCount + 1);
        ulong nextGeneration = checked(Generation + 1);
        _kernel.EnterExclusiveStoragePublicationBlockingReads();
        try
        {
            RawDataReservation left = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            RawDataReservation right = _kernel.Reserve(UInt64SetPrototypeRootLayout.PageSize);
            UInt64CountedSetSparseLayout.Initialize(left.Span, _kind, _counterBits, keys.Slice(0, splitIndex), counts.Slice(0, splitIndex));
            UInt64CountedSetSparseLayout.Initialize(right.Span, _kind, _counterBits, keys.Slice(splitIndex), counts.Slice(splitIndex));

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
                    new RouterWriter(firstRouterBytes.AsSpan(0, RouterLayout.Size)).InitializeExpandedOneByte(checked((ushort)splitDepth), 0, targets);
                    nextRouterOffset = leafOffset;
                }
                else
                {
                    RawDataReservation finalRouter = _kernel.Reserve(RouterLayout.Size);
                    new RouterWriter(finalRouter.Span).InitializeExpandedOneByte(checked((ushort)splitDepth), 0, targets);
                    nextRouterOffset = finalRouter.Extent.Offset;
                }

                for (int depth = splitDepth - 1; depth >= firstChildDepth; depth--)
                {
                    targets.Clear();
                    targets[GetKeyByte(keys[0], depth)] = nextRouterOffset;
                    if (depth == firstChildDepth)
                    {
                        new RouterWriter(firstRouterBytes.AsSpan(0, RouterLayout.Size)).InitializeExpandedOneByte(checked((ushort)depth), 0, targets);
                        nextRouterOffset = leafOffset;
                    }
                    else
                    {
                        RawDataReservation intermediate = _kernel.Reserve(RouterLayout.Size);
                        new RouterWriter(intermediate.Span).InitializeExpandedOneByte(checked((ushort)depth), 0, targets);
                        nextRouterOffset = intermediate.Extent.Offset;
                    }
                }

                RouterReader parent = ValidateDirectRouter(parentRouterBytes, parentRouterOffset);
                RouterWriter parentWriter = new(parentRouterBytes);
                bool retainedOwner = false;
                for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
                {
                    if (parent.GetDirectTarget(checked((byte)i)) != leafOffset)
                        continue;
                    if (i == sharedParentPrefix)
                    {
                        retainedOwner = true;
                        continue;
                    }
                    parentWriter.WriteRouteTarget(i, 0);
                }
                if (!retainedOwner)
                    throw new InvalidDataException("The UInt64 counted-set transform could not retain its exact parent-byte owner.");

                _kernel.StageWriteAt(leafOffset, firstRouterBytes.AsSpan(0, RouterLayout.Size));
                _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
                StageRootMutation(nextDistinct, nextOccurrences, nextGeneration);
                _kernel.Commit();
                SetVolatileRoot(nextDistinct, nextOccurrences, nextGeneration);
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
    /// Publishes removal of one complete key/count entry, optionally disconnecting and retiring its empty leaf.<br/>
    /// </summary>
    private void PublishRemove(
        long parentRouterOffset,
        Span<byte> parentRouterBytes,
        long leafOffset,
        ReadOnlySpan<byte> leafBytes,
        bool becameEmpty,
        ulong nextDistinct,
        ulong nextOccurrences)
    {
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
                    if (parent.GetDirectTarget(checked((byte)i)) == leafOffset)
                        writer.WriteRouteTarget(i, 0);
                }
                _kernel.StageWriteAt(parentRouterOffset, parentRouterBytes);
                _kernel.StageExtentRetirement(leafOffset, UInt64SetPrototypeRootLayout.PageSize);
            }
            else
            {
                _kernel.StageWriteAt(leafOffset, leafBytes);
            }

            StageRootMutation(nextDistinct, nextOccurrences, nextGeneration);
            _kernel.Commit();
            SetVolatileRoot(nextDistinct, nextOccurrences, nextGeneration);
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
    /// Stages the complete mutable counted-root range without rereading its immutable descriptor fields.<br/>
    /// </summary>
    private void StageRootMutation(ulong distinctCount, ulong occurrenceCount, ulong generation)
    {
        Span<byte> bytes = stackalloc byte[32];
        UInt64SetPrototypeRootLayout.WriteCountedMutation(bytes, distinctCount, occurrenceCount, _saturationCeiling, generation);
        _kernel.StageWriteAt(UInt64SetPrototypeRootLayout.DistinctCountOffset, bytes);
    }

    /// <summary>
    /// Publishes already-committed root counters to lock-free property readers.<br/>
    /// </summary>
    private void SetVolatileRoot(ulong distinctCount, ulong occurrenceCount, ulong generation)
    {
        Interlocked.Exchange(ref _distinctCount, unchecked((long)distinctCount));
        Interlocked.Exchange(ref _occurrenceCount, unchecked((long)occurrenceCount));
        Interlocked.Exchange(ref _generation, unchecked((long)generation));
    }

    /// <summary>
    /// Validates that one routed target is the exact or saturating leaf format required by this root.<br/>
    /// </summary>
    private void ValidateLeaf(ReadOnlySpan<byte> bytes, long offset)
    {
        if (!UInt64CountedSetSparseLayout.IsValid(bytes, _kind, _counterBits))
            throw new InvalidDataException($"UInt64 counted-set route target at offset {offset:N0} is not a valid {_kind} {_counterBits}-bit leaf.");
    }

    /// <summary>
    /// Reads and validates one traversal router into its level-owned pooled page slice.<br/>
    /// </summary>
    private void ReadAndValidateTraversalRouter(byte[] pages, int level, long offset)
    {
        Span<byte> bytes = pages.AsSpan(level * UInt64SetPrototypeRootLayout.PageSize, UInt64SetPrototypeRootLayout.PageSize);
        _kernel.Read(offset, bytes);
        _ = ValidateDirectRouter(bytes, offset);
    }

    /// <summary>
    /// Collects each reachable non-root counted topology page exactly once for fast clear.<br/>
    /// This maintenance-only path may allocate a managed visited set; ordinary lookup and ordered traversal do not.<br/>
    /// </summary>
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
                        throw new InvalidDataException($"UInt64 counted-set topology target {target:N0} has more than one logical owner.");

                    _kernel.Read(target, magicBytes);
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
                    if (magic == RouterLayout.Magic)
                        routers.Push(target);
                    else if (!UInt64CountedSetSparseLayout.IsCountedMagic(magic))
                        throw new InvalidDataException($"UInt64 counted-set clear found an unknown page format at offset {target:N0}.");
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
    /// Finds a capacity-safe key-byte transition nearest the sequence median.<br/>
    /// </summary>
    private static int FindByteBoundarySplit(ReadOnlySpan<ulong> keys, int keyDepth, int capacity)
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

        if (selected <= 0 || selected >= keys.Length || selected > capacity || keys.Length - selected > capacity)
            throw new InvalidDataException("The UInt64 counted set could not find a capacity-safe byte-boundary split.");
        return selected;
    }

    /// <summary>
    /// Extracts one big-endian route byte from a UInt64 key without allocating an encoded buffer.<br/>
    /// </summary>
    private static byte GetKeyByte(ulong key, int depth)
    {
        if ((uint)depth >= sizeof(ulong))
            throw new ArgumentOutOfRangeException(nameof(depth));
        return checked((byte)((key >> ((sizeof(ulong) - depth - 1) * 8)) & byte.MaxValue));
    }

    /// <summary>
    /// Validates one direct one-byte router used by the counted topology.<br/>
    /// </summary>
    private static RouterReader ValidateDirectRouter(ReadOnlySpan<byte> bytes, long offset)
    {
        RouterReader router = new(bytes);
        if (!router.IsValid || !router.HasDirectIndex || router.PrefixByteCount != 1 || router.KeyDepth >= sizeof(ulong))
            throw new InvalidDataException($"UInt64 counted-set router at offset {offset:N0} is invalid or incompatible with direct one-byte routing.");
        return router;
    }

    /// <summary>
    /// Resolves exact or minimal-width saturating physical policy from one public creation ceiling.<br/>
    /// A zero ceiling means exact counting; saturation begins at two because ceiling one is semantically a presence set.<br/>
    /// </summary>
    private static void ResolvePolicy(ulong saturationCeiling, out UInt64SetPrototypeKind kind, out byte counterBits)
    {
        if (saturationCeiling == 0)
        {
            kind = UInt64SetPrototypeKind.Exact;
            counterBits = 64;
            return;
        }
        if (saturationCeiling < 2)
            throw new ArgumentOutOfRangeException(nameof(saturationCeiling), saturationCeiling, "A saturating counted set requires a ceiling of at least two; use LibraDexSortedSet<TKey> or LibraDexRoutedSet<TKey> for presence-only behavior.");

        kind = UInt64SetPrototypeKind.Saturating;
        counterBits = saturationCeiling switch
        {
            <= 0x03UL => 2,
            <= 0x0FUL => 4,
            <= byte.MaxValue => 8,
            <= ushort.MaxValue => 16,
            <= uint.MaxValue => 32,
            _ => 64
        };
    }

    /// <summary>
    /// Validates reopened counted policy and rejects noncanonical width/ceiling combinations.<br/>
    /// </summary>
    private static void ValidatePolicy(UInt64SetPrototypeKind kind, byte counterBits, ulong saturationCeiling)
    {
        if (kind == UInt64SetPrototypeKind.Exact)
        {
            if (counterBits != 64 || saturationCeiling != 0)
                throw new InvalidDataException("An exact UInt64 counted set must use 64-bit counters and a zero saturation ceiling.");
            return;
        }
        if (kind != UInt64SetPrototypeKind.Saturating)
            throw new InvalidDataException($"Collection kind '{kind}' is not a counted-set format.");

        ResolvePolicy(saturationCeiling, out UInt64SetPrototypeKind expectedKind, out byte expectedBits);
        if (expectedKind != kind || expectedBits != counterBits)
            throw new InvalidDataException("The saturating UInt64 counted-set width is not canonical for its persisted ceiling.");
    }

    /// <summary>
    /// Throws when an operation follows disposal of the counted-set owner.<br/>
    /// </summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _kernel.Dispose();
    }
}

/// <summary>
/// Carries one physical UInt64 key and its exact or capped stored count through bounded internal traversal.<br/>
/// </summary>
/// <param name="Key">The ordered UInt64 key.<br/></param>
/// <param name="Count">The positive exact or capped stored count.<br/></param>
internal readonly record struct UInt64CountedSetPrototypeEntry(ulong Key, ulong Count);
