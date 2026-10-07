using LibraDex.Layouts;

namespace LibraDex;

/// <summary>
/// Maintains the current-state file allocation map for bounded homogeneous extent segments.<br/>
/// Direct index roots and routes remain authoritative; allocator metadata only answers where a compatible unoccupied extent can be reused.<br/>
/// </summary>
internal sealed class FileExtentAllocator
{
    private const int MinimumExtentLength = 4096;
    private const int MaximumExtentLength = 1024 * 1024;
    private const ushort SlotsPerSegment = FileAllocationSegmentLayout.MaximumSlotCount;
    private readonly long directoryOffset;
    private readonly byte[] directoryBytes;
    private readonly Dictionary<int, AllocationClassState> classesByExtentLength = [];
    private readonly List<AllocationSegmentState> allSegments = [];
    private readonly Dictionary<long, AllocationSlotLocation> slotsByOffset = [];
    private readonly HashSet<long> pendingRetiredOffsets = [];

    private FileExtentAllocator(long directoryOffset, byte[] directoryBytes)
    {
        this.directoryOffset = directoryOffset;
        this.directoryBytes = directoryBytes;
    }

    /// <summary>
    /// Loads a persisted allocation directory and reconstructs its bounded in-memory class/segment lookup.<br/>
    /// The persisted pages remain authoritative; the dictionaries are disposable selection accelerators rebuilt on every open.<br/>
    /// </summary>
    /// <param name="kernel">The file-backed kernel that owns raw reads.<br/></param>
    /// <param name="directoryOffset">The nonzero allocator-directory offset from the superblock.<br/></param>
    /// <returns>The validated allocator instance.<br/></returns>
    /// <exception cref="InvalidDataException">Thrown when directory or segment topology is malformed.<br/></exception>
    internal static FileExtentAllocator Load(DataKernel kernel, long directoryOffset)
    {
        byte[] directoryBytes = new byte[FileAllocationDirectoryLayout.Size];
        kernel.ReadCommittedFileForAllocator(directoryOffset, directoryBytes);
        if (!FileAllocationDirectoryLayout.IsValid(directoryBytes))
            throw new InvalidDataException($"The LibraDex allocation directory at offset {directoryOffset:N0} is invalid.");

        FileExtentAllocator allocator = new(directoryOffset, directoryBytes);
        allocator.LoadClasses(kernel);
        return allocator;
    }

    /// <summary>
    /// Creates an empty allocator over bytes already reserved by catalog initialization or legacy upgrade.<br/>
    /// The caller owns publication of the initialized directory bytes before configuring normal segmented reservations.<br/>
    /// </summary>
    /// <param name="directoryOffset">The durable allocator-directory offset.<br/></param>
    /// <param name="initializedDirectoryBytes">The initialized fixed directory bytes.<br/></param>
    /// <returns>An empty allocator ready to receive compatible reservations.<br/></returns>
    internal static FileExtentAllocator Create(long directoryOffset, ReadOnlySpan<byte> initializedDirectoryBytes)
    {
        if (!FileAllocationDirectoryLayout.IsValid(initializedDirectoryBytes))
            throw new ArgumentException("The supplied allocation directory bytes are invalid.", nameof(initializedDirectoryBytes));

        return new FileExtentAllocator(directoryOffset, initializedDirectoryBytes.Slice(0, FileAllocationDirectoryLayout.Size).ToArray());
    }

