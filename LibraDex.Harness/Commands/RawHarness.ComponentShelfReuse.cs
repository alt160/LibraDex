using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>Proves component-root composition using existing SS8-8 routers, shelves, readers and mutations.<br/>
    /// Only the top root has a catalog slot; parent shelf identities are child-root offsets, not copied composite keys.<br/>
    /// This fixture does not install a production composite implementation or claim atomic multi-component creation.<br/></summary>
    private static int RunComponentShelfReuseProbe(string[] args)
    {
        string folder = Path.GetFullPath(Path.Combine("artifacts", "component-reuse-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var reports = new List<object>();
        foreach (string shape in new[] { "one-wide", "region-status", "duplicate", "unique-prefix" })
        foreach (int shelfBytes in new[] { 4096, 32768 })
        {
            int count = shape == "region-status" ? 200000 : shape == "unique-prefix" ? 512 : 20000;
            var profile = Scalar8Scalar8Profile.Create(shelfBytes);
            string path = Path.Combine(folder, shape + "-" + shelfBytes + ".lbdx");
            var expected = new HashSet<(ulong A, ulong B, ulong C, ulong Id)>();
            var options = CreateDesignPerfOptions();
            bool passed = false;
            try
            {
                long initialBytes, finalBytes, appendGrowth, maxWrite = 0, writes = 0, rootsCreated;
                double buildMs, appendMs, reopenMs, scanMs;
                using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9618), DataKernelTelemetryOptions.EnabledOptions))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "components", 0));
                    var tree = new ComponentShelfFixture(session, top.Offset, profile);
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < count; i++)
                    {
                        var row = ComponentRow(shape, i);
                        var inserted = tree.Add(row.A, row.B, row.C, row.Id);
                        RequireComponent(inserted.InsertResult == Scalar8Scalar8InsertResult.Inserted, "Initial tuple not inserted.");
                        expected.Add(row);
                    }
                    buildMs = watch.Elapsed.TotalMilliseconds;
                    session.FlushFileToDisk(); initialBytes = new FileInfo(path).Length;
                    RequireComponent(session.IndexDirectory.ActiveSlots.Length == 1, "A child consumed a catalog slot.");
                    RequireComponent(tree.Scan().ToHashSet().SetEquals(expected), "Build oracle mismatch.");

                    // The same A/B branch already exists. Neither parent router nor its shelf should change.
                    var first = ComponentRow(shape, 0);
                    byte[][] parents = tree.ParentPages(first.A, first.B);
                    watch.Restart();
                    for (int i = 0; i < 100; i++)
                    {
                        ulong key = shape == "duplicate" ? first.C : (ulong)(count + i) * 100 + 10000;
                        ulong id = (ulong)count + (ulong)i + 1;
                        var result = tree.Add(first.A, first.B, key, id);
                        RequireComponent(result.InsertResult == Scalar8Scalar8InsertResult.Inserted, "Later tuple not inserted.");
                        expected.Add((first.A, first.B, key, id));
                        maxWrite = Math.Max(maxWrite, result.Commit.BytesWritten); writes += result.Commit.BytesWritten;
                    }
                    appendMs = watch.Elapsed.TotalMilliseconds; session.FlushFileToDisk();
                    appendGrowth = new FileInfo(path).Length - initialBytes;
                    byte[][] after = tree.ParentPages(first.A, first.B);
                    for (int p = 0; p < parents.Length; p++) RequireComponent(parents[p].AsSpan().SequenceEqual(after[p]), "Terminal insert rewrote a parent component page.");

                    // Existing exact-tuple deletion/reinsertion and no-op duplicates use the same child root.
                    ulong lastKey = shape == "duplicate" ? first.C : (ulong)(count + 99) * 100 + 10000;
                    ulong lastId = (ulong)count + 100;
                    var noOp = tree.Add(first.A, first.B, lastKey, lastId);
                    RequireComponent(noOp.InsertResult != Scalar8Scalar8InsertResult.Inserted, "Duplicate tuple inserted twice.");
                    long child = tree.Child(tree.Child(top.Offset, first.A, false), first.B, false);
                    for (int i = 0; i < 300; i++)
                    {
                        RequireComponent(session.DeleteWalkedRoutedScalar8Scalar8Exact(child, profile, lastKey, lastId, 64,
                            Scalar8Scalar8RouteReadPolicy.PreferPromotedViews, out _), "Existing child delete failed.");
                        RequireComponent(tree.Add(first.A, first.B, lastKey, lastId).InsertResult == Scalar8Scalar8InsertResult.Inserted, "Existing child reinsertion failed.");
                    }
                    RequireComponent(session.CountScalar8Scalar8Identities(child, profile) == expected.LongCount(r => r.A == first.A && r.B == first.B), "Child count mismatch.");
                    using (var range = session.OpenScalar8Scalar8RangeReader(child, profile, first.C, lastKey, QueryDirection.Ascending, 64))
                        RequireComponent(range.Count == expected.Count(r => r.A == first.A && r.B == first.B && r.C >= first.C && r.C <= lastKey), "Child range mismatch.");
                    RequireComponent(tree.Scan().ToHashSet().SetEquals(expected), "Post-mutation oracle mismatch.");
                    rootsCreated = tree.RootsCreated; finalBytes = new FileInfo(path).Length;
                    session.FlushFileToDisk();
                }
                var openWatch = Stopwatch.StartNew();
                using (var reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    reopenMs = openWatch.Elapsed.TotalMilliseconds;
                    long top = reopened.IndexDirectory.ActiveSlots[0].RootRouterOffset;
                    var tree = new ComponentShelfFixture(reopened, top, profile);
                    openWatch.Restart();
                    var actual = tree.Scan().ToArray(); scanMs = openWatch.Elapsed.TotalMilliseconds;
                    RequireComponent(actual.Length == expected.Count && actual.ToHashSet().SetEquals(expected), "Reopened tuple oracle mismatch.");
                    RequireComponent(reopened.IndexDirectory.ActiveSlots.Length == 1, "Reopen changed slot ownership.");
                }
                reports.Add(new { shape, shelfBytes, count, childRoots = rootsCreated, catalogSlots = 1, buildMs, initialBytes,
                    appendMs, appendGrowth, maxTerminalCommitBytes = maxWrite, terminalCommitBytes = writes, finalBytes, reopenMs, scanMs,
                    parentPagesUnchanged = true, exactRangeCountDeleteReinsertReopenPassed = true });
                Console.WriteLine($"PASS component reuse {shape} shelf={shelfBytes} count={count} bytes={initialBytes} build={buildMs:F1}ms +100 growth={appendGrowth} parent pages unchanged");
                passed = true;
            }
            finally
            {
                // Exact owned fixture path only; preserve failed evidence and retain compact reports.
                if (passed && File.Exists(path)) File.Delete(path);
            }
        }
        string report = Path.Combine(folder, "report.json");
        var mixed = CheckMixedComponentReuse(folder);
        var bulk = CheckComponentBulkReuse(folder);
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true,
            scope = "Existing physical engines; one top catalog slot. Reflection used only for kernel access during diagnostic root setup/readback. Scalar benchmarks use FlushToDiskOnCommit=false and explicit final flush; mixed fixtures also exercise flush-per-commit. New-child creation is not atomic with parent linking. No production composite format change.", reports, mixed, bulk }));
        Console.WriteLine("PASS component-shelf-reuse-probe " + report);
        return 0;
    }

    /// <summary>Creates independent encoded scalar scenarios, including a large duplicate-key identity run.<br/>
    /// Values model components; they are not a substitute for validating production string/decimal/null encodings.<br/></summary>
    private static (ulong A, ulong B, ulong C, ulong Id) ComponentRow(string shape, int i) => shape switch
    {
        "region-status" => ((ulong)(i % 5), (ulong)(i / 5 % 5), (ulong)i * 100 + 10000, (ulong)i + 1),
        "unique-prefix" => ((ulong)i, (ulong)i, (ulong)i * 100 + 10000, (ulong)i + 1),
        "duplicate" => (1, 1, 17500, (ulong)i + 1),
        _ => (1, 1, (ulong)i * 100 + 10000, (ulong)i + 1)
    };

    /// <summary>Fails the fixture immediately rather than reporting partial checks as acceptance.<br/></summary>
    private static void RequireComponent(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    /// <summary>Uses the existing native sorted builder on uncatalogued terminal roots, then ordinary mutation after reopen.<br/>
    /// Validates reuse of one physical representation rather than a bulk-only composite snapshot format.<br/></summary>
    private static List<object> CheckComponentBulkReuse(string folder)
    {
        var reports = new List<object>();
        foreach (int size in new[] { 4096, 32768 })
        {
            string path = Path.Combine(folder, "bulk-" + size + ".lbdx");
            var profile = Scalar8Scalar8Profile.Create(size);
            var options = CreateDesignPerfOptions();
            var expected = new HashSet<(ulong A, ulong B, ulong C, ulong Id)>();
            bool passed = false;
            try
            {
                double buildMs, insertMs; long bytes, growth, maxWrite = 0;
                var watch = Stopwatch.StartNew();
                using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9621), DataKernelTelemetryOptions.EnabledOptions))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "bulk-components", 0));
                    var tree = new ComponentShelfFixture(session, top.Offset, profile);
                    for (int a = 0; a < 5; a++)
                    for (int b = 0; b < 5; b++)
                    {
                        long child = tree.Child(tree.Child(top.Offset, (ulong)a, true), (ulong)b, true);
                        var tuples = new Scalar8Scalar8SortedTuple[8000]; int j = 0;
                        for (int i = a + b * 5; i < 200000; i += 25)
                        {
                            var row = ComponentRow("region-status", i); expected.Add(row);
                            tuples[j++] = new(row.C, row.Id);
                        }
                        session.BuildScalar8Scalar8FromSorted(child, profile, tuples, true, false, CancellationToken.None);
                    }
                    session.FlushFileToDisk(); buildMs = watch.Elapsed.TotalMilliseconds; bytes = new FileInfo(path).Length;
                    RequireComponent(tree.Scan().ToHashSet().SetEquals(expected), "Native child bulk oracle mismatch.");
                }
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    var tree = new ComponentShelfFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, profile);
                    var parents = tree.ParentPages(0, 0); watch.Restart();
                    for (int i = 200000; i < 200100; i++)
                    {
                        var row = (A: 0UL, B: 0UL, C: (ulong)i * 100 + 10000, Id: (ulong)i + 1);
                        var result = tree.Add(row.A, row.B, row.C, row.Id);
                        RequireComponent(result.InsertResult == Scalar8Scalar8InsertResult.Inserted, "Post-bulk insertion failed.");
                        maxWrite = Math.Max(maxWrite, result.Commit.BytesWritten); expected.Add(row);
                    }
                    insertMs = watch.Elapsed.TotalMilliseconds; session.FlushFileToDisk(); growth = new FileInfo(path).Length - bytes;
                    var after = tree.ParentPages(0, 0);
                    for (int p = 0; p < parents.Length; p++) RequireComponent(parents[p].AsSpan().SequenceEqual(after[p]), "Post-bulk insertion rewrote an ancestor.");
                    RequireComponent(tree.Scan().ToHashSet().SetEquals(expected), "Post-bulk oracle mismatch.");
                }
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    var tree = new ComponentShelfFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, profile);
                    var actual = tree.Scan().ToArray();
                    RequireComponent(actual.Length == expected.Count && actual.ToHashSet().SetEquals(expected), "Post-bulk reopen oracle mismatch.");
                }
                reports.Add(new { size, count = 200000, buildMs, bytes, next100InsertMs = insertMs, growth, maxTerminalCommitBytes = maxWrite, parentPagesUnchanged = true });
                Console.WriteLine($"PASS native component bulk shelf={size} bytes={bytes} build={buildMs:F1}ms next100={insertMs:F1}ms growth={growth}"); passed = true;
            }
            finally
            {
                if (passed && File.Exists(path)) File.Delete(path);
            }
        }
        return reports;
    }

    /// <summary>Composes existing UTF8 variable-key parent shelves with existing ordered-decimal scalar-16 terminal shelves.<br/>
    /// Exercises real heterogeneous physical shapes and scale canonicalization; it does not claim full string/null policy coverage.<br/></summary>
    private static List<object> CheckMixedComponentReuse(string folder)
    {
        var results = new List<object>();
        foreach (bool flush in new[] { false, true })
        {
            int count = flush ? 500 : 20000;
            string path = Path.Combine(folder, "mixed-" + flush + ".lbdx");
            var options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = flush };
            var profile = Scalar16Scalar8Profile.Default32KiB;
            byte[] west = System.Text.Encoding.UTF8.GetBytes("West");
            byte[] shipped = System.Text.Encoding.UTF8.GetBytes("Shipped");
            bool passed = false;
            try
            {
                var watch = Stopwatch.StartNew();
                using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9620), DataKernelTelemetryOptions.EnabledOptions))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "mixed", 0));
                    var kernel = (DataKernel)typeof(LibraDexFileSession).GetField("kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                    long status = Root(); long totals = Root();
                    RequireComponent(session.InsertWalkedRoutedVarKeyScalar8(top.Offset, 128, west, (ulong)status, false, 64).InsertResult == VarKeyScalar8InsertResult.Inserted, "String region link failed.");
                    RequireComponent(session.InsertWalkedRoutedVarKeyScalar8(status, 128, shipped, (ulong)totals, false, 64).InsertResult == VarKeyScalar8InsertResult.Inserted, "String status link failed.");
                    for (int i = 0; i < count; i++)
                    {
                        LibraDexOrderedDecimalCodec.Encode((i - count / 2) / 100m, out ulong high, out ulong low);
                        RequireComponent(session.InsertWalkedRoutedScalar16Scalar8(totals, profile, high, low, (ulong)i + 1, true, 64).InsertResult == Scalar16Scalar8InsertResult.Inserted, "Decimal insertion failed.");
                    }
                    foreach (decimal value in new[] { 175.0m, 175.00m })
                    {
                        LibraDexOrderedDecimalCodec.Encode(value, out ulong high, out ulong low);
                        count++;
                        RequireComponent(session.InsertWalkedRoutedScalar16Scalar8(totals, profile, high, low, (ulong)count, true, 64).InsertResult == Scalar16Scalar8InsertResult.Inserted, "Decimal duplicate-key insertion failed.");
                    }
                    session.FlushFileToDisk();
                    /// <summary>Initializes an ordinary uncatalogued router for this diagnostic component owner.<br/></summary>
                    long Root()
                    {
                        var reservation = kernel.Reserve(RouterLayout.Size);
                        new RouterWriter(reservation.Span).InitializeRoot(1);
                        long offset = reservation.Extent.Offset; kernel.Commit(); return offset;
                    }
                }
                double buildMs = watch.Elapsed.TotalMilliseconds;
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    long top = session.IndexDirectory.ActiveSlots[0].RootRouterOffset;
                    long status = Link(top, west); long totals = Link(status, shipped);
                    LibraDexOrderedDecimalCodec.Encode(decimal.MinValue, out ulong minHigh, out ulong minLow);
                    LibraDexOrderedDecimalCodec.Encode(decimal.MaxValue, out ulong maxHigh, out ulong maxLow);
                    using (var reader = new Scalar16Scalar8RangeReader(session, totals, profile, minHigh, minLow, maxHigh, maxLow, QueryDirection.Ascending, 64))
                    {
                        int position = 0;
                        while (reader.MoveNext())
                        {
                            decimal expected = position < count - 2 ? (position - (count - 2) / 2) / 100m : 175m;
                            decimal actual = LibraDexOrderedDecimalCodec.Decode(reader.CurrentEncodedKeyHigh, reader.CurrentEncodedKeyLow);
                            RequireComponent(actual == expected && reader.CurrentEncodedIdentity == (ulong)position + 1, "Mixed ordered oracle mismatch."); position++;
                        }
                        RequireComponent(position == count, "Mixed count mismatch.");
                    }
                    LibraDexOrderedDecimalCodec.Encode(175.000m, out ulong high, out ulong low);
                    using (var reader = new Scalar16Scalar8RangeReader(session, totals, profile, high, low, high, low, QueryDirection.Ascending, 64))
                        RequireComponent(reader.Count == 2, "Equivalent decimal scales did not share one key.");
                    /// <summary>Resolves the actual persisted UTF8 parent mapping without a process-local directory.<br/></summary>
                    long Link(long root, byte[] key)
                    {
                        using var reader = session.OpenVarKeyScalar8RangeReader(root, 128, key, key, maxRouterHops: 64);
                        RequireComponent(reader.MoveNext(), "Mixed parent lookup failed.");
                        ulong id = reader.CurrentEncodedIdentity;
                        RequireComponent(!reader.MoveNext(), "Mixed parent lookup was ambiguous."); return checked((long)id);
                    }
                }
                results.Add(new { shape = "UTF8/UTF8/Decimal", flushPerCommit = flush, count, buildMs, bytes = new FileInfo(path).Length, reopenedOracle = true });
                Console.WriteLine($"PASS mixed component reuse flush={flush} count={count}"); passed = true;
            }
            finally
            {
                if (passed && File.Exists(path)) File.Delete(path);
            }
        }
        return results;
    }

    /// <summary>Harness-only composition adapter; all routing, splitting, shelves, mutation and reads are existing LibraDex code.<br/>
    /// Parent payloads mean child offsets, while final payloads mean record identities; the component tier determines interpretation.<br/></summary>
    private sealed class ComponentShelfFixture
    {
        private readonly LibraDexFileSession _session;
        private readonly DataKernel _kernel;
        private readonly long _top;
        private readonly Scalar8Scalar8Profile _profile;
        internal long RootsCreated;

        /// <summary>Binds to one session and its persisted top root; reflection is setup-only, never per tuple.<br/></summary>
        internal ComponentShelfFixture(LibraDexFileSession session, long top, Scalar8Scalar8Profile profile)
        {
            _session = session; _top = top; _profile = profile;
            _kernel = (DataKernel)typeof(LibraDexFileSession).GetField("kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        }

        /// <summary>Resolves two component mappings and inserts only the final scalar/identity into the selected child mini-index.<br/></summary>
        internal Scalar8Scalar8RoutedInsertResult Add(ulong a, ulong b, ulong c, ulong id)
        {
            long child = Child(Child(_top, a, true), b, true);
            return _session.InsertWalkedRoutedScalar8Scalar8(child, _profile, c, id, true, 64);
        }

        /// <summary>Finds a persisted component-to-child mapping or initializes one ordinary root without a catalog slot.<br/>
        /// This single-writer fixture commits an unreachable root before linking it; production needs grouped publication and orphan cleanup.<br/></summary>
        internal long Child(long parent, ulong key, bool create)
        {
            using (var reader = _session.OpenScalar8Scalar8RangeReader(parent, _profile, key, key, QueryDirection.Ascending, 64))
            {
                if (reader.TryReadNextEncodedIdentity(out ulong offset))
                {
                    RequireComponent(!reader.TryReadNextEncodedIdentity(out _), "Component has multiple child roots.");
                    return checked((long)offset);
                }
            }
            RequireComponent(create, "Required component missing.");
            var reservation = _kernel.Reserve(RouterLayout.Size);
            new RouterWriter(reservation.Span).InitializeRoot(1);
            long root = reservation.Extent.Offset; _kernel.Commit(); RootsCreated++;
            var result = _session.InsertWalkedRoutedScalar8Scalar8(parent, _profile, key, checked((ulong)root), false, 64);
            RequireComponent(result.InsertResult == Scalar8Scalar8InsertResult.Inserted, "Parent link failed.");
            return root;
        }

        /// <summary>Walks persisted parent shelves and terminal mini-indexes without any in-memory component dictionary.<br/></summary>
        internal IEnumerable<(ulong A, ulong B, ulong C, ulong Id)> Scan()
        {
            using var regions = _session.OpenScalar8Scalar8RangeReader(_top, _profile, 0, ulong.MaxValue, QueryDirection.Ascending, 64);
            while (regions.MoveNext())
            {
                ulong a = regions.CurrentEncodedKey;
                using var statuses = _session.OpenScalar8Scalar8RangeReader(checked((long)regions.CurrentEncodedIdentity), _profile, 0, ulong.MaxValue, QueryDirection.Ascending, 64);
                while (statuses.MoveNext())
                {
                    ulong b = statuses.CurrentEncodedKey;
                    using var totals = _session.OpenScalar8Scalar8RangeReader(checked((long)statuses.CurrentEncodedIdentity), _profile, 0, ulong.MaxValue, QueryDirection.Ascending, 64);
                    while (totals.MoveNext()) yield return (a, b, totals.CurrentEncodedKey, totals.CurrentEncodedIdentity);
                }
            }
        }

        /// <summary>Captures parent router and selected parent shelf bytes for exact unchanged-ancestor assertions.<br/></summary>
        internal byte[][] ParentPages(ulong a, ulong b)
        {
            long status = Child(_top, a, false);
            var first = _session.WalkScalar8Scalar8RoutePathTarget(_top, a, 64, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            var second = _session.WalkScalar8Scalar8RoutePathTarget(status, b, 64, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            var bytes = new[] { new byte[RouterLayout.Size], new byte[_profile.ShelfExtentSize], new byte[RouterLayout.Size], new byte[_profile.ShelfExtentSize] };
            _kernel.Read(_top, bytes[0]); _kernel.Read(first.Target.Offset, bytes[1]);
            _kernel.Read(status, bytes[2]); _kernel.Read(second.Target.Offset, bytes[3]); return bytes;
        }
    }
}
