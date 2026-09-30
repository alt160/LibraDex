using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>Checks full-shelf classification without publication side effects across all eight fixed scalar shapes.<br/>
    /// Perf mode measures the unchanged non-full sorted/reverse paths in repeated batches; it is diagnostic, not a throughput SLA.<br/></summary>
    private static int RunFullShelfClassificationProbe(string[] args)
    {
        if (args.Contains("perf"))
        {
            for (int round = 0; round < 7; round++)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Scalar8Scalar8", orderedMs = MeasureScalar8Scalar8Classification(false), reverseMs = MeasureScalar8Scalar8Classification(true) }));
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Scalar16Scalar8", orderedMs = MeasureScalar16Scalar8Classification(false), reverseMs = MeasureScalar16Scalar8Classification(true) }));
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Scalar8Scalar16", orderedMs = MeasureScalar8Scalar16Classification(false), reverseMs = MeasureScalar8Scalar16Classification(true) }));
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Scalar16Scalar16", orderedMs = MeasureScalar16Scalar16Classification(false), reverseMs = MeasureScalar16Scalar16Classification(true) }));
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Fixed32Scalar8", orderedMs = MeasureFixed32Scalar8Classification(false), reverseMs = MeasureFixed32Scalar8Classification(true) }));
                Console.WriteLine(JsonSerializer.Serialize(new { round, shape = "Fixed32Scalar16", orderedMs = MeasureFixed32Scalar16Classification(false), reverseMs = MeasureFixed32Scalar16Classification(true) }));
            }
            return 0;
        }
        int checks = 0;
        for (int shape = 0; shape < 8; shape++)
        foreach (int size in new[] { 4096, 32768 })
        foreach (bool sameKey in new[] { false, true })
        {
            byte[] bytes = new byte[size];
            int capacity = PrepareClassificationShelf(shape, bytes);
            for (int i = 0; i < capacity; i++)
                RequireComponent(InsertClassificationShelf(shape, bytes, sameKey ? 7UL : (ulong)i * 2, (ulong)i + 1, true) == "Inserted", "Fixture fill failed.");
            byte[] before = bytes.ToArray();
            foreach (int position in new[] { 0, capacity / 2, capacity - 1 })
            {
                ulong key = sameKey ? 7UL : (ulong)position * 2;
                foreach (bool allow in new[] { true, false })
                {
                    RequireComponent(InsertClassificationShelf(shape, bytes, key, (ulong)position + 1, allow) == "AlreadyPresent", $"Existing tuple classification shape={shape} size={size} sameKey={sameKey} allow={allow}.");
                    checks++;
                }
                RequireComponent(InsertClassificationShelf(shape, bytes, key, 999999, false) == "KeyConflict", "Unique conflict not classified before capacity.");
                RequireComponent(InsertClassificationShelf(shape, bytes, key, 999999, true) == "Full", "New identity did not request space.");
                checks += 2;
            }
            RequireComponent(InsertClassificationShelf(shape, bytes, 999999, 999999, false) == "Full", "Absent unique key did not request space.");
            RequireComponent(bytes.AsSpan().SequenceEqual(before), "Non-inserting classification changed shelf bytes.");
            checks += 2;
        }
        checks += CheckFullDuplicateRunClassification();
        Console.WriteLine($"PASS full-shelf-classification-probe checks={checks} configurations=32 plus duplicate-run and attributed cases");
        return 0;
    }

    /// <summary>Checks the specialized single-key representation and attributed SS8 path at capacity.<br/>
    /// Existing tuples must not rewrite bytes even with a linked successor; absent identities still defer to the chain owner.<br/></summary>
    private static int CheckFullDuplicateRunClassification()
    {
        var profile = Scalar8Scalar8Profile.Default4KiB;
        byte[] bytes = new byte[4096]; var shelf = new Scalar8Scalar8(bytes, profile); shelf.Initialize();
        for (ulong i = 0; i < profile.MaxItemCount; i++) shelf.Insert(i, i + 1, true);
        var telemetry = default(Scalar8Scalar8BatchInsertAttributionTelemetry);
        byte[] before = bytes.ToArray();
        RequireComponent(shelf.InsertWithAttributionAndMutationBounds(5, 6, true, ref telemetry, out var bounds) == Scalar8Scalar8InsertResult.AlreadyPresent, "Attributed duplicate misclassified.");
        RequireComponent(bounds == default && bytes.AsSpan().SequenceEqual(before), "Attributed duplicate changed bytes or bounds.");
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
            if (shelf.Insert(5, 6, true) != Scalar8Scalar8InsertResult.AlreadyPresent) throw new InvalidDataException("Full retry changed result.");
        RequireComponent(GC.GetAllocatedBytesForCurrentThread() == allocatedBefore, "Full-shelf classification allocated managed memory.");
        shelf.Initialize();
        int capacity = Scalar8Scalar8Layout.GetDuplicateRunCapacity(profile);
        Scalar8Scalar8Layout.WriteFlags(bytes, Scalar8Scalar8Layout.DuplicateRunFlag);
        Scalar8Scalar8Layout.WriteItemCount(bytes, checked((ushort)capacity));
        Scalar8Scalar8Layout.WriteDuplicateRunKey(bytes, 7);
        for (int i = 0; i < capacity; i++) Scalar8Scalar8Layout.WriteDuplicateRunIdentity(bytes, i, (ulong)i + 1);
        foreach (long next in new[] { 0L, 4096L })
        {
            Scalar8Scalar8Layout.WriteDuplicateRunNextOffset(bytes, next); before = bytes.ToArray();
            RequireComponent(shelf.InsertIntoSingleShelfDuplicateRun(7, 1, true) == Scalar8Scalar8InsertResult.AlreadyPresent, "Duplicate-run first retry failed.");
            RequireComponent(shelf.InsertIntoSingleShelfDuplicateRun(7, (ulong)capacity, false) == Scalar8Scalar8InsertResult.AlreadyPresent, "Duplicate-run unique retry failed.");
            RequireComponent(shelf.InsertIntoSingleShelfDuplicateRun(7, 999999, false) == Scalar8Scalar8InsertResult.KeyConflict, "Duplicate-run unique conflict failed.");
            RequireComponent(shelf.InsertIntoSingleShelfDuplicateRun(7, 999999, true) == Scalar8Scalar8InsertResult.Full, "Duplicate-run full/chain fallback failed.");
            RequireComponent(bytes.AsSpan().SequenceEqual(before), "Duplicate-run non-insert mutated bytes.");
        }
        return 13;
    }

    /// <summary>Initializes a physical shelf and returns its capacity; no routing or test-side duplicate suppression is involved.<br/></summary>
    private static int PrepareClassificationShelf(int shape, byte[] bytes)
    {
        switch (shape)
        {
            case 0: { var profile = Scalar8Scalar8Profile.Create(bytes.Length); var shelf = new Scalar8Scalar8(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 1: { var profile = Scalar16Scalar8Profile.Create(bytes.Length); var shelf = new Scalar16Scalar8(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 2: { var profile = Scalar8Scalar16Profile.Create(bytes.Length); var shelf = new Scalar8Scalar16(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 3: { var profile = Scalar16Scalar16Profile.Create(bytes.Length); var shelf = new Scalar16Scalar16(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 4: { var profile = Fixed32Scalar8Profile.Create(bytes.Length); var shelf = new Fixed32Scalar8(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 5: { var profile = Fixed32Scalar16Profile.Create(bytes.Length); var shelf = new Fixed32Scalar16(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 6: { var profile = FixedNScalar8Profile.Create(bytes.Length, 24); var shelf = new FixedNScalar8(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            case 7: { var profile = FixedNScalar16Profile.Create(bytes.Length, 24); var shelf = new FixedNScalar16(bytes, profile); shelf.Initialize(); return profile.MaxItemCount; }
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    /// <summary>Dispatches equivalent encoded tuples to each concrete primitive, preserving actual result codes.<br/></summary>
    private static string InsertClassificationShelf(int shape, byte[] bytes, ulong k, ulong id, bool unique)
    {
        byte[] key = new byte[24]; BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), k);
        byte[] identity = new byte[16]; BinaryPrimitives.WriteUInt64BigEndian(identity.AsSpan(8), id);
        return shape switch
        {
            0 => new Scalar8Scalar8(bytes, Scalar8Scalar8Profile.Create(bytes.Length)).Insert(k, id, unique).ToString(),
            1 => new Scalar16Scalar8(bytes, Scalar16Scalar8Profile.Create(bytes.Length)).Insert(0, k, id, unique).ToString(),
            2 => new Scalar8Scalar16(bytes, Scalar8Scalar16Profile.Create(bytes.Length)).Insert(k, 0, id, unique).ToString(),
            3 => new Scalar16Scalar16(bytes, Scalar16Scalar16Profile.Create(bytes.Length)).Insert(0, k, 0, id, unique).ToString(),
            4 => new Fixed32Scalar8(bytes, Fixed32Scalar8Profile.Create(bytes.Length)).Insert(0, 0, 0, k, id, unique).ToString(),
            5 => new Fixed32Scalar16(bytes, Fixed32Scalar16Profile.Create(bytes.Length)).Insert(0, 0, 0, k, 0, id, unique).ToString(),
            6 => new FixedNScalar8(bytes, FixedNScalar8Profile.Create(bytes.Length, 24)).Insert(key, id, unique).ToString(),
            7 => new FixedNScalar16(bytes, FixedNScalar16Profile.Create(bytes.Length, 24)).Insert(key, identity, unique).ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureScalar8Scalar8Classification(bool reverse)
    {
        var profile = Scalar8Scalar8Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Scalar8Scalar8(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(k, id, unique) != Scalar8Scalar8InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureScalar16Scalar8Classification(bool reverse)
    {
        var profile = Scalar16Scalar8Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Scalar16Scalar8(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(0, k, id, unique) != Scalar16Scalar8InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureScalar8Scalar16Classification(bool reverse)
    {
        var profile = Scalar8Scalar16Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Scalar8Scalar16(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(k, 0, id, unique) != Scalar8Scalar16InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureScalar16Scalar16Classification(bool reverse)
    {
        var profile = Scalar16Scalar16Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Scalar16Scalar16(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(0, k, 0, id, unique) != Scalar16Scalar16InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureFixed32Scalar8Classification(bool reverse)
    {
        var profile = Fixed32Scalar8Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Fixed32Scalar8(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(0, 0, 0, k, id, unique) != Fixed32Scalar8InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times two million non-full physical insertions with one reusable buffer and no per-insert allocations.<br/>
    /// Ordered and reverse runs exercise the append fast path and lower-bound/slot movement respectively.<br/></summary>
    private static double MeasureFixed32Scalar16Classification(bool reverse)
    {
        var profile = Fixed32Scalar16Profile.Create(4096); byte[] bytes = new byte[4096];
        var shelf = new Fixed32Scalar16(bytes, profile);
        var watch = Stopwatch.StartNew();
        for (int batch = 0; batch < 31250; batch++)
        {
            shelf.Initialize();
            for (ulong i = 0; i < 64; i++)
            {
                ulong k = reverse ? 64 - i : i, id = i + 1; bool unique = true;
                if (shelf.Insert(0, 0, 0, k, 0, id, unique) != Fixed32Scalar16InsertResult.Inserted) throw new InvalidDataException("Perf insertion failed.");
            }
        }
        return watch.Elapsed.TotalMilliseconds;
    }
}