    /// <summary>
    /// Attempts to reserve one exact-size structural extent from an existing or new homogeneous segment.<br/>
    /// Only bounded power-of-two structural sizes participate; uncommon lengths retain DataKernel's append fallback.<br/>
    /// The first pass searches every segment for a reusable materialized slot; only a second pass may materialize a new slot, preserving the allocator's reuse-before-extension contract across segment boundaries.<br/>
    /// </summary>
    /// <param name="kernel">The owning file-backed kernel.<br/></param>
    /// <param name="length">The exact requested extent length.<br/></param>
    /// <param name="reservation">Receives the staged reusable extent when supported.<br/></param>
    /// <returns><see langword="true"/> when the segmented allocator handled the reservation; otherwise <see langword="false"/>.<br/></returns>
    internal bool TryReserve(DataKernel kernel, int length, out RawDataReservation reservation)
    {
        if (!IsControlledExtentLength(length))
        {
            reservation = default;
            return false;
        }

        if (!classesByExtentLength.TryGetValue(length, out AllocationClassState? allocationClass))
            allocationClass = CreateClass(length);

        for (int segmentIndex = 0; segmentIndex < allocationClass.Segments.Count; segmentIndex++)
        {
            AllocationSegmentState segment = allocationClass.Segments[segmentIndex];
            ushort materializedCount = FileAllocationSegmentLayout.ReadMaterializedCount(segment.HeaderBytes);
            for (int slotIndex = 0; slotIndex < materializedCount; slotIndex++)
            {
                long slotOffset = segment.GetSlotOffset(slotIndex);
                if (pendingRetiredOffsets.Contains(slotOffset) || FileAllocationSegmentLayout.IsOccupied(segment.HeaderBytes, slotIndex))
                    continue;

                SetSegmentSlotState(kernel, segment, slotIndex, occupied: true, PendingSegmentPhase.AllocationClaim);
                reservation = kernel.ReserveAtForAllocator(slotOffset, length, PendingSegmentPhase.Payload);
                reservation.Span.Clear();
                return true;
            }
        }

        for (int segmentIndex = 0; segmentIndex < allocationClass.Segments.Count; segmentIndex++)
        {
            AllocationSegmentState segment = allocationClass.Segments[segmentIndex];
            ushort materializedCount = FileAllocationSegmentLayout.ReadMaterializedCount(segment.HeaderBytes);
            if (materializedCount < segment.SlotCount)
            {
                reservation = MaterializeAndReserveNextSlot(kernel, segment);
                return true;
            }
        }

        reservation = CreateSegmentAndReserveFirstSlot(kernel, allocationClass);
        return true;
    }

    /// <summary>
    /// Stages retirement of one segment-owned extent after its authoritative route has been removed.<br/>
    /// The slot remains unavailable to allocation until the retirement phase commits, preventing same-batch reuse ahead of route publication.<br/>
    /// </summary>
    /// <param name="kernel">The owning file-backed kernel.<br/></param>
    /// <param name="offset">The exact slot offset to retire.<br/></param>
    /// <param name="length">The exact slot length.<br/></param>
    /// <returns><see langword="true"/> when the extent belongs to this allocator and retirement was staged; otherwise <see langword="false"/>.<br/></returns>
    internal bool TryRetire(DataKernel kernel, long offset, int length)
    {
        if (!slotsByOffset.TryGetValue(offset, out AllocationSlotLocation location) ||
            location.Segment.ExtentLength != length)
            return false;

        if (!FileAllocationSegmentLayout.IsOccupied(location.Segment.HeaderBytes, location.SlotIndex))
            return true;

        SetSegmentSlotState(kernel, location.Segment, location.SlotIndex, occupied: false, PendingSegmentPhase.Retirement);
        pendingRetiredOffsets.Add(offset);
        return true;
    }

    /// <summary>
    /// Stages retirement of one allocator-owned extent when a topology walk supplies only its exact persisted offset.<br/>
    /// The offset table is authoritative for allocator ownership and supplies the extent class without rereading the released shelf header.<br/>
    /// </summary>
    /// <param name="kernel">The owning file-backed kernel.<br/></param>
    /// <param name="offset">The exact payload offset to retire.<br/></param>
    /// <returns><see langword="true"/> when the offset belongs to this allocator and is now retired or was already free; otherwise <see langword="false"/>.<br/></returns>
    internal bool TryRetire(DataKernel kernel, long offset)
    {
        if (!slotsByOffset.TryGetValue(offset, out AllocationSlotLocation location))
            return false;

        if (!FileAllocationSegmentLayout.IsOccupied(location.Segment.HeaderBytes, location.SlotIndex))
            return true;

        SetSegmentSlotState(kernel, location.Segment, location.SlotIndex, occupied: false, PendingSegmentPhase.Retirement);
        pendingRetiredOffsets.Add(offset);
        return true;
    }

    /// <summary>
    /// Completes volatile allocator bookkeeping after every pending phase has published successfully.<br/>
    /// Retired slots become selectable only at this boundary.<br/>
    /// </summary>
    internal void OnCommitCompleted()
    {
        pendingRetiredOffsets.Clear();
    }

