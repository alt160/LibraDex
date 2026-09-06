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
        ValidateScalar8Scalar8MismatchedFullShelfRoute(Path.Combine(directory, "ss8-8-mismatched-full-shelf-route.lbdx"));
        ValidateScalar8Scalar8MismatchedTerminalRoute(Path.Combine(directory, "ss8-8-mismatched-terminal-route.lbdx"));
        ValidateVarKeyScalar8DuplicateRun(Path.Combine(directory, "vs8-duplicate-run.lbdx"), count);
        ValidateScalar8Scalar8TerminalDeletes(Path.Combine(directory, "ss8-8-terminal-delete.lbdx"), count);
        ValidateVarKeyScalar8TerminalDeletes(Path.Combine(directory, "vs8-terminal-delete.lbdx"), count);
        Console.WriteLine($"duplicate-run-sanity ok count={count}");
        return 0;
    }

    /// <summary>
    /// Proves that a stale root alias cannot carry a neighboring `SS8-8` key into a full shelf owned by one different key.<br/>
    /// The neighboring key differs only in the already-consumed root byte, so a deeper transform split has no valid boundary and must instead clear the stale ancestor alias.<br/>
    /// Successful insertion must preserve every duplicate tuple, create an independent neighboring route, and remain readable after reopen.<br/>
    /// </summary>
    /// <param name="path">Disposable LibraDex file used by the focused full-shelf routing fixture.<br/></param>
    private static void ValidateScalar8Scalar8MismatchedFullShelfRoute(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        const ulong duplicateKey = 0x1FA2_5D72_CC00_0000UL;
        const ulong incomingKey = 0x20A2_5D72_CC00_0000UL;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default4KiB;
        int duplicateCount = profile.MaxItemCount;
        DataKernelOptions options = CreateDesignPerfOptions();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9106), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, "ss88shelfalias", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(duplicateKey >> 56), profile, itemCount: 0);
            for (int i = 0; i < duplicateCount; i++)
            {
                Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, duplicateKey, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS8-8 mismatched-full-shelf fixture duplicate insert {i} failed with {result.Kind}/{result.InsertResult}.");
            }

            Scalar8Scalar8RoutePathTarget fullShelf = session.WalkScalar8Scalar8RoutePathTarget(handle.RootRouterOffset, duplicateKey, 32, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (fullShelf.Target.Kind != Scalar8Scalar8RouteTargetKind.Shelf || fullShelf.Target.RouterDepth != 0)
                throw new InvalidDataException($"SS8-8 mismatched-full-shelf fixture expected its original depth-zero shelf, got {fullShelf.Target.Kind} at depth {fullShelf.Target.RouterDepth} offset {fullShelf.Target.Offset}.");

            byte incomingPrefix = (byte)(incomingKey >> 56);
            _ = session.UpdateRouterRoute(root.Offset, incomingPrefix, incomingPrefix, incomingPrefix, fullShelf.Target.Offset);
            Scalar8Scalar8RoutedInsertResult incoming = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, incomingKey, 900_002UL, allowDuplicateKeys: true, maxRouterHops: 32);
            if (incoming.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException($"SS8-8 mismatched-full-shelf neighboring insert failed with {incoming.Kind}/{incoming.InsertResult}.");

            ValidateScalar8Scalar8MismatchedRouteReadback(session, handle.RootRouterOffset, profile, duplicateKey, incomingKey, duplicateCount, 900_002UL, "full-shelf");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        ValidateScalar8Scalar8MismatchedRouteReadback(reopened, reopenedHandle.RootRouterOffset, profile, duplicateKey, incomingKey, duplicateCount, 900_002UL, "full-shelf");
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

    /// <summary>
    /// Proves that a stale ancestor alias cannot carry a neighboring `SS8-8` key into another key's exhausted terminal identity route.<br/>
    /// The fixture first creates a natural depth-seven duplicate terminal, then aliases one neighboring prefix at the first differing byte to reproduce the Wherzit date-index failure with a few hundred tuples.<br/>
    /// Successful insertion must refine that alias, preserve the duplicate terminal, and remain readable after reopen.<br/>
    /// </summary>
    /// <param name="path">Disposable LibraDex file used by the focused routing fixture.<br/></param>
    private static void ValidateScalar8Scalar8MismatchedTerminalRoute(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        const ulong duplicateKey = 0x1FA2_5D72_CC00_0000UL;
        const ulong incomingKey = 0x1FA2_5E72_CC00_0000UL;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default4KiB;
        int duplicateCount = profile.MaxItemCount + 1;
        DataKernelOptions options = CreateDesignPerfOptions();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9105), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, "ss88termalias", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(duplicateKey >> 56), profile, itemCount: 0);
            for (int i = 0; i < duplicateCount; i++)
            {
                Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, duplicateKey, (ulong)i + 1UL, allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS8-8 mismatched-terminal fixture duplicate insert {i} failed with {result.Kind}/{result.InsertResult}.");
            }

            Scalar8Scalar8RoutePathTarget terminal = session.WalkScalar8Scalar8RoutePathTarget(handle.RootRouterOffset, duplicateKey, 32, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (terminal.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot || terminal.Target.RouterDepth != Scalar8Scalar8Layout.KeySize - 1)
                throw new InvalidDataException($"SS8-8 mismatched-terminal fixture expected a depth-seven terminal root, got {terminal.Target.Kind} at depth {terminal.Target.RouterDepth}.");

            long routerOffset = handle.RootRouterOffset;
            while (true)
            {
                RouterSnapshot router = session.ReadRouterSnapshot(routerOffset);
                byte duplicatePrefix = (byte)(duplicateKey >> ((sizeof(ulong) - 1 - router.KeyDepth) * 8));
                long duplicateTarget = session.FindRouterTarget(routerOffset, duplicatePrefix);
                if (router.KeyDepth == 2)
                {
                    byte incomingPrefix = (byte)(incomingKey >> ((sizeof(ulong) - 1 - router.KeyDepth) * 8));
                    _ = session.UpdateRouterRoute(routerOffset, incomingPrefix, incomingPrefix, incomingPrefix, duplicateTarget);
                    break;
                }

                routerOffset = duplicateTarget;
            }

            Scalar8Scalar8RoutedInsertResult incoming = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, incomingKey, 900_001UL, allowDuplicateKeys: true, maxRouterHops: 32);
            if (incoming.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException($"SS8-8 mismatched-terminal neighboring insert failed with {incoming.Kind}/{incoming.InsertResult}.");

            ValidateScalar8Scalar8MismatchedRouteReadback(session, handle.RootRouterOffset, profile, duplicateKey, incomingKey, duplicateCount, 900_001UL, "terminal");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        ValidateScalar8Scalar8MismatchedRouteReadback(reopened, reopenedHandle.RootRouterOffset, profile, duplicateKey, incomingKey, duplicateCount, 900_001UL, "terminal");
    }

    /// <summary>
    /// Verifies duplicate-terminal and neighboring-key tuple counts for the mismatched-route fixture before and after reopen.<br/>
    /// </summary>
    /// <param name="session">Open file session that owns the routed index.<br/></param>
    /// <param name="rootRouterOffset">Root router offset of the fixture index.<br/></param>
    /// <param name="profile">Fixed scalar shelf profile persisted by the fixture.<br/></param>
    /// <param name="duplicateKey">Key represented by the terminal identity root.<br/></param>
    /// <param name="incomingKey">Neighboring key inserted after alias repair.<br/></param>
    /// <param name="duplicateCount">Expected identity count under <paramref name="duplicateKey"/>.<br/></param>
    private static void ValidateScalar8Scalar8MismatchedRouteReadback(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong duplicateKey,
        ulong incomingKey,
        int duplicateCount,
        ulong expectedIncomingIdentity,
        string fixtureName)
    {
        using Scalar8Scalar8RangeReader duplicateReader = session.OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, duplicateKey, duplicateKey, QueryDirection.Ascending, maxRouterHops: 32);
        if (duplicateReader.Count != duplicateCount)
            throw new InvalidDataException($"SS8-8 mismatched-{fixtureName} duplicate read expected {duplicateCount}, got {duplicateReader.Count}.");

        using Scalar8Scalar8RangeReader incomingReader = session.OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, incomingKey, incomingKey, QueryDirection.Ascending, maxRouterHops: 32);
        ulong identity = 0;
        if (incomingReader.Count != 1 || !incomingReader.TryReadNextEncodedIdentity(out identity) || identity != expectedIncomingIdentity)
            throw new InvalidDataException($"SS8-8 mismatched-{fixtureName} neighboring read expected identity {expectedIncomingIdentity}, got count={incomingReader.Count} identity={identity}.");
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

        Scalar8Scalar8RoutePathTarget target = session.WalkScalar8Scalar8RoutePathTarget(handle.RootRouterOffset, key, 32, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
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

    /// <summary>
    /// Compares ascending and descending identity insertion into one exhausted-key `SS8-8` terminal route.<br/>
    /// Both catalogs must reopen with the exact ascending identity sequence, and descending physical bytes must remain within a small constant bound of the ascending control.<br/>
    /// This gate detects whole-chain replacement because reverse insertion repeatedly targets the head of the terminal chain while ascending insertion uses the existing tail fast path.<br/>
    /// </summary>
    /// <param name="args">Optional `--directory`, `--count`, and `--max-size-multiplier` command arguments.<br/></param>
    /// <returns>Zero when logical parity and the physical-locality bound both pass.<br/></returns>
    private static int RunTerminalIdentityLocalitySanity(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(Path.GetTempPath(), "libradex-terminal-identity-locality"));
        int count = GetIntOption(args, "--count", 7000);
        int maxSizeMultiplier = GetIntOption(args, "--max-size-multiplier", 4);
        if (count <= Scalar8Scalar8Profile.Default32KiB.MaxItemCount || maxSizeMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Terminal identity locality requires a count above ordinary shelf capacity and a positive size multiplier.");
        }

        Directory.CreateDirectory(directory);
        TerminalIdentityLocalityResult ascending = MeasureTerminalIdentityLocality(
            Path.Combine(directory, "terminal-identity-ascending.lbdx"),
            count,
            descendingInput: false,
            metadataSeed: 9107);
        TerminalIdentityLocalityResult descending = MeasureTerminalIdentityLocality(
            Path.Combine(directory, "terminal-identity-descending.lbdx"),
            count,
            descendingInput: true,
            metadataSeed: 9108);

        long maximumDescendingBytes = checked((ascending.FileBytes * maxSizeMultiplier) + (1024L * 1024L));
        Console.WriteLine("order,items,elapsed_ms,items_per_second,file_bytes,bytes_per_item,allocated_bytes,commit_calls,write_calls,bytes_written,staged_extents");
        Console.WriteLine($"ascending,{ascending.Count},{ascending.Elapsed.TotalMilliseconds:F3},{ascending.ItemsPerSecond:F0},{ascending.FileBytes},{ascending.BytesPerItem:F2},{ascending.AllocatedBytes},{ascending.CommitCalls},{ascending.WriteCalls},{ascending.BytesWritten},{ascending.StagedExtents}");
        Console.WriteLine($"descending,{descending.Count},{descending.Elapsed.TotalMilliseconds:F3},{descending.ItemsPerSecond:F0},{descending.FileBytes},{descending.BytesPerItem:F2},{descending.AllocatedBytes},{descending.CommitCalls},{descending.WriteCalls},{descending.BytesWritten},{descending.StagedExtents}");
        Console.WriteLine($"terminal-identity-locality bound={maximumDescendingBytes} multiplier={maxSizeMultiplier}");
        if (descending.FileBytes > maximumDescendingBytes)
        {
            throw new InvalidDataException($"Descending terminal identity growth exceeded the locality bound. AscendingBytes={ascending.FileBytes}; DescendingBytes={descending.FileBytes}; MaximumDescendingBytes={maximumDescendingBytes}; Count={count}.");
        }

        ValidateTerminalIdentityLocalMutationPatterns(directory, count, maximumDescendingBytes);
        ValidateTerminalIdentityDeleteLocality(Path.Combine(directory, "terminal-identity-delete-locality.lbdx"), count);
        ValidateTerminalIdentityDirectedLocalMutations(directory);
        ValidateTerminalIdentityLegacyTailRepair(Path.Combine(directory, "terminal-identity-legacy-tail-repair.lbdx"));

        Console.WriteLine("terminal-identity-locality-sanity ok");
        return 0;
    }

    /// <summary>
    /// Builds one fixed-key duplicate terminal using a deterministic identity order, validates exact live order, closes the catalog, and repeats the validation after reopen.<br/>
    /// The returned physical byte count is captured only after the initial session closes so buffered writes cannot understate persistent growth.<br/>
    /// </summary>
    /// <param name="path">Disposable catalog path for one insertion-order sample.<br/></param>
    /// <param name="count">Number of identities to insert under the single encoded key.<br/></param>
    /// <param name="descendingInput">Whether identities are supplied from <paramref name="count"/> down to one instead of one through <paramref name="count"/>.<br/></param>
    /// <param name="metadataSeed">Deterministic metadata seed that distinguishes the two catalogs.<br/></param>
    /// <returns>The inserted count, elapsed insertion time, and closed-file byte count.<br/></returns>
    private static TerminalIdentityLocalityResult MeasureTerminalIdentityLocality(
        string path,
        int count,
        bool descendingInput,
        int metadataSeed)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        const ulong key = 0x7788_0000_0000_0000UL;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        DataKernelOptions options = CreateDesignPerfOptions();
        long allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long commitCalls = 0;
        long writeCalls = 0;
        long bytesWritten = 0;
        long stagedExtents = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(metadataSeed), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, descendingInput ? "ss88termdesc" : "ss88termasc", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(key >> 56), profile, itemCount: 0);
            for (int i = 0; i < count; i++)
            {
                ulong identity = descendingInput ? checked((ulong)(count - i)) : checked((ulong)(i + 1));
                Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, key, identity, allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Terminal locality insert {i} failed with {result.Kind}/{result.InsertResult}; descending={descendingInput}.");
                }

                if (result.Commit.StagedExtentCount != 0 || result.Commit.WriteCallCount != 0 || result.Commit.BytesWritten != 0)
                {
                    commitCalls++;
                    writeCalls += result.Commit.WriteCallCount;
                    bytesWritten += result.Commit.BytesWritten;
                    stagedExtents += result.Commit.StagedExtentCount;
                }
            }

            ValidateTerminalIdentityLocalitySequence(session, handle.RootRouterOffset, profile, key, count, descendingInput ? "descending-live" : "ascending-live");
        }

        elapsed.Stop();
        long fileBytes = new FileInfo(path).Length;
        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            Scalar8Scalar8IndexHandle handle = reopened.GetScalar8Scalar8IndexHandle(0);
            ValidateTerminalIdentityLocalitySequence(reopened, handle.RootRouterOffset, profile, key, count, descendingInput ? "descending-reopen" : "ascending-reopen");
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;
        return new TerminalIdentityLocalityResult(
            count,
            elapsed.Elapsed,
            fileBytes,
            allocatedBytes,
            commitCalls,
            writeCalls,
            bytesWritten,
            stagedExtents);
    }

    /// <summary>
    /// Verifies that one terminal route enumerates every identity exactly once in ascending encoded order.<br/>
    /// Exact ordinal comparison catches loss, duplication, and cross-shelf boundary disorder without relying on a checksum alone.<br/>
    /// </summary>
    /// <param name="session">Open session that owns the terminal route.<br/></param>
    /// <param name="rootRouterOffset">Root router offset of the fixed scalar index.<br/></param>
    /// <param name="profile">Shelf profile used by the index and terminal identity shelves.<br/></param>
    /// <param name="key">Single encoded key represented by the terminal route.<br/></param>
    /// <param name="count">Expected identity count.<br/></param>
    /// <param name="phase">Diagnostic phase label included in failures.<br/></param>
    private static void ValidateTerminalIdentityLocalitySequence(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong key,
        int count,
        string phase)
    {
        using Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, key, key, QueryDirection.Ascending, maxRouterHops: 32);
        int ordinal = 0;
        while (reader.TryReadNextEncodedIdentity(out ulong identity))
        {
            ulong expected = checked((ulong)(ordinal + 1));
            if (identity != expected)
            {
                throw new InvalidDataException($"Terminal locality {phase} identity mismatch at ordinal {ordinal}. Expected={expected}; Actual={identity}.");
            }

            ordinal++;
        }

        if (ordinal != count || reader.Count != count)
        {
            throw new InvalidDataException($"Terminal locality {phase} count mismatch. Enumerated={ordinal}; ReaderCount={reader.Count}; Expected={count}.");
        }
    }

    private readonly record struct TerminalIdentityLocalityResult(
        int Count,
        TimeSpan Elapsed,
        long FileBytes,
        long AllocatedBytes,
        long CommitCalls,
        long WriteCalls,
        long BytesWritten,
        long StagedExtents)
    {
        internal double ItemsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Count / Elapsed.TotalSeconds;

        internal double BytesPerItem => Count == 0 ? 0 : (double)FileBytes / Count;
    }

    /// <summary>
    /// Exercises interior terminal-shelf insertion and split decisions with deterministic alternating-edge and shuffled identity permutations.<br/>
    /// Each catalog must enumerate the exact ascending sequence live and after reopen, and neither physical file may exceed the same locality bound used by the descending control.<br/>
    /// </summary>
    /// <param name="directory">Disposable fixture directory that receives the two pattern catalogs.<br/></param>
    /// <param name="count">Number of unique identities in each permutation.<br/></param>
    /// <param name="maximumFileBytes">Maximum allowed closed-file size for each pattern catalog.<br/></param>
    private static void ValidateTerminalIdentityLocalMutationPatterns(
        string directory,
        int count,
        long maximumFileBytes)
    {
        ulong[] alternating = new ulong[count];
        for (int i = 0; i < alternating.Length; i++)
        {
            alternating[i] = (i & 1) == 0
                ? checked((ulong)((i / 2) + 1))
                : checked((ulong)(count - (i / 2)));
        }

        ulong[] shuffled = new ulong[count];
        for (int i = 0; i < shuffled.Length; i++)
        {
            shuffled[i] = checked((ulong)(i + 1));
        }

        Random random = new(0x51A8_2026);
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int swapIndex = random.Next(i + 1);
            (shuffled[i], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[i]);
        }

        long alternatingBytes = BuildAndValidateTerminalIdentityPermutation(
            Path.Combine(directory, "terminal-identity-alternating.lbdx"),
            alternating,
            metadataSeed: 9109,
            phase: "alternating");
        long shuffledBytes = BuildAndValidateTerminalIdentityPermutation(
            Path.Combine(directory, "terminal-identity-shuffled.lbdx"),
            shuffled,
            metadataSeed: 9110,
            phase: "shuffled");
        Console.WriteLine($"terminal-identity-locality patterns alternating_bytes={alternatingBytes} shuffled_bytes={shuffledBytes}");
        if (alternatingBytes > maximumFileBytes || shuffledBytes > maximumFileBytes)
        {
            throw new InvalidDataException($"A terminal identity mutation pattern exceeded the locality bound. AlternatingBytes={alternatingBytes}; ShuffledBytes={shuffledBytes}; MaximumBytes={maximumFileBytes}.");
        }
    }

    /// <summary>
    /// Builds one terminal catalog from a caller-provided unique identity permutation and validates exact ascending output live and after reopen.<br/>
    /// A repeated middle identity is inserted after the build to prove the local path preserves exact-tuple no-op semantics without changing file length.<br/>
    /// </summary>
    /// <param name="path">Disposable catalog path for the pattern.<br/></param>
    /// <param name="identities">Unique permutation of the identities one through the sequence length.<br/></param>
    /// <param name="metadataSeed">Deterministic metadata seed for the catalog.<br/></param>
    /// <param name="phase">Diagnostic phase label.<br/></param>
    /// <returns>The closed-file byte length after exact-tuple no-op validation.<br/></returns>
    private static long BuildAndValidateTerminalIdentityPermutation(
        string path,
        ReadOnlySpan<ulong> identities,
        int metadataSeed,
        string phase)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        const ulong key = 0x7788_0000_0000_0000UL;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        DataKernelOptions options = CreateDesignPerfOptions();
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(metadataSeed), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, $"ss88term{phase}", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(key >> 56), profile, itemCount: 0);
            for (int i = 0; i < identities.Length; i++)
            {
                Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, key, identities[i], allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Terminal locality {phase} insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }
            }

            ValidateTerminalIdentityLocalitySequence(session, handle.RootRouterOffset, profile, key, identities.Length, $"{phase}-live");
            long beforeNoOpBytes = new FileInfo(path).Length;
            Scalar8Scalar8RoutedInsertResult noOp = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, key, checked((ulong)((identities.Length / 2) + 1)), allowDuplicateKeys: true, maxRouterHops: 32);
            if (noOp.InsertResult != Scalar8Scalar8InsertResult.AlreadyPresent || new FileInfo(path).Length != beforeNoOpBytes)
            {
                throw new InvalidDataException($"Terminal locality {phase} exact-tuple no-op changed logical or physical state.");
            }
        }

        long fileBytes = new FileInfo(path).Length;
        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        ValidateTerminalIdentityLocalitySequence(reopened, reopenedHandle.RootRouterOffset, profile, key, identities.Length, $"{phase}-reopen");
        return fileBytes;
    }

    /// <summary>
    /// Proves exact-delete shelf rewriting, empty-head unlink, delete-all root clearing, file-length stability, and reopen parity on one multi-shelf terminal route.<br/>
    /// The fixture reads the first terminal shelf's exact identities, deletes them one by one, requires the root link to change only when that shelf becomes empty, then clears the remaining chain in one logical operation.<br/>
    /// </summary>
    /// <param name="path">Disposable catalog path used by the delete-locality fixture.<br/></param>
    /// <param name="count">Initial identity count, which must produce more than one terminal shelf.<br/></param>
    private static void ValidateTerminalIdentityDeleteLocality(string path, int count)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        const ulong key = 0x7788_0000_0000_0000UL;
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        DataKernelOptions options = CreateDesignPerfOptions();
        long stableFileBytes;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9111), DataKernelTelemetryOptions.EnabledOptions))
        {
            (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, "ss88termdelocal", 0));
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(key >> 56), profile, itemCount: 0);
            for (int i = 0; i < count; i++)
            {
                Scalar8Scalar8RoutedInsertResult result = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, key, checked((ulong)(i + 1)), allowDuplicateKeys: true, maxRouterHops: 32);
                if (result.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                {
                    throw new InvalidDataException($"Terminal delete-locality setup insert {i} failed with {result.Kind}/{result.InsertResult}.");
                }
            }

            Scalar8Scalar8RoutePathTarget terminal = session.WalkScalar8Scalar8RoutePathTarget(handle.RootRouterOffset, key, 32, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (terminal.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
            {
                throw new InvalidDataException($"Terminal delete-locality expected a terminal root, got {terminal.Target.Kind}.");
            }

            byte[] rootBytes = session.ReadTerminalIdentityRootBytes(terminal.Target.Offset);
            long firstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            byte[] firstShelfBytes = session.ReadTerminalIdentity8ShelfBytes(firstShelfOffset, profile.ShelfExtentSize);
            int firstShelfCount = TerminalIdentity8ShelfLayout.ReadItemCount(firstShelfBytes);
            long expectedNextShelfOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(firstShelfBytes);
            if (firstShelfCount <= 0 || expectedNextShelfOffset == 0)
            {
                throw new InvalidDataException("Terminal delete-locality requires a non-empty head followed by another shelf.");
            }

            ulong[] headIdentities = new ulong[firstShelfCount];
            for (int i = 0; i < headIdentities.Length; i++)
            {
                headIdentities[i] = TerminalIdentity8ShelfLayout.ReadIdentity(firstShelfBytes, i);
            }

            stableFileBytes = new FileInfo(path).Length;
            for (int i = 0; i < headIdentities.Length; i++)
            {
                int deleted = session.DeleteScalar8Scalar8TerminalIdentities(terminal.Target.Offset, profile, key, headIdentities[i]);
                if (deleted != 1 || new FileInfo(path).Length != stableFileBytes)
                {
                    throw new InvalidDataException($"Terminal delete-locality exact delete {i} changed count or file length unexpectedly.");
                }

                long currentFirst = TerminalIdentityRootLayout.ReadFirstShelfOffset(session.ReadTerminalIdentityRootBytes(terminal.Target.Offset));
                long expectedFirst = i == headIdentities.Length - 1 ? expectedNextShelfOffset : firstShelfOffset;
                if (currentFirst != expectedFirst)
                {
                    throw new InvalidDataException($"Terminal delete-locality root link changed at delete {i}. Expected={expectedFirst}; Actual={currentFirst}.");
                }
            }

            int remaining = count - firstShelfCount;
            using (Scalar8Scalar8RangeReader remainingReader = session.OpenScalar8Scalar8RangeReader(handle.RootRouterOffset, profile, key, key, QueryDirection.Ascending, maxRouterHops: 32))
            {
                if (remainingReader.Count != remaining)
                {
                    throw new InvalidDataException($"Terminal delete-locality remaining count mismatch. Expected={remaining}; Actual={remainingReader.Count}.");
                }
            }

            int deletedAll = session.DeleteScalar8Scalar8TerminalIdentities(terminal.Target.Offset, profile, key, encodedIdentity: null);
            if (deletedAll != remaining || new FileInfo(path).Length != stableFileBytes)
            {
                throw new InvalidDataException($"Terminal delete-locality delete-all mismatch. Expected={remaining}; Actual={deletedAll}; Bytes={new FileInfo(path).Length}; ExpectedBytes={stableFileBytes}.");
            }

            if (TerminalIdentityRootLayout.ReadFirstShelfOffset(session.ReadTerminalIdentityRootBytes(terminal.Target.Offset)) != 0)
            {
                throw new InvalidDataException("Terminal delete-locality delete-all did not clear the root shelf link.");
            }
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        using Scalar8Scalar8RangeReader emptyReader = reopened.OpenScalar8Scalar8RangeReader(reopenedHandle.RootRouterOffset, profile, key, key, QueryDirection.Ascending, maxRouterHops: 32);
        if (emptyReader.Count != 0 || new FileInfo(path).Length != stableFileBytes)
        {
            throw new InvalidDataException($"Terminal delete-locality reopen mismatch. Count={emptyReader.Count}; Bytes={new FileInfo(path).Length}; ExpectedBytes={stableFileBytes}.");
        }

        Console.WriteLine($"terminal-identity-delete-locality ok initial={count} bytes={stableFileBytes}");
    }

    /// <summary>
    /// Runs directed terminal-identity mutations whose target shelf position and capacity state are proven before each mutation.<br/>
    /// Separate fixtures cover spare-capacity insertions, full-shelf splits, and exact deletion of the final identity for a terminal key so random input is not used as a proxy for branch coverage.<br/>
    /// </summary>
    /// <param name="directory">Disposable directory that receives the isolated directed fixtures.<br/></param>
    private static void ValidateTerminalIdentityDirectedLocalMutations(string directory)
    {
        ValidateTerminalIdentitySpareInsertionCases(directory);
        ValidateTerminalIdentityFullShelfSplitCases(directory);
        ValidateTerminalIdentityLastExactDelete(Path.Combine(directory, "terminal-identity-last-exact-delete.lbdx"));
        Console.WriteLine("terminal-identity-directed-local-mutations ok");
    }

    /// <summary>
    /// Proves first-, middle-, and last-shelf insertion when the selected shelf has exactly one spare slot.<br/>
    /// Each case builds three full terminal shelves, deletes one directed identity without changing topology, validates that deletion after reopen, reinserts into the same spare shelf, and validates exact order after a second reopen.<br/>
    /// </summary>
    /// <param name="directory">Disposable directory that receives one fixture per directed shelf position.<br/></param>
    private static void ValidateTerminalIdentitySpareInsertionCases(string directory)
    {
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        int terminalCapacity = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize);
        int baseIdentityCount = checked(terminalCapacity * 3);
        string[] labels = ["first", "middle", "last"];

        for (int caseIndex = 0; caseIndex < labels.Length; caseIndex++)
        {
            string label = labels[caseIndex];
            string path = Path.Combine(directory, $"terminal-identity-spare-{label}.lbdx");
            BuildTerminalIdentityDirectedFixture(path, baseIdentityCount, 9200 + caseIndex);
            DataKernelOptions options = CreateDesignPerfOptions();
            long stableFileBytes = new FileInfo(path).Length;
            ulong deletedIdentity;
            long[] originalOffsets;

            using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
                TerminalIdentityShelfSnapshot[] shelves = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
                if (shelves.Length < 3)
                {
                    throw new InvalidDataException($"Directed spare {label} fixture produced only {shelves.Length} terminal shelves.");
                }

                int shelfIndex = caseIndex == 0 ? 0 : caseIndex == 1 ? shelves.Length / 2 : shelves.Length - 1;
                TerminalIdentityShelfSnapshot target = shelves[shelfIndex];
                if (target.Count != terminalCapacity)
                {
                    throw new InvalidDataException($"Directed spare {label} target count was {target.Count}; expected full capacity {terminalCapacity} before delete.");
                }

                byte[] targetBytes = session.ReadTerminalIdentity8ShelfBytes(target.Offset, profile.ShelfExtentSize);
                int localIdentityIndex = caseIndex == 0 ? 0 : caseIndex == 1 ? target.Count / 2 : target.Count - 1;
                deletedIdentity = TerminalIdentity8ShelfLayout.ReadIdentity(targetBytes, localIdentityIndex);
                originalOffsets = shelves.Select(static shelf => shelf.Offset).ToArray();
                int deleted = session.DeleteScalar8Scalar8TerminalIdentities(target.RootOffset, profile, DirectedTerminalIdentityKey, deletedIdentity);
                TerminalIdentityShelfSnapshot[] afterDelete = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
                if (deleted != 1 ||
                    new FileInfo(path).Length != stableFileBytes ||
                    !afterDelete.Select(static shelf => shelf.Offset).SequenceEqual(originalOffsets) ||
                    afterDelete[shelfIndex].Count != terminalCapacity - 1)
                {
                    throw new InvalidDataException($"Directed spare {label} delete changed count, file length, or topology unexpectedly.");
                }

                ValidateTerminalIdentityDirectedSequence(session, handle.RootRouterOffset, profile, baseIdentityCount, deletedIdentity, additionalIdentity: null, phase: $"spare-{label}-delete-live");
            }

            using (LibraDexFileSession deletedReopen = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                Scalar8Scalar8IndexHandle handle = deletedReopen.GetScalar8Scalar8IndexHandle(0);
                ValidateTerminalIdentityDirectedSequence(deletedReopen, handle.RootRouterOffset, profile, baseIdentityCount, deletedIdentity, additionalIdentity: null, phase: $"spare-{label}-delete-reopen");
                Scalar8Scalar8RoutedInsertResult insert = deletedReopen.InsertWalkedRoutedScalar8Scalar8(
                    handle.RootRouterOffset,
                    profile,
                    DirectedTerminalIdentityKey,
                    deletedIdentity,
                    allowDuplicateKeys: true,
                    maxRouterHops: 32);
                TerminalIdentityShelfSnapshot[] afterInsert = ReadTerminalIdentityShelfSnapshots(deletedReopen, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
                if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted ||
                    new FileInfo(path).Length != stableFileBytes ||
                    !afterInsert.Select(static shelf => shelf.Offset).SequenceEqual(originalOffsets))
                {
                    throw new InvalidDataException($"Directed spare {label} insertion changed file length or topology instead of rewriting the selected shelf.");
                }

                ValidateTerminalIdentityDirectedSequence(deletedReopen, handle.RootRouterOffset, profile, baseIdentityCount, missingIdentity: null, additionalIdentity: null, phase: $"spare-{label}-insert-live");
            }

            using LibraDexFileSession insertedReopen = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
            Scalar8Scalar8IndexHandle reopenedHandle = insertedReopen.GetScalar8Scalar8IndexHandle(0);
            ValidateTerminalIdentityDirectedSequence(insertedReopen, reopenedHandle.RootRouterOffset, profile, baseIdentityCount, missingIdentity: null, additionalIdentity: null, phase: $"spare-{label}-insert-reopen");
            Console.WriteLine($"terminal-identity-spare-{label} ok shelves={originalOffsets.Length} bytes={stableFileBytes}");
        }
    }

    /// <summary>
    /// Proves first-, middle-, and last-shelf local splits with three initially full terminal shelves.<br/>
    /// The inserted odd identity falls strictly between two even identities in the selected full shelf, so each case must rewrite that shelf, append exactly one new shelf, preserve global order, and survive reopen.<br/>
    /// </summary>
    /// <param name="directory">Disposable directory that receives one fixture per directed full-shelf position.<br/></param>
    private static void ValidateTerminalIdentityFullShelfSplitCases(string directory)
    {
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        int terminalCapacity = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize);
        int baseIdentityCount = checked(terminalCapacity * 3);
        string[] labels = ["first", "middle", "last"];

        for (int caseIndex = 0; caseIndex < labels.Length; caseIndex++)
        {
            string label = labels[caseIndex];
            string path = Path.Combine(directory, $"terminal-identity-split-{label}.lbdx");
            BuildTerminalIdentityDirectedFixture(path, baseIdentityCount, 9210 + caseIndex);
            DataKernelOptions options = CreateDesignPerfOptions();
            long beforeBytes = new FileInfo(path).Length;
            ulong insertedIdentity;
            int originalShelfCount;

            using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            {
                Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
                TerminalIdentityShelfSnapshot[] shelves = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
                if (shelves.Length < 3)
                {
                    throw new InvalidDataException($"Directed split {label} fixture produced only {shelves.Length} terminal shelves.");
                }

                int shelfIndex = caseIndex == 0 ? 0 : caseIndex == 1 ? shelves.Length / 2 : shelves.Length - 1;
                TerminalIdentityShelfSnapshot target = shelves[shelfIndex];
                if (target.Count != terminalCapacity)
                {
                    throw new InvalidDataException($"Directed split {label} target count was {target.Count}; expected {terminalCapacity}.");
                }

                insertedIdentity = checked(target.FirstIdentity + 1);
                originalShelfCount = shelves.Length;
                Scalar8Scalar8RoutedInsertResult insert = session.InsertWalkedRoutedScalar8Scalar8(
                    handle.RootRouterOffset,
                    profile,
                    DirectedTerminalIdentityKey,
                    insertedIdentity,
                    allowDuplicateKeys: true,
                    maxRouterHops: 32);
                TerminalIdentityShelfSnapshot[] afterInsert = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
                long expectedBytes = checked(beforeBytes + profile.ShelfExtentSize);
                if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted ||
                    afterInsert.Length != originalShelfCount + 1 ||
                    new FileInfo(path).Length != expectedBytes)
                {
                    throw new InvalidDataException($"Directed split {label} did not append exactly one terminal shelf. Shelves={originalShelfCount}->{afterInsert.Length}; Bytes={beforeBytes}->{new FileInfo(path).Length}; ExpectedBytes={expectedBytes}.");
                }

                ValidateTerminalIdentityDirectedSequence(session, handle.RootRouterOffset, profile, baseIdentityCount, missingIdentity: null, additionalIdentity: insertedIdentity, phase: $"split-{label}-live");
            }

            using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
            Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
            ValidateTerminalIdentityDirectedSequence(reopened, reopenedHandle.RootRouterOffset, profile, baseIdentityCount, missingIdentity: null, additionalIdentity: insertedIdentity, phase: $"split-{label}-reopen");
            Console.WriteLine($"terminal-identity-split-{label} ok shelves={originalShelfCount}->{originalShelfCount + 1} bytes={beforeBytes}->{new FileInfo(path).Length}");
        }
    }

    /// <summary>
    /// Proves exact deletion of the sole remaining identity from an already established terminal root.<br/>
    /// The fixture clears a populated terminal chain, inserts one replacement identity into the empty terminal root, deletes that exact identity, and requires an empty live and reopened route.<br/>
    /// </summary>
    /// <param name="path">Disposable file-backed fixture path.<br/></param>
    private static void ValidateTerminalIdentityLastExactDelete(string path)
    {
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        int initialCount = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize) + 1;
        BuildTerminalIdentityDirectedFixture(path, initialCount, 9220);
        DataKernelOptions options = CreateDesignPerfOptions();
        ulong soleIdentity = checked((ulong)(initialCount * 2 + 100));

        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
            TerminalIdentityShelfSnapshot[] shelves = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
            int deletedAll = session.DeleteScalar8Scalar8TerminalIdentities(shelves[0].RootOffset, profile, DirectedTerminalIdentityKey, encodedIdentity: null);
            if (deletedAll != initialCount)
            {
                throw new InvalidDataException($"Last-exact-delete setup cleared {deletedAll} identities; expected {initialCount}.");
            }

            Scalar8Scalar8RoutedInsertResult insert = session.InsertWalkedRoutedScalar8Scalar8(
                handle.RootRouterOffset,
                profile,
                DirectedTerminalIdentityKey,
                soleIdentity,
                allowDuplicateKeys: true,
                maxRouterHops: 32);
            if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Last-exact-delete sole insert returned {insert.InsertResult}.");
            }

            TerminalIdentityShelfSnapshot[] oneShelf = ReadTerminalIdentityShelfSnapshots(session, handle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
            int deleted = session.DeleteScalar8Scalar8TerminalIdentities(oneShelf[0].RootOffset, profile, DirectedTerminalIdentityKey, soleIdentity);
            byte[] emptyRootBytes = session.ReadTerminalIdentityRootBytes(oneShelf[0].RootOffset);
            if (deleted != 1 ||
                TerminalIdentityRootLayout.ReadFirstShelfOffset(emptyRootBytes) != 0 ||
                TerminalIdentityRootLayout.ReadTailShelfOffset(emptyRootBytes) != 0)
            {
                throw new InvalidDataException("Exact deletion of the final terminal identity did not clear the terminal root.");
            }

            ValidateTerminalIdentityDirectedSequence(session, handle.RootRouterOffset, profile, baseIdentityCount: 0, missingIdentity: null, additionalIdentity: null, phase: "last-exact-delete-live");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        ValidateTerminalIdentityDirectedSequence(reopened, reopenedHandle.RootRouterOffset, profile, baseIdentityCount: 0, missingIdentity: null, additionalIdentity: null, phase: "last-exact-delete-reopen");
        Console.WriteLine("terminal-identity-last-exact-delete ok");
    }

    /// <summary>
    /// Builds a deterministic exhausted-key terminal fixture whose identities are the ascending even values `2..(count*2)`.<br/>
    /// Even spacing leaves one odd insertion point between every adjacent pair, allowing directed split tests to select an exact shelf without colliding with an existing tuple.<br/>
    /// </summary>
    /// <param name="path">Disposable file-backed fixture path.<br/></param>
    /// <param name="identityCount">Number of even identities to publish.<br/></param>
    /// <param name="metadataSeed">Deterministic developer-metadata seed.<br/></param>
    private static void BuildTerminalIdentityDirectedFixture(string path, int identityCount, int metadataSeed)
    {
        File.Delete(path);
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(metadataSeed), DataKernelTelemetryOptions.EnabledOptions);
        (Scalar8Scalar8IndexHandle handle, RouterSnapshot root, _) = session.CreateScalar8Scalar8RootRouterIndex(CreateHarnessSlot(0, $"ss88td{metadataSeed}", 0));
        _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoute(root.Offset, (byte)(DirectedTerminalIdentityKey >> 56), profile, itemCount: 0);
        for (int i = 0; i < identityCount; i++)
        {
            ulong identity = checked((ulong)((i + 1) * 2));
            Scalar8Scalar8RoutedInsertResult insert = session.InsertWalkedRoutedScalar8Scalar8(handle.RootRouterOffset, profile, DirectedTerminalIdentityKey, identity, allowDuplicateKeys: true, maxRouterHops: 32);
            if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Directed terminal fixture insert {i} returned {insert.Kind}/{insert.InsertResult}.");
            }
        }
    }

    /// <summary>
    /// Reads and validates the complete shelf chain for one directed exhausted-key fixture.<br/>
    /// The returned snapshots retain physical offsets, counts, links, and boundary identities so callers can prove which shelf a mutation targeted and whether unrelated topology moved.<br/>
    /// </summary>
    /// <param name="session">Open session that owns the terminal route.<br/></param>
    /// <param name="rootRouterOffset">Root router offset of the SS8-8 index.<br/></param>
    /// <param name="profile">Shelf profile used by the index.<br/></param>
    /// <param name="encodedKey">Exhausted encoded key to inspect.<br/></param>
    /// <returns>Ordered snapshots for every linked terminal shelf.<br/></returns>
    private static TerminalIdentityShelfSnapshot[] ReadTerminalIdentityShelfSnapshots(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        ulong encodedKey)
    {
        Scalar8Scalar8RoutePathTarget terminal = session.WalkScalar8Scalar8RoutePathTarget(rootRouterOffset, encodedKey, 32, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
        if (terminal.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
        {
            throw new InvalidDataException($"Directed terminal fixture resolved {terminal.Target.Kind} instead of TerminalIdentityRoot.");
        }

        byte[] rootBytes = session.ReadTerminalIdentityRootBytes(terminal.Target.Offset);
        long shelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
        long persistedTailShelfOffset = TerminalIdentityRootLayout.ReadTailShelfOffset(rootBytes);
        List<TerminalIdentityShelfSnapshot> shelves = [];
        HashSet<long> visited = [];
        ulong? priorLastIdentity = null;
        while (shelfOffset != 0)
        {
            if (!visited.Add(shelfOffset))
            {
                throw new InvalidDataException($"Directed terminal fixture contains a shelf cycle at {shelfOffset}.");
            }

            byte[] shelfBytes = session.ReadTerminalIdentity8ShelfBytes(shelfOffset, profile.ShelfExtentSize);
            int count = TerminalIdentity8ShelfLayout.ReadItemCount(shelfBytes);
            if (count <= 0)
            {
                throw new InvalidDataException($"Directed terminal shelf {shelfOffset} has invalid count {count}.");
            }

            ulong firstIdentity = TerminalIdentity8ShelfLayout.ReadIdentity(shelfBytes, 0);
            ulong lastIdentity = TerminalIdentity8ShelfLayout.ReadIdentity(shelfBytes, count - 1);
            if (priorLastIdentity.HasValue && priorLastIdentity.Value >= firstIdentity)
            {
                throw new InvalidDataException($"Directed terminal shelf boundary is not strict: {priorLastIdentity.Value} then {firstIdentity}.");
            }

            long nextOffset = TerminalIdentity8ShelfLayout.ReadNextShelfOffset(shelfBytes);
            shelves.Add(new TerminalIdentityShelfSnapshot(terminal.Target.Offset, shelfOffset, count, nextOffset, firstIdentity, lastIdentity));
            priorLastIdentity = lastIdentity;
            shelfOffset = nextOffset;
        }

        long expectedTailShelfOffset = shelves.Count == 0 ? 0 : shelves[^1].Offset;
        if (persistedTailShelfOffset != expectedTailShelfOffset)
        {
            throw new InvalidDataException(
                $"Directed terminal root {terminal.Target.Offset} records tail {persistedTailShelfOffset}, but its reachable final shelf is {expectedTailShelfOffset}.");
        }

        return shelves.ToArray();
    }

    /// <summary>
    /// Validates exact ascending identity output for a directed terminal fixture, with one optional missing even identity and one optional inserted odd identity.<br/>
    /// The expected vector is constructed independently from the persisted shelf chain and compared ordinal by ordinal live and after reopen.<br/>
    /// </summary>
    /// <param name="session">Open session to validate.<br/></param>
    /// <param name="rootRouterOffset">Root router offset of the SS8-8 index.<br/></param>
    /// <param name="profile">Shelf profile used by the index.<br/></param>
    /// <param name="baseIdentityCount">Number of deterministic even identities in the base fixture.<br/></param>
    /// <param name="missingIdentity">Optional even identity removed from the base fixture.<br/></param>
    /// <param name="additionalIdentity">Optional odd identity inserted into the base fixture.<br/></param>
    /// <param name="phase">Diagnostic label included in failures.<br/></param>
    private static void ValidateTerminalIdentityDirectedSequence(
        LibraDexFileSession session,
        long rootRouterOffset,
        Scalar8Scalar8Profile profile,
        int baseIdentityCount,
        ulong? missingIdentity,
        ulong? additionalIdentity,
        string phase)
    {
        List<ulong> expected = new(baseIdentityCount + (additionalIdentity.HasValue ? 1 : 0));
        for (int i = 0; i < baseIdentityCount; i++)
        {
            ulong identity = checked((ulong)((i + 1) * 2));
            if (!missingIdentity.HasValue || identity != missingIdentity.Value)
            {
                expected.Add(identity);
            }
        }

        if (additionalIdentity.HasValue)
        {
            int insertionIndex = expected.BinarySearch(additionalIdentity.Value);
            if (insertionIndex >= 0)
            {
                throw new InvalidDataException($"Directed terminal {phase} expected vector already contains {additionalIdentity.Value}.");
            }

            expected.Insert(~insertionIndex, additionalIdentity.Value);
        }

        using Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(rootRouterOffset, profile, DirectedTerminalIdentityKey, DirectedTerminalIdentityKey, QueryDirection.Ascending, maxRouterHops: 32);
        int ordinal = 0;
        while (reader.TryReadNextEncodedIdentity(out ulong actualIdentity))
        {
            if (ordinal >= expected.Count || actualIdentity != expected[ordinal])
            {
                ulong expectedIdentity = ordinal < expected.Count ? expected[ordinal] : 0;
                throw new InvalidDataException($"Directed terminal {phase} mismatch at ordinal {ordinal}. Expected={expectedIdentity}; Actual={actualIdentity}.");
            }

            ordinal++;
        }

        if (ordinal != expected.Count || reader.Count != expected.Count)
        {
            throw new InvalidDataException($"Directed terminal {phase} count mismatch. Enumerated={ordinal}; ReaderCount={reader.Count}; Expected={expected.Count}.");
        }
    }

    /// <summary>
    /// Proves compatibility with a legacy nonempty terminal root whose persisted tail field is zero.<br/>
    /// The fixture clears only the tail field after closing the writer, reopens the catalog, performs one ascending mutation, and requires that mutation to repair the exact reachable tail without rebuilding the shelf chain.<br/>
    /// A second reopen verifies the repaired first/tail relationship and exact sorted identity sequence are durable.<br/>
    /// </summary>
    /// <param name="path">Disposable file-backed fixture path.<br/></param>
    private static void ValidateTerminalIdentityLegacyTailRepair(string path)
    {
        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        int baseIdentityCount = TerminalIdentity8ShelfLayout.GetCapacity(profile.ShelfExtentSize) + 1;
        BuildTerminalIdentityDirectedFixture(path, baseIdentityCount, 9230);
        DataKernelOptions options = CreateDesignPerfOptions();
        long terminalRootOffset;
        long expectedLegacyTailOffset;

        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
            TerminalIdentityShelfSnapshot[] shelves = ReadTerminalIdentityShelfSnapshots(
                session,
                handle.RootRouterOffset,
                profile,
                DirectedTerminalIdentityKey);
            terminalRootOffset = shelves[0].RootOffset;
            expectedLegacyTailOffset = shelves[^1].Offset;
        }

        using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Span<byte> zeroTail = stackalloc byte[sizeof(long)];
            stream.Position = terminalRootOffset + TerminalIdentityRootLayout.TailShelfOffsetOffset;
            stream.Write(zeroTail);
            stream.Flush(flushToDisk: true);
        }

        ulong insertedIdentity = checked((ulong)(baseIdentityCount * 2 + 2));
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
            byte[] legacyRootBytes = session.ReadTerminalIdentityRootBytes(terminalRootOffset);
            if (TerminalIdentityRootLayout.ReadTailShelfOffset(legacyRootBytes) != 0)
            {
                throw new InvalidDataException("Legacy terminal tail fixture did not persist the intended zero-tail root.");
            }

            Scalar8Scalar8RoutedInsertResult insert = session.InsertWalkedRoutedScalar8Scalar8(
                handle.RootRouterOffset,
                profile,
                DirectedTerminalIdentityKey,
                insertedIdentity,
                allowDuplicateKeys: true,
                maxRouterHops: 32);
            if (insert.InsertResult != Scalar8Scalar8InsertResult.Inserted)
            {
                throw new InvalidDataException($"Legacy terminal tail repair insert returned {insert.Kind}/{insert.InsertResult}.");
            }

            TerminalIdentityShelfSnapshot[] repairedShelves = ReadTerminalIdentityShelfSnapshots(
                session,
                handle.RootRouterOffset,
                profile,
                DirectedTerminalIdentityKey);
            if (repairedShelves[^1].Offset != expectedLegacyTailOffset)
            {
                throw new InvalidDataException(
                    $"Legacy terminal tail repair changed the reachable tail shelf. Expected={expectedLegacyTailOffset}; Actual={repairedShelves[^1].Offset}.");
            }

            ValidateTerminalIdentityDirectedSequence(
                session,
                handle.RootRouterOffset,
                profile,
                baseIdentityCount,
                missingIdentity: null,
                additionalIdentity: insertedIdentity,
                phase: "legacy-tail-repair-live");
        }

        using LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions);
        Scalar8Scalar8IndexHandle reopenedHandle = reopened.GetScalar8Scalar8IndexHandle(0);
        _ = ReadTerminalIdentityShelfSnapshots(reopened, reopenedHandle.RootRouterOffset, profile, DirectedTerminalIdentityKey);
        ValidateTerminalIdentityDirectedSequence(
            reopened,
            reopenedHandle.RootRouterOffset,
            profile,
            baseIdentityCount,
            missingIdentity: null,
            additionalIdentity: insertedIdentity,
            phase: "legacy-tail-repair-reopen");
        Console.WriteLine($"terminal-identity-legacy-tail-repair ok tail={expectedLegacyTailOffset}");
    }

    private const ulong DirectedTerminalIdentityKey = 0x7799_0000_0000_0000UL;

    private readonly record struct TerminalIdentityShelfSnapshot(
        long RootOffset,
        long Offset,
        int Count,
        long NextOffset,
        ulong FirstIdentity,
        ulong LastIdentity);
}
