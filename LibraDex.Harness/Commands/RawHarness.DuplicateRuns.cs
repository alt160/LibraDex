using System.Diagnostics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    private static int RunDuplicateRunSanity(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine("artifacts", "duplicate-run-sanity"));
        int count = GetIntOption(args, "--count", 7000);
        Directory.CreateDirectory(directory);

        ValidateScalar8Scalar8DuplicateRun(Path.Combine(directory, "ss8-8-duplicate-run.lbdx"), count);
        ValidateVarKeyScalar8DuplicateRun(Path.Combine(directory, "vs8-duplicate-run.lbdx"), count);
        ValidateScalar8Scalar8TerminalDeletes(Path.Combine(directory, "ss8-8-terminal-delete.lbdx"), count);
        ValidateVarKeyScalar8TerminalDeletes(Path.Combine(directory, "vs8-terminal-delete.lbdx"), count);
        Console.WriteLine($"duplicate-run-sanity ok count={count}");
        return 0;
    }

    private static int RunDuplicateRunPerf(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine("artifacts", "duplicate-run-perf"));
        int count = GetIntOption(args, "--count", 20000);
        int iterations = GetIntOption(args, "--iterations", 20);
        Directory.CreateDirectory(directory);

        DuplicateRunPerfResult ss88 = MeasureScalar8Scalar8DuplicateRun(Path.Combine(directory, "ss8-8-duplicate-run-perf.lbdx"), count, iterations);
        DuplicateRunPerfResult vs8 = MeasureVarKeyScalar8DuplicateRun(Path.Combine(directory, "vs8-duplicate-run-perf.lbdx"), count, iterations);
        Console.WriteLine($"shape,items,insert_ms,read_iterations,read_ms,checksum");
        Console.WriteLine($"{ss88.Shape},{ss88.Count},{ss88.InsertElapsed.TotalMilliseconds:F3},{ss88.ReadIterations},{ss88.ReadElapsed.TotalMilliseconds:F3},{ss88.Checksum}");
        Console.WriteLine($"{vs8.Shape},{vs8.Count},{vs8.InsertElapsed.TotalMilliseconds:F3},{vs8.ReadIterations},{vs8.ReadElapsed.TotalMilliseconds:F3},{vs8.Checksum}");
        return 0;
    }

    private static void ValidateScalar8Scalar8DuplicateRun(string path, int count)
    {
        DuplicateRunPerfResult result = MeasureScalar8Scalar8DuplicateRun(path, count, readIterations: 1);
        ulong expected = CreateDuplicateRunExpectedChecksum(count);
        if (result.Checksum != expected)
        {
            throw new InvalidDataException($"SS8-8 duplicate-run checksum mismatch. Expected {expected}, got {result.Checksum}.");
        }
    }

    private static void ValidateVarKeyScalar8DuplicateRun(string path, int count)
    {
        DuplicateRunPerfResult result = MeasureVarKeyScalar8DuplicateRun(path, count, readIterations: 1);
        ulong expected = CreateDuplicateRunExpectedChecksum(count);
        if (result.Checksum != expected)
        {
            throw new InvalidDataException($"VS8 duplicate-run checksum mismatch. Expected {expected}, got {result.Checksum}.");
        }
    }

    private static DuplicateRunPerfResult MeasureScalar8Scalar8DuplicateRun(string path, int count, int readIterations)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        const ulong key = 0x4488_0000_0000_0000UL;
        Stopwatch insertWatch = Stopwatch.StartNew();
        ulong checksum = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, CreateDesignPerfOptions(), CreateDesignPerfMetadata(9101), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, "ss88duprun", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(key >> 56), Scalar8Scalar8Profile.Default32KiB, itemCount: 0);
            for (int i = 0; i < count; i++)
            {
                Scalar8Scalar8RoutedInsertResult insert = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, key, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 32);
                if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"SS8-8 duplicate-run insert {i} failed with {insert.Kind}/{insert.InsertResult}.");
                }
            }

            insertWatch.Stop();
            Stopwatch readWatch = Stopwatch.StartNew();
            for (int iteration = 0; iteration < readIterations; iteration++)
            {
                using Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(handle.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, key, key, QueryDirection.Ascending, maxRouterHops: 32);
                int rows = 0;
                while (reader.TryReadNextEncodedIdentity(out ulong identity))
                {
                    checksum += identity;
                    rows++;
                }

                if (rows != count)
                {
                    throw new InvalidDataException($"SS8-8 duplicate-run read expected {count} rows, got {rows}.");
                }
            }
            readWatch.Stop();
            return new DuplicateRunPerfResult("SS8-8", count, readIterations, insertWatch.Elapsed, readWatch.Elapsed, checksum);
        }
    }

    private static DuplicateRunPerfResult MeasureVarKeyScalar8DuplicateRun(string path, int count, int readIterations)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        byte[] key = Encoding.ASCII.GetBytes(".duplicate-run");
        Stopwatch insertWatch = Stopwatch.StartNew();
        ulong checksum = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, CreateDesignPerfOptions(), CreateDesignPerfMetadata(9102), DataKernelTelemetryOptions.EnabledOptions))
        {
            VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
            (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateHarnessSlot(0, "vs8duprun", 0), maxKeyLength: 64, optimizerRouteFanout: 16, policy);
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, key[0], VarKeyScalar8Profile.DefaultInitial);
            for (int i = 0; i < count; i++)
            {
                VarKeyScalar8RoutedInsertResult insert = session.InsertWalkedRoutedVarKeyScalar8(handle.RootRouterOffset, handle.MaxKeyLength, key, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 128);
                if (insert.InsertResult != VarKeyScalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"VS8 duplicate-run insert {i} failed with {insert.Kind}/{insert.InsertResult}.");
                }
            }

            insertWatch.Stop();
            Stopwatch readWatch = Stopwatch.StartNew();
            for (int iteration = 0; iteration < readIterations; iteration++)
            {
                using VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(handle.RootRouterOffset, handle.MaxKeyLength, key, key, maxRouterHops: 128);
                int rows = 0;
                while (reader.MoveNext())
                {
                    checksum += reader.CurrentEncodedIdentity;
                    rows++;
                }

                if (rows != count)
                {
                    throw new InvalidDataException($"VS8 duplicate-run read expected {count} rows, got {rows}.");
                }
            }
            readWatch.Stop();
            return new DuplicateRunPerfResult("VS8", count, readIterations, insertWatch.Elapsed, readWatch.Elapsed, checksum);
        }
    }

    private static void ValidateScalar8Scalar8TerminalDeletes(string path, int count)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        const ulong key = 0x5588_0000_0000_0000UL;
        ulong deletedIdentity = ((ulong)count / 2UL) + 1UL;
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, CreateDesignPerfOptions(), CreateDesignPerfMetadata(9103), DataKernelTelemetryOptions.EnabledOptions);
        (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, "ss88termdelete", 0));
        _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(key >> 56), Scalar8Scalar8Profile.Default32KiB, itemCount: 0);
        for (int i = 0; i < count; i++)
        {
            _ = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, key, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 32);
        }

        Scalar8Scalar8RoutePathTarget target = session.WalkScalar8Scalar8RoutePathTarget(handle.RootRouterOffset, key, 32, Scalar8Scalar8RouteReadPolicy.Uncached);
        if (target.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
        {
            throw new InvalidDataException($"SS8-8 terminal delete expected terminal identity root, got {target.Target.Kind}.");
        }

        int exactDeleted = session.DeleteScalar8Scalar8TerminalIdentities(target.Target.Offset, Scalar8Scalar8Profile.Default32KiB, key, deletedIdentity);
        if (exactDeleted != 1)
        {
            throw new InvalidDataException($"SS8-8 terminal exact delete expected 1, got {exactDeleted}.");
        }

        using (Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(handle.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, key, key, QueryDirection.Ascending, maxRouterHops: 32))
        {
            int rows = 0;
            ulong checksum = 0;
            while (reader.TryReadNextEncodedIdentity(out ulong identity))
            {
                checksum += identity;
                rows++;
            }

            ulong expected = CreateDuplicateRunExpectedChecksum(count) - deletedIdentity;
            if (rows != count - 1 || checksum != expected)
            {
                throw new InvalidDataException($"SS8-8 terminal exact delete verification failed rows={rows} checksum={checksum} expectedRows={count - 1} expectedChecksum={expected}.");
            }
        }

        int rangeDeleted = session.DeleteScalar8Scalar8TerminalIdentities(target.Target.Offset, Scalar8Scalar8Profile.Default32KiB, key, encodedIdentity: null);
        if (rangeDeleted != count - 1)
        {
            throw new InvalidDataException($"SS8-8 terminal range delete expected {count - 1}, got {rangeDeleted}.");
        }

        using Scalar8Scalar8RangeReader emptyReader = session.OpenScalar8Scalar8RangeReader(handle.RootRouterOffset, Scalar8Scalar8Profile.Default32KiB, key, key, QueryDirection.Ascending, maxRouterHops: 32);
        if (emptyReader.Count != 0)
        {
            throw new InvalidDataException($"SS8-8 terminal range delete expected empty route, got {emptyReader.Count} rows.");
        }
    }

    private static void ValidateVarKeyScalar8TerminalDeletes(string path, int count)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        byte[] key = Encoding.ASCII.GetBytes(".terminal-delete");
        ulong deletedIdentity = ((ulong)count / 2UL) + 1UL;
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, CreateDesignPerfOptions(), CreateDesignPerfMetadata(9104), DataKernelTelemetryOptions.EnabledOptions);
        VarLenOptimizerMaintenancePolicy policy = new(VarLenOptimizerMaintenanceMode.Disabled, 0, 0, 0, 0);
        (VarKeyScalar8IndexHandle handle, _, _) = session.CreateVarKeyScalar8RootRouterIndex(CreateHarnessSlot(0, "vs8termdelete", 0), maxKeyLength: 64, optimizerRouteFanout: 16, policy);
        _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(handle.RootRouterOffset, key[0], VarKeyScalar8Profile.DefaultInitial);
        for (int i = 0; i < count; i++)
        {
            _ = session.InsertWalkedRoutedVarKeyScalar8(handle.RootRouterOffset, handle.MaxKeyLength, key, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 128);
        }

        if (!session.DeleteVarKeyScalar8ExactTuple(handle.RootRouterOffset, handle.MaxKeyLength, key, deletedIdentity, maxRouterHops: 128))
        {
            throw new InvalidDataException("VS8 terminal exact delete returned false.");
        }

        using (VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(handle.RootRouterOffset, handle.MaxKeyLength, key, key, maxRouterHops: 128))
        {
            int rows = 0;
            ulong checksum = 0;
            while (reader.MoveNext())
            {
                checksum += reader.CurrentEncodedIdentity;
                rows++;
            }

            ulong expected = CreateDuplicateRunExpectedChecksum(count) - deletedIdentity;
            if (rows != count - 1 || checksum != expected)
            {
                throw new InvalidDataException($"VS8 terminal exact delete verification failed rows={rows} checksum={checksum} expectedRows={count - 1} expectedChecksum={expected}.");
            }
        }

        long rangeDeleted = session.DeleteVarKeyScalar8KeyRange(handle.RootRouterOffset, handle.MaxKeyLength, key, key, maxRouterHops: 128);
        if (rangeDeleted != count - 1)
        {
            throw new InvalidDataException($"VS8 terminal range delete expected {count - 1}, got {rangeDeleted}.");
        }

        using VarKeyScalar8RangeReader emptyReader = session.OpenVarKeyScalar8RangeReader(handle.RootRouterOffset, handle.MaxKeyLength, key, key, maxRouterHops: 128);
        if (emptyReader.Count != 0)
        {
            throw new InvalidDataException($"VS8 terminal range delete expected empty route, got {emptyReader.Count} rows.");
        }
    }

    private static ulong CreateDuplicateRunExpectedChecksum(int count)
    {
        return ((ulong)count * ((ulong)count + 1UL)) / 2UL;
    }

    private readonly record struct DuplicateRunPerfResult(
        string Shape,
        int Count,
        int ReadIterations,
        TimeSpan InsertElapsed,
        TimeSpan ReadElapsed,
        ulong Checksum);
}
