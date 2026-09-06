using System.Buffers.Binary;
using LibraDex;
using LibraDex.Layouts;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves the first durable file-extent allocator contract without depending on one shelf family.<br/>
    /// The fixture covers same-offset reuse before EOF growth, reopen reconstruction, discarded-retirement safety, unsupported-length append fallback, and coherent-reader publication blocking.<br/>
    /// Successful fixture files are deleted in the finalizer so the diagnostic does not accumulate database artifacts.<br/>
    /// </summary>
    /// <param name="args">No command arguments are currently supported.<br/></param>
    /// <returns>Zero when every allocator invariant passes.<br/></returns>
    private static int RunFileExtentAllocatorSanity(string[] args)
    {
        if (args.Length != 1 || !string.Equals(args[0], "file-extent-allocator-sanity", StringComparison.Ordinal))
            throw new ArgumentException("file-extent-allocator-sanity does not accept arguments.", nameof(args));

        string path = Path.Combine(Path.GetTempPath(), $"libradex-file-allocator-{Guid.NewGuid():N}.lbdx");
        DataKernelOptions options = new(
            AppendBufferSize: 64 * 1024,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: true,
            MaxCommitGapCoalesceBytes: 512);
        long directoryOffset = 0;
        long firstSlotOffset = 0;
        long alternateSlotOffset = 0;
        long fileLengthAfterFirstSegment = 0;
        try
        {
            ProveStagedMemoryExtentRetirement(options);
            ProveFileCommitPhaseFailureSafety(options);
            using (DataKernel kernel = DataKernel.Open(path, FileMode.Create, options, DataKernelTelemetryOptions.Disabled))
            {
                RawDataReservation reservedPrefix = kernel.Reserve(FileAllocationDirectoryLayout.Size);
                reservedPrefix.Span.Clear();
                RawDataReservation directory = kernel.Reserve(FileAllocationDirectoryLayout.Size);
                FileAllocationDirectoryLayout.Initialize(directory.Span);
                directoryOffset = directory.Extent.Offset;
                byte[] directoryBytes = directory.Span.ToArray();
                kernel.Commit();
                kernel.ConfigureNewFileExtentAllocator(directoryOffset, directoryBytes);

                RawDataReservation first = kernel.Reserve(4096);
                firstSlotOffset = first.Extent.Offset;
                first.Span.Fill(0x31);
                kernel.Commit();
                fileLengthAfterFirstSegment = new FileInfo(path).Length;

                DataKernel.CoherentReadLease reader = kernel.EnterCoherentRead();
                Task retireTask = Task.Run(() =>
                {
                    kernel.StageExtentRetirement(firstSlotOffset, 4096);
                    kernel.Commit();
                });
                if (retireTask.Wait(TimeSpan.FromMilliseconds(150)))
                    throw new InvalidDataException("File extent retirement published while a coherent reader still held the prior file image.");

                reader.Dispose();
                if (!retireTask.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("File extent retirement did not resume after the coherent reader completed.");

                RawDataReservation reused = kernel.Reserve(4096);
                if (reused.Extent.Offset != firstSlotOffset)
                    throw new InvalidDataException($"Expected first reusable slot {firstSlotOffset:N0}, but allocator selected {reused.Extent.Offset:N0}.");
                reused.Span.Fill(0x42);
                kernel.Commit();
                if (new FileInfo(path).Length != fileLengthAfterFirstSegment)
                    throw new InvalidDataException("Same-class reuse extended EOF even though a compatible retired slot existed.");

                kernel.StageExtentRetirement(firstSlotOffset, 4096);
                kernel.DiscardPending();
                RawDataReservation alternate = kernel.Reserve(4096);
                alternateSlotOffset = alternate.Extent.Offset;
                if (alternateSlotOffset == firstSlotOffset)
                    throw new InvalidDataException("Discarded retirement made an occupied slot reusable.");
                alternate.Span.Fill(0x53);
                kernel.Commit();

                kernel.StageExtentRetirement(alternateSlotOffset, 4096);
                kernel.Commit();
            }

            using (DataKernel reopened = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.Disabled))
            {
                reopened.ConfigureFileExtentAllocator(directoryOffset);
                RawDataReservation reopenedReuse = reopened.Reserve(4096);
                if (reopenedReuse.Extent.Offset != alternateSlotOffset)
                    throw new InvalidDataException($"Reopened allocator selected {reopenedReuse.Extent.Offset:N0}; expected retired slot {alternateSlotOffset:N0}.");
                reopenedReuse.Span.Fill(0x64);
                reopened.Commit();

                long beforeFallback = new FileInfo(path).Length;
                RawDataReservation unsupported = reopened.Reserve(5000);
                if (unsupported.Extent.Offset < beforeFallback)
                    throw new InvalidDataException("Unsupported extent length entered a homogeneous allocation segment instead of the append fallback.");
                unsupported.Span.Fill(0x75);
                reopened.Commit();
            }

            Console.WriteLine("File extent allocator sanity passed.");
            Console.WriteLine($"Directory offset: {directoryOffset:N0}");
            Console.WriteLine($"Reusable slot offset: {firstSlotOffset:N0}");
            Console.WriteLine($"Reopen-reused slot offset: {alternateSlotOffset:N0}");
            Console.WriteLine($"First segment file length: {fileLengthAfterFirstSegment:N0} bytes");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Proves that memory-backed structural retirement obeys the same publication boundary as file-backed bitmap retirement.<br/>
    /// A discarded retirement must leave the committed bytes readable, while a committed retirement must make the exact extent reusable without advancing the arena append cursor.<br/>
    /// </summary>
    /// <param name="options">The raw DataKernel options shared with the file-backed allocator fixture.<br/></param>
    private static void ProveStagedMemoryExtentRetirement(DataKernelOptions options)
    {
        using DataKernel kernel = DataKernel.OpenMemory(options, DataKernelTelemetryOptions.Disabled);
        RawDataReservation reservedPrefix = kernel.Reserve(4096);
        reservedPrefix.Span.Clear();
        kernel.Commit();
        RawDataReservation first = kernel.Reserve(4096);
        long firstOffset = first.Extent.Offset;
        first.Span.Fill(0x31);
        kernel.Commit();

        kernel.StageExtentRetirement(firstOffset, 4096);
        kernel.DiscardPending();
        byte[] retained = new byte[4096];
        kernel.Read(firstOffset, retained);
        if (retained[0] != 0x31 || retained[^1] != 0x31)
            throw new InvalidDataException("Discarded memory retirement released or altered still-authoritative bytes.");

        kernel.StageExtentRetirement(firstOffset, 4096);
        kernel.Commit();
        RawDataReservation reused = kernel.Reserve(4096);
        if (reused.Extent.Offset != firstOffset)
        {
            throw new InvalidDataException(
                $"Committed memory retirement did not reuse offset {firstOffset:N0}; selected {reused.Extent.Offset:N0}.");
        }
    }

    /// <summary>
    /// Proves the allocator's conservative crash asymmetry at every independently durable file commit phase.<br/>
    /// A failure after allocation claim or payload must leave the old root authoritative; a failure after publication must expose the complete replacement while retaining the old slot; only a completed retirement phase may make the old slot reusable.<br/>
    /// Each scenario reopens a copied seed image through a new kernel, so no process-local allocator state can compensate for or conceal the bytes actually persisted before the injected failure.<br/>
    /// </summary>
    /// <param name="options">The flush-enabled file policy used by the allocator fixture.<br/></param>
    private static void ProveFileCommitPhaseFailureSafety(DataKernelOptions options)
    {
        string seedPath = Path.Combine(Path.GetTempPath(), $"libradex-file-allocator-crash-seed-{Guid.NewGuid():N}.lbdx");
        List<string> scenarioPaths = [];
        const int extentLength = 4096;
        const byte oldMarker = 0x31;
        const byte replacementMarker = 0x42;
        long directoryOffset;
        long oldSlotOffset;
        try
        {
            using (DataKernel seed = DataKernel.Open(seedPath, FileMode.Create, options, DataKernelTelemetryOptions.Disabled))
            {
                RawDataReservation rootCellPage = seed.Reserve(FileAllocationDirectoryLayout.Size);
                rootCellPage.Span.Clear();
                RawDataReservation directory = seed.Reserve(FileAllocationDirectoryLayout.Size);
                FileAllocationDirectoryLayout.Initialize(directory.Span);
                directoryOffset = directory.Extent.Offset;
                byte[] directoryBytes = directory.Span.ToArray();
                seed.Commit();
                seed.ConfigureNewFileExtentAllocator(directoryOffset, directoryBytes);

                RawDataReservation oldSlot = seed.Reserve(extentLength);
                oldSlotOffset = oldSlot.Extent.Offset;
                oldSlot.Span.Fill(oldMarker);
                RawDataReservation initialRoot = seed.ReserveAt(0, sizeof(long));
                BinaryPrimitives.WriteInt64LittleEndian(initialRoot.Span, oldSlotOffset);
                seed.Commit();
            }

            foreach (PendingSegmentPhase injectedPhase in Enum.GetValues<PendingSegmentPhase>())
            {
                string scenarioPath = Path.Combine(
                    Path.GetTempPath(),
                    $"libradex-file-allocator-crash-{injectedPhase}-{Guid.NewGuid():N}.lbdx");
                scenarioPaths.Add(scenarioPath);
                File.Copy(seedPath, scenarioPath);

                long replacementOffset;
                using (DataKernel interrupted = DataKernel.Open(scenarioPath, FileMode.Open, options, DataKernelTelemetryOptions.Disabled))
                {
                    interrupted.ConfigureFileExtentAllocator(directoryOffset);
                    RawDataReservation replacement = interrupted.Reserve(extentLength);
                    replacementOffset = replacement.Extent.Offset;
                    replacement.Span.Fill(replacementMarker);
                    RawDataReservation replacementRoot = interrupted.ReserveAt(0, sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(replacementRoot.Span, replacementOffset);
                    interrupted.StageExtentRetirement(oldSlotOffset, extentLength);

                    const string injectedMessage = "Injected file commit phase interruption.";
                    interrupted.FileCommitPhaseCompleted = completedPhase =>
                    {
                        if (completedPhase == injectedPhase)
                            throw new InvalidOperationException(injectedMessage);
                    };

                    try
                    {
                        interrupted.Commit();
                        throw new InvalidDataException($"Commit did not stop after the injected {injectedPhase} phase.");
                    }
                    catch (InvalidOperationException ex) when (string.Equals(ex.Message, injectedMessage, StringComparison.Ordinal))
                    {
                    }
                }

                using DataKernel reopened = DataKernel.Open(scenarioPath, FileMode.Open, options, DataKernelTelemetryOptions.Disabled);
                reopened.ConfigureFileExtentAllocator(directoryOffset);
                byte[] rootBytes = new byte[sizeof(long)];
                reopened.Read(0, rootBytes);
                long publishedRootOffset = BinaryPrimitives.ReadInt64LittleEndian(rootBytes);
                bool replacementPublished = injectedPhase >= PendingSegmentPhase.Publication;
                long expectedRootOffset = replacementPublished ? replacementOffset : oldSlotOffset;
                if (publishedRootOffset != expectedRootOffset)
                {
                    throw new InvalidDataException(
                        $"Injected {injectedPhase} phase reopened root {publishedRootOffset:N0}; expected {expectedRootOffset:N0}.");
                }

                byte[] authoritativePayload = new byte[extentLength];
                reopened.Read(publishedRootOffset, authoritativePayload);
                byte expectedMarker = replacementPublished ? replacementMarker : oldMarker;
                if (authoritativePayload[0] != expectedMarker || authoritativePayload[^1] != expectedMarker)
                    throw new InvalidDataException($"Injected {injectedPhase} phase exposed an incomplete authoritative payload.");

                RawDataReservation next = reopened.Reserve(extentLength);
                if (injectedPhase == PendingSegmentPhase.Retirement)
                {
                    if (next.Extent.Offset != oldSlotOffset)
                    {
                        throw new InvalidDataException(
                            $"Completed retirement did not make old slot {oldSlotOffset:N0} reusable after reopen; selected {next.Extent.Offset:N0}.");
                    }
                }
                else if (next.Extent.Offset == oldSlotOffset || next.Extent.Offset == replacementOffset)
                {
                    throw new InvalidDataException(
                        $"Injected {injectedPhase} phase made an occupied old or replacement slot reusable after reopen. " +
                        $"Selected={next.Extent.Offset:N0}; Old={oldSlotOffset:N0}; Replacement={replacementOffset:N0}.");
                }
            }
        }
        finally
        {
            if (File.Exists(seedPath))
                File.Delete(seedPath);
            for (int pathIndex = 0; pathIndex < scenarioPaths.Count; pathIndex++)
            {
                if (File.Exists(scenarioPaths[pathIndex]))
                    File.Delete(scenarioPaths[pathIndex]);
            }
        }
    }
}