    /// <summary>
    /// Reconstructs volatile allocator state from persisted pages after staged writes are discarded.<br/>
    /// This intentionally avoids undo bookkeeping in the allocator itself.<br/>
    /// </summary>
    /// <param name="kernel">The owning file-backed kernel.<br/></param>
    internal void ReloadAfterDiscard(DataKernel kernel)
    {
        classesByExtentLength.Clear();
        allSegments.Clear();
        slotsByOffset.Clear();
        pendingRetiredOffsets.Clear();
        kernel.ReadCommittedFileForAllocator(directoryOffset, directoryBytes);
        if (!FileAllocationDirectoryLayout.IsValid(directoryBytes))
            throw new InvalidDataException($"The LibraDex allocation directory at offset {directoryOffset:N0} became invalid.");

        LoadClasses(kernel);
    }

    /// <summary>
    /// Captures exact current-state storage accounting from the durable allocator directory and its reconstructed segment headers.<br/>
    /// Allocator metadata is live catalog overhead, occupied payload remains attributable through active topology walks, and unoccupied materialized payload is reusable capacity rather than unreachable waste.<br/>
    /// </summary>
    /// <returns>The exact allocator metadata, materialized payload, occupied payload, and reusable payload totals.<br/></returns>
    internal FileAllocationStorageSnapshot CaptureStorageSnapshot()
    {
        long materializedPayloadBytes = 0;
        long occupiedPayloadBytes = 0;
        int materializedSlotCount = 0;
        int occupiedSlotCount = 0;
        for (int segmentIndex = 0; segmentIndex < allSegments.Count; segmentIndex++)
        {
            AllocationSegmentState segment = allSegments[segmentIndex];
            ushort materializedCount = FileAllocationSegmentLayout.ReadMaterializedCount(segment.HeaderBytes);
            ushort usedCount = FileAllocationSegmentLayout.ReadUsedCount(segment.HeaderBytes);
            materializedSlotCount = checked(materializedSlotCount + materializedCount);
            occupiedSlotCount = checked(occupiedSlotCount + usedCount);
            materializedPayloadBytes = checked(materializedPayloadBytes + ((long)materializedCount * segment.ExtentLength));
            occupiedPayloadBytes = checked(occupiedPayloadBytes + ((long)usedCount * segment.ExtentLength));
        }

        long metadataBytes = checked(
            (long)FileAllocationDirectoryLayout.Size +
            ((long)allSegments.Count * FileAllocationSegmentLayout.HeaderSize));
        return new FileAllocationStorageSnapshot(
            metadataBytes,
            materializedPayloadBytes,
            occupiedPayloadBytes,
            checked(materializedPayloadBytes - occupiedPayloadBytes),
            allSegments.Count,
            materializedSlotCount,
            occupiedSlotCount);
    }

    private static bool IsControlledExtentLength(int length)
    {
        return length >= MinimumExtentLength &&
            length <= MaximumExtentLength &&
            (length & (length - 1)) == 0;
    }

    private AllocationClassState CreateClass(int extentLength)
    {
        int entryIndex = FindEmptyDirectoryEntry();
        AllocationClassState allocationClass = new(entryIndex, extentLength, SlotsPerSegment);
        classesByExtentLength.Add(extentLength, allocationClass);
        return allocationClass;
    }

