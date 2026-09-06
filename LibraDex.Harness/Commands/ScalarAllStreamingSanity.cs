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
            using var index = catalog.Indexes["stream"]["values"].Create<long, ulong>();
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
        return 0;
    }

    /// <summary>
    /// Tests promoted scalar-eight and scalar-sixteen null routes through the same All iterator.<br/>
    /// Both full parity and Take(1) allocation are checked after route promotion, not just inline storage.<br/>
    /// </summary>
    private static void CheckPromotedNullRoutes()
    {
        using var catalog = Catalog.CreateMemory();
        using var scalar8 = catalog.Indexes["nulls"]["scalar8"].Create<int, ulong>();
        using var scalar16 = catalog.Indexes["nulls"]["scalar16"].Create<int, Guid>();
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
        using var index = catalog.Indexes["ready"]["values"].Int64Keys<ulong>().Create(
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

        using var binary = catalog.Indexes["binary"]["values"].Blob.Scalar<ulong>(LibraDexScalarWidth.Bytes32).Create();
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
}
