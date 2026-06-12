using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using Microsoft.Data.Sqlite;

internal static partial class RawHarness
{

    private static int RunFormatSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "format-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        DataKernelOptions options = new(
            AppendBufferSize: 16 * 1024,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);

        SuperblockDeveloperMetadata developerMetadata = new(
            DevIdentity: "LibraDexHarness",
            DevCustomText: "format sanity",
            DevGuid: Guid.Parse("9d6b6c63-0a9b-41bd-bc70-b0c64c0d11f3"),
            DevDate1UtcTicks: new DateTime(2026, 4, 22, 0, 0, 0, DateTimeKind.Utc).Ticks,
            DevDate2UtcTicks: new DateTime(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc).Ticks,
            DevNumber: 42);

        using (LibraDexFileSession initialized = LibraDexFileSession.Initialize(
            path,
            options,
            developerMetadata,
            DataKernelTelemetryOptions.EnabledOptions))
        {
            ValidateFormatSession(initialized, developerMetadata);
            IndexDirectorySlotSnapshot slotRequest = CreateHarnessSlot(0, "primary", 0);
            (RouterSnapshot router, DataKernelCommitTelemetry directoryCommit) = initialized.CreateRootRouterIndex(slotRequest);
            IndexDirectorySlotSnapshot slot = slotRequest with { State = IndexDirectoryLayout.ActiveState, RootRouterOffset = router.Offset };
            PrintCommit("root router + directory update", directoryCommit);
            ValidateRootRouter(initialized, router);
            ValidateFormatSession(initialized, developerMetadata, slot);

            SuperblockDeveloperMetadata updatedMetadata = new(
                DevIdentity: "LibraDexHarnessUpdated",
                DevCustomText: "format sanity update",
                DevGuid: Guid.Parse("726ec215-8200-4c36-9feb-a8224381b908"),
                DevDate1UtcTicks: new DateTime(2026, 4, 24, 0, 0, 0, DateTimeKind.Utc).Ticks,
                DevDate2UtcTicks: new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc).Ticks,
                DevNumber: 84);

            DataKernelCommitTelemetry updateCommit = initialized.UpdateDeveloperMetadata(updatedMetadata);
            PrintCommit("metadata update", updateCommit);
            developerMetadata = updatedMetadata;
            ValidateFormatSession(initialized, developerMetadata, slot);
        }

        using (LibraDexFileSession opened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            IndexDirectorySlotSnapshot slot = opened.IndexDirectory.ActiveSlots[0];
            ValidateFormatSession(opened, developerMetadata, slot);
            ValidateRootRouter(opened, opened.ReadRouterSnapshot(slot.RootRouterOffset));
        }

        Console.WriteLine($"sanity-format ok path={path} superblockOffset=0 indexDirectoryOffset={SuperblockLayout.Size}");
        return 0;
    }


    private static int RunFormatStress(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(@"T:\LibraDex", "FormatStress"));
        int iterations = GetIntOption(args, "--iterations", 1000);
        Directory.CreateDirectory(directory);

        DataKernelOptions options = new(
            AppendBufferSize: 16 * 1024,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);

        SuperblockDeveloperMetadata developerMetadata = new(
            DevIdentity: "Stress",
            DevCustomText: "format stress",
            DevGuid: Guid.Parse("1f8524e0-40ad-4b0f-a6d5-c2fef9db6f25"),
            DevDate1UtcTicks: 11,
            DevDate2UtcTicks: 22,
            DevNumber: 33);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            string path = Path.Combine(directory, $"format-stress-{i}.lbdx");
            File.Delete(path);

            using (LibraDexFileSession initialized = LibraDexFileSession.Initialize(
                path,
                options,
                developerMetadata,
                DataKernelTelemetryOptions.Disabled))
            {
                ValidateFormatSession(initialized, developerMetadata);
                IndexDirectorySlotSnapshot slotRequest = CreateHarnessSlot(0, "stress", 0);
                (RouterSnapshot router, _) = initialized.CreateRootRouterIndex(slotRequest);
                IndexDirectorySlotSnapshot slot = slotRequest with { State = IndexDirectoryLayout.ActiveState, RootRouterOffset = router.Offset };
                ValidateRootRouter(initialized, router);
                ValidateFormatSession(initialized, developerMetadata, slot);
            }

            using (LibraDexFileSession opened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.Disabled))
            {
                IndexDirectorySlotSnapshot slot = opened.IndexDirectory.ActiveSlots[0];
                ValidateFormatSession(opened, developerMetadata, slot);
                ValidateRootRouter(opened, opened.ReadRouterSnapshot(slot.RootRouterOffset));
                SuperblockDeveloperMetadata updatedMetadata = new(
                    DevIdentity: "StressUpdated",
                    DevCustomText: "format stress update",
                    DevGuid: Guid.Parse("f2893fb7-6524-4354-bc82-a09c56e14710"),
                    DevDate1UtcTicks: 44,
                    DevDate2UtcTicks: 55,
                    DevNumber: 66);

                opened.UpdateDeveloperMetadata(updatedMetadata);
                ValidateFormatSession(opened, updatedMetadata, slot);
                ValidateRootRouter(opened, opened.ReadRouterSnapshot(slot.RootRouterOffset));
            }

            using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.Disabled))
            {
                SuperblockDeveloperMetadata updatedMetadata = new(
                    DevIdentity: "StressUpdated",
                    DevCustomText: "format stress update",
                    DevGuid: Guid.Parse("f2893fb7-6524-4354-bc82-a09c56e14710"),
                    DevDate1UtcTicks: 44,
                    DevDate2UtcTicks: 55,
                    DevNumber: 66);

                IndexDirectorySlotSnapshot slot = reopened.IndexDirectory.ActiveSlots[0];
                ValidateFormatSession(reopened, updatedMetadata, slot);
                ValidateRootRouter(reopened, reopened.ReadRouterSnapshot(slot.RootRouterOffset));
            }

            File.Delete(path);
        }

        watch.Stop();
        double perSecond = iterations / Math.Max(watch.Elapsed.TotalSeconds, 0.000001);
        Console.WriteLine($"stress-format ok iterations={iterations} elapsedMs={watch.Elapsed.TotalMilliseconds:N3} iterationsPerSecond={perSecond:N2}");
        return 0;
    }


    private static int RunRoutePerf(string[] args)
    {
        int iterations = GetIntOption(args, "--iterations", 10_000_000);
        string artifactDirectory = GetOption(args, "--artifact-directory", Path.Combine("artifacts", "perf-runs"));
        if (iterations <= 0)
        {
            Console.Error.WriteLine("route-perf requires a positive --iterations value.");
            return 1;
        }

        byte[] rootBytes = new byte[RouterLayout.Size];
        byte[] childBytes = new byte[RouterLayout.Size];
        const long childOffset = 0x0001_0000;
        RouterRouteSnapshot[] childRoutes =
        [
            new RouterRouteSnapshot(0x00, 0x3F, 0x0010_0000),
            new RouterRouteSnapshot(0x40, 0x7F, 0x0020_0000),
            new RouterRouteSnapshot(0x80, 0xFF, 0x0030_0000)
        ];

        RouterWriter rootWriter = new(rootBytes);
        rootWriter.InitializeRoot(1);
        rootWriter.WriteRoute(0x40, 0x40, 0x40, childOffset);
        rootWriter.WriteRoute(0x41, 0x41, 0x41, childOffset + 4096);
        rootWriter.WriteRoute(0x42, 0x42, 0x42, childOffset + 8192);
        rootWriter.WriteRoute(0x43, 0x43, 0x43, childOffset + 12288);

        RouterWriter childWriter = new(childBytes);
        childWriter.InitializeCompressed(1, 1, 16, 1, childRoutes);

        RoutePerfResult rootOnly = MeasureRootRoutePerf(rootBytes, iterations);
        RoutePerfResult twoHopEarly = MeasureTwoHopRoutePerf(rootBytes, childBytes, iterations, 0x00);
        RoutePerfResult twoHopMiddle = MeasureTwoHopRoutePerf(rootBytes, childBytes, iterations, 0x40);
        RoutePerfResult twoHopLate = MeasureTwoHopRoutePerf(rootBytes, childBytes, iterations, 0x80);
        RoutePerfResult rootMiss = MeasureRootMissRoutePerf(rootBytes, iterations);

        Console.WriteLine("route perf");
        Console.WriteLine($"iterations {iterations:N0}");
        Console.WriteLine("| scenario | elapsed ms | routes/sec | ns/route | checksum |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: |");
        PrintRoutePerf("root direct hit", rootOnly);
        PrintRoutePerf("two-hop child early", twoHopEarly);
        PrintRoutePerf("two-hop child middle", twoHopMiddle);
        PrintRoutePerf("two-hop child late", twoHopLate);
        PrintRoutePerf("root miss", rootMiss);
        string reportPath = WriteRoutePerfReport(artifactDirectory, iterations, rootOnly, twoHopEarly, twoHopMiddle, twoHopLate, rootMiss);
        Console.WriteLine($"route perf report {Path.GetFullPath(reportPath)}");
        return 0;
    }


    private static int RunRouteShapes(string[] args)
    {
        int iterations = GetIntOption(args, "--iterations", 10_000_000);
        string artifactDirectory = GetOption(args, "--artifact-directory", Path.Combine("artifacts", "perf-runs"));
        if (iterations <= 0)
        {
            Console.Error.WriteLine("route-shapes requires a positive --iterations value.");
            return 1;
        }

        RouteShapeResult catchAll = RunRouteShapeCase(
            "catch-all",
            [new RouterRouteSnapshot(0x00, 0xFF, 0x0010_0000)],
            iterations);

        RouteShapeResult threeRange = RunRouteShapeCase(
            "three-range",
            [
                new RouterRouteSnapshot(0x00, 0x3F, 0x0010_0000),
                new RouterRouteSnapshot(0x40, 0x7F, 0x0020_0000),
                new RouterRouteSnapshot(0x80, 0xFF, 0x0030_0000)
            ],
            iterations);

        RouteShapeResult sparse = RunRouteShapeCase(
            "sparse",
            [
                new RouterRouteSnapshot(0x10, 0x1F, 0x0010_0000),
                new RouterRouteSnapshot(0x80, 0x8F, 0x0020_0000)
            ],
            iterations);

        RouterRouteSnapshot[] selfRangeRoutes = new RouterRouteSnapshot[RouterLayout.MaxOneByteRouteCount];
        for (int i = 0; i < selfRangeRoutes.Length; i++)
        {
            long target = 0x0010_0000 + ((long)i * 4096);
            selfRangeRoutes[i] = new RouterRouteSnapshot((byte)i, (byte)i, target);
        }

        RouteShapeResult worstCompressed = RunRouteShapeCase("self-range-compressed", selfRangeRoutes, iterations);

        RouteShapeResult[] results = [catchAll, threeRange, sparse, worstCompressed];
        Console.WriteLine("route shapes");
        Console.WriteLine($"iterations {iterations:N0}");
        Console.WriteLine("| shape | equivalent | compressed routes | compressed bytes | expanded bytes | saved bytes | compressed ns | expanded ns |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            PrintRouteShape(results[i]);
        }

        string reportPath = WriteRouteShapesReport(artifactDirectory, iterations, results);
        Console.WriteLine($"route shapes report {Path.GetFullPath(reportPath)}");
        return 0;
    }


    private static int RunRouteStress(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(@"T:\LibraDex", "RouteStress"));
        string artifactDirectory = GetOption(args, "--artifact-directory", Path.Combine("artifacts", "perf-runs"));
        int depth = GetIntOption(args, "--depth", 64);
        if (depth <= 0 || depth > 256)
        {
            Console.Error.WriteLine("route-stress requires --depth between 1 and 256.");
            return 1;
        }

        Directory.CreateDirectory(directory);
        ValidateInvalidRouteDefinitions();

        RouteStressResult combined = RunCombinedRouteStress(Path.Combine(directory, "route-stress-combined.lbdx"), depth);
        RouteStressResult separated = RunSeparatedRouteStress(Path.Combine(directory, "route-stress-separated.lbdx"), depth);

        Console.WriteLine("route stress");
        Console.WriteLine($"depth {depth}");
        Console.WriteLine("| mode | commits | writes | bytes | setLength | elapsed ms | final target |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        PrintRouteStress("combined", combined);
        PrintRouteStress("separated", separated);
        string reportPath = WriteRouteStressReport(artifactDirectory, depth, combined, separated);
        Console.WriteLine($"route stress report {Path.GetFullPath(reportPath)}");
        return 0;
    }


    /// <summary>
    /// Prints the router arena read-cache perf matrix to the console.<br/>
    /// </summary>
    /// <param name="results">The measured scenario results.</param>
    private static void PrintRouterArenaReadCachePerf(ReadOnlySpan<RouterArenaReadCachePerfResult> results)
    {
        Console.WriteLine("| scenario | iterations | elapsed ms | ns/route | reads | backing reads | bytes | bytes/route | checksum |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            RouterArenaReadCachePerfResult result = results[i];
            double nsPerRoute = result.Elapsed.TotalSeconds * 1_000_000_000d / result.Iterations;
            double bytesPerRoute = result.BytesRead / (double)result.Iterations;
            Console.WriteLine($"| {result.Name} | {result.Iterations} | {result.Elapsed.TotalMilliseconds:N3} | {nsPerRoute:N2} | {result.ReadCallCount} | {result.BackingReadCallCount} | {result.BytesRead} | {bytesPerRoute:N2} | {result.Checksum} |");
        }
    }


    /// <summary>
    /// Writes the current router arena read-cache perf matrix and archives any previous current report by timestamp.<br/>
    /// </summary>
    /// <param name="artifactDirectory">The report directory.</param>
    /// <param name="iterations">The configured iteration count.</param>
    /// <param name="results">The measured scenario results.</param>
    /// <returns>The current report path.</returns>
    private static string WriteRouterArenaReadCachePerfReport(
        string artifactDirectory,
        int iterations,
        ReadOnlySpan<RouterArenaReadCachePerfResult> results)
    {
        string currentPath = Path.Combine(artifactDirectory, "ss8-8-router-arena-read-cache-perf-current.md");
        ArchiveCurrentReport(currentPath, "ss8-8-router-arena-read-cache-perf");
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string timestampPath = Path.Combine(artifactDirectory, $"ss8-8-router-arena-read-cache-perf-{timestamp}.md");

        StringBuilder builder = new();
        builder.AppendLine($"# SS8-8 Router Arena Read-Cache Perf - {timestamp} UTC");
        builder.AppendLine();
        builder.AppendLine($"- Iterations: `{iterations}`");
        builder.AppendLine("- Existing walker is the baseline route classifier.");
        builder.AppendLine("- Cached walker is the explicit arena-aware route classifier.");
        builder.AppendLine();
        builder.AppendLine("| scenario | iterations | elapsed ms | ns/route | reads | backing reads | bytes | bytes/route | checksum |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            RouterArenaReadCachePerfResult result = results[i];
            double nsPerRoute = result.Elapsed.TotalSeconds * 1_000_000_000d / result.Iterations;
            double bytesPerRoute = result.BytesRead / (double)result.Iterations;
            builder.AppendLine($"| {result.Name} | {result.Iterations} | {result.Elapsed.TotalMilliseconds:N3} | {nsPerRoute:N2} | {result.ReadCallCount} | {result.BackingReadCallCount} | {result.BytesRead} | {bytesPerRoute:N2} | {result.Checksum} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Interpretation");
        builder.AppendLine();
        builder.AppendLine("- Cold cached reads intentionally spend one full 32 KiB arena read to seed the cache.");
        builder.AppendLine("- Same-arena repeated reads show whether that first-touch cost amortizes.");
        builder.AppendLine("- Alternating arenas shows the cost of a single-entry cache under poor locality.");
        builder.AppendLine("- Mixed direct+arena routes show whether direct shelf routes disturb arena locality.");

        File.WriteAllText(currentPath, builder.ToString());
        File.WriteAllText(timestampPath, builder.ToString());
        return currentPath;
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfLongLong(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        long[] keys = new long[itemCount];
        long[] identities = new long[itemCount];
        using LibraDexIndex<long, long> index = Indexes.CreateOrOpen<long, long>(path, DataKernelBackingKind.File, name: "ss8-8-reader", options: options, developerMetadata: CreateDesignPerfMetadata(940), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<long, long> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                keys[i] = i;
                identities[i] = i + 100_000L;
                _ = batch.Insert(keys[i], identities[i]);
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "SS8-8", keys[startIndex], keys[startIndex + rangeCount - 1], new long[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfBytes16Long(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        byte[][] keys = CreateFixedReaderByteKeys(itemCount, width: 16);
        using LibraDexIndex<byte[], long> index = Indexes.CreateOrOpen<byte[], long>(path, DataKernelBackingKind.File, name: "ss16-8-reader", keyWidth: LibraDexScalarWidth.Bytes16, options: options, developerMetadata: CreateDesignPerfMetadata(941), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<byte[], long> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                _ = batch.Insert(keys[i], i + 200_000L);
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "SS16-8", keys[startIndex], keys[startIndex + rangeCount - 1], new long[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfLongGuid(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        long[] keys = new long[itemCount];
        using LibraDexIndex<long, Guid> index = Indexes.CreateOrOpen<long, Guid>(path, DataKernelBackingKind.File, name: "ss8-16-reader", options: options, developerMetadata: CreateDesignPerfMetadata(942), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<long, Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                keys[i] = i;
                _ = batch.Insert(keys[i], CreateFixedReaderGuid(i + 300_000));
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "SS8-16", keys[startIndex], keys[startIndex + rangeCount - 1], new Guid[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfBytes16Guid(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        byte[][] keys = CreateFixedReaderByteKeys(itemCount, width: 16);
        using LibraDexIndex<byte[], Guid> index = Indexes.CreateOrOpen<byte[], Guid>(path, DataKernelBackingKind.File, name: "ss16-16-reader", keyWidth: LibraDexScalarWidth.Bytes16, options: options, developerMetadata: CreateDesignPerfMetadata(943), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<byte[], Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                _ = batch.Insert(keys[i], CreateFixedReaderGuid(i + 400_000));
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "SS16-16", keys[startIndex], keys[startIndex + rangeCount - 1], new Guid[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfBytes32Long(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        byte[][] keys = CreateFixedReaderByteKeys(itemCount, width: 32);
        using LibraDexIndex<byte[], long> index = Indexes.CreateOrOpen<byte[], long>(path, DataKernelBackingKind.File, name: "fs32-8-reader", keyWidth: LibraDexScalarWidth.Bytes32, options: options, developerMetadata: CreateDesignPerfMetadata(944), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<byte[], long> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                _ = batch.Insert(keys[i], i + 500_000L);
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "FS32-8", keys[startIndex], keys[startIndex + rangeCount - 1], new long[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerfBytes32Guid(string path, DataKernelOptions options, int itemCount, int startIndex, int rangeCount, int iterations, int repeatCount)
    {
        File.Delete(path);
        byte[][] keys = CreateFixedReaderByteKeys(itemCount, width: 32);
        using LibraDexIndex<byte[], Guid> index = Indexes.CreateOrOpen<byte[], Guid>(path, DataKernelBackingKind.File, name: "fs32-16-reader", keyWidth: LibraDexScalarWidth.Bytes32, options: options, developerMetadata: CreateDesignPerfMetadata(945), telemetryOptions: DataKernelTelemetryOptions.EnabledOptions);
        using (LibraDexBatch<byte[], Guid> batch = index.BeginBatch())
        {
            for (int i = 0; i < itemCount; i++)
            {
                _ = batch.Insert(keys[i], CreateFixedReaderGuid(i + 600_000));
            }

            _ = batch.Commit();
        }

        return MeasureFixedReaderPerf(index, "FS32-16", keys[startIndex], keys[startIndex + rangeCount - 1], new Guid[rangeCount], rangeCount, iterations, repeatCount);
    }


    private static FixedReaderPerfRow MeasureFixedReaderPerf<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        string shape,
        TKey lowerKey,
        TKey upperKey,
        TIdentity[] materializedIdentities,
        int rangeCount,
        int iterations,
        int repeatCount)
    {
        FixedReaderPerfSample[] samples = new FixedReaderPerfSample[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureFixedReaderPerfSample(index, lowerKey, upperKey, materializedIdentities, rangeCount, iterations);
        }

        FixedReaderPerfSample sample = SelectMedianFixedReaderPerfSample(samples);
        return new FixedReaderPerfRow(
            shape,
            rangeCount,
            iterations,
            repeatCount,
            sample.MaterializedNsPerIdentity,
            sample.CursorIdentityNsPerIdentity,
            sample.CursorKeyNsPerIdentity,
            sample.CursorTupleNsPerIdentity,
            sample.DescendingTupleNsPerIdentity,
            sample.CountNsPerCall,
            sample.SkipNsPerIdentity,
            sample.Checksum);
    }


    private static FixedReaderPerfSample MeasureFixedReaderPerfSample<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        TKey lowerKey,
        TKey upperKey,
        TIdentity[] materializedIdentities,
        int rangeCount,
        int iterations)
    {
        long checksum = 0;
        long totalIdentities = (long)rangeCount * iterations;

        Stopwatch materializedWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            LibraDexGenericRangeReadResult read = index.ReadRange(lowerKey, upperKey, materializedIdentities);
            if (read.IdentityCount != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf materialized read returned {read.IdentityCount} identities; expected {rangeCount}.");
            }

            for (int j = 0; j < read.IdentityCount; j++)
            {
                checksum += HashFixedReaderValue(materializedIdentities[j]);
            }
        }

        materializedWatch.Stop();

        Stopwatch identityWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            int count = 0;
            while (reader.TryReadNextIdentity(out TIdentity identity))
            {
                checksum += HashFixedReaderValue(identity);
                count++;
            }

            if (count != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf identity cursor returned {count} identities; expected {rangeCount}.");
            }
        }

        identityWatch.Stop();

        Stopwatch keyWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            int count = 0;
            while (reader.TryReadNextKey(out TKey key))
            {
                checksum += HashFixedReaderValue(key);
                count++;
            }

            if (count != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf key cursor returned {count} keys; expected {rangeCount}.");
            }
        }

        keyWatch.Stop();

        Stopwatch tupleWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            int count = 0;
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                checksum += HashFixedReaderValue(key);
                checksum += HashFixedReaderValue(identity);
                count++;
            }

            if (count != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf tuple cursor returned {count} tuples; expected {rangeCount}.");
            }
        }

        tupleWatch.Stop();

        Stopwatch descendingTupleWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey, QueryDirection.Descending);
            int count = 0;
            while (reader.TryReadNext(out TKey key, out TIdentity identity))
            {
                checksum += HashFixedReaderValue(key);
                checksum += HashFixedReaderValue(identity);
                count++;
            }

            if (count != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf descending tuple cursor returned {count} tuples; expected {rangeCount}.");
            }
        }

        descendingTupleWatch.Stop();

        Stopwatch countWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            int count = reader.Count;
            if (count != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf count cursor returned {count}; expected {rangeCount}.");
            }

            checksum += count;
        }

        countWatch.Stop();

        Stopwatch skipWatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lowerKey, upperKey);
            int skipped = 0;
            while (skipped < rangeCount)
            {
                int current = reader.Skip(Math.Min(16, rangeCount - skipped));
                if (current == 0)
                {
                    break;
                }

                skipped += current;
            }

            if (skipped != rangeCount)
            {
                throw new InvalidDataException($"Fixed reader perf skip cursor skipped {skipped}; expected {rangeCount}.");
            }

            checksum += skipped;
        }

        skipWatch.Stop();

        double materializedNs = ElapsedNsPerIdentity(materializedWatch.ElapsedTicks, totalIdentities);
        double identityNs = ElapsedNsPerIdentity(identityWatch.ElapsedTicks, totalIdentities);
        double keyNs = ElapsedNsPerIdentity(keyWatch.ElapsedTicks, totalIdentities);
        double tupleNs = ElapsedNsPerIdentity(tupleWatch.ElapsedTicks, totalIdentities);
        double descendingTupleNs = ElapsedNsPerIdentity(descendingTupleWatch.ElapsedTicks, totalIdentities);
        double countNs = ElapsedNsPerCall(countWatch.ElapsedTicks, iterations);
        double skipNs = ElapsedNsPerIdentity(skipWatch.ElapsedTicks, totalIdentities);
        return new FixedReaderPerfSample(materializedNs, identityNs, keyNs, tupleNs, descendingTupleNs, countNs, skipNs, checksum);
    }


    private static VarKeyScalar8ReadMeasurement MeasureSqliteVarKeyScalar8RangeReads(SqliteConnection connection, int keyLength, int prefixCount, int itemCount, int iterations, VarIdentityIterationMode iterationMode)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = CreateSqliteVarIdentityRangeReadCommandText(iterationMode);
        command.Parameters.AddWithValue("$lower", lower);
        command.Parameters.AddWithValue("$upper", upper);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                checksum += ChecksumSqliteVarKeyScalar8Current(reader, iterationMode);
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite VS8 read comparison expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Measures SQLite prefix-range reads for the `VS16` comparison schema.<br/>
    /// The key range is the same raw BLOB range used by `VS8`; only the selected identity column changes from integer to a 16-byte BLOB.<br/>
    /// The checksum decodes both big-endian identity halves so the SQLite row validates the same logical identity stream as the LibraDex `VS16` row.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="itemCount">The total generated identity count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The SQLite range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureSqliteVarKeyScalar16RangeReads(SqliteConnection connection, int keyLength, int prefixCount, int itemCount, int iterations, VarIdentityIterationMode iterationMode)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = CreateSqliteVarIdentityRangeReadCommandText(iterationMode);
        command.Parameters.AddWithValue("$lower", lower);
        command.Parameters.AddWithValue("$upper", upper);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                checksum += ChecksumSqliteVarKeyScalar16Current(reader, iterationMode);
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite VS16 read comparison expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    private static VarKeyScalar8ReadMeasurement MeasureSqliteVarKeyScalar8ExplicitRangeReads(
        SqliteConnection connection,
        ReadOnlySpan<byte> lower,
        ReadOnlySpan<byte> upper,
        int expected,
        int iterations)
    {
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", lower.ToArray());
        command.Parameters.AddWithValue("$upper", upper.ToArray());
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                checksum += unchecked((long)DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(0)));
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite VS8 explicit range expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Measures SQLite explicit raw-key range reads for the `VS16` comparison schema.<br/>
    /// This is the SQLite counterpart to `MeasureVarKeyScalar16ExplicitRangeReads`, using the same lower/upper raw byte keys and decoding the 16-byte identity BLOB.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="lower">The inclusive lower raw key.</param>
    /// <param name="upper">The inclusive upper raw key.</param>
    /// <param name="expected">The expected number of identities per iteration.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The SQLite range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureSqliteVarKeyScalar16ExplicitRangeReads(
        SqliteConnection connection,
        ReadOnlySpan<byte> lower,
        ReadOnlySpan<byte> upper,
        int expected,
        int iterations)
    {
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", lower.ToArray());
        command.Parameters.AddWithValue("$upper", upper.ToArray());
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                byte[] identity = (byte[])reader.GetValue(0);
                checksum += unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(identity.AsSpan(0, 8)));
                checksum += unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(identity.AsSpan(8, 8)));
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite VS16 explicit range expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Measures SQLite prefix-range reads for the `VV` comparison schema.<br/>
    /// The query orders by raw key and raw identity bytes, matching the `VV` shelf tuple order used for duplicate-key stability.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="keyLength">The generated key length in bytes.</param>
    /// <param name="prefixCount">The number of generated first-byte prefixes.</param>
    /// <param name="itemCount">The total generated identity count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The SQLite range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureSqliteVarKeyVarIdentityRangeReads(SqliteConnection connection, int keyLength, int prefixCount, int itemCount, int iterations, VarIdentityIterationMode iterationMode)
    {
        byte[] lower = CreateVarKeyScalar8PrefixLower(0, keyLength);
        byte[] upper = CreateVarKeyScalar8PrefixUpper(Math.Min(2, prefixCount - 1), keyLength);
        int expected = CountVarKeyScalar8PrefixRange(itemCount, prefixCount, 0, Math.Min(2, prefixCount - 1));
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = CreateSqliteVarIdentityRangeReadCommandText(iterationMode);
        command.Parameters.AddWithValue("$lower", lower);
        command.Parameters.AddWithValue("$upper", upper);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                checksum += ChecksumSqliteVarKeyVarIdentityCurrent(reader, iterationMode);
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite VV read comparison expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Calculates the maximum compressed multi-byte route count that fits in one router page for the supplied consumed byte count.<br/>
    /// This keeps recursive bulk planning from producing a valid logical route table that cannot be persisted inside the fixed router page.<br/>
    /// </summary>
    /// <param name="prefixByteCount">The number of key bytes consumed by the router.</param>
    /// <returns>The maximum persisted route count that fits inside the router page.</returns>
    private static int CalculateVarKeyScalar8MaxBulkRouteCount(byte prefixByteCount)
    {
        int routeCount = 256;
        while (routeCount > 0 &&
               RouterLayout.RoutesOffset + RouterLayout.GetMultiByteRouteStorageLength(routeCount, prefixByteCount) > RouterLayout.Size)
        {
            routeCount--;
        }

        return routeCount;
    }


    /// <summary>
    /// Calculates the maximum compressed multi-byte route count that fits in one router page for the supplied consumed byte count.<br/>
    /// This keeps recursive bulk planning from producing a valid logical route table that cannot be persisted inside the fixed router page.<br/>
    /// </summary>
    /// <param name="prefixByteCount">The number of key bytes consumed by the router.</param>
    /// <returns>The maximum persisted route count that fits inside the router page.</returns>
    private static int CalculateVarKeyScalar16MaxBulkRouteCount(byte prefixByteCount)
    {
        int routeCount = 256;
        while (routeCount > 0 &&
               RouterLayout.RoutesOffset + RouterLayout.GetMultiByteRouteStorageLength(routeCount, prefixByteCount) > RouterLayout.Size)
        {
            routeCount--;
        }

        return routeCount;
    }


    /// <summary>
    /// Measures SQLite scalar-key range reads for the `SV8` comparison schema.<br/>
    /// The ordered query mirrors the `SV8` tuple contract by scanning `(k, i)` and folding the returned identity BLOB bytes into the checksum.<br/>
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="duplicateModulo">The generated scalar-key modulo.</param>
    /// <param name="identityLength">The configured maximum identity length, used for validation context only.</param>
    /// <param name="lowerKey">The inclusive lower generated scalar key.</param>
    /// <param name="upperKey">The inclusive upper generated scalar key.</param>
    /// <param name="itemCount">The total generated item count.</param>
    /// <param name="iterations">The number of measured range reads.</param>
    /// <returns>The SQLite range-read measurement row.</returns>
    private static VarKeyScalar8ReadMeasurement MeasureSqliteScalar8VarIdentityRangeReads(
        SqliteConnection connection,
        int duplicateModulo,
        string keyDistribution,
        int identityLength,
        int lowerKey,
        int upperKey,
        int itemCount,
        int iterations,
        VarIdentityIterationMode iterationMode)
    {
        _ = identityLength;
        int expected = CountScalar8VarIdentityKeyRange(itemCount, duplicateModulo, lowerKey, upperKey);
        long checksum = 0;
        long total = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = CreateSqliteVarIdentityRangeReadCommandText(iterationMode);
        command.Parameters.AddWithValue("$lower", unchecked((long)CreateScalar8VarIdentitySqliteKey(lowerKey, duplicateModulo, keyDistribution)));
        command.Parameters.AddWithValue("$upper", unchecked((long)CreateScalar8VarIdentitySqliteKey(upperKey, duplicateModulo, keyDistribution)));
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                checksum += ChecksumSqliteScalar8VarIdentityCurrent(reader, iterationMode);
                count++;
            }

            if (count != expected)
            {
                throw new InvalidDataException($"SQLite SV8 range expected {expected} identities, got {count}.");
            }

            total += count;
        }

        watch.Stop();
        return CreateVarKeyScalar8ReadMeasurement(iterations, expected, total, watch.Elapsed, checksum);
    }


    /// <summary>
    /// Measures one SQLite transaction write row using the requested key order.<br/>
    /// The SQLite case intentionally mirrors the LibraDex walked rows by inserting every item inside one transaction rather than committing per inserted identity.<br/>
    /// </summary>
    /// <param name="path">The target SQLite database path.</param>
    /// <param name="keys">The generated raw byte keys.</param>
    /// <param name="order">The insertion order expressed as indexes into <paramref name="keys"/>.</param>
    /// <param name="options">The SQLite journal and sync options.</param>
    /// <param name="label">The row label to report.</param>
    /// <returns>The measured write-matrix row.</returns>
    private static VarKeyScalar8WriteMatrixResult MeasureSqliteVarKeyScalar8Write(
        string path,
        byte[][] keys,
        ReadOnlySpan<int> order,
        SqliteScalar8Scalar8Options options,
        string label)
    {
        ResetSqliteScalar8Scalar8Files(path);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        using (SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options))
        {
            InitializeSqliteVarKeyScalar8Schema(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO items(k, i) VALUES($k, $i);";
            SqliteParameter keyParameter = command.Parameters.Add("$k", SqliteType.Blob);
            SqliteParameter identityParameter = command.Parameters.Add("$i", SqliteType.Integer);
            for (int i = 0; i < order.Length; i++)
            {
                int sourceIndex = order[i];
                keyParameter.Value = keys[sourceIndex];
                identityParameter.Value = EncodeSqliteSortableUnsignedScalar8(CreateVarKeyScalar8Identity(sourceIndex));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        watch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return VarKeyScalar8WriteMatrixResult.ForSqlite(
            label,
            DescribeVarKeyScalar8WriteOrder(order, keys),
            order.Length,
            watch.Elapsed,
            GetSqliteScalar8Scalar8StorageBytes(path),
            TimeSpan.Zero,
            allocatedBytes);
    }


    /// <summary>
    /// Measures raw small positional `RandomAccess` read and write shapes that mirror current `SS8-8` routed operation sizes.<br/>
    /// The command pre-creates a rotating region file before timing so measured writes update existing offsets rather than measuring file creation or file extension.<br/>
    /// The resulting report is the fair small-I/O companion to the large sequential raw `DataKernel` baseline.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.</param>
    /// <returns>Zero when the small-I/O baseline report is written.</returns>
    private static int RunSmallIoBaseline(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "small-io-baseline.lbdx"));
        string artifactDirectory = GetOption(args, "--artifact-directory", Path.Combine("artifacts", "perf-runs"));
        int iterations = GetIntOption(args, "--iterations", 10000);
        int warmupIterations = GetIntOption(args, "--warmup-iterations", 1000);
        int regionCount = GetIntOption(args, "--regions", 1024);
        if (iterations <= 0 || warmupIterations < 0 || regionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Small-I/O baseline iterations, warmup iterations, and regions must be valid positive values.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(artifactDirectory);
        SmallIoShape[] writeShapes =
        [
            new("write-32k-1call", [32768]),
            new("write-69632-1call", [69632]),
            new("write-69632-2call", [4096, 65536]),
            new("write-7078-30call", CreateEvenSegments(7078, 30)),
            new("write-16876-44call", CreateEvenSegments(16876, 44)),
            new("write-94042-44call", CreateEvenSegments(94042, 44))
        ];

        SmallIoShape[] readShapes =
        [
            new("read-36864-2call", [4096, 32768]),
            new("read-40968-5call", [4096, 4096, 4096, 4096, 24584]),
            new("read-69672-5call", [4096, 4096, 4096, 4096, 53288]),
            new("read-528448-33call", CreateEvenSegments(528448, 33)),
            new("read-532488-5call", [4096, 4096, 4096, 4096, 516104])
        ];

        int regionStride = GetMaxSmallIoShapeBytes(writeShapes, readShapes);
        PrepareSmallIoBaselineFile(path, regionStride, regionCount);
        byte[] buffer = CreatePattern(regionStride, 71);
        byte[] readBuffer = new byte[regionStride];
        SmallIoBaselineResult[] results = new SmallIoBaselineResult[writeShapes.Length + readShapes.Length];
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.RandomAccess);

        int resultIndex = 0;
        for (int i = 0; i < writeShapes.Length; i++)
        {
            results[resultIndex++] = MeasureSmallIoWriteShape(handle, writeShapes[i], buffer, iterations, warmupIterations, regionStride, regionCount);
        }

        for (int i = 0; i < readShapes.Length; i++)
        {
            results[resultIndex++] = MeasureSmallIoReadShape(handle, readShapes[i], readBuffer, iterations, warmupIterations, regionStride, regionCount);
        }

        string reportPath = WriteSmallIoBaselineReport(artifactDirectory, path, iterations, warmupIterations, regionCount, regionStride, results);
        Console.WriteLine("small I/O baseline");
        Console.WriteLine($"iterations {iterations:N0}");
        Console.WriteLine($"warmupIterations {warmupIterations:N0}");
        Console.WriteLine($"regions {regionCount:N0}");
        Console.WriteLine($"report {Path.GetFullPath(reportPath)}");
        return 0;
    }


    /// <summary>
    /// Calculates a deterministic checksum over the shared generated identity set.<br/>
    /// The checksum is intentionally independent of key projection so both indexes can be understood as covering the same logical identities.<br/>
    /// </summary>
    /// <param name="batches">The generated batch count.</param>
    /// <param name="itemsPerBatch">The generated item count per batch.</param>
    /// <param name="prefixCount">The generated root-prefix count.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <returns>The generated identity checksum.</returns>
    private static long CalculateSameIdentityChecksum(int batches, int itemsPerBatch, int prefixCount, int[] order)
    {
        long checksum = 0;
        for (int batch = 0; batch < batches; batch++)
        {
            for (int i = 0; i < itemsPerBatch; i++)
            {
                ulong identity = CreateRoutedBulkWriteKey(batch, order[i], itemsPerBatch, prefixCount);
                checksum += unchecked((long)((identity >> 32) ^ identity ^ (ulong)(i + 1)));
            }
        }

        return checksum;
    }


    /// <summary>
    /// Measures one named `FS32-8` design-performance scenario across repeated fresh-file samples.<br/>
    /// The fresh-file pattern matches the scalar-key baselines while keeping 32-byte key costs visible without setup state bleed-through.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="runs">The number of samples to collect.</param>
    /// <param name="runner">The scenario runner that returns one measured sample.</param>
    /// <param name="directory">The directory where scenario files are written.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The aggregated scenario result.</returns>
    private static Scalar8Scalar8DesignPerfResult MeasureFixed32Scalar8DesignScenario(
        string name,
        int runs,
        Fixed32Scalar8DesignScenarioRunner runner,
        string directory,
        Fixed32Scalar8Profile profile)
    {
        long elapsedTicks = 0;
        long readCalls = 0;
        long readBytes = 0;
        long writeCalls = 0;
        long writeBytes = 0;
        long setLengthCalls = 0;
        double[] elapsedMillisecondsSamples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            string safeName = name.Replace(' ', '-');
            string path = Path.Combine(directory, $"{safeName}-{i:D4}.lbdx");
            File.Delete(path);
            Scalar8Scalar8DesignPerfSample sample = runner(path, profile);
            elapsedTicks += sample.ElapsedTicks;
            elapsedMillisecondsSamples[i] = sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            readCalls += sample.ReadCallCount;
            readBytes += sample.BytesRead;
            writeCalls += sample.WriteCallCount;
            writeBytes += sample.BytesWritten;
            setLengthCalls += sample.SetLengthCallCount;
        }

        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Scalar8Scalar8MetricStats elapsedStats = ComputeStats(elapsedMillisecondsSamples);
        return new Scalar8Scalar8DesignPerfResult(
            name,
            runs,
            elapsedMilliseconds / runs,
            elapsedStats.Median,
            elapsedStats.Min,
            elapsedStats.Max,
            elapsedStats.StandardDeviation,
            readCalls / (double)runs,
            readBytes / (double)runs,
            writeCalls / (double)runs,
            writeBytes / (double)runs,
            setLengthCalls / (double)runs);
    }


    /// <summary>
    /// Measures one direct root-to-shelf `FS32-8` no-split insert mutation after deterministic setup.<br/>
    /// The timed region covers only the routed insert call against an existing one-level shelf so direct insert CPU and one-shelf rewrite cost stay isolated from file creation and route setup.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignDirectNoSplit(string path, Fixed32Scalar8Profile profile)
    {
        const int itemCount = 512;
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(321), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pfdn", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoute(root.Offset, 0x00, profile, itemCount);
        _ = session.GetAndResetReadTelemetry();
        Fixed32Scalar8Layout.EncodeFixed32FromUInt64((ulong)(itemCount + 128), out ulong key0, out ulong key1, out ulong key2, out ulong key3);
        ulong identity = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)(itemCount + 128));
        Stopwatch stopwatch = Stopwatch.StartNew();
        (long _, Fixed32Scalar8InsertResult insertResult, DataKernelCommitTelemetry commit) = session.InsertRoutedFixed32Scalar8NoSplit(root.Offset, 0x00, profile, key0, key1, key2, key3, identity, allowDuplicateKeys: true);
        stopwatch.Stop();
        if (insertResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf direct no-split scenario did not insert.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), commit);
    }


    /// <summary>
    /// Measures one `FS32-8` root-prefix-visible split mutation after deterministic setup.<br/>
    /// The setup creates one full shelf shared by two root prefixes; the timed call rewrites the original shelf, appends the right shelf, and rewrites the root route.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignRootPrefixSplit(string path, Fixed32Scalar8Profile profile)
    {
        int leftCount = profile.MaxItemCount / 2;
        int rightCount = profile.MaxItemCount - leftCount;
        ulong[] key0s = new ulong[profile.MaxItemCount];
        ulong[] key1s = new ulong[profile.MaxItemCount];
        ulong[] key2s = new ulong[profile.MaxItemCount];
        ulong[] key3s = new ulong[profile.MaxItemCount];
        ulong[] identities = new ulong[profile.MaxItemCount];
        int index = 0;
        for (int i = 0; i < leftCount; i++)
        {
            Fixed32Scalar8Layout.EncodeFixed32FromUInt64((ulong)i, out key0s[index], out key1s[index], out key2s[index], out key3s[index]);
            identities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        const ulong rightPrefixKey0 = 0x8000_0000_0000_0000UL;
        for (int i = 0; i < rightCount; i++)
        {
            key0s[index] = rightPrefixKey0;
            key1s[index] = 0;
            key2s[index] = 0;
            key3s[index] = (ulong)i;
            identities[index] = Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i);
            index++;
        }

        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(322), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pfrs", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, key0s, key1s, key2s, key3s, identities, [0x00, 0x80]);
        _ = session.GetAndResetReadTelemetry();
        Stopwatch stopwatch = Stopwatch.StartNew();
        (
            _,
            long rightShelfOffset,
            _,
            _,
            Fixed32Scalar8InsertResult insertResult,
            DataKernelCommitTelemetry commit) = session.SplitRoutedFixed32Scalar8ByRootPrefix(root.Offset, 0x00, 0x80, profile, rightPrefixKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL));
        stopwatch.Stop();
        if (rightShelfOffset == 0 || insertResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf root-prefix split scenario did not split.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), commit);
    }


    /// <summary>
    /// Measures one `FS32-8` same-prefix transform split mutation after deterministic setup.<br/>
    /// The timed call replaces the source shelf route with a child router and two shelves, matching the transform shape used by walked deeper mutations.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignTransformSplit(string path, Fixed32Scalar8Profile profile)
    {
        CreateFixed32Scalar8TransformSplitVectors(profile, out ulong[] key0s, out ulong[] key1s, out ulong[] key2s, out ulong[] key3s, out ulong[] identities, out int _, out int _);
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(323), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pfts", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, key0s, key1s, key2s, key3s, identities, [0x00]);
        _ = session.GetAndResetReadTelemetry();
        const ulong secondByteRightKey0 = 0x0080_0000_0000_0000UL;
        Stopwatch stopwatch = Stopwatch.StartNew();
        (
            _,
            long leftShelfOffset,
            long rightShelfOffset,
            _,
            _,
            Fixed32Scalar8InsertResult insertResult,
            DataKernelCommitTelemetry commit) = session.SplitRoutedFixed32Scalar8ByShelfTransform(root.Offset, 0x00, 0x80, profile, secondByteRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL));
        stopwatch.Stop();
        if (leftShelfOffset == 0 || rightShelfOffset == 0 || insertResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf transform split scenario did not transform.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), commit);
    }


    /// <summary>
    /// Measures one direct two-level `FS32-8` no-split insert mutation after deterministic transform setup.<br/>
    /// The setup creates a child-router route first; the timed call uses the specialized two-level insert path to isolate direct child-route mutation cost.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignDirectTwoLevelNoSplit(string path, Fixed32Scalar8Profile profile)
    {
        CreateFixed32Scalar8TransformSplitVectors(profile, out ulong[] key0s, out ulong[] key1s, out ulong[] key2s, out ulong[] key3s, out ulong[] identities, out int _, out int _);
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(324), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pf2d", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, key0s, key1s, key2s, key3s, identities, [0x00]);
        const ulong secondByteRightKey0 = 0x0080_0000_0000_0000UL;
        (
            long childRouterOffset,
            _,
            _,
            _,
            _,
            Fixed32Scalar8InsertResult splitResult,
            _) = session.SplitRoutedFixed32Scalar8ByShelfTransform(root.Offset, 0x00, 0x80, profile, secondByteRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL));
        if (childRouterOffset == 0 || splitResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf direct two-level setup did not transform.");
        }

        _ = session.GetAndResetReadTelemetry();
        Stopwatch stopwatch = Stopwatch.StartNew();
        (
            _,
            _,
            Fixed32Scalar8InsertResult insertResult,
            DataKernelCommitTelemetry commit) = session.InsertTwoLevelRoutedFixed32Scalar8NoSplit(root.Offset, 0x00, 0x80, profile, secondByteRightKey0, 0, 0, 20_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(20_000UL), allowDuplicateKeys: true);
        stopwatch.Stop();
        if (insertResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf direct two-level scenario did not insert.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), commit);
    }


    /// <summary>
    /// Measures one walked two-level `FS32-8` no-split insert mutation after deterministic transform setup.<br/>
    /// The timed mutation uses the full walked insert orchestrator, so route classification and decision overhead are included beside the shelf rewrite cost.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignWalkedTwoLevelNoSplit(string path, Fixed32Scalar8Profile profile)
    {
        CreateFixed32Scalar8TransformSplitVectors(profile, out ulong[] key0s, out ulong[] key1s, out ulong[] key2s, out ulong[] key3s, out ulong[] identities, out int _, out int _);
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(325), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pf2w", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, key0s, key1s, key2s, key3s, identities, [0x00]);
        const ulong secondByteRightKey0 = 0x0080_0000_0000_0000UL;
        (
            long childRouterOffset,
            _,
            _,
            _,
            _,
            Fixed32Scalar8InsertResult splitResult,
            _) = session.SplitRoutedFixed32Scalar8ByShelfTransform(root.Offset, 0x00, 0x80, profile, secondByteRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL));
        if (childRouterOffset == 0 || splitResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf walked two-level setup did not transform.");
        }

        _ = session.GetAndResetReadTelemetry();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8(root.Offset, profile, secondByteRightKey0, 0, 0, 20_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(20_000UL), allowDuplicateKeys: true, maxRouterHops: 8);
        stopwatch.Stop();
        if (result.Kind != Fixed32Scalar8RoutedInsertKind.WalkedNoSplit)
        {
            throw new InvalidDataException("FS32-8 design perf walked two-level scenario did not take the walked no-split path.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), result.Commit);
    }


    /// <summary>
    /// Measures one walked deeper `FS32-8` transform split after deterministic child-router setup.<br/>
    /// The scenario reaches a full shelf below a child router and times the full walked insert orchestrator through its shelf-transform split branch.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignWalkedTransformSplit(string path, Fixed32Scalar8Profile profile)
    {
        CreateFixed32Scalar8DeeperTransformSplitVectors(profile, out ulong[] key0s, out ulong[] key1s, out ulong[] key2s, out ulong[] key3s, out ulong[] identities, out int _, out int _);
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(326), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pfwt", 0));
        _ = session.CreateFixed32Scalar8ShelfBehindChildRouter(root.Offset, 0x00, 0x00, profile, key0s, key1s, key2s, key3s, identities);
        _ = session.GetAndResetReadTelemetry();
        const ulong thirdByteRightKey0 = 0x0000_8000_0000_0000UL;
        Stopwatch stopwatch = Stopwatch.StartNew();
        Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8(root.Offset, profile, thirdByteRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL), allowDuplicateKeys: true, maxRouterHops: 8);
        stopwatch.Stop();
        if (result.Kind != Fixed32Scalar8RoutedInsertKind.WalkedShelfTransformSplit)
        {
            throw new InvalidDataException("FS32-8 design perf walked transform split scenario did not take the walked transform split path.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), result.Commit);
    }


    /// <summary>
    /// Measures one walked `FS32-8` parent-route split after deterministic setup fills the target child-router shelf.<br/>
    /// The timed mutation uses the full walked insert orchestrator and expects the parent-route refinement path that preserves one transformed shelf and appends the far-right split shelf.<br/>
    /// </summary>
    /// <param name="path">The sample file path.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <returns>The measured mutation sample.</returns>
    private static Scalar8Scalar8DesignPerfSample MeasureFixed32Scalar8DesignWalkedParentRouteSplit(string path, Fixed32Scalar8Profile profile)
    {
        CreateFixed32Scalar8TransformSplitVectors(profile, out ulong[] key0s, out ulong[] key1s, out ulong[] key2s, out ulong[] key3s, out ulong[] identities, out int _, out int _);
        DataKernelOptions options = CreateDesignPerfOptions();
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(327), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f32pfpr", 0));
        _ = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, key0s, key1s, key2s, key3s, identities, [0x00]);
        const ulong secondByteRightKey0 = 0x0080_0000_0000_0000UL;
        const ulong secondByteFarRightKey0 = 0x00C0_0000_0000_0000UL;
        (
            long childRouterOffset,
            _,
            long rightShelfOffset,
            _,
            int transformedRightCount,
            Fixed32Scalar8InsertResult transformResult,
            _) = session.SplitRoutedFixed32Scalar8ByShelfTransform(root.Offset, 0x00, 0x80, profile, secondByteRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL));
        if (childRouterOffset == 0 || rightShelfOffset == 0 || transformResult != Fixed32Scalar8InsertResult.Inserted)
        {
            throw new InvalidDataException("FS32-8 design perf walked parent-route split setup did not transform.");
        }

        int fillCount = profile.MaxItemCount - transformedRightCount;
        for (int i = 0; i < fillCount; i++)
        {
            Fixed32Scalar8RoutedInsertResult fill = session.InsertWalkedRoutedFixed32Scalar8(
                root.Offset,
                profile,
                secondByteFarRightKey0,
                0,
                0,
                (ulong)i,
                Fixed32Scalar8Layout.EncodeUnsignedScalar8((ulong)i),
                allowDuplicateKeys: true,
                maxRouterHops: 8);
            if (fill.PrimaryOffset != rightShelfOffset || fill.InsertResult != Fixed32Scalar8InsertResult.Inserted || fill.Kind != Fixed32Scalar8RoutedInsertKind.WalkedNoSplit)
            {
                throw new InvalidDataException("FS32-8 design perf walked parent-route split setup did not fill the expected shelf.");
            }
        }

        _ = session.GetAndResetReadTelemetry();
        Stopwatch stopwatch = Stopwatch.StartNew();
        Fixed32Scalar8RoutedInsertResult result = session.InsertWalkedRoutedFixed32Scalar8(root.Offset, profile, secondByteFarRightKey0, 0, 0, 10_000UL, Fixed32Scalar8Layout.EncodeUnsignedScalar8(10_000UL), allowDuplicateKeys: true, maxRouterHops: 8);
        stopwatch.Stop();
        if (result.Kind != Fixed32Scalar8RoutedInsertKind.WalkedParentRouteSplit)
        {
            throw new InvalidDataException("FS32-8 design perf walked parent-route split scenario did not take the parent-route split path.");
        }

        return CreateDesignPerfSample(stopwatch, session.GetAndResetReadTelemetry(), result.Commit);
    }


    /// <summary>
    /// Runs correctness checks plus tiered performance checks and writes a timestamped validation report.<br/>
    /// This command is intentionally harness-level so reporting allocations and formatting do not leak into hot library paths.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.</param>
    /// <returns>Zero when validation completed without fail-level drift; otherwise one.</returns>
    private static int RunValidate(string[] args)
    {
        string tier = GetOption(args, "--tier", "fast").ToLowerInvariant();
        string artifactDirectory = GetOption(args, "--artifact-directory", Path.Combine("artifacts", "validation-runs"));
        string dataDirectory = GetOption(args, "--data-directory", Path.Combine(@"T:\LibraDex", "ValidationRuns"));
        Directory.CreateDirectory(artifactDirectory);
        Directory.CreateDirectory(dataDirectory);

        ValidationTierOptions tierOptions = GetValidationTierOptions(tier);
        List<ValidationMetric> metrics = [];

        Console.WriteLine($"validate tier={tierOptions.Name} totalBytes={tierOptions.TotalBytes} perfRuns={tierOptions.PerfRuns} stressIterations={tierOptions.StressIterations}");

        int sanityFile = RunSanity(["sanity-raw", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-raw-sanity.lbdx")]);
        int sanityMemory = RunSanity(["sanity-raw", "--backing", "memory"]);
        int sanityFormat = RunFormatSanity(["sanity-format", "--path", Path.Combine(dataDirectory, "validate-format-sanity.lbdx")]);
        int routeSanity = RunRouteSanity(["route-sanity", "--path", Path.Combine(dataDirectory, "validate-route-sanity.lbdx")]);
        int routeShapes = RunRouteShapes(["route-shapes", "--iterations", "1000000", "--artifact-directory", Path.Combine("artifacts", "perf-runs")]);
        int routeStress = RunRouteStress(["route-stress", "--directory", Path.Combine(dataDirectory, "RouteStress"), "--depth", "16"]);
        int scalar8Scalar8Sanity = RunScalar8Scalar8Sanity(["ss8-8-sanity"]);
        int scalar16Scalar8Sanity = RunScalar16Scalar8Sanity(["ss16-8-sanity"]);
        int fixed32Scalar8Sanity = RunFixed32Scalar8Sanity(["fs32-8-sanity"]);
        int scalar8Scalar16Sanity = RunScalar8Scalar16Sanity(["ss8-16-sanity"]);
        int scalar16Scalar16Sanity = RunScalar16Scalar16Sanity(["ss16-16-sanity"]);
        int scalar8Scalar8DkFile = RunScalar8Scalar8DataKernelSanity(["ss8-8-dk-sanity", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-ss8-8-dk-sanity.lbdx")]);
        int scalar8Scalar8DkMemory = RunScalar8Scalar8DataKernelSanity(["ss8-8-dk-sanity", "--backing", "memory"]);
        int scalar16Scalar8DkFile = RunScalar16Scalar8DataKernelSanity(["ss16-8-dk-sanity", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-ss16-8-dk-sanity.lbdx")]);
        int scalar16Scalar8DkMemory = RunScalar16Scalar8DataKernelSanity(["ss16-8-dk-sanity", "--backing", "memory"]);
        int fixed32Scalar8DkFile = RunFixed32Scalar8DataKernelSanity(["fs32-8-dk-sanity", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-fs32-8-dk-sanity.lbdx")]);
        int fixed32Scalar8DkMemory = RunFixed32Scalar8DataKernelSanity(["fs32-8-dk-sanity", "--backing", "memory"]);
        int scalar8Scalar16DkFile = RunScalar8Scalar16DataKernelSanity(["ss8-16-dk-sanity", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-ss8-16-dk-sanity.lbdx")]);
        int scalar8Scalar16DkMemory = RunScalar8Scalar16DataKernelSanity(["ss8-16-dk-sanity", "--backing", "memory"]);
        int scalar16Scalar16DkFile = RunScalar16Scalar16DataKernelSanity(["ss16-16-dk-sanity", "--backing", "file", "--path", Path.Combine(dataDirectory, "validate-ss16-16-dk-sanity.lbdx")]);
        int scalar16Scalar16DkMemory = RunScalar16Scalar16DataKernelSanity(["ss16-16-dk-sanity", "--backing", "memory"]);
        int scalar8Scalar8Route = RunScalar8Scalar8RouteSanity(["ss8-8-route-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-sanity.lbdx")]);
        int scalar16Scalar8Route = RunScalar16Scalar8RouteSanity(["ss16-8-route-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-route-sanity.lbdx")]);
        int fixed32Scalar8Route = RunFixed32Scalar8RouteSanity(["fs32-8-route-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-route-sanity.lbdx")]);
        int scalar8Scalar16Route = RunScalar8Scalar16RouteSanity(["ss8-16-route-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-route-sanity.lbdx")]);
        int scalar16Scalar16Route = RunScalar16Scalar16RouteSanity(["ss16-16-route-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-route-sanity.lbdx")]);
        int scalar8Scalar8RouteInsert = RunScalar8Scalar8RouteInsertSanity(["ss8-8-route-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-insert-sanity.lbdx")]);
        int scalar16Scalar8RouteInsert = RunScalar16Scalar8RouteInsertSanity(["ss16-8-route-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-route-insert-sanity.lbdx")]);
        int fixed32Scalar8RouteInsert = RunFixed32Scalar8RouteInsertSanity(["fs32-8-route-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-route-insert-sanity.lbdx")]);
        int scalar8Scalar16RouteInsert = RunScalar8Scalar16RouteInsertSanity(["ss8-16-route-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-route-insert-sanity.lbdx")]);
        int scalar16Scalar16RouteInsert = RunScalar16Scalar16RouteInsertSanity(["ss16-16-route-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-route-insert-sanity.lbdx")]);
        int scalar8Scalar8RouteSplit = RunScalar8Scalar8RouteSplitSanity(["ss8-8-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-split-sanity.lbdx")]);
        int scalar16Scalar8RouteSplit = RunScalar16Scalar8RouteSplitSanity(["ss16-8-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-route-split-sanity.lbdx")]);
        int fixed32Scalar8RouteSplit = RunFixed32Scalar8RouteSplitSanity(["fs32-8-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-route-split-sanity.lbdx")]);
        int scalar8Scalar16RouteSplit = RunScalar8Scalar16RouteSplitSanity(["ss8-16-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-route-split-sanity.lbdx")]);
        int scalar16Scalar16RouteSplit = RunScalar16Scalar16RouteSplitSanity(["ss16-16-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-route-split-sanity.lbdx")]);
        int identity16SplitPreservation = RunIdentity16SplitPreservationSanity(["identity16-split-preservation-sanity", "--directory", Path.Combine(dataDirectory, "validate-identity16-split-preservation")]);
        int scalar8Scalar8RouteTransform = RunScalar8Scalar8RouteTransformSanity(["ss8-8-route-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-transform-sanity.lbdx")]);
        int scalar16Scalar8RouteTransform = RunScalar16Scalar8RouteTransformSanity(["ss16-8-route-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-route-transform-sanity.lbdx")]);
        int fixed32Scalar8RouteTransform = RunFixed32Scalar8RouteTransformSanity(["fs32-8-route-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-route-transform-sanity.lbdx")]);
        int scalar8Scalar16RouteTransform = RunScalar8Scalar16RouteTransformSanity(["ss8-16-route-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-route-transform-sanity.lbdx")]);
        int scalar16Scalar16RouteTransform = RunScalar16Scalar16RouteTransformSanity(["ss16-16-route-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-route-transform-sanity.lbdx")]);
        int scalar8Scalar8RoutedInsertDecision = RunScalar8Scalar8RoutedInsertDecisionSanity(["ss8-8-routed-insert-decision-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-routed-insert-decision-sanity.lbdx")]);
        int scalar8Scalar8TwoLevelInsert = RunScalar8Scalar8TwoLevelInsertSanity(["ss8-8-two-level-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-two-level-insert-sanity.lbdx")]);
        int scalar16Scalar8TwoLevelInsert = RunScalar16Scalar8TwoLevelInsertSanity(["ss16-8-two-level-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-two-level-insert-sanity.lbdx")]);
        int fixed32Scalar8TwoLevelInsert = RunFixed32Scalar8TwoLevelInsertSanity(["fs32-8-two-level-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-two-level-insert-sanity.lbdx")]);
        int scalar8Scalar16TwoLevelInsert = RunScalar8Scalar16TwoLevelInsertSanity(["ss8-16-two-level-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-two-level-insert-sanity.lbdx")]);
        int scalar16Scalar16TwoLevelInsert = RunScalar16Scalar16TwoLevelInsertSanity(["ss16-16-two-level-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-two-level-insert-sanity.lbdx")]);
        int scalar8Scalar8RouteWalker = RunScalar8Scalar8RouteWalkerSanity(["ss8-8-route-walker-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-walker-sanity.lbdx")]);
        int scalar16Scalar8RouteWalker = RunScalar16Scalar8RouteWalkerSanity(["ss16-8-route-walker-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-route-walker-sanity.lbdx")]);
        int fixed32Scalar8RouteWalker = RunFixed32Scalar8RouteWalkerSanity(["fs32-8-route-walker-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-route-walker-sanity.lbdx")]);
        int scalar8Scalar16RouteWalker = RunScalar8Scalar16RouteWalkerSanity(["ss8-16-route-walker-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-route-walker-sanity.lbdx")]);
        int scalar16Scalar16RouteWalker = RunScalar16Scalar16RouteWalkerSanity(["ss16-16-route-walker-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-route-walker-sanity.lbdx")]);
        int scalar8Scalar8WalkedInsert = RunScalar8Scalar8WalkedInsertSanity(["ss8-8-walked-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-walked-insert-sanity.lbdx")]);
        int scalar16Scalar8WalkedInsert = RunScalar16Scalar8WalkedInsertSanity(["ss16-8-walked-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-walked-insert-sanity.lbdx")]);
        int fixed32Scalar8WalkedInsert = RunFixed32Scalar8WalkedInsertSanity(["fs32-8-walked-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-walked-insert-sanity.lbdx")]);
        int scalar8Scalar16WalkedInsert = RunScalar8Scalar16WalkedInsertSanity(["ss8-16-walked-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-walked-insert-sanity.lbdx")]);
        int scalar16Scalar16WalkedInsert = RunScalar16Scalar16WalkedInsertSanity(["ss16-16-walked-insert-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-walked-insert-sanity.lbdx")]);
        int scalar8Scalar8WalkedTransform = RunScalar8Scalar8WalkedTransformSanity(["ss8-8-walked-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-walked-transform-sanity.lbdx")]);
        int scalar16Scalar8WalkedTransform = RunScalar16Scalar8WalkedTransformSanity(["ss16-8-walked-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-walked-transform-sanity.lbdx")]);
        int fixed32Scalar8WalkedTransform = RunFixed32Scalar8WalkedTransformSanity(["fs32-8-walked-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-walked-transform-sanity.lbdx")]);
        int scalar8Scalar16WalkedTransform = RunScalar8Scalar16WalkedTransformSanity(["ss8-16-walked-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-walked-transform-sanity.lbdx")]);
        int scalar16Scalar16WalkedTransform = RunScalar16Scalar16WalkedTransformSanity(["ss16-16-walked-transform-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-walked-transform-sanity.lbdx")]);
        int scalar8Scalar8ParentRouteSplit = RunScalar8Scalar8ParentRouteSplitSanity(["ss8-8-parent-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-parent-route-split-sanity.lbdx")]);
        int scalar16Scalar8ParentRouteSplit = RunScalar16Scalar8ParentRouteSplitSanity(["ss16-8-parent-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-8-parent-route-split-sanity.lbdx")]);
        int fixed32Scalar8ParentRouteSplit = RunFixed32Scalar8ParentRouteSplitSanity(["fs32-8-parent-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-fs32-8-parent-route-split-sanity.lbdx")]);
        int scalar8Scalar16ParentRouteSplit = RunScalar8Scalar16ParentRouteSplitSanity(["ss8-16-parent-route-split-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-parent-route-split-sanity.lbdx")]);
        int scalar8Scalar16WalkedDecision = RunScalar8Scalar16WalkedDecisionSanity(["ss8-16-walked-decision-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-16-walked-decision-sanity.lbdx")]);
        int scalar16Scalar16WalkedDecision = RunScalar16Scalar16WalkedDecisionSanity(["ss16-16-walked-decision-sanity", "--path", Path.Combine(dataDirectory, "validate-ss16-16-walked-decision-sanity.lbdx")]);
        int scalar8Scalar8RouterArena = RunScalar8Scalar8RouterArenaSanity(["ss8-8-router-arena-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-router-arena-sanity.lbdx")]);
        int scalar8Scalar8RouterArenaPolicy = RunScalar8Scalar8RouterArenaPolicySanity(["ss8-8-router-arena-policy-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-router-arena-policy-sanity.lbdx")]);
        int scalar8Scalar8RouterArenaReadCache = RunScalar8Scalar8RouterArenaReadCacheSanity(["ss8-8-router-arena-read-cache-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-router-arena-read-cache-sanity.lbdx")]);
        int scalar8Scalar8RouteBatchPolicy = RunScalar8Scalar8RouteBatchPolicySanity(["ss8-8-route-batch-policy-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-route-batch-policy-sanity.lbdx")]);
        int scalar8Scalar8BatchLookup = RunScalar8Scalar8BatchLookupSanity(["ss8-8-batch-lookup-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-batch-lookup-sanity.lbdx")]);
        int scalar8Scalar8RangeScoop = RunScalar8Scalar8RangeScoopSanity(["ss8-8-range-scoop-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-range-scoop-sanity.lbdx")]);
        int scalar8Scalar8HandleReopen = RunScalar8Scalar8HandleReopenSanity(["ss8-8-handle-reopen-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-handle-reopen-sanity.lbdx")]);
        int scalar8Scalar8Profile = RunScalar8Scalar8ProfileSanity(["ss8-8-profile-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-profile-sanity.lbdx"), "--shelf-size", "24576"]);
        int scalar8Scalar8TypedApi = RunUnsignedScalar8Scalar8IndexApiSanity(["ss8-8-typed-api-sanity", "--path", Path.Combine(dataDirectory, "validate-ss8-8-typed-api-sanity.lbdx")]);
        if (fixed32Scalar8Sanity != 0 || fixed32Scalar8DkFile != 0 || fixed32Scalar8DkMemory != 0 || fixed32Scalar8Route != 0 || fixed32Scalar8RouteInsert != 0 || fixed32Scalar8RouteSplit != 0 || fixed32Scalar8RouteTransform != 0 || fixed32Scalar8TwoLevelInsert != 0 || fixed32Scalar8RouteWalker != 0 || fixed32Scalar8WalkedInsert != 0 || fixed32Scalar8WalkedTransform != 0 || fixed32Scalar8ParentRouteSplit != 0)
        {
            Console.Error.WriteLine("validate failed during FS32-8 sanity checks.");
            return 1;
        }

        if (sanityFile != 0 || sanityMemory != 0 || sanityFormat != 0 || routeSanity != 0 || routeShapes != 0 || routeStress != 0 || scalar8Scalar8Sanity != 0 || scalar16Scalar8Sanity != 0 || scalar8Scalar16Sanity != 0 || scalar16Scalar16Sanity != 0 || scalar8Scalar8DkFile != 0 || scalar8Scalar8DkMemory != 0 || scalar16Scalar8DkFile != 0 || scalar16Scalar8DkMemory != 0 || scalar8Scalar16DkFile != 0 || scalar8Scalar16DkMemory != 0 || scalar16Scalar16DkFile != 0 || scalar16Scalar16DkMemory != 0 || scalar8Scalar8Route != 0 || scalar16Scalar8Route != 0 || scalar8Scalar16Route != 0 || scalar16Scalar16Route != 0 || scalar8Scalar8RouteInsert != 0 || scalar16Scalar8RouteInsert != 0 || scalar8Scalar16RouteInsert != 0 || scalar16Scalar16RouteInsert != 0 || scalar8Scalar8RouteSplit != 0 || scalar16Scalar8RouteSplit != 0 || scalar8Scalar16RouteSplit != 0 || scalar16Scalar16RouteSplit != 0 || identity16SplitPreservation != 0 || scalar8Scalar8RouteTransform != 0 || scalar16Scalar8RouteTransform != 0 || scalar8Scalar16RouteTransform != 0 || scalar16Scalar16RouteTransform != 0 || scalar8Scalar8RoutedInsertDecision != 0 || scalar8Scalar8TwoLevelInsert != 0 || scalar16Scalar8TwoLevelInsert != 0 || scalar8Scalar16TwoLevelInsert != 0 || scalar16Scalar16TwoLevelInsert != 0 || scalar8Scalar8RouteWalker != 0 || scalar16Scalar8RouteWalker != 0 || scalar8Scalar16RouteWalker != 0 || scalar16Scalar16RouteWalker != 0 || scalar8Scalar8WalkedInsert != 0 || scalar16Scalar8WalkedInsert != 0 || scalar8Scalar16WalkedInsert != 0 || scalar16Scalar16WalkedInsert != 0 || scalar8Scalar8WalkedTransform != 0 || scalar16Scalar8WalkedTransform != 0 || scalar8Scalar16WalkedTransform != 0 || scalar16Scalar16WalkedTransform != 0 || scalar8Scalar8ParentRouteSplit != 0 || scalar16Scalar8ParentRouteSplit != 0 || scalar8Scalar16ParentRouteSplit != 0 || scalar8Scalar16WalkedDecision != 0 || scalar16Scalar16WalkedDecision != 0 || scalar8Scalar8RouterArena != 0 || scalar8Scalar8RouterArenaPolicy != 0 || scalar8Scalar8RouterArenaReadCache != 0 || scalar8Scalar8RouteBatchPolicy != 0 || scalar8Scalar8BatchLookup != 0 || scalar8Scalar8RangeScoop != 0 || scalar8Scalar8HandleReopen != 0 || scalar8Scalar8Profile != 0 || scalar8Scalar8TypedApi != 0)
        {
            Console.Error.WriteLine("validate failed during sanity checks.");
            return 1;
        }

        PerfRawResult[] fileReserve = RunPerfMeasureSet(
            DataKernelBackingKind.File,
            Path.Combine(dataDirectory, "validate-file-reserve.lbdx"),
            tierOptions.TotalBytes,
            tierOptions.BlockSize,
            tierOptions.AppendBufferSize,
            AppendMode.Reserve,
            tierOptions.PerfRuns);

        PerfRawResult[] memoryReserve = RunPerfMeasureSet(
            DataKernelBackingKind.Memory,
            Path.Combine(dataDirectory, "validate-memory-reserve.lbdx"),
            tierOptions.TotalBytes,
            tierOptions.BlockSize,
            tierOptions.AppendBufferSize,
            AppendMode.Reserve,
            tierOptions.PerfRuns);

        AddPerfMetrics(metrics, "file reserve", fileReserve, DataKernelBackingKind.File, tierOptions.TotalBytes, FileReserveStageBaselineMiBs, FileReserveCommitBaselineMiBs, FileReserveReadBaselineMiBs);
        AddPerfMetrics(metrics, "memory reserve", memoryReserve, DataKernelBackingKind.Memory, tierOptions.TotalBytes, MemoryReserveStageBaselineMiBs, null, MemoryReserveReadBaselineMiBs);

        FormatStressResult formatStress = RunFormatStressMeasured(Path.Combine(dataDirectory, "FormatStress"), tierOptions.StressIterations);
        metrics.Add(CreateHigherIsBetterMetric("format stress iterations/sec", formatStress.IterationsPerSecond, FormatStressBaselineIterationsPerSecond, "iterations/sec"));

        string reportPath = WriteValidationReport(artifactDirectory, tierOptions, metrics, formatStress);
        PrintValidationSummary(metrics, reportPath);

        return HasFailingMetric(metrics, failThroughputMetrics: tierOptions.Name != "fast") ? 1 : 0;
    }


    private static void PrintRouteStress(string mode, RouteStressResult result)
    {
        Console.WriteLine($"| {mode} | {result.Commits} | {result.Writes} | {result.Bytes} | {result.SetLength} | {result.Elapsed.TotalMilliseconds:N3} | {result.FinalTarget} |");
    }


    /// <summary>
    /// Measures raw `DataKernel` writing of contiguous fixed-width logical payload rows repeatedly and returns the median throughput sample.<br/>
    /// This gives every current shelf shape a raw append floor at its logical `(key, identity)` byte width without involving router or shelf code.<br/>
    /// </summary>
    /// <param name="path">The base backing file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of payload rows per batch.</param>
    /// <param name="payloadBytes">The fixed logical payload bytes per row.</param>
    /// <param name="order">The deterministic item order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median raw payload-stream write row.</returns>
    private static Scalar8Scalar8WriteParityResult MeasureRepeatedDataKernelPayloadStreamWriteParity(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int payloadBytes,
        int[] order,
        DataKernelOptions options,
        int repeatCount)
    {
        Scalar8Scalar8WriteParityResult[] samples = new Scalar8Scalar8WriteParityResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureDataKernelPayloadStreamWriteParity(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, payloadBytes, order, options);
        }

        return SelectMedianWriteParitySample(samples);
    }


    /// <summary>
    /// Measures public-wrapper routed `SS8-8` bulk insertion with one `Scalar8Scalar8Batch.Commit` call per batch.<br/>
    /// Warmup uses the same file so the measured loop starts after initial facade/session setup and first-route creation noise has been exercised.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <returns>The measured routed bulk-write row.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedScalar8Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Scalar8Scalar8Profile profile)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar8Scalar8RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures harness-level `SS16-16` routed bulk writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh file and uses session durability batches so internal walked insert commit requests fold into one durable publication per logical batch.<br/>
    /// </summary>
    /// <param name="path">The base LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <param name="profile">The `SS16-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The median routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedScalar16Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Scalar16Scalar16Profile profile,
        bool includeAttribution)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar16Scalar16RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile, includeAttribution) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures harness-level `SS16-8` routed bulk writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh file and uses session durability batches so internal walked insert commit requests fold into one durable publication per logical batch.<br/>
    /// </summary>
    /// <param name="path">The base LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <param name="profile">The `SS16-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The median routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedScalar16Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Scalar16Scalar8Profile profile,
        bool includeAttribution)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar16Scalar8RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile, includeAttribution) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures harness-level `SS8-16` routed bulk writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh file and uses session durability batches so internal walked insert commit requests fold into one durable publication per logical batch.<br/>
    /// </summary>
    /// <param name="path">The base LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <param name="profile">The `SS8-16` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The median routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedScalar8Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Scalar8Scalar16Profile profile,
        bool includeAttribution)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar8Scalar16RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile, includeAttribution) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures harness-level `FS32-8` routed bulk writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh file and uses the same session durability-batch cadence as the other physical shelf parity rows.<br/>
    /// </summary>
    /// <param name="path">The base LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The median routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedFixed32Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Fixed32Scalar8Profile profile,
        bool includeAttribution)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureFixed32Scalar8RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile, includeAttribution) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one harness-level `FS32-8` routed bulk-write sample.<br/>
    /// The root router is created before timing, warmup rows are inserted into the same fresh file, and measured rows use deterministic 32-byte keys with the same leading routed prefix cadence as scalar rows.<br/>
    /// </summary>
    /// <param name="path">The LibraDex file path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured logical batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup logical batch count.</param>
    /// <param name="itemsPerBatch">The item count inserted per logical batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The DataKernel options.</param>
    /// <param name="profile">The `FS32-8` shelf profile.</param>
    /// <param name="includeAttribution">True to collect Stopwatch-based attribution timings in addition to cheap structural counts.</param>
    /// <returns>The measured routed bulk-write result.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureFixed32Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Fixed32Scalar8Profile profile,
        bool includeAttribution)
    {
        File.Delete(path);
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(328), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f328bulk", 0));
        _ = RunFixed32Scalar8RoutedBulkWriteLoop(session, root.Offset, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0, profile, includeAttribution);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunFixed32Scalar8RoutedBulkWriteLoop(session, root.Offset, batches, itemsPerBatch, prefixCount, order, warmupBatches, profile, includeAttribution);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }


    /// <summary>
    /// Measures routed `SS8-8` write attribution repeatedly and returns the median throughput sample.<br/>
    /// Repeated samples keep the diagnostic comparable to the routed bulk-write report while preserving the same generated key set.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of encoded tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <param name="repeatCount">The number of measured samples to collect.</param>
    /// <returns>The median write-attribution row.</returns>
    private static Scalar8Scalar8WriteAttributionResult MeasureRepeatedScalar8Scalar8WriteAttribution(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount)
    {
        Scalar8Scalar8WriteAttributionResult[] samples = new Scalar8Scalar8WriteAttributionResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar8Scalar8WriteAttribution(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianScalar8Scalar8WriteAttributionSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures typed unsigned public-wrapper routed `SS8-8` bulk insertion repeatedly and returns the median throughput sample.<br/>
    /// Repeated samples reduce host timing noise before comparing the typed facade against the encoded public wrapper.<br/>
    /// </summary>
    /// <param name="path">The backing file path for the row.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured batch count.</param>
    /// <param name="warmupBatches">The unmeasured warmup batch count.</param>
    /// <param name="itemsPerBatch">The number of tuples inserted per committed batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by each generated batch.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The `DataKernel` options.</param>
    /// <param name="repeatCount">The number of measured samples to collect.</param>
    /// <returns>The median typed routed bulk-write row.</returns>
    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedUnsignedScalar8Scalar8RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureUnsignedScalar8Scalar8RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures a public encoded range read repeatedly and returns the median latency sample.<br/>
    /// Each sample resets read telemetry, validates identities, and runs the same iteration count so timing drift is less likely to be overread from one run.<br/>
    /// </summary>
    /// <param name="index">The reopened public encoded index.</param>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations per sample.</param>
    /// <param name="enableCoalescing">Whether to enable public range-read coalescing.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="repeatCount">The number of measured samples to collect.</param>
    /// <returns>The median public bulk-read row.</returns>
    private static Scalar8Scalar8PublicBulkReadResult MeasureRepeatedScalar8Scalar8PublicBulkReadRange(
        Scalar8Scalar8Index index,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        bool enableCoalescing,
        Span<ulong> identities,
        int repeatCount)
    {
        Scalar8Scalar8PublicBulkReadResult[] samples = new Scalar8Scalar8PublicBulkReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar8Scalar8PublicBulkReadRange(index, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, enableCoalescing, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianPublicBulkReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures a typed unsigned public range read repeatedly and returns the median latency sample.<br/>
    /// Each sample validates identities and resets telemetry so typed facade overhead can be compared against encoded public reads.<br/>
    /// </summary>
    /// <param name="index">The reopened typed public unsigned index.</param>
    /// <param name="name">The result row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations per sample.</param>
    /// <param name="enableCoalescing">Whether to enable public range-read coalescing.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="repeatCount">The number of measured samples to collect.</param>
    /// <returns>The median typed public bulk-read row.</returns>
    private static Scalar8Scalar8PublicBulkReadResult MeasureRepeatedUnsignedScalar8Scalar8PublicBulkReadRange(
        UnsignedScalar8Scalar8Index index,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        bool enableCoalescing,
        Span<ulong> identities,
        int repeatCount)
    {
        Scalar8Scalar8PublicBulkReadResult[] samples = new Scalar8Scalar8PublicBulkReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureUnsignedScalar8Scalar8PublicBulkReadRange(index, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, enableCoalescing, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianPublicBulkReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures public bulk-build file growth against unique live routers and shelves reachable from the encoded index handle.<br/>
    /// Structural counts are diagnostic; the headline values are final file bytes, live bytes, overhead bytes, bytes/item, and the bulk-write throughput already measured during setup.<br/>
    /// </summary>
    /// <param name="path">The file path of the built public index.</param>
    /// <param name="handle">The reopened public index handle.</param>
    /// <param name="batches">The number of setup batches.</param>
    /// <param name="itemsPerBatch">The number of inserted items per setup batch.</param>
    /// <param name="writeTelemetry">The bulk-write telemetry collected during setup.</param>
    /// <param name="session">The reopened file session used for route classification.</param>
    /// <returns>The public growth result.</returns>
    private static Scalar8Scalar8PublicGrowthResult MeasureRepeatedScalar8Scalar8PublicGrowth(
        string path,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        Scalar8Scalar8BulkWriteLocalityMode localityMode,
        int[] sortedOrder,
        DataKernelOptions options,
        int repeatCount,
        Scalar8Scalar8Profile profile)
    {
        Scalar8Scalar8PublicGrowthResult[] samples = new Scalar8Scalar8PublicGrowthResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            string samplePath = CreateRepeatPath(path, i);
            File.Delete(samplePath);
            RoutedBulkWriteLoopTelemetry writeTelemetry;
            using (Scalar8Scalar8Index created = Indexes.SS88.Create(
                samplePath,
                DataKernelBackingKind.File,
                name: "public-growth",
                options: options,
                developerMetadata: CreateDesignPerfMetadata(96),
                telemetryOptions: DataKernelTelemetryOptions.EnabledOptions,
                shelfExtentSize: profile.ShelfExtentSize))
            {
                writeTelemetry = RunScalar8Scalar8RoutedBulkWriteLoop(created, batches, itemsPerBatch, prefixCount, sortedOrder, batchOffset: 0, localityMode);
            }

            using LibraDexFileSession session = LibraDexFileSession.Open(samplePath, options, DataKernelTelemetryOptions.EnabledOptions);
            Scalar8Scalar8IndexHandle handle = session.GetScalar8Scalar8IndexHandle(0);
            samples[i] = MeasureScalar8Scalar8PublicGrowth(samplePath, handle, batches, itemsPerBatch, writeTelemetry, session) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianPublicGrowthSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures SQLite transactional scalar writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh database so B-tree growth, page splits, journal/WAL behavior, and file footprint are included in the same way as a fresh LibraDex bulk-build row.<br/>
    /// </summary>
    /// <param name="path">The base SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteScalar8Scalar8Write(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar8Scalar8Write(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite transactional scalar write sample.<br/>
    /// The loop uses one explicit SQL transaction per generated batch, matching the public `SS8-8` batch-commit cadence while leaving SQLite to manage B-tree pages and journal/WAL publication.<br/>
    /// </summary>
    /// <param name="path">The SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <returns>The measured SQLite write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureSqliteScalar8Scalar8Write(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteScalar8Scalar8Schema(connection);
        long warmupChecksum = RunSqliteScalar8Scalar8WriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteScalar8Scalar8WriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }


    /// <summary>
    /// Measures SQLite transactional `SS16-16` BLOB writes repeatedly and returns the median throughput sample.<br/>
    /// Each sample creates a fresh database with fixed 16-byte key and identity blobs so SQLite's B-tree stores the same public-width payload shape a GUID-like workload would use.<br/>
    /// </summary>
    /// <param name="path">The base SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite BLOB write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteScalar16Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar16Scalar16BlobWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite transactional `SS16-16` BLOB write sample.<br/>
    /// The loop uses one explicit SQL transaction per generated batch, matching the current SQLite comparison cadence while widening key and identity payloads to fixed 16-byte blobs.<br/>
    /// </summary>
    /// <param name="path">The SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <returns>The measured SQLite BLOB write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureSqliteScalar16Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteScalar16Scalar16BlobSchema(connection);
        long warmupChecksum = RunSqliteScalar16Scalar16BlobWriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteScalar16Scalar16BlobWriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }


    /// <summary>
    /// Measures SQLite transactional `SS16-8` BLOB/integer writes repeatedly and returns the median throughput sample.<br/>
    /// The SQLite schema stores 16-byte keys as fixed BLOBs and 8-byte identities as order-preserving integers, matching the likely mixed-width usage shape.<br/>
    /// </summary>
    /// <param name="path">The base SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite mixed-width write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteScalar16Scalar8BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar16Scalar8BlobWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite transactional `SS16-8` BLOB/integer write sample.<br/>
    /// The measured loop uses one transaction per generated batch so the commit cadence remains aligned with routed LibraDex rows.<br/>
    /// </summary>
    /// <param name="path">The SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <returns>The measured SQLite mixed-width write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureSqliteScalar16Scalar8BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteScalar16Scalar8BlobSchema(connection);
        long warmupChecksum = RunSqliteScalar16Scalar8BlobWriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteScalar16Scalar8BlobWriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }


    /// <summary>
    /// Measures SQLite transactional `FS32-8` BLOB/integer writes repeatedly and returns the median throughput sample.<br/>
    /// The SQLite schema stores 32-byte keys as fixed BLOBs and 8-byte identities as order-preserving integers, matching the fixed-key identity-index comparison shape.<br/>
    /// </summary>
    /// <param name="path">The base SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite fixed-key write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteFixed32Scalar8BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteFixed32Scalar8BlobWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite transactional `FS32-8` BLOB/integer write sample.<br/>
    /// The measured loop uses one transaction per generated batch so commit cadence remains aligned with routed LibraDex rows.<br/>
    /// </summary>
    /// <param name="path">The SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <returns>The measured SQLite fixed-key write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureSqliteFixed32Scalar8BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteFixed32Scalar8BlobSchema(connection);
        long warmupChecksum = RunSqliteFixed32Scalar8BlobWriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteFixed32Scalar8BlobWriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }


    /// <summary>
    /// Measures SQLite transactional `SS8-16` integer/BLOB writes repeatedly and returns the median throughput sample.<br/>
    /// The SQLite schema stores 8-byte keys as order-preserving integers and 16-byte identities as fixed BLOBs, matching the inverse mixed-width usage shape.<br/>
    /// </summary>
    /// <param name="path">The base SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite mixed-width write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteScalar8Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar8Scalar16BlobWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite transactional `SS8-16` integer/BLOB write sample.<br/>
    /// The measured loop uses one transaction per generated batch so the commit cadence remains aligned with routed LibraDex rows.<br/>
    /// </summary>
    /// <param name="path">The SQLite database path.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The measured transaction count.</param>
    /// <param name="warmupBatches">The unmeasured warmup transaction count.</param>
    /// <param name="itemsPerBatch">The item count inserted in each transaction.</param>
    /// <param name="prefixCount">The number of root-prefix buckets covered by generated keys.</param>
    /// <param name="order">The deterministic insertion order vector.</param>
    /// <param name="options">The SQLite durability and journaling options.</param>
    /// <returns>The measured SQLite mixed-width write result.</returns>
    private static SqliteScalar8Scalar8WriteResult MeasureSqliteScalar8Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteScalar8Scalar16BlobSchema(connection);
        long warmupChecksum = RunSqliteScalar8Scalar16BlobWriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteScalar8Scalar16BlobWriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }


    /// <summary>
    /// Measures a SQLite range read repeatedly and returns the median latency sample.<br/>
    /// Each sample validates the full returned identity set against the same expected sorted key list used by public `SS8-8` read validation.<br/>
    /// </summary>
    /// <param name="connection">The reopened SQLite connection.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations per sample.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite read result.</returns>
    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteScalar8Scalar8ReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities,
        int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar8Scalar8ReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite ordered range-read sample.<br/>
    /// The SQL query returns identities ordered by `(key, identity)`, matching the persisted tuple ordering used by `SS8-8` shelves.<br/>
    /// </summary>
    /// <param name="connection">The reopened SQLite connection.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations.</param>
    /// <param name="identities">The reusable identity output buffer.</param>
    /// <returns>The measured SQLite read result.</returns>
    private static SqliteScalar8Scalar8ReadResult MeasureSqliteScalar8Scalar8ReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities)
    {
        ulong lowerKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)lowerPrefix << 56);
        ulong upperKey = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The SQLite bulk-read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", EncodeSqliteSortableUnsignedScalar8(lowerKey));
        command.Parameters.AddWithValue("$upper", EncodeSqliteSortableUnsignedScalar8(upperKey));

        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (count >= identities.Length)
                    {
                        throw new InvalidDataException("SQLite comparison read returned more identities than the reusable buffer can hold.");
                    }

                    identities[count++] = DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(0));
                }
            }

            if (count != expectedCount)
            {
                throw new InvalidDataException($"SQLite bulk read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expectedIdentities);
            totalIdentities += count;
        }

        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8ReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Scalar8Scalar8Layout.ItemSize, seconds),
            checksum,
            1);
    }


    private static Fixed32Scalar8ReadParityResult MeasureRepeatedFixed32Scalar8ReadRange(
        LibraDexFileSession session,
        long rootRouterOffset,
        Fixed32Scalar8Profile profile,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities,
        int repeatCount)
    {
        Fixed32Scalar8ReadParityResult[] samples = new Fixed32Scalar8ReadParityResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureFixed32Scalar8ReadRange(session, rootRouterOffset, profile, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianFixed32Scalar8ReadParitySample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    private static Fixed32Scalar8ReadParityResult MeasureFixed32Scalar8ReadRange(
        LibraDexFileSession session,
        long rootRouterOffset,
        Fixed32Scalar8Profile profile,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The FS32-8 read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateFixed32Scalar8PrefixRangeBounds(lowerPrefix, upperPrefix, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        byte[] shelfScratch = new byte[profile.ShelfExtentSize];
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        long checksum = 0;
        long totalIdentities = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = session.ReadFixed32Scalar8IdentityRange(
                rootRouterOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 8,
                identities,
                shelfScratch,
                targetKindCache);
            if (count != expectedCount)
            {
                throw new InvalidDataException($"FS32-8 read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expectedIdentities);
            totalIdentities += count;
        }

        watch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new Fixed32Scalar8ReadParityResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Fixed32Scalar8Layout.IdentitySize, seconds),
            iterations == 0 ? 0 : readTelemetry.ReadCallCount / (double)iterations,
            iterations == 0 ? 0 : readTelemetry.BytesRead / (double)iterations,
            checksum,
            1);
    }


    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteFixed32Scalar8BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities,
        int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteFixed32Scalar8BlobReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    private static SqliteScalar8Scalar8ReadResult MeasureSqliteFixed32Scalar8BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The SQLite FS32-8 read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateFixed32Scalar8PrefixRangeBounds(lowerPrefix, upperPrefix, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", CreateSqliteFixed32Scalar8Blob(lower0, lower1, lower2, lower3));
        command.Parameters.AddWithValue("$upper", CreateSqliteFixed32Scalar8Blob(upper0, upper1, upper2, upper3));

        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (count >= identities.Length)
                    {
                        throw new InvalidDataException("SQLite FS32-8 read returned more identities than the reusable buffer can hold.");
                    }

                    identities[count++] = DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(0));
                }
            }

            if (count != expectedCount)
            {
                throw new InvalidDataException($"SQLite FS32-8 read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expectedIdentities);
            totalIdentities += count;
        }

        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8ReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Fixed32Scalar8Layout.IdentitySize, seconds),
            checksum,
            1);
    }


    /// <summary>
    /// Measures a SQLite `SS16-16` BLOB range read repeatedly and returns the median latency sample.<br/>
    /// Each sample validates returned 16-byte identities against the same deterministic prefix range used by the LibraDex public bulk-read workload shape.<br/>
    /// </summary>
    /// <param name="connection">The reopened SQLite connection.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations per sample.</param>
    /// <param name="identityHighs">The reusable identity high-half output buffer.</param>
    /// <param name="repeatCount">The number of samples to collect.</param>
    /// <returns>The median SQLite BLOB read result.</returns>
    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteScalar16Scalar16BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identityHighs,
        int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar16Scalar16BlobReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identityHighs) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }


    /// <summary>
    /// Measures one SQLite ordered `SS16-16` BLOB range-read sample.<br/>
    /// The SQL query returns identity blobs ordered by `(key, identity)`, matching the fixed tuple ordering used by `SS16-16` shelves.<br/>
    /// </summary>
    /// <param name="connection">The reopened SQLite connection.</param>
    /// <param name="name">The report row name.</param>
    /// <param name="batches">The setup batch count.</param>
    /// <param name="itemsPerBatch">The setup items per batch.</param>
    /// <param name="prefixCount">The setup root-prefix count.</param>
    /// <param name="lowerPrefix">The inclusive lower root prefix.</param>
    /// <param name="upperPrefix">The inclusive upper root prefix.</param>
    /// <param name="iterations">The measured read iterations.</param>
    /// <param name="identityHighs">The reusable identity high-half output buffer.</param>
    /// <returns>The measured SQLite BLOB read result.</returns>
    private static SqliteScalar8Scalar8ReadResult MeasureSqliteScalar16Scalar16BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identityHighs)
    {
        ulong lowerKeyHigh = Scalar8Scalar8Layout.EncodeUnsignedScalar8((ulong)(byte)lowerPrefix << 56);
        ulong upperKeyHigh = Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)upperPrefix << 56) | 0x00FF_FFFF_FFFF_FFFFUL);
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identityHighs.Length < expectedCount)
        {
            throw new ArgumentException("The SQLite SS16-16 BLOB bulk-read identity buffer is too small for the requested range.", nameof(identityHighs));
        }

        ulong[] expectedIdentityHighs = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", CreateSqliteScalar16Scalar16Blob(lowerKeyHigh, 0));
        command.Parameters.AddWithValue("$upper", CreateSqliteScalar16Scalar16Blob(upperKeyHigh, ulong.MaxValue));

        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (count >= identityHighs.Length)
                    {
                        throw new InvalidDataException("SQLite SS16-16 BLOB comparison read returned more identities than the reusable buffer can hold.");
                    }

                    byte[] identity = reader.GetFieldValue<byte[]>(0);
                    if (identity.Length != 16)
                    {
                        throw new InvalidDataException($"SQLite SS16-16 BLOB comparison read returned an identity blob with {identity.Length} bytes.");
                    }

                    ulong identityHigh = ReadSqliteScalar16Scalar16BigEndianUInt64(identity, 0);
                    ulong identityLow = ReadSqliteScalar16Scalar16BigEndianUInt64(identity, 8);
                    ulong expectedLow = CreateSqliteScalar16Scalar16LowHalf(identityHigh);
                    if (identityLow != expectedLow)
                    {
                        throw new InvalidDataException($"SQLite SS16-16 BLOB comparison read returned identity low half {identityLow}; expected {expectedLow}.");
                    }

                    identityHighs[count++] = identityHigh;
                }
            }

            if (count != expectedCount)
            {
                throw new InvalidDataException($"SQLite SS16-16 BLOB bulk read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identityHighs, count, expectedIdentityHighs);
            totalIdentities += count;
        }

        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8ReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Scalar16Scalar16Layout.ItemSize, seconds),
            checksum,
            1);
    }


    /// <summary>
    /// Converts a byte count and elapsed seconds into MiB/s.<br/>
    /// The caller controls whether the elapsed period represents raw I/O or a structured operation that includes CPU work.<br/>
    /// </summary>
    /// <param name="bytes">The measured bytes moved.</param>
    /// <param name="seconds">The measured elapsed seconds.</param>
    /// <returns>The effective throughput in MiB/s.</returns>
    private static double CalculateMiBs(double bytes, double seconds)
    {
        return bytes / 1024d / 1024d / Math.Max(seconds, 0.000000001d);
    }


    /// <summary>
    /// Converts an effective throughput value into percent of the selected baseline.<br/>
    /// Values above one hundred are possible when the operating-system cache or smaller working sets outperform the raw baseline run shape.<br/>
    /// </summary>
    /// <param name="actualMiBs">The measured effective MiB/s value.</param>
    /// <param name="baselineMiBs">The raw baseline MiB/s value.</param>
    /// <returns>The measured value as a percentage of the baseline.</returns>
    private static double CalculateBaselinePercent(double actualMiBs, double baselineMiBs)
    {
        return baselineMiBs <= 0 ? 0 : actualMiBs * 100d / baselineMiBs;
    }


    private static string WriteRouteStressReport(string artifactDirectory, int depth, RouteStressResult combined, RouteStressResult separated)
    {
        Directory.CreateDirectory(artifactDirectory);
        string reportPath = Path.Combine(artifactDirectory, "route-stress-current.md");
        ArchiveCurrentReport(reportPath, "route-stress");

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        StringBuilder builder = new();
        builder.AppendLine($"# LibraDex Route Stress - {timestamp} UTC");
        builder.AppendLine();
        builder.AppendLine("## Scope");
        builder.AppendLine();
        builder.AppendLine($"- Depth: `{depth}` one-byte routers.");
        builder.AppendLine("- Combined creation stages the router chain and index-directory update into one commit.");
        builder.AppendLine("- Separated creation commits root creation, each child creation, each route link, and the final target link separately.");
        builder.AppendLine("- Invalid compressed route definitions are checked before write-shape measurement.");
        builder.AppendLine();
        builder.AppendLine("## Results");
        builder.AppendLine();
        builder.AppendLine("| Mode | Commits | Writes | Bytes | SetLength | Elapsed ms | Final target |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        AppendRouteStress(builder, "combined", combined);
        AppendRouteStress(builder, "separated", separated);
        builder.AppendLine();
        builder.AppendLine("## Notes");
        builder.AppendLine();
        builder.AppendLine("- Small-file elapsed time is treated as directional only; commit count, write count, bytes, and `SetLength` are the important shape checks.");
        builder.AppendLine("- The combined path should remain close to one append write plus one fixed directory rewrite when the append buffer can hold the staged routers.");

        File.WriteAllText(reportPath, builder.ToString());
        return reportPath;
    }


    private static int RunSanity(string[] args)
    {
        DataKernelBackingKind backing = GetBackingOption(args);
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "raw-sanity.lbdx"));
        if (backing == DataKernelBackingKind.File)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Delete(path);
        }

        DataKernelOptions options = new(
            AppendBufferSize: 64 * 1024,
            ReservedPrefixBytes: DefaultReservedPrefixBytes,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);

        RawDataExtent[] extents = new RawDataExtent[5];
        byte[][] expected = new byte[extents.Length][];

        using (DataKernel kernel = OpenKernel(backing, path, FileMode.Create, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            for (int i = 0; i < expected.Length; i++)
            {
                int length = i switch
                {
                    0 => 1,
                    1 => 17,
                    2 => 4096,
                    3 => 70 * 1024,
                    _ => 131 * 1024
                };

                expected[i] = CreatePattern(length, i + 11);
                if ((i & 1) == 0)
                {
                    extents[i] = kernel.Append(expected[i]);
                }
                else
                {
                    RawDataReservation reservation = kernel.Reserve(length);
                    FillPattern(reservation.Span, i + 11);
                    extents[i] = reservation.Extent;
                }
            }

            DataKernelCommitTelemetry commit = kernel.Commit();
            PrintCommit("sanity commit", commit);

            for (int i = 0; i < extents.Length; i++)
            {
                byte[] actual = new byte[extents[i].Length];
                kernel.Read(extents[i].Offset, actual);

                if (!actual.AsSpan().SequenceEqual(expected[i]))
                {
                    Console.Error.WriteLine($"sanity-raw failed: extent {i} did not round-trip.");
                    return 1;
                }
            }

            PrintRead("sanity read", kernel.GetAndResetReadTelemetry());

            RawDataReservation overwrite = kernel.ReserveAt(extents[2].Offset, extents[2].Length);
            FillPattern(overwrite.Span, 99);
            DataKernelCommitTelemetry overwriteCommit = kernel.Commit();
            PrintCommit("sanity overwrite commit", overwriteCommit);

            byte[] overwritten = new byte[extents[2].Length];
            byte[] overwriteExpected = CreatePattern(extents[2].Length, 99);
            kernel.Read(extents[2].Offset, overwritten);
            if (!overwritten.AsSpan().SequenceEqual(overwriteExpected))
            {
                Console.Error.WriteLine("sanity-raw failed: fixed-offset overwrite did not round-trip.");
                return 1;
            }

            PrintRead("sanity overwrite read", kernel.GetAndResetReadTelemetry());
        }

        Console.WriteLine(backing == DataKernelBackingKind.File
            ? $"sanity-raw ok backing=file path={path}"
            : "sanity-raw ok backing=memory");
        return 0;
    }


    private static int RunPerf(string[] args)
    {
        DataKernelBackingKind backing = GetBackingOption(args);
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "raw-perf.lbdx"));
        int blockSize = GetIntOption(args, "--block-size", 64 * 1024);
        long totalBytes = GetLongOption(args, "--total-bytes", 64L * 1024 * 1024);
        int appendBufferSize = GetIntOption(args, "--append-buffer-size", DefaultAppendBufferSize);
        AppendMode appendMode = GetAppendModeOption(args);
        bool telemetryEnabled = GetBoolOption(args, "--telemetry", true);
        bool flush = GetBoolOption(args, "--flush", false);
        int runs = GetIntOption(args, "--runs", 1);

        if (blockSize <= 0 || totalBytes <= 0 || appendBufferSize <= 0 || runs <= 0)
        {
            Console.Error.WriteLine("perf-raw requires positive --block-size, --total-bytes, --append-buffer-size, and --runs values.");
            return 1;
        }

        if (runs > 1)
        {
            RunPerfSet(backing, path, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled, flush, runs);
            return 0;
        }

        RunPerfOnce(backing, path, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled, flush, print: true);
        return 0;
    }


    private static PerfRawResult RunPerfOnce(
        DataKernelBackingKind backing,
        string path,
        long totalBytes,
        int blockSize,
        int appendBufferSize,
        AppendMode appendMode,
        bool telemetryEnabled,
        bool flush,
        bool print)
    {
        if (backing == DataKernelBackingKind.File)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Delete(path);
        }

        DataKernelOptions options = new(
            AppendBufferSize: appendBufferSize,
            ReservedPrefixBytes: DefaultReservedPrefixBytes,
            FlushToDiskOnCommit: flush,
            MaxCommitGapCoalesceBytes: 512);

        DataKernelTelemetryOptions telemetryOptions = new(telemetryEnabled);
        int blockCount = checked((int)((totalBytes + blockSize - 1) / blockSize));
        RawDataExtent[] extents = new RawDataExtent[blockCount];
        byte[] block = appendMode == AppendMode.Append
            ? CreatePattern(blockSize, 23)
            : [];

        Stopwatch stageWatch = Stopwatch.StartNew();
        DataKernelCommitTelemetry commit;
        TimeSpan commitElapsed;
        DataKernelReadTelemetry readTelemetry;
        TimeSpan readElapsed;

        using (DataKernel kernel = OpenKernel(backing, path, FileMode.Create, options, telemetryOptions))
        {
            for (int i = 0; i < blockCount; i++)
            {
                int length = (int)Math.Min(blockSize, totalBytes - ((long)i * blockSize));
                if (appendMode == AppendMode.Append)
                {
                    extents[i] = kernel.Append(block.AsSpan(0, length));
                }
                else
                {
                    RawDataReservation reservation = kernel.Reserve(length);
                    FillPattern(reservation.Span, i + 23);
                    extents[i] = reservation.Extent;
                }
            }

            stageWatch.Stop();
            Stopwatch commitWatch = Stopwatch.StartNew();
            commit = kernel.Commit();
            commitWatch.Stop();
            commitElapsed = commitWatch.Elapsed;

            byte[] readBuffer = new byte[blockSize];
            Stopwatch readWatch = Stopwatch.StartNew();
            for (int i = 0; i < extents.Length; i++)
            {
                kernel.Read(extents[i].Offset, readBuffer.AsSpan(0, extents[i].Length));
            }

            readWatch.Stop();
            readElapsed = readWatch.Elapsed;
            readTelemetry = kernel.GetAndResetReadTelemetry();
        }

        PerfRawResult result = new(stageWatch.Elapsed, commitElapsed, readElapsed, commit, readTelemetry, totalBytes);

        if (print)
        {
            PrintPerf(backing, path, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled, flush, result);
        }

        return result;
    }


    private static void RunPerfSet(
        DataKernelBackingKind backing,
        string path,
        long totalBytes,
        int blockSize,
        int appendBufferSize,
        AppendMode appendMode,
        bool telemetryEnabled,
        bool flush,
        int runs)
    {
        PerfRawResult[] results = new PerfRawResult[runs];
        for (int i = 0; i < runs; i++)
        {
            string runPath = backing == DataKernelBackingKind.File
                ? Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-run-{i + 1}{Path.GetExtension(path)}")
                : path;

            results[i] = RunPerfOnce(backing, runPath, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled, flush, print: false);

            if (backing == DataKernelBackingKind.File)
            {
                File.Delete(runPath);
            }
        }

        PrintPerfSet(backing, path, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled, flush, results);
    }


    /// <summary>
    /// Runs a measured raw performance set and returns individual run results without printing the aggregate table.<br/>
    /// This keeps `validate` able to write its own comparison report while reusing the same raw perf path.<br/>
    /// </summary>
    /// <param name="backing">The raw backing kind to measure.</param>
    /// <param name="path">The file path used when measuring file backing.</param>
    /// <param name="totalBytes">The total bytes to stage, commit, and read per run.</param>
    /// <param name="blockSize">The caller block size.</param>
    /// <param name="appendBufferSize">The DataKernel append buffer size.</param>
    /// <param name="appendMode">Whether to use copy append or direct reserve.</param>
    /// <param name="runs">The number of measured runs.</param>
    /// <returns>The measured raw performance results.</returns>
    private static PerfRawResult[] RunPerfMeasureSet(
        DataKernelBackingKind backing,
        string path,
        long totalBytes,
        int blockSize,
        int appendBufferSize,
        AppendMode appendMode,
        int runs)
    {
        PerfRawResult[] results = new PerfRawResult[runs];
        for (int i = 0; i < runs; i++)
        {
            string runPath = backing == DataKernelBackingKind.File
                ? Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-run-{i + 1}{Path.GetExtension(path)}")
                : path;

            results[i] = RunPerfOnce(backing, runPath, totalBytes, blockSize, appendBufferSize, appendMode, telemetryEnabled: true, flush: false, print: false);

            if (backing == DataKernelBackingKind.File)
            {
                File.Delete(runPath);
            }
        }

        return results;
    }


    /// <summary>
    /// Creates a throughput-style validation metric where lower current value means negative drift.<br/>
    /// Warning and fail thresholds are expressed as percentage below the recorded baseline.<br/>
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="actual">The measured value.</param>
    /// <param name="baseline">The comparison baseline.</param>
    /// <param name="unit">The metric unit.</param>
    /// <returns>A validation metric with computed status.</returns>
    private static ValidationMetric CreateHigherIsBetterMetric(string name, double actual, double baseline, string unit)
    {
        double driftPercent = baseline <= 0 ? 0 : ((baseline - actual) / baseline) * 100;
        ValidationStatus status = driftPercent >= ValidationFailDriftPercent
            ? ValidationStatus.Fail
            : driftPercent >= ValidationWarnDriftPercent
                ? ValidationStatus.Warn
                : ValidationStatus.Pass;

        return new ValidationMetric(name, actual, baseline, driftPercent, unit, status, IsShapeMetric: false);
    }


    /// <summary>
    /// Creates an exact-shape validation metric for syscall and structural counters.<br/>
    /// These counters fail on any mismatch because syscall shape is a design invariant.<br/>
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="actual">The measured value.</param>
    /// <param name="expected">The expected value.</param>
    /// <param name="unit">The metric unit.</param>
    /// <returns>A validation metric with pass or fail status.</returns>
    private static ValidationMetric CreateExactMetric(string name, double actual, double expected, string unit)
    {
        ValidationStatus status = actual.Equals(expected) ? ValidationStatus.Pass : ValidationStatus.Fail;
        double driftPercent = expected == 0
            ? actual == 0 ? 0 : 100
            : ((actual - expected) / expected) * 100;

        return new ValidationMetric(name, actual, expected, driftPercent, unit, status, IsShapeMetric: true);
    }


    /// <summary>
    /// Runs the format lifecycle stress path and returns measured throughput without using console parsing.<br/>
    /// The path mirrors `stress-format` so validation detects lifecycle drift in the current metadata update shape.<br/>
    /// </summary>
    /// <param name="directory">The directory used for temporary stress files.</param>
    /// <param name="iterations">The number of stress iterations to run.</param>
    /// <returns>The measured format stress result.</returns>
    private static FormatStressResult RunFormatStressMeasured(string directory, int iterations)
    {
        Directory.CreateDirectory(directory);

        DataKernelOptions options = new(
            AppendBufferSize: 16 * 1024,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);

        SuperblockDeveloperMetadata developerMetadata = new(
            DevIdentity: "Stress",
            DevCustomText: "format stress",
            DevGuid: Guid.Parse("1f8524e0-40ad-4b0f-a6d5-c2fef9db6f25"),
            DevDate1UtcTicks: 11,
            DevDate2UtcTicks: 22,
            DevNumber: 33);

        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            string path = Path.Combine(directory, $"validate-format-stress-{i}.lbdx");
            File.Delete(path);

            using (LibraDexFileSession initialized = LibraDexFileSession.Initialize(path, options, developerMetadata, DataKernelTelemetryOptions.Disabled))
            {
                IndexDirectorySlotSnapshot slotRequest = CreateHarnessSlot(0, "stress", 0);
                (RouterSnapshot router, _) = initialized.CreateRootRouterIndex(slotRequest);
                IndexDirectorySlotSnapshot slot = slotRequest with { State = IndexDirectoryLayout.ActiveState, RootRouterOffset = router.Offset };
                ValidateRootRouter(initialized, router);
                ValidateFormatSession(initialized, developerMetadata, slot);
            }

            using (LibraDexFileSession opened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.Disabled))
            {
                IndexDirectorySlotSnapshot slot = opened.IndexDirectory.ActiveSlots[0];
                ValidateRootRouter(opened, opened.ReadRouterSnapshot(slot.RootRouterOffset));
                SuperblockDeveloperMetadata updatedMetadata = new(
                    DevIdentity: "StressUpdated",
                    DevCustomText: "format stress update",
                    DevGuid: Guid.Parse("f2893fb7-6524-4354-bc82-a09c56e14710"),
                    DevDate1UtcTicks: 44,
                    DevDate2UtcTicks: 55,
                    DevNumber: 66);

                opened.UpdateDeveloperMetadata(updatedMetadata);
                ValidateFormatSession(opened, updatedMetadata, slot);
                ValidateRootRouter(opened, opened.ReadRouterSnapshot(slot.RootRouterOffset));
            }

            using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.Disabled))
            {
                SuperblockDeveloperMetadata updatedMetadata = new(
                    DevIdentity: "StressUpdated",
                    DevCustomText: "format stress update",
                    DevGuid: Guid.Parse("f2893fb7-6524-4354-bc82-a09c56e14710"),
                    DevDate1UtcTicks: 44,
                    DevDate2UtcTicks: 55,
                    DevNumber: 66);

                IndexDirectorySlotSnapshot slot = reopened.IndexDirectory.ActiveSlots[0];
                ValidateFormatSession(reopened, updatedMetadata, slot);
                ValidateRootRouter(reopened, reopened.ReadRouterSnapshot(slot.RootRouterOffset));
            }

            File.Delete(path);
        }

        watch.Stop();
        double iterationsPerSecond = iterations / Math.Max(watch.Elapsed.TotalSeconds, 0.000001);
        return new FormatStressResult(iterations, watch.Elapsed, iterationsPerSecond);
    }


    /// <summary>
    /// Writes the current validation report and archives the previous current report with its recorded timestamp.<br/>
    /// The stable current file is the easiest comparison target, while archived timestamped files preserve history.<br/>
    /// </summary>
    /// <param name="artifactDirectory">The directory where validation reports are written.</param>
    /// <param name="tierOptions">The validation tier policy used for the run.</param>
    /// <param name="metrics">The measured validation metrics.</param>
    /// <param name="formatStress">The measured format stress result.</param>
    /// <returns>The written report path.</returns>
    private static string WriteValidationReport(
        string artifactDirectory,
        ValidationTierOptions tierOptions,
        List<ValidationMetric> metrics,
        FormatStressResult formatStress)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string reportPath = Path.Combine(artifactDirectory, $"validation-{tierOptions.Name}-current.md");
        ArchiveCurrentValidationReport(reportPath, tierOptions.Name);

        StringBuilder builder = new();
        builder.AppendLine($"# LibraDex Validation - {tierOptions.Name} - {timestamp} UTC");
        builder.AppendLine();
        builder.AppendLine("## Scope");
        builder.AppendLine();
        builder.AppendLine($"- Total bytes: `{tierOptions.TotalBytes}`");
        builder.AppendLine($"- Block size: `{tierOptions.BlockSize}`");
        builder.AppendLine($"- Append buffer size: `{tierOptions.AppendBufferSize}`");
        builder.AppendLine($"- Perf runs: `{tierOptions.PerfRuns}`");
        builder.AppendLine($"- Format stress iterations: `{formatStress.Iterations}`");
        builder.AppendLine();
        builder.AppendLine("## Drift");
        builder.AppendLine();
        builder.AppendLine("| Status | Metric | Actual | Baseline/Expected | Drift % | Unit |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | --- |");

        for (int i = 0; i < metrics.Count; i++)
        {
            ValidationMetric metric = metrics[i];
            builder.AppendLine($"| {metric.Status} | {metric.Name} | {metric.Actual:N2} | {metric.Baseline:N2} | {FormatDrift(metric)} | {metric.Unit} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Notes");
        builder.AppendLine();
        builder.AppendLine("- Throughput warning threshold: `10%` below baseline.");
        builder.AppendLine("- Throughput fail threshold: `20%` below baseline.");
        builder.AppendLine("- Syscall-shape metrics fail on any mismatch.");
        builder.AppendLine("- Fast-tier throughput can be noisier than checkpoint or baseline tiers; use checkpoint before changing recorded baselines.");

        File.WriteAllText(reportPath, builder.ToString());
        return reportPath;
    }


    /// <summary>
    /// Archives an existing current validation report using the timestamp recorded inside the file.<br/>
    /// If timestamp parsing fails, the file's last-write UTC timestamp is used so the current report is still preserved.<br/>
    /// </summary>
    /// <param name="currentReportPath">The stable current report path.</param>
    /// <param name="tierName">The validation tier name.</param>
    private static void ArchiveCurrentValidationReport(string currentReportPath, string tierName)
    {
        ArchiveCurrentReport(currentReportPath, $"validation-{tierName}");
    }


    private static void ArchiveCurrentReport(string currentReportPath, string archivePrefix)
    {
        if (!File.Exists(currentReportPath))
        {
            return;
        }

        string content = File.ReadAllText(currentReportPath);
        string timestamp = ExtractValidationTimestamp(content);
        if (timestamp.Length == 0)
        {
            timestamp = File.GetLastWriteTimeUtc(currentReportPath).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        string directory = Path.GetDirectoryName(currentReportPath)!;
        string archivePath = Path.Combine(directory, $"{archivePrefix}-{timestamp}.md");
        int collision = 1;
        while (File.Exists(archivePath))
        {
            archivePath = Path.Combine(directory, $"{archivePrefix}-{timestamp}-{collision}.md");
            collision++;
        }

        File.Move(currentReportPath, archivePath);
    }


    /// <summary>
    /// Prints a compact validation summary to the console.<br/>
    /// The summary keeps interactive output short while the report carries detailed evidence.<br/>
    /// </summary>
    /// <param name="metrics">The measured validation metrics.</param>
    /// <param name="reportPath">The written validation report path.</param>
    private static void PrintValidationSummary(List<ValidationMetric> metrics, string reportPath)
    {
        int pass = 0;
        int warn = 0;
        int fail = 0;
        for (int i = 0; i < metrics.Count; i++)
        {
            switch (metrics[i].Status)
            {
                case ValidationStatus.Warn:
                    warn++;
                    break;
                case ValidationStatus.Fail:
                    fail++;
                    break;
                default:
                    pass++;
                    break;
            }
        }

        Console.WriteLine($"validate summary pass={pass} warn={warn} fail={fail}");
        Console.WriteLine($"validate report {Path.GetFullPath(reportPath)}");

        for (int i = 0; i < metrics.Count; i++)
        {
            ValidationMetric metric = metrics[i];
            if (metric.Status != ValidationStatus.Pass)
            {
                Console.WriteLine($"{metric.Status}: {metric.Name} actual={metric.Actual:N2} baseline={metric.Baseline:N2} drift={metric.DriftPercent:N2}% {metric.Unit}");
            }
        }
    }


    /// <summary>
    /// Determines whether validation should exit as failed.<br/>
    /// Shape metrics always fail on mismatch, while throughput fail drift is tier-controlled because fast runs are intentionally noisy.<br/>
    /// </summary>
    /// <param name="metrics">The measured validation metrics.</param>
    /// <param name="failThroughputMetrics">Whether fail-level throughput drift should fail the process.</param>
    /// <returns>True when any metric failed.</returns>
    private static bool HasFailingMetric(List<ValidationMetric> metrics, bool failThroughputMetrics)
    {
        for (int i = 0; i < metrics.Count; i++)
        {
            ValidationMetric metric = metrics[i];
            if (metric.Status == ValidationStatus.Fail && (metric.IsShapeMetric || failThroughputMetrics))
            {
                return true;
            }
        }

        return false;
    }


    private static int RunStorageBaseline(string[] args)
    {
        string directory = GetOption(args, "--directory", Path.Combine(@"T:\LibraDex", "StorageBaseline"));
        long totalBytes = GetLongOption(args, "--total-bytes", 1024L * 1024 * 1024);
        bool warmup = GetBoolOption(args, "--warmup", true);
        int runs = GetIntOption(args, "--runs", 1);
        string blockSizeText = GetOption(args, "--block-sizes", "65536,1048576,4194304,16777216,67108864,268435456");

        Directory.CreateDirectory(directory);

        int[] blockSizes = ParseIntList(blockSizeText);
        if (blockSizes.Length == 0 || runs <= 0)
        {
            Console.Error.WriteLine("storage-baseline requires at least one block size and a positive run count.");
            return 1;
        }

        if (warmup)
        {
            string warmupPath = Path.Combine(directory, "warmup.bin");
            RunStorageBaselineCase(warmupPath, 256L * 1024 * 1024, 4 * 1024 * 1024, StorageBaselineMode.Chunked, print: false);
            File.Delete(warmupPath);
        }

        Console.WriteLine("RandomAccess storage baseline");
        Console.WriteLine($"directory    {directory}");
        Console.WriteLine($"total MiB    {totalBytes / 1024d / 1024d:N2}");
        Console.WriteLine($"warmup       {warmup}");
        Console.WriteLine($"runs         {runs}");
        Console.WriteLine();
        Console.WriteLine("| mode | block bytes | runs | write mean MiB/s | write min | write max | write stddev | write calls | read mean MiB/s | read min | read max | read stddev | read calls |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (int blockSize in blockSizes)
        {
            RunStorageBaselineSet(directory, totalBytes, blockSize, StorageBaselineMode.Chunked, runs);

            if (blockSize >= 64 * 1024 * 1024)
            {
                RunStorageBaselineSet(directory, totalBytes, blockSize, StorageBaselineMode.HugeBuffer, runs);
            }
        }

        return 0;
    }


    private static void RunStorageBaselineSet(
        string directory,
        long totalBytes,
        int blockSize,
        StorageBaselineMode mode,
        int runs)
    {
        StorageBaselineResult[] results = new StorageBaselineResult[runs];
        for (int i = 0; i < runs; i++)
        {
            string path = Path.Combine(directory, $"{mode}-{blockSize}-run-{i + 1}.bin");
            results[i] = RunStorageBaselineCase(path, totalBytes, blockSize, mode, print: false);
            File.Delete(path);
        }

        PrintStorageBaselineSet(mode, blockSize, results);
    }


    private static StorageBaselineResult RunStorageBaselineCase(
        string path,
        long totalBytes,
        int blockSize,
        StorageBaselineMode mode,
        bool print)
    {
        File.Delete(path);
        byte[] buffer = CreatePattern(blockSize, blockSize & 0xFF);
        long writeCalls = 0;
        long readCalls = 0;

        Stopwatch writeWatch = Stopwatch.StartNew();
        using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.RandomAccess))
        {
            if (mode == StorageBaselineMode.HugeBuffer)
            {
                byte[] hugeBuffer = CreatePattern(checked((int)totalBytes), 97);
                RandomAccess.Write(handle, hugeBuffer, 0);
                writeCalls = 1;
            }
            else
            {
                long written = 0;
                while (written < totalBytes)
                {
                    int length = (int)Math.Min(blockSize, totalBytes - written);
                    RandomAccess.Write(handle, buffer.AsSpan(0, length), written);
                    written += length;
                    writeCalls++;
                }
            }
        }

        writeWatch.Stop();

        byte[] readBuffer = new byte[blockSize];
        Stopwatch readWatch = Stopwatch.StartNew();
        using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess))
        {
            long readOffset = 0;
            while (readOffset < totalBytes)
            {
                int length = (int)Math.Min(blockSize, totalBytes - readOffset);
                int read = RandomAccess.Read(handle, readBuffer.AsSpan(0, length), readOffset);
                if (read != length)
                {
                    throw new EndOfStreamException($"Expected {length} bytes at offset {readOffset}, read {read}.");
                }

                readOffset += read;
                readCalls++;
            }
        }

        readWatch.Stop();

        if (!print)
        {
            return new StorageBaselineResult(
                WriteElapsed: writeWatch.Elapsed,
                WriteCalls: writeCalls,
                ReadElapsed: readWatch.Elapsed,
                ReadCalls: readCalls,
                TotalBytes: totalBytes);
        }

        double totalMiB = totalBytes / 1024d / 1024d;
        double writeMiBs = totalMiB / Math.Max(writeWatch.Elapsed.TotalSeconds, 0.000001);
        double readMiBs = totalMiB / Math.Max(readWatch.Elapsed.TotalSeconds, 0.000001);
        Console.WriteLine($"| {mode} | {blockSize} | {writeWatch.Elapsed.TotalMilliseconds:N3} | {writeMiBs:N2} | {writeCalls} | {readWatch.Elapsed.TotalMilliseconds:N3} | {readMiBs:N2} | {readCalls} |");

        return new StorageBaselineResult(
            WriteElapsed: writeWatch.Elapsed,
            WriteCalls: writeCalls,
            ReadElapsed: readWatch.Elapsed,
            ReadCalls: readCalls,
            TotalBytes: totalBytes);
    }


    private static double Mean(ReadOnlySpan<double> values)
    {
        double total = 0;
        foreach (double value in values)
        {
            total += value;
        }

        return total / values.Length;
    }


    private static double StdDev(ReadOnlySpan<double> values)
    {
        double mean = Mean(values);
        double total = 0;
        foreach (double value in values)
        {
            double delta = value - mean;
            total += delta * delta;
        }

        return Math.Sqrt(total / values.Length);
    }


    private static void PrintPerf(
        DataKernelBackingKind backing,
        string path,
        long totalBytes,
        int blockSize,
        int appendBufferSize,
        AppendMode appendMode,
        bool telemetryEnabled,
        bool flush,
        PerfRawResult result)
    {
        double totalMiB = totalBytes / 1024d / 1024d;
        double stageMiBs = totalMiB / Math.Max(result.StageElapsed.TotalSeconds, 0.000001);
        double commitSeconds = result.CommitElapsed.TotalSeconds;
        double commitMiBs = commitSeconds == 0 ? 0 : totalMiB / commitSeconds;
        double readMiBs = totalMiB / Math.Max(result.ReadElapsed.TotalSeconds, 0.000001);

        Console.WriteLine("raw DataKernel perf");
        Console.WriteLine($"backing              {backing}");
        if (backing == DataKernelBackingKind.File)
        {
            Console.WriteLine($"path                 {path}");
        }

        Console.WriteLine($"total MiB            {totalMiB:N2}");
        Console.WriteLine($"block bytes          {blockSize}");
        Console.WriteLine($"append buffer bytes  {appendBufferSize}");
        Console.WriteLine($"append mode          {appendMode}");
        Console.WriteLine($"telemetry            {telemetryEnabled}");
        Console.WriteLine($"flush                {flush}");
        Console.WriteLine();
        Console.WriteLine("| phase  | elapsed ms | MiB/s | syscalls | bytes | details |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | --- |");
        Console.WriteLine($"| stage | {result.StageElapsed.TotalMilliseconds:N3} | {stageMiBs:N2} | 0 | {totalBytes} | {GetStageDetails(appendMode)} |");
        Console.WriteLine($"| commit | {commitSeconds * 1000:N3} | {commitMiBs:N2} | {result.CommitTelemetry.WriteCallCount + result.CommitTelemetry.SetLengthCallCount + result.CommitTelemetry.FlushCallCount} | {result.CommitTelemetry.BytesWritten} | writes={result.CommitTelemetry.WriteCallCount}, backingWrites={result.CommitTelemetry.BackingWriteCallCount}, setLength={result.CommitTelemetry.SetLengthCallCount}, flush={result.CommitTelemetry.FlushCallCount}, segments={result.CommitTelemetry.StagedSegmentCount} |");
        Console.WriteLine($"| read | {result.ReadElapsed.TotalMilliseconds:N3} | {readMiBs:N2} | {result.ReadTelemetry.ReadCallCount} | {result.ReadTelemetry.BytesRead} | backingReads={result.ReadTelemetry.BackingReadCallCount} |");
    }


    private static void PrintPerfSet(
        DataKernelBackingKind backing,
        string path,
        long totalBytes,
        int blockSize,
        int appendBufferSize,
        AppendMode appendMode,
        bool telemetryEnabled,
        bool flush,
        PerfRawResult[] results)
    {
        double[] stageMiBs = new double[results.Length];
        double[] commitMiBs = new double[results.Length];
        double[] readMiBs = new double[results.Length];
        double totalMiB = totalBytes / 1024d / 1024d;

        for (int i = 0; i < results.Length; i++)
        {
            stageMiBs[i] = totalMiB / Math.Max(results[i].StageElapsed.TotalSeconds, 0.000001);
            commitMiBs[i] = totalMiB / Math.Max(results[i].CommitElapsed.TotalSeconds, 0.000001);
            readMiBs[i] = totalMiB / Math.Max(results[i].ReadElapsed.TotalSeconds, 0.000001);
        }

        DataKernelCommitTelemetry commit = results[0].CommitTelemetry;
        DataKernelReadTelemetry read = results[0].ReadTelemetry;

        Console.WriteLine("raw DataKernel perf aggregate");
        Console.WriteLine($"backing              {backing}");
        if (backing == DataKernelBackingKind.File)
        {
            Console.WriteLine($"path                 {path}");
        }

        Console.WriteLine($"total MiB            {totalMiB:N2}");
        Console.WriteLine($"block bytes          {blockSize}");
        Console.WriteLine($"append buffer bytes  {appendBufferSize}");
        Console.WriteLine($"append mode          {appendMode}");
        Console.WriteLine($"telemetry            {telemetryEnabled}");
        Console.WriteLine($"flush                {flush}");
        Console.WriteLine($"runs                 {results.Length}");
        Console.WriteLine();
        Console.WriteLine("| phase | mean MiB/s | min | max | stddev | syscalls | bytes | details |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        Console.WriteLine($"| stage | {Mean(stageMiBs):N2} | {stageMiBs.Min():N2} | {stageMiBs.Max():N2} | {StdDev(stageMiBs):N2} | 0 | {totalBytes} | {GetStageDetails(appendMode)} |");
        Console.WriteLine($"| commit | {Mean(commitMiBs):N2} | {commitMiBs.Min():N2} | {commitMiBs.Max():N2} | {StdDev(commitMiBs):N2} | {commit.WriteCallCount + commit.SetLengthCallCount + commit.FlushCallCount} | {commit.BytesWritten} | writes={commit.WriteCallCount}, backingWrites={commit.BackingWriteCallCount}, setLength={commit.SetLengthCallCount}, flush={commit.FlushCallCount}, segments={commit.StagedSegmentCount} |");
        Console.WriteLine($"| read | {Mean(readMiBs):N2} | {readMiBs.Min():N2} | {readMiBs.Max():N2} | {StdDev(readMiBs):N2} | {read.ReadCallCount} | {read.BytesRead} | backingReads={read.BackingReadCallCount} |");
    }


    private static void PrintRoutePerf(string scenario, RoutePerfResult result)
    {
        double seconds = Math.Max(result.Elapsed.TotalSeconds, 0.000001);
        double routesPerSecond = result.Iterations / seconds;
        double nsPerRoute = (seconds * 1_000_000_000d) / result.Iterations;
        Console.WriteLine($"| {scenario} | {result.Elapsed.TotalMilliseconds:N3} | {routesPerSecond:N2} | {nsPerRoute:N2} | {result.Checksum} |");
    }


    private static void PrintRouteShape(RouteShapeResult result)
    {
        double compressedNs = GetNsPerRoute(result.CompressedPerf);
        double expandedNs = GetNsPerRoute(result.ExpandedPerf);
        Console.WriteLine($"| {result.Name} | {result.Equivalent} | {result.CompressedRouteCount} | {result.CompressedRouteBytes} | {result.ExpandedRouteBytes} | {result.SavedRouteBytes} | {compressedNs:N2} | {expandedNs:N2} |");
    }


    private static string WriteRouteShapesReport(string artifactDirectory, int iterations, RouteShapeResult[] results)
    {
        Directory.CreateDirectory(artifactDirectory);
        string reportPath = Path.Combine(artifactDirectory, "route-shapes-current.md");
        ArchiveCurrentReport(reportPath, "route-shapes");

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        StringBuilder builder = new();
        builder.AppendLine($"# LibraDex Route Shapes - {timestamp} UTC");
        builder.AppendLine();
        builder.AppendLine("## Scope");
        builder.AppendLine();
        builder.AppendLine($"- Iterations per shape: `{iterations}`");
        builder.AppendLine("- Compressed routers use linear route range lookup.");
        builder.AppendLine("- Expanded routers use direct-index lookup over 256 self-range slots.");
        builder.AppendLine("- Equivalence checks compare all 256 possible prefix bytes.");
        builder.AppendLine();
        builder.AppendLine("## Results");
        builder.AppendLine();
        builder.AppendLine("| Shape | Equivalent | Compressed routes | Compressed bytes | Expanded bytes | Saved bytes | Compressed ns | Expanded ns |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        for (int i = 0; i < results.Length; i++)
        {
            RouteShapeResult result = results[i];
            builder.AppendLine($"| {result.Name} | {result.Equivalent} | {result.CompressedRouteCount} | {result.CompressedRouteBytes} | {result.ExpandedRouteBytes} | {result.SavedRouteBytes} | {GetNsPerRoute(result.CompressedPerf):N2} | {GetNsPerRoute(result.ExpandedPerf):N2} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Notes");
        builder.AppendLine();
        builder.AppendLine("- Route slot bytes count only persisted route slots, not the fixed 4 KiB page.");
        builder.AppendLine("- Self-range compressed is intentionally worst-case compression and should be equivalent to expanded direct routing.");

        File.WriteAllText(reportPath, builder.ToString());
        return reportPath;
    }


    private static string WriteRoutePerfReport(
        string artifactDirectory,
        int iterations,
        RoutePerfResult rootOnly,
        RoutePerfResult twoHopEarly,
        RoutePerfResult twoHopMiddle,
        RoutePerfResult twoHopLate,
        RoutePerfResult rootMiss)
    {
        Directory.CreateDirectory(artifactDirectory);
        string reportPath = Path.Combine(artifactDirectory, "route-perf-current.md");
        ArchiveCurrentReport(reportPath, "route-perf");

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        StringBuilder builder = new();
        builder.AppendLine($"# LibraDex Route Perf - {timestamp} UTC");
        builder.AppendLine();
        builder.AppendLine("## Scope");
        builder.AppendLine();
        builder.AppendLine($"- Iterations: `{iterations}`");
        builder.AppendLine("- Root router: expanded direct-index 1-byte router.");
        builder.AppendLine("- Child router: compressed 1-byte router with three route ranges.");
        builder.AppendLine();
        builder.AppendLine("## Results");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Elapsed ms | Routes/sec | Ns/route | Checksum |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        AppendRoutePerf(builder, "root direct hit", rootOnly);
        AppendRoutePerf(builder, "two-hop child early", twoHopEarly);
        AppendRoutePerf(builder, "two-hop child middle", twoHopMiddle);
        AppendRoutePerf(builder, "two-hop child late", twoHopLate);
        AppendRoutePerf(builder, "root miss", rootMiss);
        builder.AppendLine();
        builder.AppendLine("## Notes");
        builder.AppendLine();
        builder.AppendLine("- Prefix bytes vary across the loop to reduce constant-route optimization risk.");
        builder.AppendLine("- This is an in-memory router-view benchmark; it does not include file I/O.");

        File.WriteAllText(reportPath, builder.ToString());
        return reportPath;
    }


    private static Scalar8Scalar8RoutedBulkWriteResult MeasureRepeatedFixed32Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        int repeatCount,
        Fixed32Scalar16Profile profile,
        bool includeAttribution)
    {
        Scalar8Scalar8RoutedBulkWriteResult[] samples = new Scalar8Scalar8RoutedBulkWriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureFixed32Scalar16RoutedBulkWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options, profile, includeAttribution) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianRoutedBulkWriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }



    private static Scalar8Scalar8RoutedBulkWriteResult MeasureFixed32Scalar16RoutedBulkWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        DataKernelOptions options,
        Fixed32Scalar16Profile profile,
        bool includeAttribution)
    {
        File.Delete(path);
        using LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(3216), DataKernelTelemetryOptions.EnabledOptions);
        (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "f3216bulk", 0));
        _ = RunFixed32Scalar16RoutedBulkWriteLoop(session, root.Offset, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0, profile, includeAttribution);
        Stopwatch watch = Stopwatch.StartNew();
        RoutedBulkWriteLoopTelemetry telemetry = RunFixed32Scalar16RoutedBulkWriteLoop(session, root.Offset, batches, itemsPerBatch, prefixCount, order, warmupBatches, profile, includeAttribution);
        watch.Stop();
        return CreateRoutedBulkWriteResult(name, batches, itemsPerBatch, watch.Elapsed, telemetry);
    }



    private static SqliteScalar8Scalar8WriteResult MeasureRepeatedSqliteFixed32Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options,
        int repeatCount)
    {
        SqliteScalar8Scalar8WriteResult[] samples = new SqliteScalar8Scalar8WriteResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteFixed32Scalar16BlobWrite(CreateRepeatPath(path, i), name, batches, warmupBatches, itemsPerBatch, prefixCount, order, options) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8WriteSample(samples) with
        {
            SampleCount = repeatCount
        };
    }



    private static SqliteScalar8Scalar8WriteResult MeasureSqliteFixed32Scalar16BlobWrite(
        string path,
        string name,
        int batches,
        int warmupBatches,
        int itemsPerBatch,
        int prefixCount,
        int[] order,
        SqliteScalar8Scalar8Options options)
    {
        ResetSqliteScalar8Scalar8Files(path);
        using SqliteConnection connection = OpenSqliteScalar8Scalar8Connection(path, options);
        InitializeSqliteFixed32Scalar16BlobSchema(connection);
        long warmupChecksum = RunSqliteFixed32Scalar16BlobWriteLoop(connection, warmupBatches, itemsPerBatch, prefixCount, order, batchOffset: 0);
        Stopwatch watch = Stopwatch.StartNew();
        long checksum = RunSqliteFixed32Scalar16BlobWriteLoop(connection, batches, itemsPerBatch, prefixCount, order, batchOffset: warmupBatches);
        watch.Stop();
        connection.Close();

        long measuredItems = (long)batches * itemsPerBatch;
        long totalItems = (long)(batches + warmupBatches) * itemsPerBatch;
        long storageBytes = GetSqliteScalar8Scalar8StorageBytes(path);
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8WriteResult(
            name,
            batches,
            warmupBatches,
            itemsPerBatch,
            measuredItems,
            totalItems,
            watch.Elapsed,
            measuredItems / seconds,
            watch.Elapsed.TotalMilliseconds * 1000d / measuredItems,
            batches / seconds,
            storageBytes,
            totalItems == 0 ? 0 : storageBytes / (double)totalItems,
            unchecked(checksum + warmupChecksum),
            1);
    }



    private static Fixed32Scalar16ReadParityResult MeasureRepeatedFixed32Scalar16ReadRange(
        LibraDexFileSession session,
        long rootRouterOffset,
        Fixed32Scalar16Profile profile,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities,
        int repeatCount)
    {
        Fixed32Scalar16ReadParityResult[] samples = new Fixed32Scalar16ReadParityResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureFixed32Scalar16ReadRange(session, rootRouterOffset, profile, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianFixed32Scalar16ReadParitySample(samples) with
        {
            SampleCount = repeatCount
        };
    }



    private static Fixed32Scalar16ReadParityResult MeasureFixed32Scalar16ReadRange(
        LibraDexFileSession session,
        long rootRouterOffset,
        Fixed32Scalar16Profile profile,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The FS32-16 read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateFixed32Scalar16PrefixRangeBounds(lowerPrefix, upperPrefix, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        byte[] shelfScratch = new byte[profile.ShelfExtentSize];
        using RouteTargetKindCache targetKindCache = RouteTargetKindCache.Rent();
        long checksum = 0;
        long totalIdentities = 0;
        _ = session.GetAndResetReadTelemetry();
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = session.ReadFixed32Scalar16IdentityLowRange(
                rootRouterOffset,
                profile,
                lower0,
                lower1,
                lower2,
                lower3,
                upper0,
                upper1,
                upper2,
                upper3,
                maxRouterHops: 8,
                identities,
                shelfScratch,
                targetKindCache);
            if (count != expectedCount)
            {
                throw new InvalidDataException($"FS32-16 read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expectedIdentities);
            totalIdentities += count;
        }

        watch.Stop();
        DataKernelReadTelemetry readTelemetry = session.GetAndResetReadTelemetry();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new Fixed32Scalar16ReadParityResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Fixed32Scalar16Layout.IdentitySize, seconds),
            iterations == 0 ? 0 : readTelemetry.ReadCallCount / (double)iterations,
            iterations == 0 ? 0 : readTelemetry.BytesRead / (double)iterations,
            checksum,
            1);
    }



    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteFixed32Scalar16BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities,
        int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteFixed32Scalar16BlobReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with
            {
                SampleCount = 1
            };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with
        {
            SampleCount = repeatCount
        };
    }



    private static SqliteScalar8Scalar8ReadResult MeasureSqliteFixed32Scalar16BlobReadRange(
        SqliteConnection connection,
        string name,
        int batches,
        int itemsPerBatch,
        int prefixCount,
        int lowerPrefix,
        int upperPrefix,
        int iterations,
        Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        if (identities.Length < expectedCount)
        {
            throw new ArgumentException("The SQLite FS32-16 read identity buffer is too small for the requested range.", nameof(identities));
        }

        ulong[] expectedIdentities = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateFixed32Scalar16PrefixRangeBounds(lowerPrefix, upperPrefix, out ulong lower0, out ulong lower1, out ulong lower2, out ulong lower3, out ulong upper0, out ulong upper1, out ulong upper2, out ulong upper3);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", CreateSqliteFixed32Scalar8Blob(lower0, lower1, lower2, lower3));
        command.Parameters.AddWithValue("$upper", CreateSqliteFixed32Scalar8Blob(upper0, upper1, upper2, upper3));

        long checksum = 0;
        long totalIdentities = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (count >= identities.Length)
                    {
                        throw new InvalidDataException("SQLite FS32-16 read returned more identities than the reusable buffer can hold.");
                    }

                    byte[] identity = reader.GetFieldValue<byte[]>(0);
                    if (identity.Length != Fixed32Scalar16Layout.IdentitySize)
                    {
                        throw new InvalidDataException($"SQLite FS32-16 read returned an identity blob with {identity.Length} bytes.");
                    }

                    ulong identityHigh = ReadSqliteScalar16Scalar16BigEndianUInt64(identity, 0);
                    ulong identityLow = ReadSqliteScalar16Scalar16BigEndianUInt64(identity, sizeof(ulong));
                    if (identityHigh != 0)
                    {
                        throw new InvalidDataException($"SQLite FS32-16 read returned identity high lane {identityHigh}; expected zero for this deterministic 64-bit identity fixture.");
                    }

                    identities[count++] = identityLow;
                }
            }

            if (count != expectedCount)
            {
                throw new InvalidDataException($"SQLite FS32-16 read {name} returned {count} identities; expected {expectedCount}.");
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expectedIdentities);
            totalIdentities += count;
        }

        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.000000001d);
        return new SqliteScalar8Scalar8ReadResult(
            name,
            iterations,
            expectedCount,
            totalIdentities,
            watch.Elapsed,
            totalIdentities / seconds,
            seconds * 1_000_000_000d / totalIdentities,
            CalculateMiBs((double)totalIdentities * Fixed32Scalar16Layout.IdentitySize, seconds),
            checksum,
            1);
    }


    private static AllShapeReadRangeSweepSample MeasureRepeatedScalar16Scalar8ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar16Scalar8Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identities, int repeatCount)
    {
        AllShapeReadRangeSweepSample[] samples = new AllShapeReadRangeSweepSample[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar16Scalar8ReadRange(session, rootRouterOffset, profile, range, batches, itemsPerBatch, prefixCount, iterations, identities) with { SampleCount = 1 };
        }

        return SelectMedianAllShapeReadRangeSweepSample(samples) with { SampleCount = repeatCount };
    }


    private static AllShapeReadRangeSweepSample MeasureRepeatedScalar8Scalar16ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar8Scalar16Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identityHighs, Span<ulong> identityLows, int repeatCount)
    {
        AllShapeReadRangeSweepSample[] samples = new AllShapeReadRangeSweepSample[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar8Scalar16ReadRange(session, rootRouterOffset, profile, range, batches, itemsPerBatch, prefixCount, iterations, identityHighs, identityLows) with { SampleCount = 1 };
        }

        return SelectMedianAllShapeReadRangeSweepSample(samples) with { SampleCount = repeatCount };
    }


    private static AllShapeReadRangeSweepSample MeasureRepeatedScalar16Scalar16ReadRange(LibraDexFileSession session, long rootRouterOffset, Scalar16Scalar16Profile profile, AllShapeReadRangeCase range, int batches, int itemsPerBatch, int prefixCount, int iterations, Span<ulong> identityHighs, Span<ulong> identityLows, int repeatCount)
    {
        AllShapeReadRangeSweepSample[] samples = new AllShapeReadRangeSweepSample[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureScalar16Scalar16ReadRange(session, rootRouterOffset, profile, range, batches, itemsPerBatch, prefixCount, iterations, identityHighs, identityLows) with { SampleCount = 1 };
        }

        return SelectMedianAllShapeReadRangeSweepSample(samples) with { SampleCount = repeatCount };
    }


    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteScalar16Scalar8BlobReadRange(SqliteConnection connection, string name, int batches, int itemsPerBatch, int prefixCount, int lowerPrefix, int upperPrefix, int iterations, Span<ulong> identities, int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar16Scalar8BlobReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identities) with { SampleCount = 1 };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with { SampleCount = repeatCount };
    }


    private static SqliteScalar8Scalar8ReadResult MeasureSqliteScalar16Scalar8BlobReadRange(SqliteConnection connection, string name, int batches, int itemsPerBatch, int prefixCount, int lowerPrefix, int upperPrefix, int iterations, Span<ulong> identities)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        ulong[] expected = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateScalar16RangeBounds(new AllShapeReadRangeCase(name, lowerPrefix, upperPrefix), out ulong lowerHigh, out ulong lowerLow, out ulong upperHigh, out ulong upperLow);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", CreateSqliteScalar16Scalar16Blob(lowerHigh, lowerLow));
        command.Parameters.AddWithValue("$upper", CreateSqliteScalar16Scalar16Blob(upperHigh, upperLow));
        long checksum = 0;
        long total = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                identities[count++] = DecodeSqliteSortableUnsignedScalar8(reader.GetInt64(0));
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identities, count, expected);
            total += count;
        }

        watch.Stop();
        return CreateSqliteRangeReadResult(name, iterations, expectedCount, total, watch.Elapsed, Scalar16Scalar8Layout.IdentitySize, checksum);
    }


    private static SqliteScalar8Scalar8ReadResult MeasureRepeatedSqliteScalar8Scalar16BlobReadRange(SqliteConnection connection, string name, int batches, int itemsPerBatch, int prefixCount, int lowerPrefix, int upperPrefix, int iterations, Span<ulong> identityHighs, int repeatCount)
    {
        SqliteScalar8Scalar8ReadResult[] samples = new SqliteScalar8Scalar8ReadResult[repeatCount];
        for (int i = 0; i < repeatCount; i++)
        {
            samples[i] = MeasureSqliteScalar8Scalar16BlobReadRange(connection, name, batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, iterations, identityHighs) with { SampleCount = 1 };
        }

        return SelectMedianSqliteScalar8Scalar8ReadSample(samples) with { SampleCount = repeatCount };
    }


    private static SqliteScalar8Scalar8ReadResult MeasureSqliteScalar8Scalar16BlobReadRange(SqliteConnection connection, string name, int batches, int itemsPerBatch, int prefixCount, int lowerPrefix, int upperPrefix, int iterations, Span<ulong> identityHighs)
    {
        int expectedCount = CountScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix);
        ulong[] expected = CreateSortedScalar8Scalar8PublicBulkReadExpectedKeys(batches, itemsPerBatch, prefixCount, lowerPrefix, upperPrefix, expectedCount);
        CreateScalar8RangeBounds(new AllShapeReadRangeCase(name, lowerPrefix, upperPrefix), out ulong lower, out ulong upper);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT i FROM items WHERE k >= $lower AND k <= $upper ORDER BY k, i;";
        command.Parameters.AddWithValue("$lower", EncodeSqliteSortableUnsignedScalar8(lower));
        command.Parameters.AddWithValue("$upper", EncodeSqliteSortableUnsignedScalar8(upper));
        long checksum = 0;
        long total = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                byte[] identity = reader.GetFieldValue<byte[]>(0);
                identityHighs[count++] = ReadSqliteScalar16Scalar16BigEndianUInt64(identity, 0);
            }

            checksum += CheckScalar8Scalar8PublicBulkReadRange(identityHighs, count, expected);
            total += count;
        }

        watch.Stop();
        return CreateSqliteRangeReadResult(name, iterations, expectedCount, total, watch.Elapsed, Scalar8Scalar16Layout.IdentitySize, checksum);
    }

}
