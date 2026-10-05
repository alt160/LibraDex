using System.Diagnostics;
using LibraDex;

/// <summary>Focused scalar All stream parity, early-stop, and allocation acceptance.<br/></summary>
internal static class ScalarAllStreamingSanity
{
    /// <summary>
    /// Tests memory catalogs at small, quarter-million, and million tuple scales.<br/>
    /// Native sorted construction keeps setup outside measured traversal; catalogs are disposed per size.<br/>
    /// </summary>
    /// <returns>Zero when every invariant passes; failures throw with a precise assertion.<br/></returns>
    internal static int Run()
    {
        foreach (int count in new[] { 4_000, 250_000, 1_000_000 })
        foreach (bool oneKey in new[] { false, true })
        {
            using var catalog = Catalog.CreateMemory();
            using var index = catalog.Indexes.IndexSet("stream").Define("values").Create<long, ulong>();
            var tuples = new LibraDexSortedTuple<long, ulong>[count];
            for (int i = 0; i < count; i++) tuples[i] = new(oneKey ? 0 : i / 2, (ulong)i + 1);
            index.BuildFromSorted(tuples);
            var stream = (IIdentityPrimitiveTupleStreamer)index;
            var request = new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>());
            foreach (QueryDirection direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                var directed = request with { Direction = direction };
                if (stream.IterateTuplePrimitive(directed with { TakeLimit = 0 }).Any()) throw new Exception("Take(0).");
                // Warm the generic iterator and reader before comparing allocations.
                _ = stream.IterateTuplePrimitive(directed).Take(1).Single();
                long start = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                var first = stream.IterateTuplePrimitive(directed).Take(1).Single();
                timer.Stop();
                long firstAlloc = GC.GetAllocatedBytesForCurrentThread() - start;
                if ((long)first.Key! != (direction == QueryDirection.Ascending || oneKey ? 0 : (count - 1) / 2)) throw new Exception("First key.");
                start = GC.GetAllocatedBytesForCurrentThread();
                long visited = 0;
                long prior = direction == QueryDirection.Ascending ? long.MinValue : long.MaxValue;
                foreach (var tuple in stream.IterateTuplePrimitive(directed))
                {
                    long key = (long)tuple.Key!;
                    if (direction == QueryDirection.Ascending ? key < prior : key > prior) throw new Exception("Order.");
                    prior = key;
                    visited++;
                }
                long fullAlloc = GC.GetAllocatedBytesForCurrentThread() - start;
                if (visited != count || (count >= 250_000 && firstAlloc >= fullAlloc / 4)) throw new Exception("Eager All or missing tuples.");
                Console.WriteLine($"PASS All {count:N0} {direction} {(oneKey ? "one-key" : "many-keys")}: first {timer.Elapsed.TotalMilliseconds:F3} ms / {firstAlloc:N0} B; full {fullAlloc:N0} B; rows {visited:N0}");
                if (stream.IterateTuplePrimitive(directed with { TakeLimit = 1 }).Count() != 1) throw new Exception("Take(1).");
            }
            index.Insert(ScalarNull.Null, (ulong)count + 1);
            var all = stream.IterateTuplePrimitive(request).ToList();
            if (all.Count != count + 1 || all[0].Key is not null) throw new Exception("Null route.");
            try
            {
                foreach (var ignored in stream.IterateTuplePrimitive(request)) throw new OperationCanceledException();
            }
            catch (OperationCanceledException) { }
            if (!stream.IterateTuplePrimitive(request).Any()) throw new Exception("Reader reuse after consumer failure.");
            try { _ = stream.IterateTuplePrimitive(request with { TakeLimit = -1 }).Any(); throw new Exception("Negative limit accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        CheckPromotedNullRoutes();
        CheckReadyMapAndBinaryRoutes();
        CheckNullPrimitiveTupleStreaming();
        CheckScanPredicateTupleStreaming();
        CheckPreparedObjectSetSnapshot();
        return 0;
    }

    /// <summary>
    /// Tests promoted scalar-eight and scalar-sixteen null routes through the same All iterator.<br/>
    /// Both full parity and Take(1) allocation are checked after route promotion, not just inline storage.<br/>
    /// </summary>
    private static void CheckPromotedNullRoutes()
    {
        using var catalog = Catalog.CreateMemory();
        using var scalar8 = catalog.Indexes.IndexSet("nulls").Define("scalar8").Create<int, ulong>();
        using var scalar16 = catalog.Indexes.IndexSet("nulls").Define("scalar16").Create<int, Guid>();
        for (int i = 1; i <= 4000; i++)
        {
            scalar8.Insert(ScalarNull.Null, (ulong)i);
            scalar16.Insert(ScalarNull.Null, new Guid(i, 0, 0, new byte[8]));
        }
        var request = new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>());
        foreach (var source in new IIdentityPrimitiveTupleStreamer[] { scalar8, scalar16 })
        {
            _ = source.IterateTuplePrimitive(request).Take(1).Single();
            long start = GC.GetAllocatedBytesForCurrentThread();
            if (source.IterateTuplePrimitive(request with { TakeLimit = 1 }).Single().Key is not null) throw new Exception("Promoted null key.");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            var rows = source.IterateTuplePrimitive(request).ToList();
            if (rows.Count != 4000 || rows.Any(r => r.Key is not null) || rows.Select(r => r.Identity).Distinct().Count() != 4000)
                throw new Exception("Promoted null identity parity.");
            if (allocated > 32768) throw new Exception("Promoted null first-result materialization.");
            Console.WriteLine($"PASS promoted null {source.GetType().Name}: 4000 identities; first {allocated:N0} B");
        }
    }

    /// <summary>
    /// Checks readiness refusal after invalidation, explicit map reinitialization, and promoted binary key-state boundaries.<br/>
    /// Keeps the metadata Count terminal out of this traversal-parity test because its separate promotion regression is tracked.<br/>
    /// </summary>
    private static void CheckReadyMapAndBinaryRoutes()
    {
        using var catalog = Catalog.CreateMemory();
        using var index = catalog.Indexes.IndexSet("ready").Define("values").Int64Keys<ulong>().Create(
            options: new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
        index.Insert(3, 1);
        index.Insert(7, 2);
        long sum = 0;
        if (!index.TryVisitReadySingleKeys(new ulong[] { 2, 1, 99 }, key => sum += key, out long matched) || sum != 10 || matched != 2)
            throw new Exception("Ready map missing-key parity.");
        index.InvalidateSingleKeyIdentityMapAfterStagedPublication();
        if (index.TryVisitReadySingleKeys(new ulong[] { 1 }, _ => throw new Exception("Visitor invoked on cold map."), out _))
            throw new Exception("Invalidated map was silently initialized.");
        if (!index.TryGetSingleKey(1, out long key) || key != 3 ||
            !index.TryVisitReadySingleKeys(new ulong[] { 1 }, _ => { }, out matched) || matched != 1)
            throw new Exception("Explicit map initialization failed.");
        if (!LibraDexTupleEqualityComparer<byte[]>.Instance.Equals(new byte[] { 1, 2 }, new byte[] { 1, 2 }) ||
            LibraDexTupleEqualityComparer<ulong>.Instance.Equals(1, 2)) throw new Exception("Typed comparer semantics.");

        using var binary = catalog.Indexes.IndexSet("binary").Define("values").Blob.Scalar<ulong>(LibraDexScalarWidth.Bytes32).Create();
        for (ulong i = 1; i <= 1024; i++)
        {
            binary.Insert(NullKey.Null, i);
            binary.Insert(NullKey.Empty, i + 1024);
        }
        binary.Insert(new byte[32], 2049);
        var request = new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>());
        var source = (IIdentityPrimitiveTupleStreamer)binary;
        foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
        {
            var rows = source.IterateTuplePrimitive(request with { Direction = direction }).ToList();
            if (rows.Count != 2049 || rows[0].Key is not null || rows[1024].Key is not byte[] { Length: 0 } ||
                rows[^1].Key is not byte[] { Length: 32 } || rows.Select(r => r.Identity).Distinct().Count() != 2049)
                throw new Exception("Binary null/empty/ordinary route parity.");
            if (source.IterateTuplePrimitive(request with { Direction = direction, TakeLimit = 1025 }).Count() != 1025)
                throw new Exception("Cross-route Take boundary.");
        }
        Console.WriteLine("PASS ready-map invalidation/no implicit initialization, scalar/content equality, promoted binary null/empty All boundaries.");
    }

    /// <summary>Verifies that scalar-null and binary key-state tuple primitives yield their first row without capturing a promoted duplicate run.<br/>
    /// The test also checks full membership, null-before-empty ordering, and Take boundaries so changing a closed executor to a lazy iterator cannot silently alter results.<br/>
    /// Allocation is measured after one warm call and bounded independently of the total number of matching identities.<br/></summary>
    private static void CheckNullPrimitiveTupleStreaming()
    {
        const int perRoute = 4000;
        using var catalog = Catalog.CreateMemory();
        using var scalar = catalog.Indexes.IndexSet("null-stream").Define("scalar").Create<int, ulong>();
        using var binary = catalog.Indexes.IndexSet("null-stream").Define("binary").Blob.Scalar<ulong>(LibraDexScalarWidth.Bytes32).Create();
        for (int i = 1; i <= perRoute; i++)
        {
            scalar.Insert(ScalarNull.Null, (ulong)i);
            binary.Insert(NullKey.Null, (ulong)i);
            binary.Insert(NullKey.Empty, (ulong)(perRoute + i));
        }

        var scalarStream = (IIdentityPrimitiveTupleStreamer)scalar;
        var scalarRequest = new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.ScalarNull, new object?[] { ScalarNull.Null });
        _ = scalarStream.IterateTuplePrimitive(scalarRequest with { TakeLimit = 1 }).Single();
        long start = GC.GetAllocatedBytesForCurrentThread();
        LibraDexObjectTuple scalarFirst = scalarStream.IterateTuplePrimitive(scalarRequest with { TakeLimit = 1 }).Single();
        long scalarFirstBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        if (scalarFirst.Key is not null || scalarFirstBytes > 65536 ||
            scalarStream.IterateTuplePrimitive(scalarRequest).Count() != perRoute)
            throw new InvalidDataException($"Scalar-null tuple streaming failed: first={scalarFirstBytes:N0} B.");

        var binaryStream = (IIdentityPrimitiveTupleStreamer)binary;
        var binaryRequest = new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.KeyState, new object?[] { NullKey.NullOrEmpty });
        _ = binaryStream.IterateTuplePrimitive(binaryRequest with { TakeLimit = 1 }).Single();
        start = GC.GetAllocatedBytesForCurrentThread();
        LibraDexObjectTuple binaryFirst = binaryStream.IterateTuplePrimitive(binaryRequest with { TakeLimit = 1 }).Single();
        long binaryFirstBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        var rows = binaryStream.IterateTuplePrimitive(binaryRequest).ToArray();
        if (binaryFirst.Key is not null || binaryFirstBytes > 65536 || rows.Length != perRoute * 2 ||
            rows[perRoute - 1].Key is not null || rows[perRoute].Key is not byte[] { Length: 0 } ||
            binaryStream.IterateTuplePrimitive(binaryRequest with { TakeLimit = perRoute + 1 }).Count() != perRoute + 1)
            throw new InvalidDataException($"Binary key-state tuple streaming failed: first={binaryFirstBytes:N0} B, rows={rows.Length}.");

