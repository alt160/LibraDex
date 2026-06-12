using System.Buffers;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace LibraDex;

/// <summary>
/// Provides the first raw byte movement core for LibraDex.<br/>
/// This slice deliberately knows nothing about shelves, routers, indexes, or superblock layout beyond reserving the file prefix.<br/>
/// </summary>
public sealed class DataKernel : IDisposable
{
    private const int InitialPendingSegmentCapacity = 16;
    private const int InitialMemorySegmentCapacity = 16;
    private const int InitialCommitSliceCapacity = 16;
    private const int InitialCoveredRangeCapacity = 16;
    private const int InitialRemainingRangeCapacity = 8;

    private readonly SafeFileHandle? handle;
    private readonly DataKernelOptions options;
    private readonly DataKernelTelemetryOptions telemetryOptions;
    private readonly List<PendingSegment> pendingSegments = new(InitialPendingSegmentCapacity);
    private readonly List<CommittedMemorySegment> memorySegments = new(InitialMemorySegmentCapacity);
    private readonly List<CommitSlice> fileCommitSlices = new(InitialCommitSliceCapacity);
    private readonly List<CommittedRange> coveredRanges = new(InitialCoveredRangeCapacity);
    private readonly List<CommittedRange> remainingRanges = new(InitialRemainingRangeCapacity);
    private readonly DataKernelBackingKind backingKind;
    private long nextAppendOffset;
    private long stagedExtentCount;
    private long readCallCount;
    private long backingReadCallCount;
    private long bytesRead;
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
    /// Memory mode preserves raw offset, append, read, commit, and telemetry semantics but is not durable.<br/>
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
        ThrowIfDisposed();

        if (source.Length == 0)
        {
            return new RawDataExtent(nextAppendOffset, 0);
        }

        long extentOffset = nextAppendOffset;
        int remaining = source.Length;
        int sourceOffset = 0;

        while (remaining > 0)
        {
            PendingSegment segment = GetAppendSegment(remaining);
            int writable = Math.Min(remaining, segment.Capacity - segment.Length);
            source.Slice(sourceOffset, writable).CopyTo(segment.Buffer.AsSpan(segment.Length, writable));
            segment.Length += writable;
            sourceOffset += writable;
            remaining -= writable;
            nextAppendOffset += writable;
        }

