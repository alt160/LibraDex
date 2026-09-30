using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>Compares packed sparse continuations with direct-shelf-first ownership using existing physical engines.<br/>
    /// Each A owns one B=7 here; bounded inline C/id tuples promote to an ordinary shelf and then an ordinary root.<br/>
    /// Publication is deliberately single-writer and multi-commit; this does not prove crash atomicity or production composite compatibility.<br/></summary>
    private static int RunComponentSparsePromotionProbe(string[] args)
    {
        string folder = Path.GetFullPath(Path.Combine("artifacts", "component-sparse-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var reports = new List<object>();
        bool hasFailures = false;
        foreach (bool packed in new[] { true, false })
        foreach (bool flush in new[] { false, true })
        {
            string path = Path.Combine(folder, $"packed-{packed}-flush-{flush}.lbdx");
            bool passed = false;
            try
            {
                var options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = flush };
                long sparseBytes, finalBytes; double buildMs, growthMs, scanMs; string? duplicateFailure = null;
                var expected = new HashSet<(ulong A, ulong C, ulong Id)>();
                using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9622), DataKernelTelemetryOptions.EnabledOptions))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "sparse", 0));
                    var tree = new SparsePromotionFixture(session, top.Offset, packed);
                    var watch = Stopwatch.StartNew();
                    for (ulong a = 0; a < 512; a++) { tree.Add(a, 175, a + 1); expected.Add((a, 175, a + 1)); }
                    buildMs = watch.Elapsed.TotalMilliseconds; session.FlushFileToDisk(); sparseBytes = new FileInfo(path).Length;
                    tree.Add(1, 175, 2);
                    RequireComponent(tree.Scan().Count == 512, "Sparse duplicate was not a no-op.");
                    byte[][] untouched = Enumerable.Range(1, 511).Select(a => tree.Payload((ulong)a)).ToArray();
                    watch.Restart();
                    // Same child value across all owners; grow only owner zero across both representation boundaries.
                    for (ulong i = 0; i < 2000; i++)
                    {
                        try { tree.Add(0, i, i + 10000); }
                        catch (Exception ex) { throw new InvalidDataException($"packed={packed}; flush={flush}; incoming={i}; sparseBytes={sparseBytes}; buildMs={buildMs}; profileCapacity={Scalar8Scalar8Profile.Default4KiB.MaxItemCount}", ex); }
                        expected.Add((0, i, i + 10000));
                    }
                    growthMs = watch.Elapsed.TotalMilliseconds;
                    try { tree.Add(0, 175, 10175); } // Exact duplicate must remain a no-op after promotion.
                    catch (InvalidDataException ex) { duplicateFailure = ex.Message; hasFailures = true; }
                    for (int a = 1; a < 512; a++) RequireComponent(tree.Payload((ulong)a).AsSpan().SequenceEqual(untouched[a - 1]), "Unrelated owner changed.");
                    RequireComponent(tree.Payload(0)[8] == 2, "Growing owner did not reach native routed representation.");
                    RequireComponent(tree.Scan().ToHashSet().SetEquals(expected), "Live sparse/promoted oracle mismatch.");
                    session.FlushFileToDisk(); finalBytes = new FileInfo(path).Length;
                }
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    var tree = new SparsePromotionFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, packed);
                    var watch = Stopwatch.StartNew(); var rows = tree.Scan(); scanMs = watch.Elapsed.TotalMilliseconds;
                    RequireComponent(rows.Count == expected.Count && rows.ToHashSet().SetEquals(expected), "Reopen ownership/oracle mismatch.");
                    RequireComponent(session.IndexDirectory.ActiveSlots.Length == 1, "Unexpected child catalog slot.");
                }
                reports.Add(new { packed, flushPerCommit = flush, owners = 512, tuples = expected.Count, sparseBytes, finalBytes, buildMs, growthMs, reopenedScanMs = scanMs, ownershipAndReopenPassed = true, duplicateFailure });
                Console.WriteLine($"{(duplicateFailure is null ? "PASS" : "PARTIAL")} sparse packed={packed} flush={flush} bytes={sparseBytes} build={buildMs:F2}ms growth={growthMs:F2}ms final={finalBytes}");
                passed = true;
            }
            catch (Exception ex)
            {
                hasFailures = true;
                reports.Add(new { packed, flushPerCommit = flush, failed = true, detail = ex.ToString(), fixture = path });
                Console.WriteLine("FAIL " + ex);
            }
            finally { if (passed && File.Exists(path)) File.Delete(path); }
        }
        string report = Path.Combine(folder, "report.json");
        var nativeBoundary = CheckNativeSparseBoundary(folder);
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = !hasFailures, scope = "Harness only. One B per A; scalar C/id. Existing VV packing, SS8 shelf, native sorted promotion and routed mutations. Multi-commit publication; no crash/concurrency proof. No production edits.", reports, nativeBoundary }));
        Console.WriteLine("RESULT component-sparse-promotion-probe " + report); return hasFailures ? 1 : 0;
    }

    /// <summary>Separates native full-shelf duplicate/new-identity behavior from all sparse descriptor and promotion logic.<br/>
    /// A normal catalogued root is populated either incrementally or by the existing sorted builder, then mutated identically.<br/></summary>
    private static List<object> CheckNativeSparseBoundary(string folder)
    {
        var results = new List<object>();
        foreach (bool bulk in new[] { false, true })
        foreach (bool duplicate in new[] { true, false })
        {
            string path = Path.Combine(folder, $"native-{bulk}-{duplicate}.lbdx");
            using (var session = LibraDexFileSession.Initialize(path, CreateDesignPerfOptions(), CreateDesignPerfMetadata(9623), DataKernelTelemetryOptions.EnabledOptions))
            {
                var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "native-control", 0));
                var profile = Scalar8Scalar8Profile.Default4KiB;
                var rows = new List<Scalar8Scalar8SortedTuple> { new(175, 1) };
                for (ulong i = 0; i < 2000; i++) rows.Add(new(i, i + 10000));
                rows.Sort((x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Identity.CompareTo(y.Identity));
                if (bulk) session.BuildScalar8Scalar8FromSorted(top.Offset, profile, rows.ToArray(), true, false, CancellationToken.None);
                else foreach (var row in rows) session.InsertWalkedRoutedScalar8Scalar8(top.Offset, profile, row.Key, row.Identity, true, 64);
                string? failure = null;
                var target = session.WalkScalar8Scalar8RoutePathTarget(top.Offset, 175, 64, Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
                var kernel = (DataKernel)typeof(LibraDexFileSession).GetField("kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                byte[] bytes = new byte[4096]; kernel.Read(target.Target.Offset, bytes); var shelf = new Scalar8Scalar8ReadOnly(bytes, profile);
                int count = shelf.ItemCount; ulong first = shelf.ReadKeyAt(0), last = shelf.ReadKeyAt(count - 1);
                byte[] beforeRetry = ReadSparseBacking(path);
                try
                {
                    var result = session.InsertWalkedRoutedScalar8Scalar8(top.Offset, profile, 175, duplicate ? 10175UL : 999999UL, true, 64);
                    RequireComponent(result.InsertResult == (duplicate ? Scalar8Scalar8InsertResult.AlreadyPresent : Scalar8Scalar8InsertResult.Inserted), "Native result mismatch.");
                    if (duplicate) RequireComponent(result.Commit.BytesWritten == 0 && ReadSparseBacking(path).AsSpan().SequenceEqual(beforeRetry), "Native exact retry wrote bytes.");
                }
                catch (InvalidDataException ex) { failure = ex.Message; }
                RequireComponent(failure is null, "Native boundary regression: " + failure);
                results.Add(new { bulk, duplicate, count, capacity = profile.MaxItemCount, first, last, depth = target.Target.RouterDepth, failure });
                Console.WriteLine($"NATIVE bulk={bulk} duplicate={duplicate} shelf={count}/{profile.MaxItemCount} keys={first}..{last} depth={target.Target.RouterDepth} error={failure ?? "none"}");
            }
            // The small JSON scenario is sufficient to reproduce this deterministic diagnostic.
            File.Delete(path);
        }
        return results;
    }

    /// <summary>Diagnostic owner adapter with bounded inline tuples, direct shelves, and native routed children.<br/>
    /// The persisted parent value supplies B and a representation tag, never a concatenated full composite key.<br/></summary>
    private sealed class SparsePromotionFixture
    {
        private readonly LibraDexFileSession _session;
        private readonly DataKernel _kernel;
        private readonly long _top;
        private readonly bool _packed;
        private bool _deferCommits;
        private readonly Scalar8Scalar8Profile _profile = Scalar8Scalar8Profile.Default4KiB;

        /// <summary>Binds physical primitives once; reflection only accesses the diagnostic kernel during setup.<br/></summary>
        internal SparsePromotionFixture(LibraDexFileSession session, long top, bool packed)
        {
            _session = session; _top = top; _packed = packed;
            _kernel = (DataKernel)typeof(LibraDexFileSession).GetField("kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        }

        /// <summary>Returns physical allocator accounting for independent normal-versus-recovered publication comparison.<br/></summary>
        internal FileAllocationStorageSnapshot Allocations => _kernel.GetFileAllocationStorageSnapshot();

        /// <summary>Encodes only the owning A component for the shared VV directory.<br/></summary>
        private static byte[] Key(ulong a) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, a); return bytes; }

        /// <summary>Reads a unique persisted owner descriptor without a process-local map.<br/></summary>
        internal byte[] Payload(ulong a)
        {
            byte[] key = Key(a);
            using var reader = _session.OpenVarKeyVarIdentityRangeReader(_top, 32, 256, key, key, maxRouterHops: 64);
            if (!reader.MoveNext()) return Array.Empty<byte>();
            byte[] value = reader.CurrentIdentity.ToArray();
            RequireComponent(!reader.MoveNext(), "Ambiguous owner descriptor."); return value;
        }

        /// <summary>Publishes a diagnostic owner descriptor through existing exact delete and insert primitives.<br/>
        /// Separate commits leave a known crash window; production publication must close it before adopting this representation.<br/></summary>
        private void Publish(ulong a, byte[] old, byte[] value)
        {
            byte[] key = Key(a);
            if (old.Length != 0) RequireComponent(_session.DeleteVarKeyVarIdentityExactTuple(_top, 32, 256, key, old, 64), "Descriptor delete failed.");
            _session.InsertWalkedRoutedVarKeyVarIdentity(_top, 32, 256, key, value, true, 64);
            RequireComponent(Payload(a).AsSpan().SequenceEqual(value), "Descriptor publication failed.");
        }

        /// <summary>Reads the bounded inline list or existing SS8 shelf/root using its native reader.<br/></summary>
        private List<Scalar8Scalar8SortedTuple> Tuples(byte[] payload)
        {
            var rows = new List<Scalar8Scalar8SortedTuple>();
            if (payload.Length == 0) return rows;
            RequireComponent(BinaryPrimitives.ReadUInt64LittleEndian(payload) == 7, "B ownership lost.");
            if (payload[8] == 0)
            {
                for (int i = 9; i < payload.Length; i += 16) rows.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(i)), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(i + 8))));
            }
            else
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(9));
                if (payload[8] == 1)
                {
                    byte[] bytes = new byte[4096]; _kernel.Read(offset, bytes); var shelf = new Scalar8Scalar8ReadOnly(bytes, _profile);
                    RequireComponent(shelf.IsValid, "Invalid direct shelf.");
                    for (int i = 0; i < shelf.ItemCount; i++) rows.Add(new(shelf.ReadKeyAt(i), shelf.ReadIdentityAt(i)));
                }
                else
                {
                    using var reader = _session.OpenScalar8Scalar8RangeReader(offset, _profile, 0, ulong.MaxValue, QueryDirection.Ascending, 64);
                    while (reader.MoveNext()) rows.Add(new(reader.CurrentEncodedKey, reader.CurrentEncodedIdentity));
                }
            }
            return rows;
        }

        /// <summary>Exercises bounded inline growth, native direct-shelf insertion, and native sorted root promotion.<br/>
        /// Retirement follows descriptor publication here; crash recovery and coordinated reader ownership remain integration work.<br/></summary>
        internal void Add(ulong a, ulong c, ulong id)
        {
            byte[] old = Payload(a);
            if (old.Length != 0 && old[8] == 2)
            {
                _session.InsertWalkedRoutedScalar8Scalar8(BinaryPrimitives.ReadInt64LittleEndian(old.AsSpan(9)), _profile, c, id, true, 64); return;
            }
            if (old.Length != 0 && old[8] == 1)
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(old.AsSpan(9));
                byte[] bytes = new byte[4096]; _kernel.Read(offset, bytes); var shelf = new Scalar8Scalar8(bytes, _profile);
                var result = shelf.Insert(c, id, true);
                if (result == Scalar8Scalar8InsertResult.AlreadyPresent) return;
                if (result == Scalar8Scalar8InsertResult.Inserted) { _kernel.StageWriteAt(offset, bytes); if (!_deferCommits) _kernel.Commit(); return; }
                RequireComponent(result == Scalar8Scalar8InsertResult.Full, "Unexpected direct insert result.");
            }
            var rows = Tuples(old);
            if (rows.Contains(new Scalar8Scalar8SortedTuple(c, id))) return;
            rows.Add(new(c, id)); rows.Sort((x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Identity.CompareTo(y.Identity));
            byte[] value;
            if (_packed && rows.Count <= 4)
            {
                value = new byte[9 + rows.Count * 16];
                for (int i = 0; i < rows.Count; i++) { BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(9 + i * 16), rows[i].Key); BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(17 + i * 16), rows[i].Identity); }
            }
            else
            {
                bool routed = rows.Count > _profile.MaxItemCount;
                var reservation = _kernel.Reserve(4096); long offset = reservation.Extent.Offset;
                if (routed) new RouterWriter(reservation.Span).InitializeRoot(1);
                else
                {
                    var shelf = new Scalar8Scalar8(reservation.Span, _profile); shelf.Initialize();
                    foreach (var row in rows) RequireComponent(shelf.Insert(row.Key, row.Identity, true) == Scalar8Scalar8InsertResult.Inserted, "Direct shelf setup failed.");
                }
                if (!_deferCommits) _kernel.Commit();
                if (routed)
                {
                    if (_deferCommits)
                    {
                        // The standalone sorted builder owns its commit boundary. This bounded 226-tuple
                        // promotion uses existing routed inserts inside the caller's batch instead.
                        foreach (var row in rows)
                            RequireComponent(_session.InsertWalkedRoutedScalar8Scalar8(offset, _profile, row.Key, row.Identity, true, 64).InsertResult == Scalar8Scalar8InsertResult.Inserted, "Staged native promotion failed.");
                    }
                    else _session.BuildScalar8Scalar8FromSorted(offset, _profile, rows.ToArray(), true, false, CancellationToken.None);
                }
                value = new byte[17]; value[8] = routed ? (byte)2 : (byte)1; BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(9), offset);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(value, 7); Publish(a, old, value);
            if (old.Length != 0 && old[8] == 1) { _kernel.StageExtentRetirement(BinaryPrimitives.ReadInt64LittleEndian(old.AsSpan(9)), 4096); if (!_deferCommits) _kernel.Commit(); }
        }

        /// <summary>Stages one diagnostic promotion using the existing durability-batch boundary and optionally aborts it.<br/>
        /// This tests coordinated staging only; durability batching is not assumed to provide torn-write recovery or transaction isolation.<br/></summary>
        internal (long BytesWritten, long DeferredRequests, long JournalBytes) AddStaged(ulong a, ulong c, ulong id, bool abort, bool recoverable = false)
        {
            using var batch = _session.BeginDurabilityBatch();
            _deferCommits = true;
            try
            {
                Add(a, c, id);
                if (abort) return (0, batch.Abort(), 0);
                var result = recoverable ? batch.CommitRecoverablePublication() : batch.Commit();
                return (result.Commit.BytesWritten, result.DeferredCommitRequests, recoverable ? _kernel.LastPublicationJournalBytes : 0);
            }
            finally { _deferCommits = false; }
        }

        /// <summary>Injects a diagnostic interruption after one fully written/flushed kernel phase.<br/>
        /// It deliberately does not model torn sectors or interruption between writes within the same phase.<br/></summary>
        internal void InterruptAfter(PendingSegmentPhase phase)
            => _kernel.FileCommitPhaseCompleted = completed =>
            {
                if (completed == phase) throw new InvalidOperationException("Injected component promotion phase interruption.");
            };

        /// <summary>Reconstructs every owner from persisted storage for an independent identity-set oracle.<br/></summary>
        internal List<(ulong A, ulong C, ulong Id)> Scan()
        {
            var rows = new List<(ulong A, ulong C, ulong Id)>();
            for (ulong a = 0; a < 512; a++) foreach (var row in Tuples(Payload(a))) rows.Add((a, row.Key, row.Identity));
            return rows;
        }
    }

    /// <summary>Tests pre-commit abort and committed reopen across both representation promotions.<br/>
    /// Uses one explicit owner and independent persisted oracles, not an assertion that batches are crash-atomic transactions.<br/></summary>
    private static int RunComponentPromotionPublicationProbe(string[] args)
    {
        string folder = Path.GetFullPath(Path.Combine("artifacts", "promotion-publication-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder); var reports = new List<object>(); bool allPassed = true;
        bool recoverable = !args.Contains("--legacy-publication");
        var expectedAllocations = new Dictionary<int, FileAllocationStorageSnapshot>();
        foreach (int count in new[] { 4, (int)Scalar8Scalar8Profile.Default4KiB.MaxItemCount })
        {
            string path = Path.Combine(folder, $"promotion-{count}.lbdx"); bool passed = false;
            try
            {
                var options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = true };
                using (var session = LibraDexFileSession.Initialize(
                    path,
                    options,
                    CreateDesignPerfMetadata(9624),
                    DataKernelTelemetryOptions.EnabledOptions,
                    recoverable ? SuperblockLayout.RecoverableFormatVersion : SuperblockLayout.LegacyFormatVersion))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "publication", 0));
                    var tree = new SparsePromotionFixture(session, top.Offset, true);
                    for (ulong i = 0; i < (ulong)count; i++) tree.Add(0, i, i + 1);
                    session.FlushFileToDisk(); byte[] before = ReadSparseBacking(path);
                    tree.AddStaged(0, (ulong)count, (ulong)count + 1, true, recoverable);
                    RequireComponent(ReadSparseBacking(path).AsSpan().SequenceEqual(before), "Aborted promotion changed durable bytes.");
                    RequireComponent(tree.Scan().Count == count, "Abort did not restore visible owner.");
                    var watch = Stopwatch.StartNew();
                    var committed = tree.AddStaged(0, (ulong)count, (ulong)count + 1, false, recoverable);
                    double elapsedMs = watch.Elapsed.TotalMilliseconds;
                    reports.Add(new { initialCount = count, mainFileBytes = committed.BytesWritten, journalBytes = committed.JournalBytes, receiptBytes = recoverable ? 40 : 0, deferredRequests = committed.DeferredRequests, elapsedMs });
                    Console.WriteLine($"staged promotion {count} bytes={committed.BytesWritten} journalBytes={committed.JournalBytes} deferred={committed.DeferredRequests} elapsedMs={elapsedMs:F3}");
                    RequireComponent(tree.Scan().Count == count + 1, "Committed promotion count mismatch.");
                }
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    var tree = new SparsePromotionFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, true);
                    var rows = tree.Scan();
                    RequireComponent(rows.Count == count + 1 && rows.All(r => r.A == 0 && r.C < (ulong)count + 1 && r.Id == r.C + 1), "Committed promotion reopen mismatch.");
                    expectedAllocations[count] = tree.Allocations;
                }
                reports.Add(new { initialCount = count, abortAndCommitReopen = true }); passed = true;
                Console.WriteLine($"PASS staged promotion {count}->{count + 1}");
            }
            catch (Exception ex) { reports.Add(new { initialCount = count, failure = ex.ToString() }); allPassed = false; Console.WriteLine(ex); }
            finally { if (passed && File.Exists(path)) File.Delete(path); }
        }
        bool testInterruptions = !args.Contains("--commit-only");
        if (testInterruptions) allPassed &= CheckPromotionPhaseInterruptions(folder, reports, recoverable, expectedAllocations);
        string report = Path.Combine(folder, "report.json"); File.WriteAllText(report, JsonSerializer.Serialize(new { passed = allPassed, recoverable, phaseInterruptionsTested = testInterruptions, tornWritesTested = false, reports }));
        Console.WriteLine(report); return allPassed ? 0 : 1;
    }

    /// <summary>Reads committed fixture bytes through an independent handle that permits the session's existing writer.<br/>
    /// Unlike a kernel overlay read, this cannot accidentally count pending bytes as durable evidence.<br/></summary>
    private static byte[] ReadSparseBacking(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }

    /// <summary>Reopens interrupted promotions at existing kernel phase boundaries and checks exact old/new ownership.<br/>
    /// This is a bounded durability test, not a claim of arbitrary-crash atomicity for multi-write publication.<br/></summary>
    private static bool CheckPromotionPhaseInterruptions(string folder, List<object> reports, bool recoverable, Dictionary<int, FileAllocationStorageSnapshot> expectedAllocations)
    {
        bool allPassed = true;
        foreach (int count in new[] { 4, (int)Scalar8Scalar8Profile.Default4KiB.MaxItemCount })
        foreach (var phase in new[] { PendingSegmentPhase.Payload, PendingSegmentPhase.Publication, PendingSegmentPhase.Retirement })
        {
            if (count == 4 && phase == PendingSegmentPhase.Retirement) continue;
            string path = Path.Combine(folder, $"interrupt-{count}-{phase}.lbdx"); bool passed = false;
            try
            {
                var options = CreateDesignPerfOptions() with { FlushToDiskOnCommit = true };
                bool interrupted = false;
                using (var session = LibraDexFileSession.Initialize(
                    path,
                    options,
                    CreateDesignPerfMetadata(9625),
                    DataKernelTelemetryOptions.EnabledOptions,
                    recoverable ? SuperblockLayout.RecoverableFormatVersion : SuperblockLayout.LegacyFormatVersion))
                {
                    var (top, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "interruption", 0));
                    var tree = new SparsePromotionFixture(session, top.Offset, true);
                    for (ulong i = 0; i < (ulong)count; i++) tree.Add(0, i, i + 1);
                    tree.InterruptAfter(phase);
                    try { tree.AddStaged(0, (ulong)count, (ulong)count + 1, false, recoverable); }
                    catch (InvalidOperationException ex) when (ex.Message == "Injected component promotion phase interruption.") { interrupted = true; }
                }
                // Some ordinary shelf rewrites currently use Payload, so a promotion may have no
                // separate Publication phase. Atomicity requires a complete old OR new owner, not a phase-name assumption.
                int expected = count + 1;
                using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
                {
                    var tree = new SparsePromotionFixture(session, session.IndexDirectory.ActiveSlots[0].RootRouterOffset, true);
                    var rows = tree.Scan();
                    if (interrupted && phase == PendingSegmentPhase.Payload && rows.Count == count) expected = count;
                    RequireComponent(rows.Count == expected && rows.Distinct().Count() == expected && rows.All(r => r.A == 0 && r.C < (ulong)expected && r.Id == r.C + 1), $"Interrupted promotion exposed incomplete ownership: phase={phase}, before={count}, actual={rows.Count}, expected old={count} or new={count + 1}.");
                    if (recoverable) RequireComponent(tree.Allocations == expectedAllocations[count], "Recovered allocation/retirement accounting differs from normal committed promotion.");
                }
                reports.Add(new { initialCount = count, requestedPhase = phase.ToString(), interruptionReached = interrupted, expectedCount = expected, passed = true });
                Console.WriteLine($"PASS promotion {count} phase={phase} interrupted={interrupted} reopened={expected}"); passed = true;
            }
            catch (Exception ex) { reports.Add(new { initialCount = count, interruptedPhase = phase.ToString(), failure = ex.ToString() }); allPassed = false; Console.WriteLine(ex); }
            finally { if (passed && File.Exists(path)) File.Delete(path); }
        }
        return allPassed;
    }
}