    private RawDataReservation CreateSegmentAndReserveFirstSlot(DataKernel kernel, AllocationClassState allocationClass)
    {
        RawDataReservation segmentReservation = kernel.ReserveAppendForAllocator(
            FileAllocationSegmentLayout.HeaderSize,
            PendingSegmentPhase.AllocationClaim);
        segmentReservation.Span.Clear();
        long priorHeadOffset = allocationClass.Segments.Count == 0 ? 0 : allocationClass.Segments[0].Offset;
        FileAllocationSegmentLayout.Initialize(
            segmentReservation.Span.Slice(0, FileAllocationSegmentLayout.HeaderSize),
            allocationClass.ExtentLength,
            allocationClass.SlotsPerSegment,
            priorHeadOffset,
            allocationClass.EntryIndex);

        AllocationSegmentState segment = new(
            segmentReservation.Extent.Offset,
            allocationClass.ExtentLength,
            allocationClass.SlotsPerSegment,
            segmentReservation.Span.Slice(0, FileAllocationSegmentLayout.HeaderSize).ToArray());
        allocationClass.Segments.Insert(0, segment);
        allSegments.Add(segment);

        Span<byte> entry = FileAllocationDirectoryLayout.GetEntry(directoryBytes, allocationClass.EntryIndex);
        FileAllocationDirectoryLayout.WriteExtentLength(entry, allocationClass.ExtentLength);
        FileAllocationDirectoryLayout.WriteSlotsPerSegment(entry, allocationClass.SlotsPerSegment);
        FileAllocationDirectoryLayout.WriteHeadSegmentOffset(entry, segment.Offset);
        FileAllocationDirectoryLayout.WriteSegmentCount(entry, allocationClass.Segments.Count);
        FileAllocationDirectoryLayout.WriteGeneration(directoryBytes, checked(FileAllocationDirectoryLayout.ReadGeneration(directoryBytes) + 1));
        RawDataReservation directoryRewrite = kernel.ReserveAtForAllocator(
            directoryOffset,
            FileAllocationDirectoryLayout.Size,
            PendingSegmentPhase.AllocationClaim);
        directoryBytes.CopyTo(directoryRewrite.Span);

        return MaterializeAndReserveNextSlot(kernel, segment);
    }

    /// <summary>
    /// Appends one exact-size payload extent and records it as occupied in an existing homogeneous segment header.<br/>
    /// The header claim publishes before payload and route phases, so interruption can leak the new offset but cannot authorize reuse of a live route target.<br/>
    /// </summary>
    /// <param name="kernel">The owning file-backed kernel.<br/></param>
    /// <param name="segment">The segment with remaining offset-table capacity.<br/></param>
    /// <returns>The newly materialized payload reservation.<br/></returns>
    private RawDataReservation MaterializeAndReserveNextSlot(DataKernel kernel, AllocationSegmentState segment)
    {
        ushort slotIndex = FileAllocationSegmentLayout.ReadMaterializedCount(segment.HeaderBytes);
        if (slotIndex >= segment.SlotCount)
            throw new InvalidOperationException("The allocation segment offset table is full.");

        RawDataReservation payload = kernel.ReserveAppendForAllocator(segment.ExtentLength, PendingSegmentPhase.Payload);
        payload.Span.Clear();
        FileAllocationSegmentLayout.WriteSlotOffset(segment.HeaderBytes, slotIndex, payload.Extent.Offset);
        slotsByOffset.Add(payload.Extent.Offset, new AllocationSlotLocation(segment, slotIndex));
        FileAllocationSegmentLayout.WriteMaterializedCount(segment.HeaderBytes, checked((ushort)(slotIndex + 1)));
        FileAllocationSegmentLayout.SetOccupied(segment.HeaderBytes, slotIndex, occupied: true);
        FileAllocationSegmentLayout.WriteUsedCount(
            segment.HeaderBytes,
            checked((ushort)(FileAllocationSegmentLayout.ReadUsedCount(segment.HeaderBytes) + 1)));
        FileAllocationSegmentLayout.WriteGeneration(
            segment.HeaderBytes,
            checked(FileAllocationSegmentLayout.ReadGeneration(segment.HeaderBytes) + 1));
        RawDataReservation headerRewrite = kernel.ReserveAtForAllocator(
            segment.Offset,
            FileAllocationSegmentLayout.HeaderSize,
            PendingSegmentPhase.AllocationClaim);
        segment.HeaderBytes.CopyTo(headerRewrite.Span);
        return payload;
    }

    private void SetSegmentSlotState(
        DataKernel kernel,
        AllocationSegmentState segment,
        int slotIndex,
        bool occupied,
        PendingSegmentPhase phase)
    {
        bool wasOccupied = FileAllocationSegmentLayout.IsOccupied(segment.HeaderBytes, slotIndex);
        if (wasOccupied == occupied)
            return;

        FileAllocationSegmentLayout.SetOccupied(segment.HeaderBytes, slotIndex, occupied);
        ushort usedCount = FileAllocationSegmentLayout.ReadUsedCount(segment.HeaderBytes);
        FileAllocationSegmentLayout.WriteUsedCount(segment.HeaderBytes, checked((ushort)(occupied ? usedCount + 1 : usedCount - 1)));
        FileAllocationSegmentLayout.WriteGeneration(
            segment.HeaderBytes,
            checked(FileAllocationSegmentLayout.ReadGeneration(segment.HeaderBytes) + 1));
        RawDataReservation headerRewrite = kernel.ReserveAtForAllocator(
            segment.Offset,
            FileAllocationSegmentLayout.HeaderSize,
            phase);
        segment.HeaderBytes.CopyTo(headerRewrite.Span);
    }

