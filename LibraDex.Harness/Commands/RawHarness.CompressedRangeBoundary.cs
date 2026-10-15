using System.Diagnostics;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves that compressed nearest-route fallbacks retain query boundaries rather than becoming metadata-contained subtrees.<br/>
    /// Checks actual VS8/VS16/VV reader enumeration against constructed keys, exact misses and broad ranges, live and reopened.<br/>
    /// The optional expected-bug switch characterizes the preserved original without accepting mismatches after repair.<br/>
    /// </summary>
    /// <param name="args">Requires a fresh --path; --expect-bug accepts only characterized count/edge mismatches while still requiring correct enumeration.<br/></param>
    /// <returns>Zero after all assertions and exact successful-fixture cleanup.<br/></returns>
    private static int RunCompressedRangeBoundarySanity(string[] args)
    {
        string path = GetOption(args, "--path", string.Empty);
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) throw new ArgumentException("Supply a fresh --path for the compressed-boundary proof.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool expectBug = args.Contains("--expect-bug");
        int checks = 0, countMismatches = 0, flagMismatches = 0;
        byte[] routerBytes = new byte[RouterLayout.Size];
        RouterMultiByteRouteSnapshot[] routes = [new(new byte[] { 0x40 }, 0x20, 0x2F, 4096), new(new byte[] { 0x60 }, 0x20, 0x2F, 8192), new(new byte[] { 0x80 }, 0x20, 0x2F, 12288)];
        var writer = new RouterWriter(routerBytes); writer.InitializeCompressedMultiByte(2, 0, 3, 0, routes);
        byte[][] boundaries = [[], [0x30], [0x40], [0x40, 0x1F], [0x40, 0x20], [0x40, 0x20, 1], [0x40, 0x2F, 255, 255], [0x40, 0x30], [0x50], [0x60, 0x24], [0x70], [0x80, 0x20], [0x80, 0x2F, 255, 255], [0x90], [255]];
        foreach (byte[] lower in boundaries)
        foreach (byte[] upper in boundaries)
        {
            if (lower.AsSpan().SequenceCompareTo(upper) > 0) continue;
            foreach (int edges in new[] { 0, 1, 2, 3 })
            {
                var router = new RouterReader(routerBytes);
                bool lowerActive = (edges & 1) != 0, upperActive = (edges & 2) != 0;
                int lowerIndex = -1, upperIndex = -1;
                if (lowerActive) _ = router.FindTarget(lower, 0, out lowerIndex);
                if (upperActive) _ = router.FindTarget(upper, 0, out upperIndex);
                for (int i = 0; i < routes.Length; i++)
                {
                    bool selected = router.TrySelectMultiByteRangeRoute(i, lower, upper, 4, lowerActive, upperActive, lowerIndex, upperIndex, out long target, out bool low, out bool high);
                    if (!selected) { checks++; continue; }
                    byte[] lowBound = [routes[i].PrefixStem.Span[0], routes[i].PrefixStart, 0, 0];
                    byte[] highBound = [routes[i].PrefixStem.Span[0], routes[i].PrefixEnd, 255, 255];
                    bool wantedLow = lowerActive && i == lowerIndex && !PaddedEqual(lowBound, lower);
                    bool wantedHigh = upperActive && i == upperIndex && !PaddedEqual(highBound, upper);
                    if (target != routes[i].TargetOffset) throw new InvalidDataException("Compressed boundary target changed.");
                    if (low != wantedLow || high != wantedHigh) flagMismatches++;
                    checks += 3;
                }
            }
        }
        var cachedRouter = new RouterReader(routerBytes);
        byte[] inside = [0x60, 0x24]; long checksum = 0;
        for (int i = 0; i < 10000; i++) _ = cachedRouter.TrySelectMultiByteRangeRoute(1, inside, inside, 4, true, true, 1, 1, out _, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        long measuredBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000000; i++) if (cachedRouter.TrySelectMultiByteRangeRoute(1, inside, inside, 4, true, true, 1, 1, out long target, out bool low, out bool high)) checksum += target + (low ? 1 : 0) + (high ? 1 : 0);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - measuredBefore; watch.Stop();
        if (bytes != 0 || checksum != 8194000000L) throw new InvalidDataException("Compressed boundary allocation/result control failed.");
        Console.WriteLine($"COMPRESSED_BOUNDARY_ALLOCATION: bytes={bytes}; n=1000000; checksum={checksum}; ms={watch.Elapsed.TotalMilliseconds:F3}; setup-bytes={measuredBefore-before}");
        var options = CreateDesignPerfOptions();
        byte[][] keys = CreateVarLenMultiByteRangeCountKeys(8, 96);
        long vs8, vs16, vv;
        using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9281), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root8, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "edgevs8", 0)); vs8 = root8.Offset; CreateVarKeyScalar8MultiByteRangeCountTree(session, vs8, 8, 96);
            (RouterSnapshot root16, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "edgevs16", 0)); vs16 = root16.Offset; CreateVarKeyScalar16MultiByteRangeCountTree(session, vs16, 8, 96);
            (RouterSnapshot rootvv, _) = session.CreateRootRouterIndex(CreateHarnessSlot(2, "edgevv", 0)); vv = rootvv.Offset; CreateVarKeyVarIdentityMultiByteRangeCountTree(session, vv, 8, 96, 32);
            Verify(session);
        }
        using (var reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Verify(reopened);
        if (expectBug ? countMismatches == 0 || flagMismatches == 0 : countMismatches != 0 || flagMismatches != 0) throw new InvalidDataException($"Compressed boundary result differs: counts={countMismatches}; flags={flagMismatches}; expected-bug={expectBug}.");
        Console.WriteLine($"COMPRESSED BOUNDARIES PASS: checks={checks}; count-mismatches={countMismatches}; flag-mismatches={flagMismatches}; expected-bug={expectBug}; path={path}");
        File.Delete(path);
        return 0;

        void Verify(LibraDexFileSession session)
        {
            List<byte[]> queries = [keys[0], keys[95], keys[96], keys[383], keys[^1], [0x43], [0x43, 0x10], [0x43, 0x70], [0x42], [0x44]];
            for (int depth = 1; depth < 10; depth++) { byte[] below = (byte[])keys[200].Clone(); below[depth] = 0x10; queries.Add(below); byte[] above = (byte[])keys[200].Clone(); above[depth] = 0x70; queries.Add(above); }
            foreach (byte[] lower in queries)
            foreach (byte[] upper in queries)
            {
                if (lower.AsSpan().SequenceCompareTo(upper) > 0) continue;
                long wanted = CountVarLenMultiByteRangeCountExpected(keys, lower, upper);
                long[] actual = [session.CountVarKeyScalar8IdentityRange(vs8, 1024, lower, upper), session.CountVarKeyScalar16IdentityRange(vs16, 1024, lower, upper), session.CountVarKeyVarIdentityRange(vv, 1024, 1024, lower, upper)];
                long[] enumerated = new long[3];
                using (var reader = session.OpenVarKeyScalar8RangeReader(vs8, 1024, lower, upper)) while (reader.MoveNext()) enumerated[0]++;
                using (var reader = session.OpenVarKeyScalar16RangeReader(vs16, 1024, lower, upper)) while (reader.MoveNext()) enumerated[1]++;
                using (var reader = session.OpenVarKeyVarIdentityRangeReader(vv, 1024, 1024, lower, upper)) while (reader.MoveNext()) enumerated[2]++;
                for (int shape = 0; shape < 3; shape++) { if (enumerated[shape] != wanted) throw new InvalidDataException($"Compressed boundary reader changed: shape={shape}; wanted={wanted}; got={enumerated[shape]}."); if (actual[shape] != wanted) countMismatches++; checks += 2; }
            }
        }
    }

    /// <summary>Checks synthetic padded-bound equality independently of the router's private comparison implementation.<br/></summary>
    /// <param name="bound">Four-byte oracle bound.<br/></param>
    /// <param name="key">Raw boundary padded with zero only for this conservative routing comparison.<br/></param>
    /// <returns>Whether every compared byte is equal.<br/></returns>
    private static bool PaddedEqual(byte[] bound, byte[] key)
    { for (int i = 0; i < Math.Max(bound.Length, key.Length); i++) if ((i < bound.Length ? bound[i] : 0) != (i < key.Length ? key[i] : 0)) return false; return true; }
}
