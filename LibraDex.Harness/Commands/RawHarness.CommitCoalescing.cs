using LibraDex;

internal static partial class RawHarness
{
    /// <summary>Exercises real pending-segment overlap independently of writable-reservation reuse.<br/>
    /// Borrowed slices retain separate segment identities and nonzero source offsets through commit.<br/>
    /// File and memory cases run independently; failures cannot suppress the remaining coverage.<br/>
    /// Checks complete byte images, file reopen, exact I/O counts, gap thresholds, and phase-boundary bytes.<br/></summary>
    private static void RunCommitCoalescingMatrix(string folder, List<Exception> failures)
    {
        string[] cases = { "full", "partial", "nested", "adjacent", "gap-disabled", "gap-boundary", "gap-rejected", "cross-partial", "cross-full" };
        foreach (var backing in new[] { DataKernelBackingKind.File, DataKernelBackingKind.Memory })
        foreach (bool flush in new[] { false, true })
        foreach (string scenario in cases)
        {
            string path = Path.Combine(folder, $"{scenario}-{backing}-{flush}-{Guid.NewGuid():N}.lbdx");
            try
            {
                bool file = backing == DataKernelBackingKind.File, cross = scenario.StartsWith("cross-", StringComparison.Ordinal);
                int gapLimit = scenario == "gap-disabled" ? 0 : scenario == "gap-rejected" ? 7 : 8;
                var options = new DataKernelOptions(AppendBufferSize: 64, ReservedPrefixBytes: 0, FlushToDiskOnCommit: flush, MaxCommitGapCoalesceBytes: gapLimit);
                byte[] expected = new byte[cross ? 96 : 64]; expected.AsSpan().Fill(0x55);
                long expectedWrites, expectedBytes, expectedGaps = 0; int segments;
                using (var kernel = OpenKernel(backing, path, FileMode.CreateNew, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    kernel.Reserve(64).Span.Fill(0x55); kernel.Commit();
                    (int Offset, int Length)[] writes;
                    if (cross)
                    {
                        var append = kernel.Reserve(32);
                        if (append.Extent.Offset != 64) throw new InvalidDataException("Unexpected append offset.");
                        append.Span.Fill(0x33); expected.AsSpan(64).Fill(0x33);
                        writes = scenario == "cross-full" ? new[] { (64, 32) } : new[] { (72, 16) };
                        expectedWrites = 2; expectedBytes = 32 + writes[0].Length; segments = file ? 2 : 1;
                    }
                    else
                    {
                        writes = scenario switch
                        {
                            "full" => new[] { (0, 16), (0, 16) },
                            "partial" => new[] { (0, 16), (8, 16) },
                            "nested" => new[] { (0, 32), (8, 8), (16, 8) },
                            "adjacent" => new[] { (0, 8), (8, 8) },
                            _ => new[] { (0, 8), (16, 8) }
                        };
                        segments = writes.Length;
                        expectedWrites = file || scenario == "full" ? 1 : writes.Length;
                        expectedBytes = scenario switch
                        {
                            "full" => 16,
                            "partial" => file ? 24 : 32,
                            "nested" => file ? 32 : 48,
                            "gap-boundary" => file ? 24 : 16,
                            _ => 16
                        };
                        if (file && scenario is "gap-disabled" or "gap-rejected") expectedWrites = 2;
                        if (file && scenario == "gap-boundary") expectedGaps = 1;
                    }
                    for (int i = 0; i < writes.Length; i++)
                    {
                        var write = writes[i]; byte[] source = new byte[write.Length + 7];
                        source.AsSpan().Fill((byte)(0x70 + i));
                        kernel.StageBorrowedAt(write.Offset, source, 3, write.Length);
                        expected.AsSpan(write.Offset, write.Length).Fill((byte)(0x70 + i));
                    }
                    int phases = 0;
                    kernel.FileCommitPhaseCompleted = phase =>
                    {
                        phases++;
                        byte[] actual = ReadSparseBacking(path);
                        byte[] phaseExpected = (byte[])expected.Clone();
                        if (cross && phase == PendingSegmentPhase.Payload) phaseExpected.AsSpan(64).Fill(0x33);
                        if (phase != (cross && phases == 1 ? PendingSegmentPhase.Payload : PendingSegmentPhase.Publication) || !actual.AsSpan().SequenceEqual(phaseExpected))
                            throw new InvalidDataException($"Phase order/bytes mismatch: {scenario} {phase}.");
                    };
                    byte[] pending = new byte[expected.Length]; kernel.Read(0, pending);
                    if (!pending.AsSpan().SequenceEqual(expected))
                    {
                        int mismatch = 0;
                        while (pending[mismatch] == expected[mismatch]) mismatch++;
                        var failure = new InvalidDataException($"Pending overlay {scenario} {backing} flush={flush}: first mismatch at {mismatch}, actual={pending[mismatch]:X2}, expected={expected[mismatch]:X2}.");
                        failures.Add(failure); Console.WriteLine("FAIL " + failure.Message);
                    }
                    var commit = kernel.Commit();
                    ValidateKernelRange(kernel, 0, expected, scenario + " committed");
                    if (commit.StagedSegmentCount != segments || commit.BackingWriteCallCount != expectedWrites || commit.BytesWritten != expectedBytes ||
                        commit.WriteCallCount != (file ? expectedWrites : 0) || commit.CoalescedGapCount != expectedGaps || commit.CoalescedGapBytes != expectedGaps * 8 ||
                        commit.SetLengthCallCount != 0 || phases != (file ? cross ? 2 : 1 : 0) || commit.FlushCallCount != (flush ? phases : 0))
                        throw new InvalidDataException($"{scenario} {backing}: segments={commit.StagedSegmentCount}/{segments} writes={commit.BackingWriteCallCount}/{expectedWrites} bytes={commit.BytesWritten}/{expectedBytes} gaps={commit.CoalescedGapCount}/{expectedGaps} phases={phases} flushes={commit.FlushCallCount}.");
                }
                if (file)
                {
                    using (var reopened = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
                        ValidateKernelRange(reopened, 0, expected, scenario + " reopened");
                    if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Independent backing bytes mismatch.");
                    File.Delete(path);
                }
                Console.WriteLine($"PASS committed coalescing {scenario} {backing} flush={flush} writes={expectedWrites} bytes={expectedBytes}");
            }
            catch (Exception ex) { failures.Add(ex); Console.WriteLine($"FAIL coalescing {scenario} {backing} flush={flush}: {ex}"); }
        }
    }
}