    private int FindEmptyDirectoryEntry()
    {
        for (int entryIndex = 0; entryIndex < FileAllocationDirectoryLayout.EntryCount; entryIndex++)
        {
            if (FileAllocationDirectoryLayout.ReadExtentLength(FileAllocationDirectoryLayout.GetEntry(directoryBytes, entryIndex)) == 0)
                return entryIndex;
        }

        throw new InvalidOperationException($"The LibraDex allocation directory exhausted its {FileAllocationDirectoryLayout.EntryCount} homogeneous extent classes.");
    }

    private void LoadClasses(DataKernel kernel)
    {
        HashSet<long> visitedSegmentOffsets = [];
        HashSet<long> visitedPayloadOffsets = [];
        long materializedPayloadEnd = 0;
        for (int entryIndex = 0; entryIndex < FileAllocationDirectoryLayout.EntryCount; entryIndex++)
        {
            ReadOnlySpan<byte> entry = FileAllocationDirectoryLayout.GetEntry(directoryBytes, entryIndex);
            int extentLength = FileAllocationDirectoryLayout.ReadExtentLength(entry);
            if (extentLength == 0)
                continue;

            ushort slotsPerSegment = FileAllocationDirectoryLayout.ReadSlotsPerSegment(entry);
            int expectedSegmentCount = FileAllocationDirectoryLayout.ReadSegmentCount(entry);
            long segmentOffset = FileAllocationDirectoryLayout.ReadHeadSegmentOffset(entry);
            if (!IsControlledExtentLength(extentLength) ||
                slotsPerSegment == 0 ||
                slotsPerSegment > FileAllocationSegmentLayout.MaximumSlotCount ||
                expectedSegmentCount <= 0 ||
                segmentOffset <= 0)
            {
                throw new InvalidDataException($"Allocation directory entry {entryIndex} has invalid class geometry.");
            }

            AllocationClassState allocationClass = new(entryIndex, extentLength, slotsPerSegment);
            for (int segmentIndex = 0; segmentIndex < expectedSegmentCount; segmentIndex++)
            {
                if (segmentOffset <= 0 || !visitedSegmentOffsets.Add(segmentOffset))
                    throw new InvalidDataException($"Allocation segment chain for entry {entryIndex} is truncated or cyclic at offset {segmentOffset:N0}.");

                byte[] header = new byte[FileAllocationSegmentLayout.HeaderSize];
                kernel.ReadCommittedFileForAllocator(segmentOffset, header);
                if (!FileAllocationSegmentLayout.IsValid(header) ||
                    FileAllocationSegmentLayout.ReadExtentLength(header) != extentLength ||
                    FileAllocationSegmentLayout.ReadDirectoryEntryIndex(header) != entryIndex)
                {
                    throw new InvalidDataException($"Allocation segment at offset {segmentOffset:N0} does not match directory entry {entryIndex}.");
                }

                AllocationSegmentState segment = new(segmentOffset, extentLength, slotsPerSegment, header);
                ushort materializedCount = FileAllocationSegmentLayout.ReadMaterializedCount(header);
                for (int slotIndex = 0; slotIndex < materializedCount; slotIndex++)
                {
                    long payloadOffset = FileAllocationSegmentLayout.ReadSlotOffset(header, slotIndex);
                    long payloadEnd;
                    try
                    {
                        payloadEnd = checked(payloadOffset + extentLength);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException(
                            $"Allocation segment at offset {segmentOffset:N0} contains an overflowing payload offset {payloadOffset:N0}.",
                            ex);
                    }

                    if (payloadOffset <= 0 ||
                        !visitedPayloadOffsets.Add(payloadOffset) ||
                        !slotsByOffset.TryAdd(payloadOffset, new AllocationSlotLocation(segment, slotIndex)))
                        throw new InvalidDataException($"Allocation segment at offset {segmentOffset:N0} contains an invalid or duplicate payload offset {payloadOffset:N0}.");

                    materializedPayloadEnd = Math.Max(materializedPayloadEnd, payloadEnd);
                }

                allocationClass.Segments.Add(segment);
                allSegments.Add(segment);
                segmentOffset = FileAllocationSegmentLayout.ReadNextSegmentOffset(header);
            }

            if (segmentOffset != 0)
                throw new InvalidDataException($"Allocation segment chain for entry {entryIndex} exceeds its persisted segment count {expectedSegmentCount:N0}.");

            classesByExtentLength.Add(extentLength, allocationClass);
        }

        kernel.EnsureAppendOffsetForAllocator(materializedPayloadEnd);
    }