        Console.WriteLine($"PASS null primitive streams: scalar first {scalarFirstBytes:N0} B, key-state first {binaryFirstBytes:N0} B, rows {perRoute:N0}/{rows.Length:N0}.");
    }

    /// <summary>Verifies that bitmask and numeric-transform tuple scans honor direction and Take without retaining all matched rows.<br/>
    /// Both predicates match many stored keys, making an eager closed-executor fallback visible in first-result allocations.<br/></summary>
    private static void CheckScanPredicateTupleStreaming()
    {
        const int count = 4000;
        using var catalog = Catalog.CreateMemory();
        using var flags = catalog.Indexes.IndexSet("predicate-stream").Define("flags").Create<long, ulong>();
        using var numbers = catalog.Indexes.IndexSet("predicate-stream").Define("numbers").Create<double, ulong>();
        flags.BuildFromSorted(Enumerable.Range(0, count).Select(i => new LibraDexSortedTuple<long, ulong>(i, (ulong)i + 1)).ToArray());
        numbers.BuildFromSorted(Enumerable.Range(0, count).Select(i => new LibraDexSortedTuple<double, ulong>(i + 0.25, (ulong)i + 1)).ToArray());

        var bitmask = LibraDexBitmaskPredicate.Create(typeof(long), 1L, 1L, LibraDexBitmaskComparisonMode.EqualTo);
        var transform = new LibraDexNumericTransformPredicate(typeof(double),
            new LibraDexNumericTransformDescriptor(LibraDexNumericTransformKind.Floor, 0, MidpointRounding.ToEven),
            LibraDexConditionOperatorKind.GreaterOrEqual, new object?[] { 0.0 });
        var cases = new[]
        {
            (Stream: (IIdentityPrimitiveTupleStreamer)flags,
                Request: new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Bitmask, new object?[] { bitmask }),
                ExpectedCount: count / 2, First: 1.0, Last: 3999.0),
            (Stream: (IIdentityPrimitiveTupleStreamer)numbers,
                Request: new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.NumericTransform, new object?[] { transform }),
                ExpectedCount: count, First: 0.25, Last: 3999.25)
        };
        foreach (var test in cases)
        foreach (QueryDirection direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
        {
            var request = test.Request with { Direction = direction };
            _ = test.Stream.IterateTuplePrimitive(request with { TakeLimit = 1 }).Single();
            long start = GC.GetAllocatedBytesForCurrentThread();
            LibraDexObjectTuple first = test.Stream.IterateTuplePrimitive(request with { TakeLimit = 1 }).Single();
            long firstBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            double expectedFirst = direction == QueryDirection.Ascending ? test.First : test.Last;
            if (Convert.ToDouble(first.Key) != expectedFirst || firstBytes > 65536 ||
                test.Stream.IterateTuplePrimitive(request).Count() != test.ExpectedCount ||
                test.Stream.IterateTuplePrimitive(request with { TakeLimit = 3 }).Count() != 3)
                throw new InvalidDataException($"{request.CriteriaKind} tuple streaming failed: {direction}, first={first.Key}, allocated={firstBytes:N0} B.");
            Console.WriteLine($"PASS {request.CriteriaKind} {direction}: first {firstBytes:N0} B; rows {test.ExpectedCount:N0}.");
        }
    }

    /// <summary>Checks that a lazy prepared-set source is enumerated once and published as one stable value snapshot.<br/>
    /// Concurrent consumers must observe the same list instance without replacing the caller's original Source and comparer metadata.<br/></summary>
    private static void CheckPreparedObjectSetSnapshot()
    {
        int enumerations = 0;
        IEnumerable<object> Source()
        {
            Interlocked.Increment(ref enumerations);
            yield return 3;
            yield return 5;
        }

        var prepared = new LibraDexPreparedObjectSet(typeof(int), null, Source());
        IReadOnlyList<object>[] snapshots = new IReadOnlyList<object>[16];
        Parallel.For(0, snapshots.Length, i => snapshots[i] = prepared.Values);
        if (enumerations != 1 || snapshots.Any(snapshot => !ReferenceEquals(snapshot, snapshots[0])) ||
            snapshots[0].Count != 2 || !ReferenceEquals(prepared.Values, snapshots[0]))
            throw new InvalidDataException($"Prepared object set enumerated its source {enumerations} times.");
        Console.WriteLine("PASS prepared object set snapshot: one source enumeration across 16 concurrent reads.");
    }
}
