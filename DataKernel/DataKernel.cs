using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LibraDex;

/// <summary>
/// Provides the first raw byte movement core for LibraDex.<br/>
/// This slice deliberately knows nothing about shelves, routers, indexes, or superblock layout beyond reserving the file prefix.<br/>
/// </summary>
internal sealed class DataKernel : IDisposable
{
    private const int InitialPendingSegmentCapacity = 16;
    private const int InitialCommitSliceCapacity = 16;
    private const int InitialCoveredRangeCapacity = 16;
    private const int InitialRemainingRangeCapacity = 8;
    private const int MemoryPageSize = 256 * 1024;

    private readonly SafeFileHandle? handle;
    private readonly DataKernelOptions options;
    private readonly DataKernelTelemetryOptions telemetryOptions;
    private readonly List<PendingSegment> pendingSegments = new(InitialPendingSegmentCapacity);
    private readonly List<CommitSlice> fileCommitSlices = new(InitialCommitSliceCapacity);
    private readonly List<CommittedRange> coveredRanges = new(InitialCoveredRangeCapacity);
    private readonly List<CommittedRange> remainingRanges = new(InitialRemainingRangeCapacity);
    private readonly VolatileMemoryArena memoryArena = new(MemoryPageSize);
    private readonly ReaderWriterLockSlim storageSync = new(LockRecursionPolicy.SupportsRecursion);
    private readonly DataKernelBackingKind backingKind;
    [ThreadStatic]
    private static CoherentReadState? currentCoherentRead;
    private VolatileMemoryArenaSnapshot? activeMemoryReadSnapshot;
    private int coherentMemoryReadCount;
    private int exclusiveStoragePublicationDepth;
    private long nextAppendOffset;
    private long stagedExtentCount;
    private long readCallCount;
    private long backingReadCallCount;
    private long bytesRead;
    private long directMemoryStagedExtentCount;
    private long directMemoryWriteCallCount;
    private long directMemoryBytesWritten;
    private long mutationVersion;
    private bool disposed;

    private DataKernel(
        SafeFileHandle? handle,
        DataKernelOptions options,
        DataKernelTelemetryOptions telemetryOptions,
        DataKernelBackingKind backingKind,
        long nextAppendOffset)
    {
        this.handle = handle;
        this.options = options;
        this.telemetryOptions = telemetryOptions;
        this.backingKind = backingKind;
        this.nextAppendOffset = nextAppendOffset;
    }

    /// <summary>
    /// Gets the raw backing kind selected for this DataKernel instance.<br/>
    /// This is intended for diagnostics and harness output, not for shelf/router branching.<br/>
    /// </summary>
    public DataKernelBackingKind BackingKind => backingKind;

    /// <summary>
    /// Gets the in-memory raw-storage mutation version for this kernel.<br/>
    /// The value advances when bytes are staged, directly written, released, or discarded; it is not persisted and does not add storage writes.<br/>
    /// Count/read-side derived caches use this value to avoid serving metadata that predates a raw storage mutation.<br/>
    /// </summary>
    internal long MutationVersion => Volatile.Read(ref mutationVersion);

    /// <summary>
    /// Opens a raw file-backed DataKernel over a file path.<br/>
    /// The file is opened for positional reads and writes and the append cursor starts after the reserved prefix.<br/>
    /// </summary>
    /// <param name="path">The data file path.</param>
    /// <param name="mode">The file open mode.</param>
    /// <param name="options">The raw DataKernel policy.</param>
    /// <param name="telemetryOptions">The telemetry collection policy.</param>
    /// <returns>An opened raw DataKernel.</returns>
    public static DataKernel Open(
        string path,
        FileMode mode,
        DataKernelOptions options,
        DataKernelTelemetryOptions telemetryOptions)
    {
        options.Validate();

        SafeFileHandle handle = File.OpenHandle(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.RandomAccess);

        long fileLength = RandomAccess.GetLength(handle);
        long nextAppendOffset = Math.Max(fileLength, options.ReservedPrefixBytes);

        return new DataKernel(handle, options, telemetryOptions, DataKernelBackingKind.File, nextAppendOffset);
    }

    /// <summary>
    /// Opens a raw memory-backed DataKernel.<br/>
    /// Memory mode uses a volatile page arena for committed bytes and does not provide durability.<br/>
    /// </summary>
    /// <param name="options">The raw DataKernel policy.</param>
    /// <param name="telemetryOptions">The telemetry collection policy.</param>
    /// <returns>An opened memory-backed raw DataKernel.</returns>
    public static DataKernel OpenMemory(
        DataKernelOptions options,
        DataKernelTelemetryOptions telemetryOptions)
    {
        options.Validate();
        return new DataKernel(null, options, telemetryOptions, DataKernelBackingKind.Memory, options.ReservedPrefixBytes);
    }

    /// <summary>
    /// Stages raw bytes for append during the next commit.<br/>
    /// Data is copied into pooled append buffers so adjacent appends can be committed with fewer backing writes.<br/>
    /// </summary>
    /// <param name="source">The bytes to append.</param>
    /// <returns>The raw extent where the bytes will exist after commit.</returns>
    public RawDataExtent Append(ReadOnlySpan<byte> source)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (source.Length == 0)
            {
                return new RawDataExtent(nextAppendOffset, 0);
            }

            AdvanceMutationVersion();

            long extentOffset = nextAppendOffset;
            if (backingKind == DataKernelBackingKind.Memory)
            {
                memoryArena.Write(extentOffset, source);
                nextAppendOffset += source.Length;
                stagedExtentCount++;
                if (telemetryOptions.Enabled)
                {
                    directMemoryStagedExtentCount++;
                    directMemoryWriteCallCount++;
                    directMemoryBytesWritten += source.Length;
                }

                return new RawDataExtent(extentOffset, source.Length);
            }

            int remaining = source.Length;
            int sourceOffset = 0;

            while (remaining > 0)
            {
                PendingSegment segment = GetAppendSegment(remaining);
                int writable = Math.Min(remaining, segment.Capacity - segment.Length);
                source.Slice(sourceOffset, writable).CopyTo(segment.Buffer.AsSpan(segment.SourceOffset + segment.Length, writable));
                segment.Length += writable;
                sourceOffset += writable;
                remaining -= writable;
                nextAppendOffset += writable;
            }