    private sealed class AllocationClassState
    {
        internal AllocationClassState(int entryIndex, int extentLength, ushort slotsPerSegment)
        {
            EntryIndex = entryIndex;
            ExtentLength = extentLength;
            SlotsPerSegment = slotsPerSegment;
        }

        internal int EntryIndex { get; }

        internal int ExtentLength { get; }

        internal ushort SlotsPerSegment { get; }

        internal List<AllocationSegmentState> Segments { get; } = [];
    }

    private sealed class AllocationSegmentState
    {
        internal AllocationSegmentState(long offset, int extentLength, ushort slotCount, byte[] headerBytes)
        {
            Offset = offset;
            ExtentLength = extentLength;
            SlotCount = slotCount;
            HeaderBytes = headerBytes;
        }

        internal long Offset { get; }

        internal int ExtentLength { get; }

        internal ushort SlotCount { get; }

        internal byte[] HeaderBytes { get; }

        internal long GetSlotOffset(int slotIndex) =>
            FileAllocationSegmentLayout.ReadSlotOffset(HeaderBytes, slotIndex);

    }

    /// <summary>
    /// Binds one materialized payload offset to its owning segment and fixed bitmap slot.<br/>
    /// The map is reconstructed from durable segment offset tables on open and prevents bulk retirement from rescanning every segment for every topology extent.<br/>
    /// </summary>
    /// <param name="Segment">The allocator segment whose header owns the occupancy bit.<br/></param>
    /// <param name="SlotIndex">The zero-based slot within the segment's materialized offset table.<br/></param>
    private readonly record struct AllocationSlotLocation(AllocationSegmentState Segment, int SlotIndex);
}

/// <summary>
/// Reports exact current-state storage owned by the persisted homogeneous file allocator.<br/>
/// The snapshot distinguishes live allocator metadata and occupied payload from already-materialized reusable payload so maintenance does not misclassify reserved capacity as unreachable growth.<br/>
/// </summary>
/// <param name="MetadataBytes">The durable allocator directory plus every segment header.<br/></param>
/// <param name="MaterializedPayloadBytes">The total payload bytes whose exact offsets have been materialized into segment tables.<br/></param>
/// <param name="OccupiedPayloadBytes">The materialized payload bytes whose segment bits remain occupied.<br/></param>
/// <param name="ReusablePayloadBytes">The materialized payload bytes whose segment bits are free and immediately reusable by their exact allocation class.<br/></param>
/// <param name="SegmentCount">The number of persisted allocation segment headers.<br/></param>
/// <param name="MaterializedSlotCount">The number of payload offsets materialized across all segment tables.<br/></param>
/// <param name="OccupiedSlotCount">The number of materialized payload slots currently marked occupied.<br/></param>
internal readonly record struct FileAllocationStorageSnapshot(
    long MetadataBytes,
    long MaterializedPayloadBytes,
    long OccupiedPayloadBytes,
    long ReusablePayloadBytes,
    int SegmentCount,
    int MaterializedSlotCount,
    int OccupiedSlotCount);

/// <summary>
/// Orders file-backed pending writes around authoritative topology publication.<br/>
/// Allocation claims become durable first, payload bytes precede route/metadata publication, and retirement is last.<br/>
/// </summary>
internal enum PendingSegmentPhase : byte
{
    AllocationClaim = 0,
    Payload = 1,
    Publication = 2,
    Retirement = 3
}
