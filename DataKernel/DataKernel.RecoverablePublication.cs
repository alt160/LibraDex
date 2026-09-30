using System.Buffers.Binary;
using System.Security.Cryptography;
using LibraDex.Layouts;
using Microsoft.Win32.SafeHandles;

namespace LibraDex;

internal sealed partial class DataKernel
{
    internal const string PublicationReadySuffix = ".publication-ready";
    internal const string PublicationPrepareSuffix = ".publication-prepare";
    private const ulong PublicationMagic = 0x315055424458424CUL;
    private const ulong PublicationCompletedMagic = 0x31454E4F4442584CUL;
    private const int PublicationHeaderSize = 64;
    private const int PublicationHashSize = 32;
    private const int MaxPublicationBytes = 64 * 1024 * 1024;
    private readonly string? filePath;

    /// <summary>Reports the most recent structural recovery image size, excluding the 40-byte completion receipt.<br/>
    /// Ordinary Commit does not create an image and does not pay serialization/hash/flush work.<br/></summary>
    internal long LastPublicationJournalBytes { get; private set; }

    /// <summary>Optional diagnostic boundary observer: Prepared, DataFlushed, Completed.<br/>
    /// Throwing after preparation requires reopen/recovery; production leaves this null.<br/></summary>
    internal Action<string>? PublicationCheckpoint { get; set; }

    /// <summary>Optional recovery-only observer after each replayed extent; used to prove repeatable recovery interruption.<br/>
    /// This is never invoked by ordinary reads, writes, or commits.<br/></summary>
    internal static Action<int>? PublicationReplayCheckpoint { get; set; }

