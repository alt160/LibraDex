using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    private const int RecoverableFixtureOriginalLength = 16384;
    private const int RecoverableFixtureTargetLength = 20480;
    private const int RecoverableFixturePayloadOffset = 8192;

    /// <summary>
    /// Exercises version-two embedded redo publication across every flushed protocol state, partial main writes, torn state slots, interrupted replay, invalid images, stale completion state, and process termination.<br/>
    /// Every successful recovery is checked against an independent byte oracle while the intentionally evolving recovery-control page is masked from payload comparison.<br/>
    /// These deterministic boundaries prove protocol behavior after acknowledged file flushes; they do not claim that a storage device honors flushes when its own cache or firmware violates the platform contract.<br/>
    /// </summary>
    /// <param name="args">Optional child-process crash mode and shared artifact folder.<br/></param>
    /// <returns>Zero when every embedded-recovery case passes; otherwise one.<br/></returns>
    private static int RunRecoverablePublicationSanity(string[] args)
    {
        int crashArgument = Array.IndexOf(args, "--crash-mode");
        string? crashMode = crashArgument >= 0 ? args[crashArgument + 1] : null;
        string folder = crashMode is null
            ? Path.GetFullPath(Path.Combine("artifacts", "recoverable-publication-" + Guid.NewGuid().ToString("N")))
            : args[Array.IndexOf(args, "--crash-folder") + 1];
        Directory.CreateDirectory(folder);
        var reports = new List<object>();
        bool allPassed = true;
        string[] modes =
        {
            "normal", "Preparing", "JournalFlushed", "Prepared", "Payload", "DataFlushed", "Completed",
            "torn-first", "torn-middle", "torn-last", "torn-active-slot", "torn-complete-slot",
            "replay-first", "replay-last", "corrupt", "wrong-file", "truncated", "stale-completed",
            "identity-change", "format-change", "control-change", "noop"
        };
        if (crashMode is not null)
            modes = new[] { crashMode };

        foreach (bool flush in crashMode is null ? new[] { false, true } : new[] { true })
        foreach (string mode in modes)
        {
            string path = Path.Combine(folder, $"{mode}-{flush}.lbdx");
            bool passed = false;
            try
            {
                DataKernelOptions options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = flush };
                byte[] original = CreateRecoverableFixture(path, options);
                long journalBytes = 0;
                var watch = Stopwatch.StartNew();
                bool expectsNew = mode is not ("Preparing" or "JournalFlushed" or "torn-active-slot" or "identity-change" or "format-change" or "control-change" or "noop");

                using (var kernel = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    if (mode == "noop")
                    {
                        kernel.CommitRecoverablePublication(SuperblockLayout.FileGuidOffset);
                    }
                    else
                    {
                        RawDataReservation child = kernel.Reserve(4096);
                        RequireComponent(child.Extent.Offset == RecoverableFixtureOriginalLength, "Unexpected embedded-recovery child offset.");
                        child.Span.Fill(0x33);
                        kernel.ReserveAt(RecoverableFixturePayloadOffset, 4096).Span.Fill(0x22);
                        byte[] borrowed = Enumerable.Repeat((byte)0x44, 32).ToArray();
                        kernel.StageBorrowedAt(RecoverableFixturePayloadOffset + 8, borrowed, 5, 16);
                        if (mode == "identity-change")
                            kernel.StageWriteAt(SuperblockLayout.FileGuidOffset, Guid.NewGuid().ToByteArray());
                        if (mode == "format-change")
                            kernel.StageWriteAt(SuperblockLayout.FormatVersionOffset, new byte[] { 1, 0 });
                        if (mode == "control-change")
                            kernel.StageWriteAt(PublicationRecoveryLayout.PageOffset + 512, new byte[] { 0x7F });

                        string checkpoint = mode switch
                        {
                            "normal" => string.Empty,
                            "Preparing" or "JournalFlushed" or "Prepared" or "DataFlushed" or "Completed" => mode,
                            "stale-completed" => "Completed",
                            "torn-complete-slot" => "Completed",
                            _ => "Prepared"
                        };
                        if (mode == "Payload")
                        {
                            kernel.FileCommitPhaseCompleted = phase =>
                            {
                                if (phase != PendingSegmentPhase.Payload)
                                    return;
                                if (crashMode is not null)
                                    Environment.Exit(73);
                                throw new IOException("Injected publication interruption.");
                            };
                        }
                        else
                        {
                            kernel.PublicationCheckpoint = point =>
                            {
                                if (point != checkpoint)
                                    return;
                                if (crashMode is not null)
                                    Environment.Exit(73);
                                throw new IOException("Injected publication interruption.");
                            };
                        }

                        try
                        {
                            kernel.CommitRecoverablePublication(SuperblockLayout.FileGuidOffset);
                        }
                        catch (IOException ex) when (ex.Message == "Injected publication interruption.")
                        {
                        }
                        catch (InvalidOperationException ex) when (
                            (mode == "identity-change" && ex.Message == "A recoverable publication cannot change its file identity.") ||
                            (mode == "format-change" && ex.Message == "A recoverable publication cannot change its superblock magic or file-format version.") ||
                            (mode == "control-change" && ex.Message == "A recoverable publication cannot write into the version-two recovery-control page."))
                        {
                        }
                        journalBytes = kernel.LastPublicationJournalBytes;
                    }
                }

                if (mode.StartsWith("torn-", StringComparison.Ordinal) && mode is not ("torn-active-slot" or "torn-complete-slot"))
                    ApplyPartialPublicationRecord(path, mode);
                if (mode == "torn-active-slot" || mode == "torn-complete-slot")
                    CorruptLatestRecoverySlot(path);

                bool invalid = mode is "corrupt" or "wrong-file" or "truncated";
                if (invalid)
                {
                    PublicationRecoverySlot state = ReadFixtureRecoveryState(path);
                    if (mode == "corrupt")
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        stream.Position = state.JournalOffset + 80;
                        int value = stream.ReadByte();
                        stream.Position = state.JournalOffset + 80;
                        stream.WriteByte((byte)(value ^ 1));
                        stream.Flush(true);
                    }
                    else if (mode == "wrong-file")
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                        stream.Position = SuperblockLayout.FileGuidOffset;
                        stream.Write(Guid.NewGuid().ToByteArray());
                        stream.Flush(true);
                    }
                    else
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                        stream.SetLength(state.JournalOffset + state.JournalLength - 11);
                        stream.Flush(true);
                    }

                    byte[] before = File.ReadAllBytes(path);
                    bool rejected = false;
                    try
                    {
                        using var ignored = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions);
                    }
                    catch (InvalidDataException)
                    {
                        rejected = true;
                    }
                    RequireComponent(rejected && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "Invalid embedded recovery state did not fail closed before writes.");
                }
                else
                {
                    if (mode.StartsWith("replay-", StringComparison.Ordinal))
                    {
                        PublicationRecoverySlot state = ReadFixtureRecoveryState(path);
                        int recordCount = PublicationFixtureRecords(ReadFixtureJournal(path, state)).Count;
                        int stop = mode == "replay-first" ? 0 : recordCount - 1;
                        DataKernel.PublicationReplayCheckpoint = index =>
                        {
                            if (index == stop)
                                throw new IOException("Injected replay interruption.");
                        };
                        bool interrupted = false;
                        try
                        {
                            using var ignored = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions);
                        }
                        catch (IOException ex) when (ex.Message == "Injected replay interruption.")
                        {
                            interrupted = true;
                        }
                        finally
                        {
                            DataKernel.PublicationReplayCheckpoint = null;
                        }
                        RequireComponent(interrupted && ReadFixtureRecoveryState(path).State == PublicationRecoveryState.Active, "Interrupted replay did not retain its active embedded image.");
                    }

                    if (mode == "stale-completed")
                    {
                        PublicationRecoverySlot stale = ReadFixtureRecoveryState(path);
                        using (var recovered = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
                        {
                        }
                        using (var later = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
                        {
                            later.Reserve(1).Span[0] = 0x77;
                            later.Commit();
                        }
                        InstallNewerStaleCompleteSlot(path, stale);
                        byte[] before = File.ReadAllBytes(path);
                        bool rejected = false;
                        try
                        {
                            using var ignored = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions);
                        }
                        catch (InvalidDataException)
                        {
                            rejected = true;
                        }
                        RequireComponent(rejected && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "A stale completed state was allowed to truncate later durable bytes.");
                    }
                    else
                    {
                        using var reopened = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions);
                    }

                    if (mode != "stale-completed")
                    {
                        AssertRecoverableFixture(path, original, expectsNew);
                        RequireComponent(ReadFixtureRecoveryState(path).State == PublicationRecoveryState.Clean, "Embedded recovery state was not retired to clean.");
                        RequireComponent(!File.Exists(path + DataKernel.PublicationReadySuffix) && !File.Exists(path + DataKernel.PublicationPrepareSuffix), "New embedded recovery created a sidecar artifact.");
                    }
                }

                reports.Add(new { mode, flushPerCommit = flush, passed = true, journalBytes, elapsedMs = watch.Elapsed.TotalMilliseconds });
                Console.WriteLine($"PASS embedded recoverable publication {mode} flush={flush} journalBytes={journalBytes}");
                passed = true;
            }
            catch (Exception ex)
            {
                allPassed = false;
                reports.Add(new { mode, flushPerCommit = flush, error = ex.ToString() });
                Console.WriteLine(ex);
            }
            finally
            {
                DataKernel.PublicationReplayCheckpoint = null;
                if (passed && File.Exists(path))
                    File.Delete(path);
            }
        }

        if (crashMode is null)
        {
            allPassed &= CheckPublicationProcessExit(folder, reports);
            allPassed &= CheckVersionOneRecoverableRejection(folder, reports);
        }
        string report = Path.Combine(folder, "report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = allPassed, reports }));
        Console.WriteLine(report);
        return allPassed ? 0 : 1;
    }

    /// <summary>
    /// Creates a raw version-two file with a real superblock, initialized recovery-control page, immutable file identity, and deterministic payload bytes.<br/>
    /// </summary>
    private static byte[] CreateRecoverableFixture(string path, DataKernelOptions options)
    {
        byte[] original = new byte[RecoverableFixtureOriginalLength];
        new SuperblockWriter(original.AsSpan(0, SuperblockLayout.Size)).Initialize(
            0,
            RecoverableFixturePayloadOffset,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.UtcTicks,
            default,
            allocationDirectoryOffset: 12288,
            formatVersion: SuperblockLayout.RecoverableFormatVersion);
        PublicationRecoveryLayout.InitializePage(original.AsSpan(SuperblockLayout.Size, PublicationRecoveryLayout.PageSize));
        original.AsSpan(RecoverableFixturePayloadOffset).Fill(0x11);
        using var seed = DataKernel.Open(path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions);
        seed.StageWriteAt(0, original);
        seed.Commit();
        return original;
    }

    /// <summary>
    /// Compares all authoritative fixture bytes while ignoring the recovery-control page whose clean generation intentionally advances after each protocol.<br/>
    /// </summary>
    private static void AssertRecoverableFixture(string path, byte[] original, bool expectsNew)
    {
        byte[] expected = expectsNew ? new byte[RecoverableFixtureTargetLength] : original.ToArray();
        if (expectsNew)
        {
            original.CopyTo(expected, 0);
            expected.AsSpan(RecoverableFixturePayloadOffset, 4096).Fill(0x22);
            expected.AsSpan(RecoverableFixturePayloadOffset + 8, 16).Fill(0x44);
            expected.AsSpan(RecoverableFixtureOriginalLength, 4096).Fill(0x33);
        }
        byte[] actual = File.ReadAllBytes(path);
        RequireComponent(actual.Length == expected.Length, $"Recovered fixture length mismatch: expected={expected.Length}, actual={actual.Length}.");
        actual.AsSpan(SuperblockLayout.Size, PublicationRecoveryLayout.PageSize).CopyTo(expected.AsSpan(SuperblockLayout.Size, PublicationRecoveryLayout.PageSize));
        RequireComponent(actual.AsSpan().SequenceEqual(expected), "Recovered version-two file differs from the independent byte oracle.");
    }

    /// <summary>
    /// Reads the newest valid recovery-control slot directly from a fixture without opening it through the production recovery path.<br/>
    /// </summary>
    private static PublicationRecoverySlot ReadFixtureRecoveryState(string path)
    {
        byte[] page = new byte[PublicationRecoveryLayout.PageSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = PublicationRecoveryLayout.PageOffset;
        stream.ReadExactly(page);
        RequireComponent(PublicationRecoveryLayout.TryReadLatest(page, out PublicationRecoverySlot state), "Fixture recovery-control page has no valid slot.");
        return state;
    }

    /// <summary>
    /// Reads the complete embedded redo image named by one validated active or complete state slot.<br/>
    /// </summary>
    private static byte[] ReadFixtureJournal(string path, PublicationRecoverySlot state)
    {
        byte[] image = new byte[state.JournalLength];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = state.JournalOffset;
        stream.ReadExactly(image);
        return image;
    }

    /// <summary>
    /// Applies half of one selected redo record to emulate a torn or interrupted authoritative write after the active state became durable.<br/>
    /// </summary>
    private static void ApplyPartialPublicationRecord(string path, string mode)
    {
        PublicationRecoverySlot state = ReadFixtureRecoveryState(path);
        List<(long Offset, byte[] Bytes)> records = PublicationFixtureRecords(ReadFixtureJournal(path, state));
        int selected = mode == "torn-first" ? 0 : mode == "torn-last" ? records.Count - 1 : records.Count / 2;
        (long offset, byte[] bytes) = records[selected];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        stream.Write(bytes, 0, Math.Max(1, bytes.Length / 2));
        stream.Flush(true);
    }

    /// <summary>
    /// Corrupts the checksum area of the newest slot so production recovery must select the preceding independently valid state.<br/>
    /// </summary>
    private static void CorruptLatestRecoverySlot(string path)
    {
        PublicationRecoverySlot state = ReadFixtureRecoveryState(path);
        long offset = PublicationRecoveryLayout.PageOffset +
            (state.SlotIndex == 0 ? PublicationRecoveryLayout.Slot0Offset : PublicationRecoveryLayout.Slot1Offset) + 100;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 1));
        stream.Flush(true);
    }

    /// <summary>
    /// Publishes a syntactically valid but stale complete state with a newer generation after unrelated bytes were appended.<br/>
    /// Recovery must reject the impossible physical length rather than truncate the later durable append.<br/>
    /// </summary>
    private static void InstallNewerStaleCompleteSlot(string path, PublicationRecoverySlot stale)
    {
        PublicationRecoverySlot clean = ReadFixtureRecoveryState(path);
        int slotIndex = clean.SlotIndex == 0 ? 1 : 0;
        Span<byte> slot = stackalloc byte[PublicationRecoveryLayout.SlotSize];
        PublicationRecoveryLayout.WriteSlot(
            slot,
            PublicationRecoveryState.Complete,
            clean.Generation + 1,
            stale.OriginalLength,
            stale.TargetLength,
            stale.JournalOffset,
            stale.JournalLength,
            stale.JournalHash);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = PublicationRecoveryLayout.PageOffset +
            (slotIndex == 0 ? PublicationRecoveryLayout.Slot0Offset : PublicationRecoveryLayout.Slot1Offset);
        stream.Write(slot);
        stream.Flush(true);
    }

    /// <summary>
    /// Reads prepared fixture records to choose deterministic partial-write and replay-interruption locations.<br/>
    /// This is test-only parsing; production recovery independently validates the complete checksummed format before replay.<br/>
    /// </summary>
    private static List<(long Offset, byte[] Bytes)> PublicationFixtureRecords(byte[] image)
    {
        var records = new List<(long Offset, byte[] Bytes)>();
        int count = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(56));
        int cursor = 64;
        for (int i = 0; i < count; i++)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(cursor));
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(cursor + 8));
            cursor += 12;
            records.Add((offset, image.AsSpan(cursor, bytes).ToArray()));
            cursor += bytes;
        }
        return records;
    }

    /// <summary>
    /// Terminates isolated harness processes at each flushed protocol boundary without running managed disposal.<br/>
    /// Preparing and journal-flushed states must preserve the old image; active and later states must recover the new image.<br/>
    /// </summary>
    private static bool CheckPublicationProcessExit(string folder, List<object> reports)
    {
        bool passed = true;
        foreach (string point in new[] { "Preparing", "JournalFlushed", "Prepared", "Payload", "DataFlushed", "Completed" })
        {
            string childFolder = Path.Combine(folder, "process-" + point);
            Directory.CreateDirectory(childFolder);
            string path = Path.Combine(childFolder, point + "-True.lbdx");
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "recoverable-publication-sanity", "--crash-mode", point, "--crash-folder", childFolder })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(30000))
                {
                    child.Kill();
                    child.WaitForExit();
                    throw new TimeoutException("Embedded-publication crash child timed out.");
                }
                RequireComponent(child.ExitCode == 73, $"Crash checkpoint '{point}' was not reached.");
                byte[] original = File.ReadAllBytes(path).AsSpan(0, RecoverableFixtureOriginalLength).ToArray();
                using (var reopened = DataKernel.Open(path, FileMode.Open, CreateDesignPerfOptions(), DataKernelTelemetryOptions.EnabledOptions))
                {
                }
                bool expectsNew = point is not ("Preparing" or "JournalFlushed");
                AssertRecoverableFixture(path, original, expectsNew);
                RequireComponent(ReadFixtureRecoveryState(path).State == PublicationRecoveryState.Clean, "Process-exit recovery did not finish clean.");
                reports.Add(new { processExit = point, expectedImage = expectsNew ? "new" : "old", passed = true });
                Console.WriteLine($"PASS embedded process exit {point} -> {(expectsNew ? "new" : "old")}");
                File.Delete(path);
            }
            catch (Exception ex)
            {
                passed = false;
                reports.Add(new { processExit = point, error = ex.ToString() });
                Console.WriteLine(ex);
            }
        }
        return passed;
    }

    /// <summary>
    /// Proves that protected publication rejects a version-one file without changing its bytes or creating a sidecar.<br/>
    /// This is the no-silent-upgrade boundary; version-one remains readable through ordinary open and can be migrated only by an explicit rebuild such as compaction.<br/>
    /// </summary>
    private static bool CheckVersionOneRecoverableRejection(string folder, List<object> reports)
    {
        string path = Path.Combine(folder, "version-one-rejection.lbdx");
        try
        {
            DataKernelOptions options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = true };
            byte[] bytes = new byte[8192];
            new SuperblockWriter(bytes.AsSpan(0, SuperblockLayout.Size)).Initialize(
                0,
                4096,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.UtcTicks,
                default,
                formatVersion: SuperblockLayout.LegacyFormatVersion);
            using (var seed = DataKernel.Open(path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                seed.StageWriteAt(0, bytes);
                seed.Commit();
            }
            byte[] before = File.ReadAllBytes(path);
            bool rejected = false;
            using (var kernel = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                kernel.StageWriteAt(4096, new byte[] { 1, 2, 3, 4 });
                try
                {
                    kernel.CommitRecoverablePublication(SuperblockLayout.FileGuidOffset);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("version-two recoverable file format", StringComparison.Ordinal))
                {
                    rejected = true;
                }
            }
            RequireComponent(rejected && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "Version-one recoverable publication changed bytes or was not rejected.");
            RequireComponent(!File.Exists(path + DataKernel.PublicationReadySuffix) && !File.Exists(path + DataKernel.PublicationPrepareSuffix), "Version-one rejection created a sidecar.");
            reports.Add(new { versionOneProtectedPublicationRejected = true });
            Console.WriteLine("PASS version-one protected publication rejected without mutation");
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            reports.Add(new { versionOneProtectedPublicationRejected = false, error = ex.ToString() });
            Console.WriteLine(ex);
            return false;
        }
    }
}