        stagedExtentCount++;
        return new RawDataExtent(extentOffset, source.Length);
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
        ThrowIfDisposed();

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Reservation length cannot be negative.");
        }

        if (length == 0)
        {
            return new RawDataReservation(new RawDataExtent(nextAppendOffset, 0), Span<byte>.Empty);
        }

        PendingSegment segment = GetContiguousAppendSegment(length);
        long extentOffset = nextAppendOffset;
        Span<byte> span = segment.Buffer.AsSpan(segment.Length, length);

        segment.Length += length;
        nextAppendOffset += length;
        stagedExtentCount++;

        return new RawDataReservation(new RawDataExtent(extentOffset, length), span);
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

        int capacity = length >= options.AppendBufferSize
            ? length
            : options.AppendBufferSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        PendingSegment segment = new(buffer, offset, capacity, isAppend: false);
        segment.Length = length;
        pendingSegments.Add(segment);
        stagedExtentCount++;

        return new RawDataReservation(new RawDataExtent(offset, length), buffer.AsSpan(0, length));
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

    /// <summary>
    /// Discards currently staged writes without publishing them to the selected backing store.<br/>
    /// This supports higher-level abort/reload flows where uncommitted durability-batch changes should be abandoned explicitly.<br/>
    /// </summary>
    public void DiscardPending()
    {
        ThrowIfDisposed();

        ReturnPendingSegments();
        nextAppendOffset = backingKind == DataKernelBackingKind.File
            ? Math.Max(RandomAccess.GetLength(handle!), options.ReservedPrefixBytes)
            : Math.Max(GetCommittedMemoryEndOffset(), options.ReservedPrefixBytes);

        stagedExtentCount = 0;
    }

    /// <summary>
    /// Commits staged append buffers to the selected backing store.<br/>
    /// File mode uses positional writes; memory mode publishes committed pooled segments for later reads.<br/>
    /// </summary>
    /// <returns>Telemetry describing the commit write shape.</returns>
    public DataKernelCommitTelemetry Commit()
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
        Stopwatch stopwatch = Stopwatch.StartNew();

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

                if (segment.IsAppend)
                {
                    memorySegments.Add(new CommittedMemorySegment(segment.Buffer, segment.Offset, segment.Length));
                    memorySegments.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
                    segment.TransferredToMemory = true;
                }
                else
                {
                    ApplyMemoryOverwrite(segment);
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

        stopwatch.Stop();

        int stagedSegmentCount = pendingSegments.Count;
        ReturnPendingSegments();

        DataKernelCommitTelemetry telemetry = new(
            TelemetryEnabled: telemetryOptions.Enabled,
            StagedExtentCount: telemetryOptions.Enabled ? stagedExtentCount : 0,
            StagedSegmentCount: telemetryOptions.Enabled ? stagedSegmentCount : 0,
            WriteCallCount: telemetryOptions.Enabled && backingKind == DataKernelBackingKind.File ? writeCallCount : 0,
            BackingWriteCallCount: telemetryOptions.Enabled ? backingWriteCallCount : 0,
            SetLengthCallCount: setLengthCallCount,
            FlushCallCount: flushCallCount,
            BytesWritten: telemetryOptions.Enabled ? bytesWritten : 0,
            ElapsedTicks: telemetryOptions.Enabled ? stopwatch.ElapsedTicks : 0,
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
        return telemetry;
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
                    RandomAccess.Write(handle!, firstSegment.Buffer.AsSpan(first.BufferOffset, first.Length), first.Offset);
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

            remainingRanges.Clear();
            remainingRanges.Add(new CommittedRange(segment.Offset, segment.Offset + segment.Length));
            for (int coveredIndex = 0; coveredIndex < coveredRanges.Count && remainingRanges.Count > 0; coveredIndex++)
            {
                SubtractCoveredRange(remainingRanges, coveredRanges[coveredIndex]);
            }

            for (int rangeIndex = 0; rangeIndex < remainingRanges.Count; rangeIndex++)
            {
                CommittedRange range = remainingRanges[rangeIndex];
                int bufferOffset = checked((int)(range.StartOffset - segment.Offset));
                int length = checked((int)(range.EndOffset - range.StartOffset));
                fileCommitSlices.Add(new CommitSlice(segmentIndex, range.StartOffset, bufferOffset, length, segment.IsAppend));
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
        int insertIndex = 0;
        while (insertIndex < ranges.Count && ranges[insertIndex].EndOffset < range.StartOffset)
        {
            insertIndex++;
        }

        long startOffset = range.StartOffset;
        long endOffset = range.EndOffset;
        while (insertIndex < ranges.Count && ranges[insertIndex].StartOffset <= endOffset)
        {
            CommittedRange current = ranges[insertIndex];
            startOffset = Math.Min(startOffset, current.StartOffset);
            endOffset = Math.Max(endOffset, current.EndOffset);
            ranges.RemoveAt(insertIndex);
        }

        EnsureListCapacity(ranges, ranges.Count + 1);
        ranges.Insert(insertIndex, new CommittedRange(startOffset, endOffset));
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
        buffers[bufferIndex++] = firstSegment.Buffer.AsMemory(first.BufferOffset, first.Length);
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
            buffers[bufferIndex++] = segment.Buffer.AsMemory(slice.BufferOffset, slice.Length);
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

    /// <summary>
    /// Releases staged buffers, committed memory buffers, and any file handle.<br/>
    /// Uncommitted staged data is discarded by design for this raw development slice.<br/>
    /// </summary>
    public void Dispose()
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
        int index = FindMemorySegmentIndex(offset);
        if (index < 0)
        {
            return 0;
        }

        CommittedMemorySegment segment = memorySegments[index];
        int sourceOffset = checked((int)(offset - segment.Offset));
        int readable = Math.Min(destination.Length, segment.Length - sourceOffset);
        segment.Buffer.AsSpan(sourceOffset, readable).CopyTo(destination);

        if (telemetryOptions.Enabled)
        {
            backingReadCallCount++;
        }

        return readable;
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
                segment.Buffer.AsSpan(sourceOffset, readable).CopyTo(destination);
                return readable;
            }
        }

        return 0;
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

    private void ApplyMemoryOverwrite(PendingSegment source)
    {
        int index = FindMemorySegmentIndex(source.Offset);
        if (index >= 0)
        {
            CommittedMemorySegment target = memorySegments[index];
            long targetEnd = target.Offset + target.Length;
            long sourceEnd = source.Offset + source.Length;
            if (sourceEnd <= targetEnd)
            {
                int targetOffset = checked((int)(source.Offset - target.Offset));
                source.Buffer.AsSpan(0, source.Length).CopyTo(target.Buffer.AsSpan(targetOffset, source.Length));
                return;
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        source.Buffer.AsSpan(0, source.Length).CopyTo(buffer);
        memorySegments.Add(new CommittedMemorySegment(buffer, source.Offset, source.Length));
        memorySegments.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
    }

    private int FindMemorySegmentIndex(long offset)
    {
        int low = 0;
        int high = memorySegments.Count - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            CommittedMemorySegment segment = memorySegments[middle];

            if (offset < segment.Offset)
            {
                high = middle - 1;
                continue;
            }

            long segmentEnd = segment.Offset + segment.Length;
            if (offset >= segmentEnd)
            {
                low = middle + 1;
                continue;
            }

            return middle;
        }

        return -1;
    }

    private void ReturnPendingSegments()
    {
        foreach (PendingSegment segment in pendingSegments)
        {
            if (!segment.TransferredToMemory)
            {
                ArrayPool<byte>.Shared.Return(segment.Buffer);
            }
        }

        pendingSegments.Clear();
    }

    private void ReturnMemorySegments()
    {
        foreach (CommittedMemorySegment segment in memorySegments)
        {
            ArrayPool<byte>.Shared.Return(segment.Buffer);
        }

        memorySegments.Clear();
    }

    private long GetCommittedMemoryEndOffset()
    {
        long endOffset = options.ReservedPrefixBytes;
        for (int i = 0; i < memorySegments.Count; i++)
        {
            CommittedMemorySegment segment = memorySegments[i];
            endOffset = Math.Max(endOffset, segment.Offset + segment.Length);
        }

        return endOffset;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PendingSegment
    {
        public PendingSegment(byte[] buffer, long offset, int capacity, bool isAppend)
        {
            Buffer = buffer;
            Offset = offset;
            Capacity = capacity;
            IsAppend = isAppend;
        }

        public byte[] Buffer { get; }

        public long Offset { get; }

        public int Capacity { get; }

        public int Length { get; set; }

        public bool IsAppend { get; }

        public bool TransferredToMemory { get; set; }
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

    private readonly record struct CommittedRange(long StartOffset, long EndOffset);

    private sealed class CommittedMemorySegment
    {
        public CommittedMemorySegment(byte[] buffer, long offset, int length)
        {
            Buffer = buffer;
            Offset = offset;
            Length = length;
        }

        public byte[] Buffer { get; }

        public long Offset { get; }

        public int Length { get; }
    }
}