    /// <summary>
    /// Protects a bounded version-two structural publication with a checksummed redo image stored inside the owning data file.<br/>
    /// The caller supplies the offset of an immutable sixteen-byte file identity; staged writes may not change it.<br/>
    /// The fixed recovery-control page alternates checksummed state slots while the redo image temporarily occupies the target file tail.<br/>
    /// Preparing, active, complete, truncate, and clean transitions are individually flushed so each surviving state has one deterministic reopen action.<br/>
    /// Version-one files are rejected rather than silently upgraded or given the detached lifecycle semantics of the earlier sidecar prototype.<br/>
    /// Any failure closes this kernel because reopen may finish or abandon the protected publication before the file can be used again.<br/>
    /// </summary>
    /// <param name="identityOffset">The immutable sixteen-byte file-identity offset used to bind every redo record to this catalog.<br/></param>
    /// <returns>The main-file commit telemetry; recovery-control, redo-image, truncate, and forced-flush work is intentionally additional.<br/></returns>
    internal DataKernelCommitTelemetry CommitRecoverablePublication(long identityOffset)
    {
        storageSync.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (backingKind != DataKernelBackingKind.File || filePath is null)
                throw new InvalidOperationException("Recoverable publication requires file backing.");
            if (pendingSegments.Count == 0) return Commit();

            ushort formatVersion = ReadPublicationFormatVersion(handle!);
            if (formatVersion != SuperblockLayout.RecoverableFormatVersion)
            {
                throw new InvalidOperationException(
                    "Recoverable structural publication requires the LibraDex version-two recoverable file format. " +
                    "Create the catalog with CatalogOptions.UseRecoverableFileFormat or explicitly migrate a closed version-one catalog through compaction.");
            }

            PublicationRecoverySlot state = ReadEmbeddedRecoveryState(handle!);
            if (state.State != PublicationRecoveryState.Clean)
                throw new InvalidDataException("The version-two recovery-control page is not clean; reopen the catalog to finish recovery before publishing again.");

            byte[] image = CapturePublication(identityOffset);
            ReadOnlySpan<byte> header = image.AsSpan(0, PublicationHeaderSize);
            long originalLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(40));
            long targetLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(48));
            long journalOffset = targetLength;
            ReadOnlySpan<byte> journalHash = image.AsSpan(image.Length - PublicationHashSize);

            state = TransitionEmbeddedRecoveryState(
                handle!,
                state,
                PublicationRecoveryState.Preparing,
                originalLength,
                targetLength,
                journalOffset,
                image.Length,
                journalHash);
            PublicationCheckpoint?.Invoke("Preparing");

            RandomAccess.Write(handle!, image, journalOffset);
            RandomAccess.FlushToDisk(handle!);
            PublicationCheckpoint?.Invoke("JournalFlushed");

            state = TransitionEmbeddedRecoveryState(
                handle!,
                state,
                PublicationRecoveryState.Active,
                originalLength,
                targetLength,
                journalOffset,
                image.Length,
                journalHash);
            LastPublicationJournalBytes = image.Length;
            PublicationCheckpoint?.Invoke("Prepared");

            DataKernelCommitTelemetry telemetry = Commit();
            if (!options.FlushToDiskOnCommit)
                RandomAccess.FlushToDisk(handle!);
            PublicationCheckpoint?.Invoke("DataFlushed");

            state = TransitionEmbeddedRecoveryState(
                handle!,
                state,
                PublicationRecoveryState.Complete,
                originalLength,
                targetLength,
                journalOffset,
                image.Length,
                journalHash);
            PublicationCheckpoint?.Invoke("Completed");

            RandomAccess.SetLength(handle!, targetLength);
            RandomAccess.FlushToDisk(handle!);
            TransitionEmbeddedRecoveryState(handle!, state, PublicationRecoveryState.Clean, 0, 0, 0, 0, default);
            return telemetry;
        }
        catch { Dispose(); throw; }
        finally { storageSync.ExitWriteLock(); }
    }

    /// <summary>Serializes only pending commit slices, not the recordbase, with a fixed admission bound.<br/>
    /// Phase-local overlap resolution is reused from ordinary commit planning; replay preserves phase order exactly.<br/></summary>
    private byte[] CapturePublication(long identityOffset)
    {
        long originalLength = RandomAccess.GetLength(handle!);
        if (identityOffset < 0 || identityOffset > originalLength - 16) throw new ArgumentOutOfRangeException(nameof(identityOffset));
        byte[] identity = new byte[16]; ReadPublicationBytes(handle!, identityOffset, identity);
        if (identity.AsSpan().IndexOfAnyExcept((byte)0) < 0) throw new InvalidDataException("Recovery requires a nonzero immutable file identity.");
        byte[] immutableFormatHeader = new byte[SuperblockLayout.FormatVersionOffset + sizeof(ushort)];
        ReadPublicationBytes(handle!, 0, immutableFormatHeader);
        using var output = new MemoryStream(); output.SetLength(PublicationHeaderSize); output.Position = PublicationHeaderSize;
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        int count = 0; long targetLength = originalLength;
        for (PendingSegmentPhase phase = PendingSegmentPhase.AllocationClaim; phase <= PendingSegmentPhase.Retirement; phase++)
        {
            long ignored = 0;
            foreach (CommitSlice slice in CreateFileCommitSlices(phase, ref ignored))
            {
                PendingSegment segment = pendingSegments[slice.SegmentIndex];
                ReadOnlySpan<byte> data = segment.Buffer.AsSpan(segment.SourceOffset + slice.BufferOffset, slice.Length);
                long recoveryControlStart = PublicationRecoveryLayout.PageOffset;
                long recoveryControlEnd = recoveryControlStart + PublicationRecoveryLayout.PageSize;
                if (slice.Offset < recoveryControlEnd && slice.Offset + slice.Length > recoveryControlStart)
                    throw new InvalidOperationException("A recoverable publication cannot write into the version-two recovery-control page.");
                long formatStart = Math.Max(slice.Offset, 0);
                long formatEnd = Math.Min(slice.Offset + slice.Length, immutableFormatHeader.Length);
                if (formatStart < formatEnd &&
                    !data.Slice(checked((int)(formatStart - slice.Offset)), checked((int)(formatEnd - formatStart)))
                        .SequenceEqual(immutableFormatHeader.AsSpan(checked((int)formatStart), checked((int)(formatEnd - formatStart)))))
                {
                    throw new InvalidOperationException("A recoverable publication cannot change its superblock magic or file-format version.");
                }
                long start = Math.Max(slice.Offset, identityOffset), end = Math.Min(slice.Offset + slice.Length, identityOffset + 16);
                if (start < end && !data.Slice(checked((int)(start - slice.Offset)), checked((int)(end - start))).SequenceEqual(identity.AsSpan(checked((int)(start - identityOffset)), checked((int)(end - start)))))
                    throw new InvalidOperationException("A recoverable publication cannot change its file identity.");
                if (output.Length + 12L + slice.Length + PublicationHashSize > MaxPublicationBytes) throw new InvalidOperationException("Structural publication exceeds the 64 MiB recovery-image limit.");
                writer.Write(slice.Offset); writer.Write(slice.Length); writer.Write(data); count++;
                targetLength = Math.Max(targetLength, checked(slice.Offset + slice.Length));
            }
        }
        int bodyLength = checked((int)output.Length);
        output.Position = 0;
        writer.Write(PublicationMagic); writer.Write(1); writer.Write(bodyLength); writer.Write(identityOffset); writer.Write(identity);
        writer.Write(originalLength); writer.Write(targetLength); writer.Write(count); writer.Write(0);
        writer.Flush();
        byte[] body = output.ToArray(); byte[] image = new byte[bodyLength + PublicationHashSize];
        body.CopyTo(image, 0); SHA256.HashData(body, image.AsSpan(bodyLength)); return image;
    }

    /// <summary>
    /// Recovers any earlier sidecar prototype first, then resolves a version-two embedded recovery state before normal parsing.<br/>
    /// Sidecar recovery remains read-only compatibility for interrupted development fixtures; new protected publications never create sidecars.<br/>
    /// </summary>
    /// <param name="target">The newly opened writable target handle.<br/></param>
    /// <param name="path">The normalized target path used only for prototype-sidecar compatibility.<br/></param>
    private static void RecoverPublication(SafeFileHandle target, string path)
    {
        if (ReadPublicationFormatVersion(target) == SuperblockLayout.RecoverableFormatVersion &&
            (File.Exists(path + PublicationReadySuffix) || File.Exists(path + PublicationPrepareSuffix)))
        {
            throw new InvalidDataException(
                "A version-two catalog has both embedded recovery state and a prototype sidecar; recovery fails closed because the two publication authorities cannot be ordered safely.");
        }
        RecoverExternalPublication(target, path);
        RecoverEmbeddedPublication(target);
    }

    /// <summary>Validates a legacy ready sidecar completely before any replay and finishes it before embedded recovery is considered.<br/>
    /// Unpublished prepare files can be discarded; corrupt ready images fail closed and are preserved.<br/>
    /// Recovery is repeatable after interruption because the ready image remains until a durable completion receipt exists.<br/></summary>
    private static void RecoverExternalPublication(SafeFileHandle target, string path)
    {
        string ready = path + PublicationReadySuffix, prepare = path + PublicationPrepareSuffix;
        if (!File.Exists(ready))
        {
            if (File.Exists(prepare)) File.Delete(prepare);
            return;
        }
        long length = new FileInfo(ready).Length;
        if (length < PublicationHeaderSize + PublicationHashSize || length > MaxPublicationBytes + 40L) throw new InvalidDataException("Invalid publication recovery image length.");
        byte[] image = File.ReadAllBytes(ready); ReadOnlySpan<byte> header = image.AsSpan(0, PublicationHeaderSize);
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != PublicationMagic || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8)) != 1 ||
            bodyLength < PublicationHeaderSize || bodyLength > image.Length - PublicationHashSize || image.Length > bodyLength + PublicationHashSize + 40)
            throw new InvalidDataException("Invalid publication recovery header.");
        ReadOnlySpan<byte> hash = image.AsSpan(bodyLength, PublicationHashSize);
        Span<byte> actualHash = stackalloc byte[PublicationHashSize]; SHA256.HashData(image.AsSpan(0, bodyLength), actualHash);
        if (!CryptographicOperations.FixedTimeEquals(hash, actualHash)) throw new InvalidDataException("Publication recovery checksum mismatch.");
        long identityOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16));
        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(40)), targetLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(48));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(56));
        long currentLength = RandomAccess.GetLength(target);
        if (originalLength < 16 || identityOffset < 0 || identityOffset > originalLength - 16 || currentLength < originalLength || targetLength < originalLength || count < 0 || count > (bodyLength - PublicationHeaderSize) / 13)
            throw new InvalidDataException("Invalid publication recovery bounds.");
        Span<byte> identity = stackalloc byte[16]; ReadPublicationBytes(target, identityOffset, identity);
        if (!identity.SequenceEqual(header.Slice(24, 16))) throw new InvalidDataException("Publication recovery belongs to another data file.");
        int cursor = PublicationHeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (cursor > bodyLength - 12) throw new InvalidDataException("Truncated publication record.");
            long offset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(cursor)); int bytes = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(cursor + 8)); cursor += 12;
            if (offset < 0 || bytes <= 0 || offset > targetLength - bytes || bytes > bodyLength - cursor) throw new InvalidDataException("Invalid publication record bounds.");
            long start = Math.Max(offset, identityOffset), end = Math.Min(offset + bytes, identityOffset + 16);
            if (start < end && !image.AsSpan(cursor + checked((int)(start - offset)), checked((int)(end - start))).SequenceEqual(identity.Slice(checked((int)(start - identityOffset)), checked((int)(end - start)))))
                throw new InvalidDataException("Publication image changes its binding identity.");
            cursor += bytes;
        }
        if (cursor != bodyLength) throw new InvalidDataException("Publication record count does not match body.");
        int receipt = bodyLength + PublicationHashSize;
        bool completed = image.Length == receipt + 40 && BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(receipt)) == PublicationCompletedMagic && image.AsSpan(receipt + 8, 32).SequenceEqual(hash);
        if (!completed)
        {
            if (currentLength > targetLength) throw new InvalidDataException("Uncompleted publication has an unexpected later file extent.");
            cursor = PublicationHeaderSize;
            for (int i = 0; i < count; i++)
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(cursor)); int bytes = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(cursor + 8)); cursor += 12;
                RandomAccess.Write(target, image.AsSpan(cursor, bytes), offset); cursor += bytes;
                PublicationReplayCheckpoint?.Invoke(i);
            }
            RandomAccess.FlushToDisk(target); CompletePublication(ready, hash);
        }
        File.Delete(ready);
        if (File.Exists(prepare)) File.Delete(prepare);
    }

    /// <summary>
    /// Resolves the newest valid version-two recovery-control slot before the DataKernel append cursor or catalog parser observes the file.<br/>
    /// Preparing truncates back to the old authoritative length because main-file publication was not admitted yet.<br/>
    /// Active validates and replays the complete in-file redo image; complete only finalizes truncation because the authoritative bytes were already flushed.<br/>
    /// Every terminal path writes a newer clean slot only after the required data or truncation flush succeeds.<br/>
    /// </summary>
    /// <param name="target">The newly opened writable data-file handle.<br/></param>
    private static void RecoverEmbeddedPublication(SafeFileHandle target)
    {
        if (RandomAccess.GetLength(target) < SuperblockLayout.Size)
            return;

        ushort formatVersion = ReadPublicationFormatVersion(target);
        if (formatVersion != SuperblockLayout.RecoverableFormatVersion)
            return;

        PublicationRecoverySlot state = ReadEmbeddedRecoveryState(target);
        if (state.State == PublicationRecoveryState.Clean)
            return;

        ValidateEmbeddedRecoveryBounds(state, RandomAccess.GetLength(target));
        if (state.State == PublicationRecoveryState.Preparing)
        {
            RandomAccess.SetLength(target, state.OriginalLength);
            RandomAccess.FlushToDisk(target);
            TransitionEmbeddedRecoveryState(target, state, PublicationRecoveryState.Clean, 0, 0, 0, 0, default);
            return;
        }

        if (state.State == PublicationRecoveryState.Active)
        {
            byte[] image = new byte[state.JournalLength];
            ReadPublicationBytes(target, state.JournalOffset, image);
            ValidateEmbeddedJournalBinding(image, state);
            ReplayPublicationImage(target, image);
            RandomAccess.FlushToDisk(target);
            state = TransitionEmbeddedRecoveryState(
                target,
                state,
                PublicationRecoveryState.Complete,
                state.OriginalLength,
                state.TargetLength,
                state.JournalOffset,
                state.JournalLength,
                state.JournalHash);
        }

        RandomAccess.SetLength(target, state.TargetLength);
        RandomAccess.FlushToDisk(target);
        TransitionEmbeddedRecoveryState(target, state, PublicationRecoveryState.Clean, 0, 0, 0, 0, default);
    }

    /// <summary>
    /// Reads and validates the newest checksummed slot from the fixed version-two recovery-control page.<br/>
    /// A control page with no valid slot fails closed because choosing an old or new catalog image would otherwise be guesswork.<br/>
    /// </summary>
    /// <param name="target">The owning catalog handle.<br/></param>
    /// <returns>The newest independently valid recovery state.<br/></returns>
    private static PublicationRecoverySlot ReadEmbeddedRecoveryState(SafeFileHandle target)
    {
        byte[] page = new byte[PublicationRecoveryLayout.PageSize];
        ReadPublicationBytes(target, PublicationRecoveryLayout.PageOffset, page);
        if (!PublicationRecoveryLayout.TryReadLatest(page, out PublicationRecoverySlot state))
            throw new InvalidDataException("The version-two publication recovery-control page has no valid state slot.");
        return state;
    }

    /// <summary>
    /// Writes the next recovery state to the alternate control-page slot and forces it to stable storage.<br/>
    /// The prior slot is intentionally retained so a torn write can be rejected by checksum and recovery can resume from the previous state.<br/>
    /// </summary>
    /// <param name="target">The owning catalog handle.<br/></param>
    /// <param name="current">The newest valid state being advanced.<br/></param>
    /// <param name="nextState">The state to publish.<br/></param>
    /// <param name="originalLength">The pre-publication authoritative length, or zero for clean.<br/></param>
    /// <param name="targetLength">The post-publication authoritative length, or zero for clean.<br/></param>
    /// <param name="journalOffset">The temporary redo-image offset, or zero for clean.<br/></param>
    /// <param name="journalLength">The complete redo-image length, or zero for clean.<br/></param>
    /// <param name="journalHash">The redo-image SHA-256 hash, or an empty span for clean.<br/></param>
    /// <returns>The newly published state and slot index.<br/></returns>
    private static PublicationRecoverySlot TransitionEmbeddedRecoveryState(
        SafeFileHandle target,
        PublicationRecoverySlot current,
        PublicationRecoveryState nextState,
        long originalLength,
        long targetLength,
        long journalOffset,
        int journalLength,
        ReadOnlySpan<byte> journalHash)
    {
        if (current.Generation == long.MaxValue)
            throw new InvalidDataException("The publication recovery generation is exhausted.");
        bool validTransition = (current.State, nextState) switch
        {
            (PublicationRecoveryState.Clean, PublicationRecoveryState.Preparing) => true,
            (PublicationRecoveryState.Preparing, PublicationRecoveryState.Active) => true,
            (PublicationRecoveryState.Preparing, PublicationRecoveryState.Clean) => true,
            (PublicationRecoveryState.Active, PublicationRecoveryState.Complete) => true,
            (PublicationRecoveryState.Complete, PublicationRecoveryState.Clean) => true,
            _ => false
        };
        if (!validTransition)
        {
            throw new InvalidOperationException(
                $"Publication recovery state cannot transition from {current.State} to {nextState}.");
        }

        int nextSlotIndex = current.SlotIndex == 0 ? 1 : 0;
        long nextGeneration = current.Generation + 1;
        Span<byte> slotBytes = stackalloc byte[PublicationRecoveryLayout.SlotSize];
        PublicationRecoveryLayout.WriteSlot(
            slotBytes,
            nextState,
            nextGeneration,
            originalLength,
            targetLength,
            journalOffset,
            journalLength,
            journalHash);
        long slotOffset = PublicationRecoveryLayout.PageOffset +
            (nextSlotIndex == 0 ? PublicationRecoveryLayout.Slot0Offset : PublicationRecoveryLayout.Slot1Offset);
        RandomAccess.Write(target, slotBytes, slotOffset);
        RandomAccess.FlushToDisk(target);
        return new PublicationRecoverySlot(
            nextState,
            nextGeneration,
            originalLength,
            targetLength,
            journalOffset,
            journalLength,
            journalHash.ToArray(),
            nextSlotIndex);
    }

    /// <summary>
    /// Validates recovery-state lengths against the only physical file sizes reachable at a flushed protocol boundary.<br/>
    /// Unexpected later bytes fail closed instead of allowing a stale state slot to truncate unrelated durable data.<br/>
    /// </summary>
    /// <param name="state">The newest valid non-clean state.<br/></param>
    /// <param name="currentLength">The current physical file length.<br/></param>
    private static void ValidateEmbeddedRecoveryBounds(PublicationRecoverySlot state, long currentLength)
    {
        long minimumCatalogLength = PublicationRecoveryLayout.PageOffset + PublicationRecoveryLayout.PageSize;
        if (state.OriginalLength < minimumCatalogLength ||
            state.TargetLength < state.OriginalLength ||
            state.JournalOffset != state.TargetLength ||
            state.JournalLength < PublicationHeaderSize + PublicationHashSize ||
            state.JournalLength > MaxPublicationBytes ||
            state.JournalOffset > long.MaxValue - state.JournalLength)
        {
            throw new InvalidDataException("The version-two publication recovery state has invalid bounds.");
        }

        long journalEnd = state.JournalOffset + state.JournalLength;
        bool validLength = state.State switch
        {
            PublicationRecoveryState.Preparing => currentLength >= state.OriginalLength && currentLength <= journalEnd,
            PublicationRecoveryState.Active => currentLength == journalEnd,
            PublicationRecoveryState.Complete => currentLength == journalEnd || currentLength == state.TargetLength,
            _ => false
        };
        if (!validLength)
            throw new InvalidDataException("The version-two publication recovery state does not match the physical file length.");
    }

    /// <summary>
    /// Validates that an embedded redo image is complete, internally checksummed, and identical to the hash recorded by the active state slot.<br/>
    /// </summary>
    /// <param name="image">The complete in-file redo image.<br/></param>
    /// <param name="state">The active recovery slot that names and hashes the image.<br/></param>
    private static void ValidateEmbeddedJournalBinding(ReadOnlySpan<byte> image, PublicationRecoverySlot state)
    {
        if (image.Length != state.JournalLength)
            throw new InvalidDataException("The embedded publication image length does not match its recovery state.");
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(12));
        if (bodyLength < PublicationHeaderSize || bodyLength != image.Length - PublicationHashSize)
            throw new InvalidDataException("The embedded publication image header length is invalid.");
        ReadOnlySpan<byte> hash = image.Slice(bodyLength, PublicationHashSize);
        Span<byte> actualHash = stackalloc byte[PublicationHashSize];
        SHA256.HashData(image.Slice(0, bodyLength), actualHash);
        if (!CryptographicOperations.FixedTimeEquals(hash, actualHash) ||
            !CryptographicOperations.FixedTimeEquals(hash, state.JournalHash))
        {
            throw new InvalidDataException("The embedded publication image checksum does not match its recovery state.");
        }
        if (BinaryPrimitives.ReadInt64LittleEndian(image.Slice(40)) != state.OriginalLength ||
            BinaryPrimitives.ReadInt64LittleEndian(image.Slice(48)) != state.TargetLength)
        {
            throw new InvalidDataException("The embedded publication image lengths do not match its recovery state.");
        }
    }

    /// <summary>
    /// Validates and replays one complete redo image in its recorded commit-phase order.<br/>
    /// Immutable file identity, record bounds, checksum, and record count are all proven before the first backing write.<br/>
    /// </summary>
    /// <param name="target">The owning catalog handle.<br/></param>
    /// <param name="image">The complete validated-length redo image.<br/></param>
    private static void ReplayPublicationImage(SafeFileHandle target, ReadOnlySpan<byte> image)
    {
        ReadOnlySpan<byte> header = image.Slice(0, PublicationHeaderSize);
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != PublicationMagic ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8)) != 1 ||
            bodyLength < PublicationHeaderSize ||
            bodyLength != image.Length - PublicationHashSize)
        {
            throw new InvalidDataException("Invalid embedded publication recovery header.");
        }

        long identityOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16));
        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(40));
        long targetLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(48));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(56));
        if (originalLength < 16 || identityOffset < 0 || identityOffset > originalLength - 16 ||
            targetLength < originalLength || count < 0 || count > (bodyLength - PublicationHeaderSize) / 13)
        {
            throw new InvalidDataException("Invalid embedded publication recovery bounds.");
        }

        Span<byte> identity = stackalloc byte[16];
        ReadPublicationBytes(target, identityOffset, identity);
        if (!identity.SequenceEqual(header.Slice(24, 16)))
            throw new InvalidDataException("Embedded publication recovery belongs to another data file.");

        int cursor = PublicationHeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (cursor > bodyLength - 12)
                throw new InvalidDataException("Truncated embedded publication record.");
            long offset = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(cursor));
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(cursor + 8));
            cursor += 12;
            if (offset < 0 || bytes <= 0 || offset > targetLength - bytes || bytes > bodyLength - cursor)
                throw new InvalidDataException("Invalid embedded publication record bounds.");
            long start = Math.Max(offset, identityOffset);
            long end = Math.Min(offset + bytes, identityOffset + 16);
            if (start < end &&
                !image.Slice(cursor + checked((int)(start - offset)), checked((int)(end - start)))
                    .SequenceEqual(identity.Slice(checked((int)(start - identityOffset)), checked((int)(end - start)))))
            {
                throw new InvalidDataException("Embedded publication image changes its binding identity.");
            }
            cursor += bytes;
        }
        if (cursor != bodyLength)
            throw new InvalidDataException("Embedded publication record count does not match the body.");

        cursor = PublicationHeaderSize;
        for (int i = 0; i < count; i++)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(cursor));
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(cursor + 8));
            cursor += 12;
            RandomAccess.Write(target, image.Slice(cursor, bytes), offset);
            cursor += bytes;
            PublicationReplayCheckpoint?.Invoke(i);
        }
    }

    /// <summary>
    /// Reads the catalog superblock magic and format version without invoking catalog parsing.<br/>
    /// Raw or truncated files return zero so ordinary DataKernel use remains format-agnostic unless recoverable publication is requested.<br/>
    /// </summary>
    /// <param name="target">The opened file handle.<br/></param>
    /// <returns>The recognized LibraDex superblock version, or zero for a non-catalog file.<br/></returns>
    private static ushort ReadPublicationFormatVersion(SafeFileHandle target)
    {
        if (RandomAccess.GetLength(target) < SuperblockLayout.FormatVersionOffset + sizeof(ushort))
            return 0;
        Span<byte> prefix = stackalloc byte[SuperblockLayout.FormatVersionOffset + sizeof(ushort)];
        ReadPublicationBytes(target, 0, prefix);
        return BinaryPrimitives.ReadUInt64LittleEndian(prefix) == SuperblockLayout.Magic
            ? BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(SuperblockLayout.FormatVersionOffset))
            : (ushort)0;
    }

    /// <summary>Persists a completion receipt bound to this exact recovery image before allowing future mutations.<br/>
    /// A torn/incomplete receipt replays safely; a valid receipt prevents a stale directory entry from replaying over later writes.<br/></summary>
    private static void CompletePublication(string ready, ReadOnlySpan<byte> hash)
    {
        using var stream = new FileStream(ready, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[16]; stream.ReadExactly(header);
        long receiptOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12)) + PublicationHashSize;
        stream.Position = receiptOffset;
        Span<byte> receipt = stackalloc byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(receipt, PublicationCompletedMagic); hash.CopyTo(receipt.Slice(8));
        stream.Write(receipt); stream.SetLength(receiptOffset + receipt.Length); stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads an exact committed range for journal binding without consulting pending buffers.<br/></summary>
    private static void ReadPublicationBytes(SafeFileHandle source, long offset, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int next = RandomAccess.Read(source, destination.Slice(read), offset + read);
            if (next == 0) throw new EndOfStreamException("Publication identity range is truncated."); read += next;
        }
    }

    /// <summary>Refuses installation at a path with recovery sidecars that must not be detached from their owning image.<br/>
    /// Used at maintenance admission and just before replacement; this is not a cross-process namespace lock or an arbitrary-copy guarantee.<br/></summary>
    /// <param name="path">Exact destination or replacement path.<br/></param>
    internal static void RejectPublicationSidecarCollision(string path)
    {
        if (Path.Exists(path + PublicationPrepareSuffix) || Path.Exists(path + PublicationReadySuffix))
            throw new IOException($"Publication recovery files exist at destination '{path}'; recover or resolve that file before replacing it.");
    }
}