            stagedExtentCount++;
            return new RawDataExtent(extentOffset, source.Length);
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reserves a contiguous staged byte span for direct caller population.<br/>
    /// This is the zero-copy append path for generated data such as future shelf, router, and metadata bytes.<br/>
    /// The returned reservation is structurally staged immediately; callers must fill the returned span before commit.<br/>
    /// </summary>
    /// <param name="length">The number of contiguous bytes to reserve.</param>
    /// <returns>A reservation containing the future raw extent and the writable staged span.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative.</exception>
    public RawDataReservation Reserve(int length)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "Reservation length cannot be negative.");
            }

            if (length == 0)
            {
                return new RawDataReservation(new RawDataExtent(nextAppendOffset, 0), Span<byte>.Empty);
            }

            AdvanceMutationVersion();

            if (backingKind == DataKernelBackingKind.Memory)
            {
                if (memoryArena.TryReserveReleased(length, out long releasedOffset) &&
                    memoryArena.TryGetWritableSpan(releasedOffset, length, out Span<byte> releasedSpan))
                {
                    stagedExtentCount++;
                    if (telemetryOptions.Enabled)
                    {
                        directMemoryStagedExtentCount++;
                        directMemoryWriteCallCount++;
                        directMemoryBytesWritten += length;
                    }

                    return new RawDataReservation(new RawDataExtent(releasedOffset, length), releasedSpan);
                }

                if (memoryArena.TryReserveAppend(length, ref nextAppendOffset, out long memoryOffset, out Span<byte> memorySpan))
                {
                    stagedExtentCount++;
                    if (telemetryOptions.Enabled)
                    {
                        directMemoryStagedExtentCount++;
                        directMemoryWriteCallCount++;
                        directMemoryBytesWritten += length;
                    }

                    return new RawDataReservation(new RawDataExtent(memoryOffset, length), memorySpan);
                }
            }

            if (backingKind == DataKernelBackingKind.Memory && memoryArena.TryReserveReleased(length, out long releasedFallbackOffset))
            {
                int releasedCapacity = length >= options.AppendBufferSize
                    ? length
                    : options.AppendBufferSize;
                byte[] releasedBuffer = ArrayPool<byte>.Shared.Rent(releasedCapacity);
                PendingSegment releasedSegment = new(releasedBuffer, releasedFallbackOffset, releasedCapacity, isAppend: false);
                releasedSegment.Length = length;
                pendingSegments.Add(releasedSegment);
                stagedExtentCount++;
                return new RawDataReservation(new RawDataExtent(releasedFallbackOffset, length), releasedBuffer.AsSpan(0, length));
            }

            PendingSegment segment = GetContiguousAppendSegment(length);
            long extentOffset = nextAppendOffset;
            Span<byte> span = segment.Buffer.AsSpan(segment.SourceOffset + segment.Length, length);

            segment.Length += length;
            nextAppendOffset += length;
            stagedExtentCount++;

            return new RawDataReservation(new RawDataExtent(extentOffset, length), span);
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reserves a fixed-offset staged byte span for direct caller population.<br/>
    /// This is the low-level positional update primitive for durable metadata and later dirty structural pages.<br/>
    /// The append cursor is not changed, and the staged bytes are published at the supplied offset during commit.<br/>
    /// </summary>
    /// <param name="offset">The absolute backing offset where the bytes should be written during commit.</param>
    /// <param name="length">The number of contiguous bytes to reserve.</param>
    /// <returns>A reservation containing the target raw extent and the writable staged span.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="length"/> is negative.</exception>
    public RawDataReservation ReserveAt(long offset, int length)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Reservation offset cannot be negative.");
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "Reservation length cannot be negative.");
            }

            if (length == 0)
            {
                return new RawDataReservation(new RawDataExtent(offset, 0), Span<byte>.Empty);
            }

            AdvanceMutationVersion();

            if (backingKind == DataKernelBackingKind.Memory &&
                memoryArena.TryGetWritableSpan(offset, length, out Span<byte> memorySpan))
            {
                stagedExtentCount++;
                if (telemetryOptions.Enabled)
                {
                    directMemoryStagedExtentCount++;
                    directMemoryWriteCallCount++;
                    directMemoryBytesWritten += length;
                }

                return new RawDataReservation(new RawDataExtent(offset, length), memorySpan);
            }

            if (TryGetPendingWritableSpan(offset, length, out Span<byte> pendingSpan))
            {
                return new RawDataReservation(new RawDataExtent(offset, length), pendingSpan);
            }

            int capacity = length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
            PendingSegment segment = new(buffer, offset, capacity, isAppend: false);
            segment.Length = length;
            pendingSegments.Add(segment);
            stagedExtentCount++;

            return new RawDataReservation(new RawDataExtent(offset, length), buffer.AsSpan(0, length));
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Stages a fixed-offset write by borrowing an immutable slice of a caller-owned byte array through the next commit or discard boundary.<br/>
    /// The caller must retain the array and must not modify the selected slice until that boundary completes.<br/>
    /// Borrowed arrays are never returned to <see cref="ArrayPool{T}"/>; this path removes the rent-and-copy cost when an enclosing durability batch already owns the final byte image.<br/>
    /// </summary>
    /// <param name="offset">The absolute backing offset where the borrowed bytes will be published.<br/></param>
    /// <param name="source">The caller-owned array containing the final staged bytes.<br/></param>
    /// <param name="sourceOffset">The starting byte offset inside <paramref name="source"/>.<br/></param>
    /// <param name="length">The positive number of bytes to stage.<br/></param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the backing or source range is invalid.<br/></exception>
    internal void StageBorrowedAt(long offset, byte[] source, int sourceOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(source);
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The borrowed write offset cannot be negative.");
            }

            if (sourceOffset < 0 || length <= 0 || sourceOffset > source.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceOffset), sourceOffset, "The borrowed source slice must be a positive range inside the source array.");
            }

            AdvanceMutationVersion();
            pendingSegments.Add(new PendingSegment(source, sourceOffset, offset, length, isAppend: false, returnsBuffer: false)
            {
                Length = length
            });
            stagedExtentCount++;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reads raw bytes from a direct offset into a caller-provided destination span.<br/>
    /// The method loops until the destination is filled or the backing store ends unexpectedly.<br/>
    /// </summary>
    /// <param name="offset">The offset to read from.</param>
    /// <param name="destination">The destination buffer.</param>
    /// <exception cref="EndOfStreamException">Thrown when the backing store ends before the requested bytes are read.</exception>
    public void Read(long offset, Span<byte> destination)
    {
        VolatileMemoryArenaSnapshot? snapshot = backingKind == DataKernelBackingKind.Memory &&
            Volatile.Read(ref coherentMemoryReadCount) != 0
                ? GetCurrentCoherentMemorySnapshot()
                : null;
        snapshot ??= Volatile.Read(ref activeMemoryReadSnapshot);
        if (backingKind == DataKernelBackingKind.Memory && snapshot is not null)
        {
            ReadMemorySnapshot(offset, destination, snapshot);
            return;
        }

        storageSync.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            int totalRead = 0;
            while (totalRead < destination.Length)
            {
                int read = ReadPending(offset + totalRead, destination[totalRead..]);
                if (read == 0)
                {
                    read = backingKind == DataKernelBackingKind.File
                        ? ReadFile(offset + totalRead, destination[totalRead..])
                        : ReadMemory(offset + totalRead, destination[totalRead..]);
                }

                if (telemetryOptions.Enabled)
                {
                    readCallCount++;
                }

                if (read == 0)
                {
                    throw new EndOfStreamException($"Unable to read {destination.Length} bytes from offset {offset}.");
                }

                totalRead += read;
            }

            if (telemetryOptions.Enabled)
            {
                bytesRead += totalRead;
            }
        }
        finally
        {
            storageSync.ExitReadLock();
        }
    }

    /// <summary>
    /// Discards currently staged writes without publishing them to the selected backing store.<br/>
    /// This supports higher-level abort/reload flows where uncommitted durability-batch changes should be abandoned explicitly.<br/>
    /// </summary>
    public void DiscardPending()
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            ReturnPendingSegments();
            nextAppendOffset = backingKind == DataKernelBackingKind.File
                ? Math.Max(RandomAccess.GetLength(handle!), options.ReservedPrefixBytes)
                : Math.Max(GetCommittedMemoryEndOffset(), options.ReservedPrefixBytes);

            AdvanceMutationVersion();

            stagedExtentCount = 0;
            directMemoryStagedExtentCount = 0;
            directMemoryWriteCallCount = 0;
            directMemoryBytesWritten = 0;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Releases one committed extent back to the volatile memory arena when this kernel is memory backed.<br/>
    /// File-backed kernels intentionally ignore the call because durable files cannot reclaim arbitrary middle extents without a compaction pass.<br/>
    /// </summary>
    /// <param name="offset">The committed extent offset to release.</param>
    /// <param name="length">The committed extent length to release.</param>
    internal void ReleaseMemoryExtent(long offset, int length)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (backingKind != DataKernelBackingKind.Memory || length <= 0 || offset <= 0)
            {
                return;
            }

            AdvanceMutationVersion();
            memoryArena.Release(offset, length);
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Writes directly into the volatile memory arena when this kernel is memory backed.<br/>
    /// Memory-backed catalogs have no durability boundary, so dirty shelf publication does not need a temporary staged buffer before becoming readable.<br/>
    /// File-backed kernels return `false` so callers can keep using the ordinary staged commit path.<br/>
    /// </summary>
    /// <param name="offset">The arena offset to update.</param>
    /// <param name="source">The bytes to copy into the volatile arena.</param>
    /// <returns>`true` when the bytes were copied directly into memory; otherwise `false`.</returns>
    internal bool TryWriteMemoryDirect(long offset, ReadOnlySpan<byte> source)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (backingKind != DataKernelBackingKind.Memory)
            {
                return false;
            }

            if (source.Length == 0)
            {
                return true;
            }

            AdvanceMutationVersion();

            if (exclusiveStoragePublicationDepth != 0 || Volatile.Read(ref coherentMemoryReadCount) != 0)
            {
                memoryArena.WriteCopyOnWrite(offset, source);
            }
            else
            {
                memoryArena.Write(offset, source);
            }
            if (telemetryOptions.Enabled)
            {
                directMemoryStagedExtentCount++;
                directMemoryWriteCallCount++;
                directMemoryBytesWritten += source.Length;
            }

            return true;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Stages or directly publishes one copied fixed-offset write without exposing a caller-filled storage span.<br/>
    /// Memory-backed kernels copy under the storage write gate so readers never observe arena range/list mutation in progress.<br/>
    /// File-backed kernels copy into a completed pending segment under the same gate, preserving the existing commit/coalescing behavior without returning a mutable span that could be read before it is filled.<br/>
    /// </summary>
    /// <param name="offset">The absolute backing offset where <paramref name="source"/> should be published.<br/></param>
    /// <param name="source">The source bytes to copy into storage-owned memory.<br/></param>
    internal void StageWriteAt(long offset, ReadOnlySpan<byte> source)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Stage offset cannot be negative.");
            }

            if (source.Length == 0)
            {
                return;
            }

            AdvanceMutationVersion();

            if (backingKind == DataKernelBackingKind.Memory)
            {
                if (exclusiveStoragePublicationDepth != 0 || Volatile.Read(ref coherentMemoryReadCount) != 0)
                {
                    memoryArena.WriteCopyOnWrite(offset, source);
                }
                else
                {
                    memoryArena.Write(offset, source);
                }
                if (telemetryOptions.Enabled)
                {
                    directMemoryStagedExtentCount++;
                    directMemoryWriteCallCount++;
                    directMemoryBytesWritten += source.Length;
                }

                return;
            }

            if (TryGetPendingWritableSpan(offset, source.Length, out Span<byte> pendingSpan))
            {
                source.CopyTo(pendingSpan);
                return;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(source.Length);
            source.CopyTo(buffer.AsSpan(0, source.Length));
            PendingSegment segment = new(buffer, offset, source.Length, isAppend: false);
            segment.Length = source.Length;
            pendingSegments.Add(segment);
            stagedExtentCount++;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Enters an exclusive storage-publication scope that blocks raw reads while a higher layer copies a coherent set of fixed-offset writes and commits them.<br/>
    /// This is narrower than a LibraDex session writer gate: callers can still perform route planning and writer-context staging, but raw byte reads wait while the storage-owned pending/direct-write state is being published.<br/>
    /// The lock supports recursion so scoped publication can call normal copied-write and commit helpers without special unlocked variants.<br/>
    /// </summary>
    internal void EnterExclusiveStoragePublication()
    {
        storageSync.EnterWriteLock();
        exclusiveStoragePublicationDepth++;
        if (backingKind == DataKernelBackingKind.Memory && exclusiveStoragePublicationDepth == 1)
        {
            Volatile.Write(ref activeMemoryReadSnapshot, memoryArena.CaptureSnapshot());
        }
    }

    /// <summary>
    /// Enters a coherent multi-read scope for one logical cursor operation.<br/>
    /// Memory-backed kernels capture an immutable page-map snapshot and let publications continue through copy-on-write pages; file-backed kernels keep their existing per-read storage gate without retaining a thread-affine lock across caller code.<br/>
    /// Scopes are thread-affine and nest in last-in/first-out order, matching the synchronous cursor contract that consumes and disposes readers on one thread.<br/>
    /// </summary>
    /// <returns>A scope that must be disposed after the logical reader finishes.<br/></returns>
    internal CoherentReadLease EnterCoherentRead()
    {
        storageSync.EnterReadLock();
        VolatileMemoryArenaSnapshot? snapshot = null;
        bool holdsStorageReadLock = false;
        try
        {
            ThrowIfDisposed();
            if (backingKind == DataKernelBackingKind.Memory)
            {
                snapshot = memoryArena.CaptureSnapshot();
                Interlocked.Increment(ref coherentMemoryReadCount);
            }

            CoherentReadState state = new(this, snapshot, currentCoherentRead, Environment.CurrentManagedThreadId, holdsStorageReadLock);
            currentCoherentRead = state;
            storageSync.ExitReadLock();

            return new CoherentReadLease(state);
        }
        catch
        {
            if (snapshot is not null)
            {
                Interlocked.Decrement(ref coherentMemoryReadCount);
            }

            storageSync.ExitReadLock();
            throw;
        }
    }

    /// <summary>
    /// Leaves an exclusive storage-publication scope entered by <see cref="EnterExclusiveStoragePublication"/>.<br/>
    /// Callers must pair this in a finally block so raw readers are released after successful or failed publication attempts.<br/>
    /// </summary>
    internal void ExitExclusiveStoragePublication()
    {
        try
        {
            exclusiveStoragePublicationDepth--;
            if (exclusiveStoragePublicationDepth == 0)
            {
                Volatile.Write(ref activeMemoryReadSnapshot, null);
            }
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets a writable volatile arena span without creating a staged write record.<br/>
    /// Memory-backed catalogs have no durability boundary, so hot in-place RAM mutations can update already-committed shelf bytes without generating `ReserveAt` telemetry or pending commit work.<br/>
    /// File-backed kernels return <see langword="false"/> so callers keep the staged durable path.<br/>
    /// </summary>
    /// <param name="offset">The arena offset to expose.<br/></param>
    /// <param name="length">The number of writable bytes requested.<br/></param>
    /// <param name="span">Receives the writable arena span when available.<br/></param>
    /// <returns><see langword="true"/> when a direct memory span was returned; otherwise <see langword="false"/>.</returns>
    internal bool TryGetMemoryWritableSpanDirect(long offset, int length, out Span<byte> span)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            if (backingKind != DataKernelBackingKind.Memory ||
                Volatile.Read(ref coherentMemoryReadCount) != 0 ||
                length < 0)
            {
                span = Span<byte>.Empty;
                return false;
            }

            return memoryArena.TryGetWritableSpan(offset, length, out span);
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Finds the coherent memory snapshot owned by this kernel in the current thread's nested read-scope stack.<br/>
    /// The common case matches the stack head; the short walk exists for correctly nested readers opened against more than one catalog on the same thread.<br/>
    /// </summary>
    /// <returns>The active snapshot for this kernel, or <see langword="null"/> when the current thread has no coherent memory scope.<br/></returns>
    private VolatileMemoryArenaSnapshot? GetCurrentCoherentMemorySnapshot()
    {
        CoherentReadState? state = currentCoherentRead;
        while (state is not null)
        {
            if (ReferenceEquals(state.Owner, this))
            {
                return state.ActiveDepth != 0 ? state.Snapshot : null;
            }

            state = state.Previous;
        }

        return null;
    }

    /// <summary>
    /// Leaves one thread-affine coherent read scope and releases its memory snapshot count or retained file read lock.<br/>
    /// Strict last-in/first-out validation prevents a mismatched cursor disposal from silently exposing later reads to a different storage version.<br/>
    /// </summary>
    /// <param name="state">The exact state created for the lease being disposed.<br/></param>
    private void ExitCoherentRead(CoherentReadState state)
    {
        if (state.ThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("A coherent DataKernel read scope must be disposed on the thread that entered it.");
        }

        if (!ReferenceEquals(currentCoherentRead, state))
        {
            throw new InvalidOperationException("Coherent DataKernel read scopes must be disposed in last-in/first-out order.");
        }

        currentCoherentRead = state.Previous;
        if (state.HoldsStorageReadLock)
        {
            storageSync.ExitReadLock();
        }
        else if (state.Snapshot is not null)
        {
            Interlocked.Decrement(ref coherentMemoryReadCount);
        }
    }

    /// <summary>
    /// Captures the current volatile memory arena shape for workbench diagnostics.<br/>
    /// File-backed kernels return zeros because their committed bytes live outside the managed arena.<br/>
    /// </summary>
    /// <returns>A compact snapshot of committed memory arena pages, ranges, and reusable free extents.</returns>
    internal DataKernelMemoryDiagnostics GetMemoryDiagnostics()
    {
        storageSync.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return backingKind == DataKernelBackingKind.Memory
                ? memoryArena.GetDiagnostics()
                : default;
        }
        finally
        {
            storageSync.ExitReadLock();
        }
    }

    /// <summary>
    /// Advances the volatile raw-storage mutation version used by derived read-side caches.<br/>
    /// This is intentionally memory-only bookkeeping: it records that bytes may have changed or pending visibility may have changed without creating any additional durable write.<br/>
    /// </summary>
    private void AdvanceMutationVersion()
    {
        unchecked
        {
            mutationVersion++;
        }
    }

    /// <summary>
    /// Commits staged append buffers to the selected backing store.<br/>
    /// File mode uses positional writes; memory mode writes the latest staged bytes into the volatile arena.<br/>
    /// </summary>
    /// <returns>Telemetry describing the commit write shape.</returns>
    public DataKernelCommitTelemetry Commit()
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            long writeCallCount = 0;
            long backingWriteCallCount = 0;
            long setLengthCallCount = 0;
            long flushCallCount = 0;
            long bytesWritten = 0;
            long coalescedAdjacentSegmentCount = 0;
            long coalescedGapCount = 0;
            long coalescedGapBytes = 0;
            long maxCoalescedGapBytes = 0;
            long rejectedGapCount = 0;
            long rejectedGapBytes = 0;
            long maxRejectedGapBytes = 0;
            long overlapBreakCount = 0;
            long fileCommitSliceBuildTicks = 0;
            long fileCommitCoveredRangeMergeTicks = 0;
            long fileCommitGroupShapeTicks = 0;
            long fileCommitBufferBuildTicks = 0;
            long fileCommitGapReadTicks = 0;
            long fileCommitBackingWriteTicks = 0;
            long commitStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;

            if (backingKind == DataKernelBackingKind.File)
            {
                CommitFileSegments(
                    ref writeCallCount,
                    ref backingWriteCallCount,
                    ref bytesWritten,
                    ref coalescedAdjacentSegmentCount,
                    ref coalescedGapCount,
                    ref coalescedGapBytes,
                    ref maxCoalescedGapBytes,
                    ref rejectedGapCount,
                    ref rejectedGapBytes,
                    ref maxRejectedGapBytes,
                    ref overlapBreakCount,
                    ref fileCommitSliceBuildTicks,
                    ref fileCommitCoveredRangeMergeTicks,
                    ref fileCommitGroupShapeTicks,
                    ref fileCommitBufferBuildTicks,
                    ref fileCommitGapReadTicks,
                    ref fileCommitBackingWriteTicks);
            }
            else
            {
                for (int i = 0; i < pendingSegments.Count; i++)
                {
                    if (IsPendingSegmentFullyCoveredByLaterSegment(i))
                    {
                        continue;
                    }

                    PendingSegment segment = pendingSegments[i];
                    if (segment.Length == 0)
                    {
                        continue;
                    }

                    if (exclusiveStoragePublicationDepth != 0 || Volatile.Read(ref coherentMemoryReadCount) != 0)
                    {
                        memoryArena.WriteCopyOnWrite(segment.Offset, segment.Buffer.AsSpan(segment.SourceOffset, segment.Length));
                    }
                    else
                    {
                        memoryArena.Write(segment.Offset, segment.Buffer.AsSpan(segment.SourceOffset, segment.Length));
                    }

                    if (telemetryOptions.Enabled)
                    {
                        writeCallCount++;
                        backingWriteCallCount++;
                        bytesWritten += segment.Length;
                    }
                }
            }

            if (options.FlushToDiskOnCommit && pendingSegments.Count > 0 && backingKind == DataKernelBackingKind.File)
            {
                RandomAccess.FlushToDisk(handle!);
                flushCallCount = telemetryOptions.Enabled ? 1 : 0;
            }

            long elapsedTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() - commitStartTicks : 0;

            int stagedSegmentCount = pendingSegments.Count;
            ReturnPendingSegments();

            DataKernelCommitTelemetry telemetry = new(
                Level: telemetryOptions.Level,
                StagedExtentCount: telemetryOptions.Enabled ? stagedExtentCount + directMemoryStagedExtentCount : 0,
                StagedSegmentCount: telemetryOptions.Enabled ? stagedSegmentCount : 0,
                WriteCallCount: telemetryOptions.Enabled && backingKind == DataKernelBackingKind.File ? writeCallCount : 0,
                BackingWriteCallCount: telemetryOptions.Enabled ? backingWriteCallCount + directMemoryWriteCallCount : 0,
                SetLengthCallCount: setLengthCallCount,
                FlushCallCount: flushCallCount,
                BytesWritten: telemetryOptions.Enabled ? bytesWritten + directMemoryBytesWritten : 0,
                ElapsedTicks: elapsedTicks,
                CoalescedAdjacentSegmentCount: telemetryOptions.Enabled ? coalescedAdjacentSegmentCount : 0,
                CoalescedGapCount: telemetryOptions.Enabled ? coalescedGapCount : 0,
                CoalescedGapBytes: telemetryOptions.Enabled ? coalescedGapBytes : 0,
                MaxCoalescedGapBytes: telemetryOptions.Enabled ? maxCoalescedGapBytes : 0,
                RejectedGapCount: telemetryOptions.Enabled ? rejectedGapCount : 0,
                RejectedGapBytes: telemetryOptions.Enabled ? rejectedGapBytes : 0,
                MaxRejectedGapBytes: telemetryOptions.Enabled ? maxRejectedGapBytes : 0,
                OverlapBreakCount: telemetryOptions.Enabled ? overlapBreakCount : 0,
                FileCommitSliceBuildTicks: telemetryOptions.Enabled ? fileCommitSliceBuildTicks : 0,
                FileCommitCoveredRangeMergeTicks: telemetryOptions.Enabled ? fileCommitCoveredRangeMergeTicks : 0,
                FileCommitGroupShapeTicks: telemetryOptions.Enabled ? fileCommitGroupShapeTicks : 0,
                FileCommitBufferBuildTicks: telemetryOptions.Enabled ? fileCommitBufferBuildTicks : 0,
                FileCommitGapReadTicks: telemetryOptions.Enabled ? fileCommitGapReadTicks : 0,
                FileCommitBackingWriteTicks: telemetryOptions.Enabled ? fileCommitBackingWriteTicks : 0);

            stagedExtentCount = 0;
            directMemoryStagedExtentCount = 0;
            directMemoryWriteCallCount = 0;
            directMemoryBytesWritten = 0;
            return telemetry;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Commits pending file-backed segments while coalescing adjacent or near-adjacent staged segments and skipping older segments that are fully superseded by later staged writes.<br/>
    /// The method preserves final pending-read semantics: when a later staged segment completely covers an earlier byte range, only the later bytes need durable publication.<br/>
    /// Near-adjacent coalescing bridges small unchanged gaps by copying the current gap bytes into the committed write, trading a small byte increase for fewer positional write calls.<br/>
    /// This keeps durability batches from writing every intermediate version of a dirty shelf while still allowing adjacent router/shelf writes to publish with vectored positional I/O.<br/>
    /// </summary>
    /// <param name="writeCallCount">The accumulated public file write call count.</param>
    /// <param name="backingWriteCallCount">The accumulated backing write call count.</param>
    /// <param name="bytesWritten">The accumulated committed byte count.</param>
    private void CommitFileSegments(
        ref long writeCallCount,
        ref long backingWriteCallCount,
        ref long bytesWritten,
        ref long coalescedAdjacentSegmentCount,
        ref long coalescedGapCount,
        ref long coalescedGapBytes,
        ref long maxCoalescedGapBytes,
        ref long rejectedGapCount,
        ref long rejectedGapBytes,
        ref long maxRejectedGapBytes,
        ref long overlapBreakCount,
        ref long fileCommitSliceBuildTicks,
        ref long fileCommitCoveredRangeMergeTicks,
        ref long fileCommitGroupShapeTicks,
        ref long fileCommitBufferBuildTicks,
        ref long fileCommitGapReadTicks,
        ref long fileCommitBackingWriteTicks)
    {
        long sliceBuildStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
        List<CommitSlice> commitSlices = CreateFileCommitSlices(ref fileCommitCoveredRangeMergeTicks);
        if (telemetryOptions.Enabled)
        {
            fileCommitSliceBuildTicks += Stopwatch.GetTimestamp() - sliceBuildStartTicks;
        }

        int[]? rentedGroupGapBytes = null;
        Span<int> stackGroupGapBytes = commitSlices.Count <= 128 ? stackalloc int[commitSlices.Count] : default;
        Span<int> groupGapBytesBuffer = stackGroupGapBytes.Length != 0
            ? stackGroupGapBytes
            : (rentedGroupGapBytes = ArrayPool<int>.Shared.Rent(commitSlices.Count));
        try
        {
            for (int i = 0; i < commitSlices.Count; i++)
            {
                long groupShapeStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
                CommitSlice first = commitSlices[i];
                long groupEnd = first.Offset + first.Length;
                int groupCount = 1;
                long groupBytes = first.Length;
                for (int j = i + 1; j < commitSlices.Count; j++)
                {
                    CommitSlice next = commitSlices[j];
                    long gapBytes = next.Offset - groupEnd;
                    if (gapBytes < 0)
                    {
                        overlapBreakCount++;
                        break;
                    }

                    if (gapBytes > 0 && !CanCoalesceCommitGap(first, next, gapBytes))
                    {
                        rejectedGapCount++;
                        rejectedGapBytes += gapBytes;
                        maxRejectedGapBytes = Math.Max(maxRejectedGapBytes, gapBytes);
                        break;
                    }

                    groupGapBytesBuffer[groupCount - 1] = checked((int)gapBytes);
                    groupCount++;
                    if (gapBytes == 0)
                    {
                        coalescedAdjacentSegmentCount++;
                    }
                    else
                    {
                        coalescedGapCount++;
                        coalescedGapBytes += gapBytes;
                        maxCoalescedGapBytes = Math.Max(maxCoalescedGapBytes, gapBytes);
                    }

                    groupEnd = next.Offset + next.Length;
                    groupBytes += gapBytes + next.Length;
                }

                if (telemetryOptions.Enabled)
                {
                    fileCommitGroupShapeTicks += Stopwatch.GetTimestamp() - groupShapeStartTicks;
                }

                PendingSegment firstSegment = pendingSegments[first.SegmentIndex];
                long backingWriteStartTicks;
                if (groupCount == 1)
                {
                    backingWriteStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
                    RandomAccess.Write(handle!, firstSegment.Buffer.AsSpan(firstSegment.SourceOffset + first.BufferOffset, first.Length), first.Offset);
                    if (telemetryOptions.Enabled)
                    {
                        fileCommitBackingWriteTicks += Stopwatch.GetTimestamp() - backingWriteStartTicks;
                    }
                }
                else
                {
                    byte[]?[]? gapBuffers = null;
                    ReadOnlyMemory<byte>[]? rentedBuffers = null;
                    long bufferBuildStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
                    ReadOnlyMemory<byte>[] buffers = CreateCommitGroupBuffers(commitSlices, i, groupCount, groupGapBytesBuffer, ref fileCommitGapReadTicks, out gapBuffers, out int gapBufferCount, out rentedBuffers);
                    if (telemetryOptions.Enabled)
                    {
                        fileCommitBufferBuildTicks += Stopwatch.GetTimestamp() - bufferBuildStartTicks;
                    }

                    try
                    {
                        backingWriteStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
                        RandomAccess.Write(handle!, buffers, first.Offset);
                        if (telemetryOptions.Enabled)
                        {
                            fileCommitBackingWriteTicks += Stopwatch.GetTimestamp() - backingWriteStartTicks;
                        }
                    }
                    finally
                    {
                        ReturnCommitGapBuffers(gapBuffers, gapBufferCount);
                        if (rentedBuffers is not null)
                        {
                            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(rentedBuffers, clearArray: true);
                        }
                    }

                    i += groupCount - 1;
                }

                if (telemetryOptions.Enabled)
                {
                    writeCallCount++;
                    backingWriteCallCount++;
                    bytesWritten += groupBytes;
                }
            }
        }
        finally
        {
            if (rentedGroupGapBytes is not null)
            {
                ArrayPool<int>.Shared.Return(rentedGroupGapBytes);
            }
        }
    }

    private List<CommitSlice> CreateFileCommitSlices(ref long coveredRangeMergeTicks)
    {
        fileCommitSlices.Clear();
        coveredRanges.Clear();
        EnsureListCapacity(fileCommitSlices, pendingSegments.Count);
        EnsureListCapacity(coveredRanges, pendingSegments.Count);
        for (int segmentIndex = pendingSegments.Count - 1; segmentIndex >= 0; segmentIndex--)
        {
            PendingSegment segment = pendingSegments[segmentIndex];
            if (segment.Length == 0)
            {
                continue;
            }

            long segmentEnd = segment.Offset + segment.Length;
            int low = 0;
            int high = coveredRanges.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (coveredRanges[middle].EndOffset <= segment.Offset)
                    low = middle + 1;
                else
                    high = middle;
            }

            long uncoveredStart = segment.Offset;
            for (int coveredIndex = low; coveredIndex < coveredRanges.Count; coveredIndex++)
            {
                CommittedRange covered = coveredRanges[coveredIndex];
                if (covered.StartOffset >= segmentEnd)
                    break;

                if (covered.StartOffset > uncoveredStart)
                {
                    long uncoveredEnd = Math.Min(covered.StartOffset, segmentEnd);
                    int bufferOffset = checked((int)(uncoveredStart - segment.Offset));
                    int length = checked((int)(uncoveredEnd - uncoveredStart));
                    fileCommitSlices.Add(new CommitSlice(segmentIndex, uncoveredStart, bufferOffset, length, segment.IsAppend));
                }

                uncoveredStart = Math.Max(uncoveredStart, covered.EndOffset);
                if (uncoveredStart >= segmentEnd)
                    break;
            }

            if (uncoveredStart < segmentEnd)
            {
                int bufferOffset = checked((int)(uncoveredStart - segment.Offset));
                int length = checked((int)(segmentEnd - uncoveredStart));
                fileCommitSlices.Add(new CommitSlice(segmentIndex, uncoveredStart, bufferOffset, length, segment.IsAppend));
            }

            long coveredRangeMergeStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
            AddCoveredRange(coveredRanges, new CommittedRange(segment.Offset, segment.Offset + segment.Length));
            if (telemetryOptions.Enabled)
            {
                coveredRangeMergeTicks += Stopwatch.GetTimestamp() - coveredRangeMergeStartTicks;
            }
        }

        fileCommitSlices.Sort(CommitSliceComparer.Instance);
        return fileCommitSlices;
    }

    /// <summary>
    /// Ensures a reusable hot-path list has enough capacity before the loop that will fill it.<br/>
    /// This keeps necessary `List&lt;T&gt;` use from paying repeated geometric growth costs during commit planning.<br/>
    /// </summary>
    /// <typeparam name="T">The list element type.</typeparam>
    /// <param name="list">The reusable list to size.</param>
    /// <param name="capacity">The minimum capacity needed for the upcoming operation.</param>
    private static void EnsureListCapacity<T>(List<T> list, int capacity)
    {
        if (list.Capacity < capacity)
        {
            list.Capacity = capacity;
        }
    }

    private static void SubtractCoveredRange(List<CommittedRange> ranges, CommittedRange covered)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            CommittedRange current = ranges[i];
            if (covered.EndOffset <= current.StartOffset || covered.StartOffset >= current.EndOffset)
            {
                continue;
            }

            ranges.RemoveAt(i);
            if (covered.EndOffset < current.EndOffset)
            {
                EnsureListCapacity(ranges, ranges.Count + 1);
                ranges.Insert(i, new CommittedRange(covered.EndOffset, current.EndOffset));
            }

            if (covered.StartOffset > current.StartOffset)
            {
                EnsureListCapacity(ranges, ranges.Count + 1);
                ranges.Insert(i, new CommittedRange(current.StartOffset, covered.StartOffset));
            }
        }
    }

    private static void AddCoveredRange(List<CommittedRange> ranges, CommittedRange range)
    {
        int low = 0;
        int high = ranges.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (ranges[middle].EndOffset < range.StartOffset)
                low = middle + 1;
            else
                high = middle;
        }

        int insertIndex = low;
        long startOffset = range.StartOffset;
        long endOffset = range.EndOffset;
        int mergeEnd = insertIndex;
        while (mergeEnd < ranges.Count && ranges[mergeEnd].StartOffset <= endOffset)
        {
            CommittedRange current = ranges[mergeEnd];
            startOffset = Math.Min(startOffset, current.StartOffset);
            endOffset = Math.Max(endOffset, current.EndOffset);
            mergeEnd++;
        }

        CommittedRange merged = new(startOffset, endOffset);
        if (mergeEnd == insertIndex)
        {
            EnsureListCapacity(ranges, ranges.Count + 1);
            ranges.Insert(insertIndex, merged);
            return;
        }

        ranges[insertIndex] = merged;
        int removeCount = mergeEnd - insertIndex - 1;
        if (removeCount > 0)
            ranges.RemoveRange(insertIndex + 1, removeCount);
    }

    private bool CanCoalesceCommitGap(CommitSlice first, CommitSlice next, long gapBytes)
    {
        return gapBytes <= options.MaxCommitGapCoalesceBytes &&
            gapBytes <= int.MaxValue &&
            !first.IsAppend &&
            !next.IsAppend;
    }

    private ReadOnlyMemory<byte>[] CreateCommitGroupBuffers(
        List<CommitSlice> commitSlices,
        int groupStartIndex,
        int groupCount,
        ReadOnlySpan<int> groupGapBytes,
        ref long gapReadTicks,
        out byte[]?[]? gapBuffers,
        out int gapBufferCount,
        out ReadOnlyMemory<byte>[]? rentedBuffers)
    {
        int positiveGapCount = 0;
        for (int i = 0; i < groupCount - 1; i++)
        {
            positiveGapCount += groupGapBytes[i] > 0 ? 1 : 0;
        }

        gapBufferCount = 0;
        gapBuffers = positiveGapCount == 0 ? null : ArrayPool<byte[]?>.Shared.Rent(positiveGapCount);
        rentedBuffers = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(groupCount + positiveGapCount);
        ReadOnlyMemory<byte>[] buffers = rentedBuffers;
        int bufferIndex = 0;
        CommitSlice first = commitSlices[groupStartIndex];
        PendingSegment firstSegment = pendingSegments[first.SegmentIndex];
        buffers[bufferIndex++] = firstSegment.Buffer.AsMemory(firstSegment.SourceOffset + first.BufferOffset, first.Length);
        long previousEnd = first.Offset + first.Length;

        for (int groupIndex = 1; groupIndex < groupCount; groupIndex++)
        {
            int gapLength = groupGapBytes[groupIndex - 1];
            if (gapLength > 0)
            {
                byte[] gapBuffer = ArrayPool<byte>.Shared.Rent(gapLength);
                long gapReadStartTicks = telemetryOptions.Enabled ? Stopwatch.GetTimestamp() : 0;
                bool copiedGap = TryCopyCurrentBytesForCommitGap(previousEnd, gapBuffer.AsSpan(0, gapLength));
                if (telemetryOptions.Enabled)
                {
                    gapReadTicks += Stopwatch.GetTimestamp() - gapReadStartTicks;
                }

                if (!copiedGap)
                {
                    ArrayPool<byte>.Shared.Return(gapBuffer);
                    ReturnCommitGapBuffers(gapBuffers, gapBufferCount);
                    throw new EndOfStreamException($"Unable to read {gapLength} commit coalescing gap bytes from offset {previousEnd}.");
                }

                gapBuffers![gapBufferCount++] = gapBuffer;
                buffers[bufferIndex++] = gapBuffer.AsMemory(0, gapLength);
            }

            CommitSlice slice = commitSlices[groupStartIndex + groupIndex];
            PendingSegment segment = pendingSegments[slice.SegmentIndex];
            buffers[bufferIndex++] = segment.Buffer.AsMemory(segment.SourceOffset + slice.BufferOffset, slice.Length);
            previousEnd = slice.Offset + slice.Length;
        }

        for (int i = bufferIndex; i < buffers.Length; i++)
        {
            buffers[i] = default;
        }

        return buffers;
    }

    private bool TryCopyCurrentBytesForCommitGap(long offset, Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = RandomAccess.Read(handle!, destination[totalRead..], offset + totalRead);

            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private static void ReturnCommitGapBuffers(byte[]?[]? gapBuffers, int gapBufferCount)
    {
        if (gapBuffers is null)
        {
            return;
        }

        for (int i = 0; i < gapBufferCount; i++)
        {
            byte[]? gapBuffer = gapBuffers[i];
            if (gapBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(gapBuffer);
            }
        }

        ArrayPool<byte[]?>.Shared.Return(gapBuffers, clearArray: true);
    }

    private bool IsCommittablePendingSegment(int index)
    {
        PendingSegment segment = pendingSegments[index];
        return segment.Length != 0 && !IsPendingSegmentFullyCoveredByLaterSegment(index);
    }

    private bool IsPendingSegmentFullyCoveredByLaterSegment(int index)
    {
        PendingSegment segment = pendingSegments[index];
        if (segment.Length == 0)
        {
            return false;
        }

        long segmentEnd = segment.Offset + segment.Length;
        for (int i = pendingSegments.Count - 1; i > index; i--)
        {
            PendingSegment later = pendingSegments[i];
            if (later.Length == 0)
            {
                continue;
            }

            long laterEnd = later.Offset + later.Length;
            if (later.Offset <= segment.Offset && laterEnd >= segmentEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets and resets accumulated raw read telemetry.<br/>
    /// This keeps read counters cheap and explicit for harness baselines.<br/>
    /// </summary>
    /// <returns>The accumulated read telemetry since the previous reset.</returns>
    public DataKernelReadTelemetry GetAndResetReadTelemetry()
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            DataKernelReadTelemetry telemetry = new(
                TelemetryEnabled: telemetryOptions.Enabled,
                ReadCallCount: telemetryOptions.Enabled && backingKind == DataKernelBackingKind.File ? readCallCount : 0,
                BackingReadCallCount: telemetryOptions.Enabled ? backingReadCallCount : 0,
                BytesRead: telemetryOptions.Enabled ? bytesRead : 0);

            readCallCount = 0;
            backingReadCallCount = 0;
            bytesRead = 0;
            return telemetry;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    /// <summary>
    /// Releases staged buffers, committed memory buffers, and any file handle.<br/>
    /// Uncommitted staged data is discarded by design for this raw development slice.<br/>
    /// </summary>
    public void Dispose()
    {
        storageSync.EnterWriteLock();
        try
        {
            if (disposed)
            {
                return;
            }

            ReturnPendingSegments();
            ReturnMemorySegments();
            handle?.Dispose();
            disposed = true;
        }
        finally
        {
            storageSync.ExitWriteLock();
        }
    }

    private int ReadFile(long offset, Span<byte> destination)
    {
        int read = RandomAccess.Read(handle!, destination, offset);
        if (telemetryOptions.Enabled)
        {
            backingReadCallCount++;
        }

        return read;
    }

    private int ReadMemory(long offset, Span<byte> destination)
    {
        int readable = memoryArena.Read(offset, destination);
        if (telemetryOptions.Enabled)
        {
            backingReadCallCount += readable == 0 ? 0 : 1;
        }

        return readable;
    }

    private void ReadMemorySnapshot(
        long offset,
        Span<byte> destination,
        VolatileMemoryArenaSnapshot snapshot)
    {
        ThrowIfDisposed();

        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = snapshot.Read(offset + totalRead, destination[totalRead..]);
            if (telemetryOptions.Enabled && read != 0)
            {
                Interlocked.Increment(ref backingReadCallCount);
            }

            if (telemetryOptions.Enabled)
            {
                Interlocked.Increment(ref readCallCount);
            }

            if (read == 0)
            {
                throw new EndOfStreamException($"Unable to read {destination.Length} bytes from offset {offset}.");
            }

            totalRead += read;
        }

        if (telemetryOptions.Enabled)
        {
            Interlocked.Add(ref bytesRead, totalRead);
        }
    }

    private int ReadPending(long offset, Span<byte> destination)
    {
        for (int i = pendingSegments.Count - 1; i >= 0; i--)
        {
            PendingSegment segment = pendingSegments[i];
            long segmentEnd = segment.Offset + segment.Length;
            if (offset >= segment.Offset && offset < segmentEnd)
            {
                int sourceOffset = checked((int)(offset - segment.Offset));
                int readable = Math.Min(destination.Length, segment.Length - sourceOffset);
                segment.Buffer.AsSpan(segment.SourceOffset + sourceOffset, readable).CopyTo(destination);
                return readable;
            }
        }

        return 0;
    }

    /// <summary>
    /// Returns a writable view over the latest staged segment that fully owns a requested fixed-offset range.<br/>
    /// The scan walks from newest to oldest and refuses reuse if a newer segment partially overlaps the requested range, preserving normal later-write-wins commit/read semantics.<br/>
    /// This avoids renting another buffer when a durability batch rewrites the same shelf or router offset repeatedly before commit.<br/>
    /// </summary>
    /// <param name="offset">The absolute backing offset requested for update.<br/></param>
    /// <param name="length">The byte count requested for update.<br/></param>
    /// <param name="span">Receives the writable staged span when one segment fully owns the requested range.<br/></param>
    /// <returns><see langword="true"/> when the pending range can be updated in place; otherwise <see langword="false"/>.<br/></returns>
    private bool TryGetPendingWritableSpan(long offset, int length, out Span<byte> span)
    {
        long endOffset = offset + length;
        for (int i = pendingSegments.Count - 1; i >= 0; i--)
        {
            PendingSegment segment = pendingSegments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            long segmentEnd = segment.Offset + segment.Length;
            bool overlaps = offset < segmentEnd && endOffset > segment.Offset;
            if (!overlaps)
            {
                continue;
            }

            if (offset >= segment.Offset && endOffset <= segmentEnd)
            {
                int sourceOffset = checked((int)(offset - segment.Offset));
                if (!segment.ReturnsBuffer)
                {
                    span = Span<byte>.Empty;
                    return false;
                }

                span = segment.Buffer.AsSpan(segment.SourceOffset + sourceOffset, length);
                return true;
            }

            span = Span<byte>.Empty;
            return false;
        }

        span = Span<byte>.Empty;
        return false;
    }

    private PendingSegment GetAppendSegment(int remaining)
    {
        PendingSegment? current = pendingSegments.Count == 0 ? null : pendingSegments[^1];
        if (current is not null &&
            current.IsAppend &&
            current.Offset + current.Length == nextAppendOffset &&
            current.Length < current.Capacity)
        {
            return current;
        }

        int capacity = remaining >= options.AppendBufferSize
            ? remaining
            : options.AppendBufferSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        PendingSegment segment = new(buffer, nextAppendOffset, capacity, isAppend: true);
        pendingSegments.Add(segment);
        return segment;
    }

    private PendingSegment GetContiguousAppendSegment(int length)
    {
        PendingSegment? current = pendingSegments.Count == 0 ? null : pendingSegments[^1];
        if (current is not null &&
            current.IsAppend &&
            current.Offset + current.Length == nextAppendOffset &&
            current.Capacity - current.Length >= length)
        {
            return current;
        }

        int capacity = length >= options.AppendBufferSize
            ? length
            : options.AppendBufferSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        PendingSegment segment = new(buffer, nextAppendOffset, capacity, isAppend: true);
        pendingSegments.Add(segment);
        return segment;
    }

    private void ReturnPendingSegments()
    {
        foreach (PendingSegment segment in pendingSegments)
        {
            if (segment.ReturnsBuffer)
            {
                ArrayPool<byte>.Shared.Return(segment.Buffer);
            }
        }

        pendingSegments.Clear();
    }

    private void ReturnMemorySegments()
    {
        memoryArena.Clear();
    }

    private long GetCommittedMemoryEndOffset()
    {
        return Math.Max(options.ReservedPrefixBytes, memoryArena.EndOffset);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PendingSegment
    {
        public PendingSegment(byte[] buffer, long offset, int capacity, bool isAppend)
            : this(buffer, 0, offset, capacity, isAppend, returnsBuffer: true)
        {
        }

        /// <summary>
        /// Creates a staged segment over either a kernel-rented buffer or a caller-owned borrowed slice.<br/>
        /// The explicit source offset lets commit slices reference a region of a larger retained shelf image without copying it.<br/>
        /// </summary>
        /// <param name="buffer">The byte array containing the staged data.<br/></param>
        /// <param name="sourceOffset">The first staged byte inside <paramref name="buffer"/>.<br/></param>
        /// <param name="offset">The absolute target offset in the backing store.<br/></param>
        /// <param name="capacity">The number of source bytes available to the segment.<br/></param>
        /// <param name="isAppend">Whether the segment advances the append cursor.<br/></param>
        /// <param name="returnsBuffer">Whether cleanup returns <paramref name="buffer"/> to the shared array pool.<br/></param>
        public PendingSegment(byte[] buffer, int sourceOffset, long offset, int capacity, bool isAppend, bool returnsBuffer)
        {
            Buffer = buffer;
            SourceOffset = sourceOffset;
            Offset = offset;
            Capacity = capacity;
            IsAppend = isAppend;
            ReturnsBuffer = returnsBuffer;
        }

        public byte[] Buffer { get; }

        public int SourceOffset { get; }

        public long Offset { get; }

        public int Capacity { get; }

        public int Length { get; set; }

        public bool IsAppend { get; }

        public bool ReturnsBuffer { get; }
    }

    private readonly record struct CommitSlice(
        int SegmentIndex,
        long Offset,
        int BufferOffset,
        int Length,
        bool IsAppend);

    private sealed class CommitSliceComparer : IComparer<CommitSlice>
    {
        public static readonly CommitSliceComparer Instance = new();

        private CommitSliceComparer()
        {
        }

        public int Compare(CommitSlice left, CommitSlice right)
        {
            int offsetComparison = left.Offset.CompareTo(right.Offset);
            return offsetComparison != 0 ? offsetComparison : left.SegmentIndex.CompareTo(right.SegmentIndex);
        }
    }

    internal readonly record struct CommittedRange(long StartOffset, long EndOffset);

    internal sealed class CoherentReadState
    {
        internal CoherentReadState(
            DataKernel owner,
            VolatileMemoryArenaSnapshot? snapshot,
            CoherentReadState? previous,
            int threadId,
            bool holdsStorageReadLock)
        {
            Owner = owner;
            Snapshot = snapshot;
            Previous = previous;
            ThreadId = threadId;
            HoldsStorageReadLock = holdsStorageReadLock;
        }

        internal DataKernel Owner { get; }

        internal VolatileMemoryArenaSnapshot? Snapshot { get; }

        internal CoherentReadState? Previous { get; }

        internal int ThreadId { get; }

        internal bool HoldsStorageReadLock { get; }

        internal int ActiveDepth { get; set; } = 1;
    }

    internal sealed class CoherentReadLease : IDisposable
    {
        private CoherentReadState? state;

        internal CoherentReadLease(CoherentReadState state)
        {
            this.state = state;
        }

        /// <summary>
        /// Pauses ambient use of this cursor snapshot after the concrete reader has been constructed.<br/>
        /// The snapshot remains retained for later cursor calls, while unrelated same-thread index operations read the current publication.<br/>
        /// </summary>
        internal void Pause()
        {
            CoherentReadState current = state ?? throw new ObjectDisposedException(nameof(CoherentReadLease));
            if (current.ThreadId != Environment.CurrentManagedThreadId || current.ActiveDepth != 1)
            {
                throw new InvalidOperationException("A coherent read snapshot can be paused only once on its owning thread after reader construction.");
            }

            current.ActiveDepth = 0;
        }

        /// <summary>
        /// Activates this retained snapshot for one concrete cursor call.<br/>
        /// The returned value is allocation-free and restores the inactive state when disposed.<br/>
        /// </summary>
        /// <returns>A stack-only-style value that ends the cursor-call activation when disposed.<br/></returns>
        internal CoherentReadUse Use()
        {
            CoherentReadState current = state ?? throw new ObjectDisposedException(nameof(CoherentReadLease));
            if (current.ThreadId != Environment.CurrentManagedThreadId || current.ActiveDepth < 0)
            {
                throw new InvalidOperationException("A coherent read snapshot can be activated only by its owning thread.");
            }

            current.ActiveDepth++;
            return new CoherentReadUse(current);
        }

        public void Dispose()
        {
            CoherentReadState? current = Interlocked.Exchange(ref state, null);
            current?.Owner.ExitCoherentRead(current);
        }
    }

    internal readonly struct CoherentReadUse : IDisposable
    {
        private readonly CoherentReadState? state;

        /// <summary>
        /// Creates one allocation-free cursor-call activation.<br/>
        /// </summary>
        /// <param name="state">The retained coherent state activated for the call.<br/></param>
        internal CoherentReadUse(CoherentReadState state)
        {
            this.state = state;
        }

        /// <summary>
        /// Restores the retained cursor snapshot to its inactive between-call state.<br/>
        /// </summary>
        public void Dispose()
        {
            if (state is not null)
            {
                state.ActiveDepth--;
            }
        }
    }

    internal sealed class VolatileMemoryArenaSnapshot
    {
        private readonly Dictionary<long, byte[]> pages;
        private readonly CommittedRange[] ranges;
        private readonly int pageSize;
        private readonly int pageShift;
        private readonly int pageMask;

        public VolatileMemoryArenaSnapshot(
            Dictionary<long, byte[]> pages,
            CommittedRange[] ranges,
            int pageSize,
            int pageShift,
            int pageMask)
        {
            this.pages = pages;
            this.ranges = ranges;
            this.pageSize = pageSize;
            this.pageShift = pageShift;
            this.pageMask = pageMask;
        }

        public int Read(long offset, Span<byte> destination)
        {
            int rangeIndex = FindRangeIndex(offset);
            if (rangeIndex < 0)
            {
                return 0;
            }

            long pageIndex = offset >> pageShift;
            if (!pages.TryGetValue(pageIndex, out byte[]? page))
            {
                return 0;
            }

            int pageOffset = checked((int)(offset & pageMask));
            CommittedRange range = ranges[rangeIndex];
            int rangeReadable = checked((int)Math.Min(destination.Length, range.EndOffset - offset));
            int readable = Math.Min(rangeReadable, pageSize - pageOffset);
            page.AsSpan(pageOffset, readable).CopyTo(destination);
            return readable;
        }

        private int FindRangeIndex(long offset)
        {
            int low = 0;
            int high = ranges.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                CommittedRange range = ranges[middle];
                if (offset < range.StartOffset)
                {
                    high = middle - 1;
                    continue;
                }

                if (offset >= range.EndOffset)
                {
                    low = middle + 1;
                    continue;
                }

                return middle;
            }

            return -1;
        }
    }

    private sealed class VolatileMemoryArena
    {
        private readonly Dictionary<long, byte[]> pages = new();
        private readonly List<CommittedRange> ranges = new();
        private readonly List<RawDataExtent> freeExtents = new();
        private readonly int pageSize;
        private readonly int pageShift;
        private readonly int pageMask;
        private long endOffset;

        public VolatileMemoryArena(int pageSize)
        {
            if (pageSize <= 0 || (pageSize & (pageSize - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Memory page size must be a positive power of two.");
            }

            this.pageSize = pageSize;
            pageShift = BitOperations.Log2((uint)pageSize);
            pageMask = pageSize - 1;
        }

        public long EndOffset => endOffset;

        public VolatileMemoryArenaSnapshot CaptureSnapshot()
        {
            return new VolatileMemoryArenaSnapshot(
                new Dictionary<long, byte[]>(pages),
                ranges.ToArray(),
                pageSize,
                pageShift,
                pageMask);
        }

        public bool TryReserveAppend(int length, ref long nextOffset, out long offset, out Span<byte> span)
        {
            if (length < 0 || length > pageSize)
            {
                offset = 0;
                span = default;
                return false;
            }

            long writeOffset = nextOffset;
            int pageOffset = checked((int)(writeOffset & pageMask));
            if (pageOffset + length > pageSize)
            {
                writeOffset = checked(((writeOffset >> pageShift) + 1) << pageShift);
                pageOffset = 0;
            }

            long pageIndex = writeOffset >> pageShift;
            byte[] page = GetOrCreatePage(pageIndex);
            offset = writeOffset;
            span = page.AsSpan(pageOffset, length);
            nextOffset = writeOffset + length;
            AddRange(offset, offset + length);
            endOffset = Math.Max(endOffset, nextOffset);
            return true;
        }

        public bool TryGetWritableSpan(long offset, int length, out Span<byte> span)
        {
            if (length < 0 || length > pageSize)
            {
                span = default;
                return false;
            }

            int pageOffset = checked((int)(offset & pageMask));
            if (pageOffset + length > pageSize)
            {
                span = default;
                return false;
            }

            long pageIndex = offset >> pageShift;
            byte[] page = GetOrCreatePage(pageIndex);
            span = page.AsSpan(pageOffset, length);
            AddRange(offset, offset + length);
            endOffset = Math.Max(endOffset, offset + length);
            return true;
        }

        public bool TryReserveReleased(int length, out long offset)
        {
            for (int i = 0; i < freeExtents.Count; i++)
            {
                RawDataExtent extent = freeExtents[i];
                if (extent.Length < length || !CanCreateSingleSpan(extent.Offset, length))
                {
                    continue;
                }

                offset = extent.Offset;
                if (extent.Length == length)
                {
                    freeExtents.RemoveAt(i);
                }
                else
                {
                    freeExtents[i] = new RawDataExtent(extent.Offset + length, extent.Length - length);
                }

                return true;
            }

            offset = 0;
            return false;
        }

        private bool CanCreateSingleSpan(long offset, int length)
        {
            if (length < 0 || length > pageSize)
            {
                return false;
            }

            int pageOffset = checked((int)(offset & pageMask));
            return pageOffset + length <= pageSize;
        }

        public void Write(long offset, ReadOnlySpan<byte> source)
        {
            int sourceOffset = 0;
            int remaining = source.Length;
            long writeOffset = offset;
            while (remaining > 0)
            {
                long pageIndex = writeOffset >> pageShift;
                int pageOffset = checked((int)(writeOffset & pageMask));
                int writable = Math.Min(remaining, pageSize - pageOffset);
                byte[] page = GetOrCreatePage(pageIndex);
                source.Slice(sourceOffset, writable).CopyTo(page.AsSpan(pageOffset, writable));
                sourceOffset += writable;
                remaining -= writable;
                writeOffset += writable;
            }

            long writeEndOffset = offset + source.Length;
            AddRange(offset, writeEndOffset);
            endOffset = Math.Max(endOffset, writeEndOffset);
        }

        public void WriteCopyOnWrite(long offset, ReadOnlySpan<byte> source)
        {
            int sourceOffset = 0;
            int remaining = source.Length;
            long writeOffset = offset;
            while (remaining > 0)
            {
                long pageIndex = writeOffset >> pageShift;
                int pageOffset = checked((int)(writeOffset & pageMask));
                int writable = Math.Min(remaining, pageSize - pageOffset);
                byte[] page = GetOrCreateWritablePageCopy(pageIndex);
                source.Slice(sourceOffset, writable).CopyTo(page.AsSpan(pageOffset, writable));
                sourceOffset += writable;
                remaining -= writable;
                writeOffset += writable;
            }

            long writeEndOffset = offset + source.Length;
            AddRange(offset, writeEndOffset);
            endOffset = Math.Max(endOffset, writeEndOffset);
        }

        public int Read(long offset, Span<byte> destination)
        {
            int rangeIndex = FindRangeIndex(offset);
            if (rangeIndex < 0)
            {
                return 0;
            }

            long pageIndex = offset >> pageShift;
            if (!pages.TryGetValue(pageIndex, out byte[]? page))
            {
                return 0;
            }

            int pageOffset = checked((int)(offset & pageMask));
            CommittedRange range = ranges[rangeIndex];
            int rangeReadable = checked((int)Math.Min(destination.Length, range.EndOffset - offset));
            int readable = Math.Min(rangeReadable, pageSize - pageOffset);
            page.AsSpan(pageOffset, readable).CopyTo(destination);
            return readable;
        }

        public void Release(long offset, int length)
        {
            long end = offset + length;
            RemoveRange(offset, end);
            AddFreeExtent(offset, length);
            ReleaseUnusedPages(offset, end);
        }

        public void Clear()
        {
            pages.Clear();
            ranges.Clear();
            freeExtents.Clear();
            endOffset = 0;
        }

        public DataKernelMemoryDiagnostics GetDiagnostics()
        {
            long liveBytes = 0;
            for (int i = 0; i < ranges.Count; i++)
            {
                liveBytes += ranges[i].EndOffset - ranges[i].StartOffset;
            }

            long freeBytes = 0;
            for (int i = 0; i < freeExtents.Count; i++)
            {
                freeBytes += freeExtents[i].Length;
            }

            return new DataKernelMemoryDiagnostics(
                PageCount: pages.Count,
                PageSize: pageSize,
                ArenaBytes: (long)pages.Count * pageSize,
                LiveRangeCount: ranges.Count,
                LiveBytes: liveBytes,
                FreeExtentCount: freeExtents.Count,
                FreeBytes: freeBytes,
                EndOffset: endOffset);
        }

        private byte[] GetOrCreatePage(long pageIndex)
        {
            if (pages.TryGetValue(pageIndex, out byte[]? page))
            {
                return page;
            }

            page = GC.AllocateUninitializedArray<byte>(pageSize);
            pages.Add(pageIndex, page);
            return page;
        }

        private byte[] GetOrCreateWritablePageCopy(long pageIndex)
        {
            byte[] page = GC.AllocateUninitializedArray<byte>(pageSize);
            if (pages.TryGetValue(pageIndex, out byte[]? current))
            {
                current.AsSpan(0, pageSize).CopyTo(page);
                pages[pageIndex] = page;
            }
            else
            {
                page.AsSpan(0, pageSize).Clear();
                pages.Add(pageIndex, page);
            }

            return page;
        }

        private void AddRange(long startOffset, long endOffset)
        {
            int insertIndex = 0;
            while (insertIndex < ranges.Count && ranges[insertIndex].EndOffset < startOffset)
            {
                insertIndex++;
            }

            while (insertIndex < ranges.Count && ranges[insertIndex].StartOffset <= endOffset)
            {
                CommittedRange current = ranges[insertIndex];
                startOffset = Math.Min(startOffset, current.StartOffset);
                endOffset = Math.Max(endOffset, current.EndOffset);
                ranges.RemoveAt(insertIndex);
            }

            ranges.Insert(insertIndex, new CommittedRange(startOffset, endOffset));
        }

        private void RemoveRange(long startOffset, long endOffset)
        {
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                CommittedRange range = ranges[i];
                if (endOffset <= range.StartOffset || startOffset >= range.EndOffset)
                {
                    continue;
                }

                ranges.RemoveAt(i);
                if (endOffset < range.EndOffset)
                {
                    ranges.Insert(i, new CommittedRange(endOffset, range.EndOffset));
                }

                if (startOffset > range.StartOffset)
                {
                    ranges.Insert(i, new CommittedRange(range.StartOffset, startOffset));
                }
            }
        }

        private void AddFreeExtent(long offset, int length)
        {
            int insertIndex = 0;
            long startOffset = offset;
            long endOffset = offset + length;
            while (insertIndex < freeExtents.Count && freeExtents[insertIndex].Offset + freeExtents[insertIndex].Length < startOffset)
            {
                insertIndex++;
            }

            while (insertIndex < freeExtents.Count && freeExtents[insertIndex].Offset <= endOffset)
            {
                RawDataExtent current = freeExtents[insertIndex];
                startOffset = Math.Min(startOffset, current.Offset);
                endOffset = Math.Max(endOffset, current.Offset + current.Length);
                freeExtents.RemoveAt(insertIndex);
            }

            freeExtents.Insert(insertIndex, new RawDataExtent(startOffset, checked((int)(endOffset - startOffset))));
        }

        private void ReleaseUnusedPages(long startOffset, long endOffset)
        {
            long firstPage = startOffset >> pageShift;
            long lastPage = (endOffset - 1) >> pageShift;
            for (long pageIndex = firstPage; pageIndex <= lastPage; pageIndex++)
            {
                long pageStart = pageIndex << pageShift;
                long pageEnd = pageStart + pageSize;
                if (!HasCommittedRangeInPage(pageStart, pageEnd))
                {
                    pages.Remove(pageIndex);
                }
            }
        }

        private bool HasCommittedRangeInPage(long pageStart, long pageEnd)
        {
            for (int i = 0; i < ranges.Count; i++)
            {
                CommittedRange range = ranges[i];
                if (range.EndOffset <= pageStart)
                {
                    continue;
                }

                if (range.StartOffset >= pageEnd)
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private int FindRangeIndex(long offset)
        {
            int low = 0;
            int high = ranges.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                CommittedRange range = ranges[middle];
                if (offset < range.StartOffset)
                {
                    high = middle - 1;
                    continue;
                }

                if (offset >= range.EndOffset)
                {
                    low = middle + 1;
                    continue;
                }

                return middle;
            }

            return -1;
        }
    }
}
