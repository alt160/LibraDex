using System.Diagnostics;
using System.Text.Json;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>Checks pending visibility against a byte-array oracle, plus committed snapshot isolation.<br/>
    /// Optional perf mode measures existing-correct aligned reads so baseline and repaired kernels do identical logical work.<br/></summary>
    private static int RunPendingReadProbe(string[] args)
    {
        if (args.Contains("--perf")) return RunPendingReadPerf();
        string folder = Path.GetFullPath(Path.Combine("artifacts", "pending-reads-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder); int checks = 0;
        var options = new DataKernelOptions(AppendBufferSize: 4096, ReservedPrefixBytes: 0, FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 0);
        foreach (var backing in new[] { DataKernelBackingKind.File, DataKernelBackingKind.Memory })
        for (int seed = 0; seed < 16; seed++)
        {
            string path = Path.Combine(folder, $"{backing}-{seed}.lbdx");
            byte[] initial = new byte[2048]; new Random(seed).NextBytes(initial); byte[] expected = (byte[])initial.Clone();
            var random = new Random(7301 + seed);
            using (var kernel = OpenKernel(backing, path, FileMode.CreateNew, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                initial.CopyTo(kernel.Reserve(initial.Length).Span); kernel.Commit();
                for (int write = 0; write < 64; write++)
                {
                    int offset = random.Next(2048), length = random.Next(1, Math.Min(128, 2048 - offset) + 1);
                    byte[] source = new byte[length + 11]; random.NextBytes(source);
                    kernel.StageBorrowedAt(offset, source, 5, length); source.AsSpan(5, length).CopyTo(expected.AsSpan(offset));
                    ValidateKernelRange(kernel, 0, expected, "random whole pending"); checks++;
                    for (int read = 0; read < 3; read++)
                    {
                        int start = random.Next(2048), size = random.Next(1, 2048 - start + 1);
                        ValidateKernelRange(kernel, start, expected.AsSpan(start, size), "random sliced pending"); checks++;
                    }
                    kernel.Read(2048, Span<byte>.Empty); checks++;
                }
                if ((seed & 1) == 0) kernel.Commit();
                else { kernel.DiscardPending(); expected = initial; }
                ValidateKernelRange(kernel, 0, expected, "commit/discard oracle"); checks++;
            }
            if (backing == DataKernelBackingKind.File)
            {
                using (var reopened = DataKernel.Open(path, FileMode.Open, options, DataKernelTelemetryOptions.EnabledOptions))
                    ValidateKernelRange(reopened, 0, expected, "reopen oracle");
                checks++; File.Delete(path);
            }
        }
        foreach (var backing in new[] { DataKernelBackingKind.File, DataKernelBackingKind.Memory })
        foreach (bool hole in new[] { false, true })
        {
            string path = Path.Combine(folder, $"append-fallback-{backing}-{hole}.lbdx");
            using (var kernel = OpenKernel(backing, path, FileMode.CreateNew, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                kernel.Reserve(128).Span.Clear(); kernel.Commit(); byte[] expected = new byte[512];
                for (int i = 0; i < 64; i++)
                {
                    if (hole && i == 32) continue;
                    byte[] data = new byte[8]; data.AsSpan().Fill((byte)(i + 1));
                    kernel.StageBorrowedAt(i * 8, data, 0, 8); data.CopyTo(expected.AsSpan(i * 8));
                }
                if (hole)
                {
                    bool rejected = false;
                    try { kernel.Read(0, new byte[512]); } catch (EndOfStreamException) { rejected = true; }
                    if (!rejected) throw new InvalidDataException("An unbacked hole was incorrectly treated as readable.");
                }
                else ValidateKernelRange(kernel, 0, expected, "partial-backing overlay fallback");
                checks++;
            }
            if (backing == DataKernelBackingKind.File) File.Delete(path);
        }
        using (var kernel = DataKernel.OpenMemory(options, DataKernelTelemetryOptions.EnabledOptions))
        {
            byte[] expected = new byte[2048]; kernel.Reserve(2048).Span.Clear(); kernel.Commit();
            for (int i = 0; i < 1024; i++) { kernel.StageBorrowedAt(i * 2 + 1, new byte[] { 0x77 }, 0, 1); expected[i * 2 + 1] = 0x77; }
            ValidateKernelRange(kernel, 0, expected, "1024 disjoint fragments"); checks++;
            byte[] destination = new byte[2048]; long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) kernel.Read(0, destination);
            long ticks = Stopwatch.GetTimestamp() - start; allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"Fragmented 2KiB read: 1024 staged bytes, averageMs={ticks * 1000d / Stopwatch.Frequency / 100:F4}, allocated={allocated}");
        }
        using (var kernel = DataKernel.OpenMemory(options, DataKernelTelemetryOptions.EnabledOptions))
        {
            byte[] original = new byte[64], changed = new byte[64]; changed.AsSpan(8, 16).Fill(0x77);
            kernel.Reserve(64).Span.Clear(); kernel.Commit();
            using (var scope = kernel.EnterCoherentRead())
            {
                kernel.StageBorrowedAt(8, changed, 8, 16);
                ValidateKernelRange(kernel, 0, original, "coherent before commit"); checks++;
                kernel.Commit();
                ValidateKernelRange(kernel, 0, original, "coherent after commit"); checks++;
            }
            ValidateKernelRange(kernel, 0, changed, "after coherent scope"); checks++;
            kernel.EnterExclusiveStoragePublication();
            try
            {
                kernel.StageBorrowedAt(0, new byte[64], 0, 64); kernel.Commit();
                ValidateKernelRange(kernel, 0, changed, "ambient publication snapshot"); checks++;
            }
            finally { kernel.ExitExclusiveStoragePublication(); }
            ValidateKernelRange(kernel, 0, original, "after publication snapshot"); checks++;
        }
        Console.WriteLine($"PASS pending-read-probe checks={checks} seeds=32 writes=2048 snapshots=5");
        return 0;
    }

    /// <summary>Measures allocation-free kernel reads with no pending data or one/many staged segments.<br/>
    /// Latest/oldest/backing reads are identical and correct on the baseline; warm rounds report nanoseconds per 32-byte read.<br/>
    /// The backing case lies before all pending segments, exposing scan cost without changing the returned bytes.<br/></summary>
    private static int RunPendingReadPerf()
    {
        const int iterations = 100000;
        var options = new DataKernelOptions(AppendBufferSize: 4096, ReservedPrefixBytes: 0, FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 0);
        foreach (int count in new[] { 0, 1, 64, 512 })
        foreach (string mode in count == 0 ? new[] { "backing" } : new[] { "latest", "oldest", "backing" })
        {
            using var kernel = DataKernel.OpenMemory(options, DataKernelTelemetryOptions.EnabledOptions);
            kernel.Reserve(65536).Span.Clear(); kernel.Commit();
            for (int i = 0; i < count; i++) kernel.StageBorrowedAt(1024 + i * 64, new byte[32], 0, 32);
            long offset = mode == "backing" ? 0 : mode == "oldest" ? 1024 : 1024 + (count - 1) * 64;
            byte[] buffer = new byte[32];
            for (int round = 0; round < 6; round++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) kernel.Read(offset, buffer);
                long ticks = Stopwatch.GetTimestamp() - start; allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (round != 0) Console.WriteLine(JsonSerializer.Serialize(new { count, mode, round, nsPerRead = ticks * 1e9 / Stopwatch.Frequency / iterations, allocated }));
            }
        }
        return 0;
    }
}
