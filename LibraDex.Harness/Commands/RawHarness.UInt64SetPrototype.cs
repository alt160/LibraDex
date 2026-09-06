using System.Diagnostics;
using System.Globalization;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Validates the harness-gated UInt64 ordered presence set against a managed reference in memory and file modes.<br/>
    /// The fixture forces deep shared-prefix transforms, distributed root routes, duplicate no-ops, removal, ordered traversal, reopen, clear, and allocator-backed repopulation before reporting timing and allocation evidence.<br/>
    /// </summary>
    /// <param name="args">The complete harness argument vector; the optional argument after the command name selects the distinct input count, with a default of 20,000 and a minimum of 2,048.<br/></param>
    /// <returns>Zero when both backing modes preserve exact membership, count, order, reopen, and reuse contracts.<br/></returns>
    private static int RunUInt64SetPrototypeSanity(string[] args)
    {
        int count = args.Length <= 1
            ? 20_000
            : int.Parse(args[1], CultureInfo.InvariantCulture);
        if (count < 2_048)
            throw new ArgumentOutOfRangeException(nameof(args), count, "UInt64 set prototype sanity requires at least 2,048 keys to force structural transforms.");

        ulong[] keys = CreateUInt64SetPrototypeKeys(count);
        RunUInt64SetPrototypeBackingSanity(keys, fileBacked: false);
        RunUInt64SetPrototypeBackingSanity(keys, fileBacked: true);
        RunUInt64SetPublicSurfaceSanity();
        RunUInt64RoutedSetPublicSurfaceSanity(keys);
        return 0;
    }

    /// <summary>
    /// Proves that the public generic handle preserves UInt64 membership, ordering, disposal, durable reopen, and unsupported-codec behavior.<br/>
    /// This intentionally uses only public members so the harness catches adapter errors that the physical-engine checks cannot observe.<br/>
    /// </summary>
    private static void RunUInt64SetPublicSurfaceSanity()
    {
        ulong[] input = [ulong.MaxValue, 42UL, 0UL, 1UL, 0x0102_0304_0506_0708UL];
        using (LibraDexSortedSet<ulong> memory = LibraDexSortedSet<ulong>.CreateMemory())
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (!memory.TryAdd(input[i]))
                    throw new InvalidDataException($"The public UInt64 memory set rejected distinct key 0x{input[i]:X16}.");
            }

            if (memory.TryAdd(input[0]) || !memory.Contains(input[0]))
                throw new InvalidDataException("The public UInt64 memory set violated duplicate or membership semantics.");

            ulong[] expectedAscending = input.Order().ToArray();
            if (!memory.ToArray().SequenceEqual(expectedAscending))
                throw new InvalidDataException("The public UInt64 memory set did not materialize ascending natural order.");

            ulong[] descending = new ulong[input.Length];
            memory.CopyTo(descending, descending: true);
            if (!descending.SequenceEqual(expectedAscending.Reverse()))
                throw new InvalidDataException("The public UInt64 memory set did not copy descending natural order.");
        }


        using (LibraDexSortedSet<ulong> batch = LibraDexSortedSet<ulong>.CreateMemory())
        {
            ulong[] batchInput = [42UL, 7UL, 42UL, ulong.MaxValue, 0UL, 7UL];
            if (batch.AddMany(batchInput) != 4 || batch.Count != 4 || batch.Generation != 1)
                throw new InvalidDataException("The public UInt64 memory set batch did not preserve distinct-add or one-publication generation semantics.");
            if (batch.AddMany(batchInput) != 0 || batch.Generation != 1)
                throw new InvalidDataException("The public UInt64 memory set duplicate-only batch changed membership or generation.");
            ulong[] extension = [7UL, 9UL, 8UL, 9UL];
            if (batch.AddMany(extension) != 2 || batch.Count != 6 || batch.Generation != 2)
                throw new InvalidDataException("The public UInt64 memory set partial-overlap batch did not publish exactly the absent keys.");
            if (!batch.ToArray().SequenceEqual(batchInput.Concat(extension).Distinct().Order()))
                throw new InvalidDataException("The public UInt64 memory set batch did not preserve ascending natural order.");
        }

        bool unsupportedRejected = false;
        try
        {
            using LibraDexSortedSet<int> unsupported = LibraDexSortedSet<int>.CreateMemory();
        }
        catch (NotSupportedException)
        {
            unsupportedRejected = true;
        }

        if (!unsupportedRejected)
            throw new InvalidDataException("The public generic set accepted an unpromoted Int32 physical key codec.");

        string path = Path.Combine(Path.GetTempPath(), $"libradex-public-uint64-set-{Guid.NewGuid():N}.lbdxset");
        try
        {
            using (LibraDexSortedSet<ulong> created = LibraDexSortedSet<ulong>.Create(path))
            {
                if (created.AddMany(input) != input.Length || created.Generation != 1)
                    throw new InvalidDataException("The public UInt64 file set batch did not publish every distinct input exactly once.");
                if (created.AddMany([input[0], 99UL, 99UL]) != 1 || created.Generation != 2)
                    throw new InvalidDataException("The public UInt64 file set partial-overlap batch did not publish exactly one absent key.");
            }

            using LibraDexSortedSet<ulong> reopened = LibraDexSortedSet<ulong>.Open(path);
            if (reopened.IsMemoryBacked || reopened.FilePath != Path.GetFullPath(path) || !reopened.ToArray().SequenceEqual(input.Append(99UL).Order()))
                throw new InvalidDataException("The public UInt64 file set did not preserve path, backing, or ordered contents across reopen.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        Console.WriteLine("uint64-set-public-surface sanity passed.");
    }

    /// <summary>
    /// Creates deterministic distinct keys containing both a deep common-prefix lane and broad root-byte distribution.<br/>
    /// The final Fisher-Yates pass prevents the insertion path from receiving an artificially sorted workload.<br/>
    /// </summary>
    /// <param name="count">The required distinct key count.<br/></param>
    /// <returns>A caller-owned shuffled key array.<br/></returns>
    private static ulong[] CreateUInt64SetPrototypeKeys(int count)
    {
        ulong[] keys = new ulong[count];
        int deepPrefixCount = Math.Min(count / 2, 8_000);
        for (int i = 0; i < deepPrefixCount; i++)
            keys[i] = 0xA1B2_C3D4_0000_0000UL | checked((uint)i);

        for (int i = deepPrefixCount; i < count; i++)
        {
            ulong rootPrefix = checked((ulong)(((i - deepPrefixCount) % 255) + 1));
            keys[i] = (rootPrefix << 56) | checked((uint)i);
        }

        var random = new Random(0x51E7_2026);
        for (int i = keys.Length - 1; i > 0; i--)
        {
            int swap = random.Next(i + 1);
            (keys[i], keys[swap]) = (keys[swap], keys[i]);
        }

        return keys;
    }

    /// <summary>
    /// Runs the complete correctness and lifecycle proof for one DataKernel backing kind.<br/>
    /// File mode additionally closes/reopens the exact image and requires same-shape repopulation to reuse retired 4 KiB pages without extending EOF.<br/>
    /// </summary>
    /// <param name="keys">The deterministic distinct input keys.<br/></param>
    /// <param name="fileBacked">Whether to use a disposable file rather than the process-local memory arena.<br/></param>
    private static void RunUInt64SetPrototypeBackingSanity(ulong[] keys, bool fileBacked)
    {
        string? path = fileBacked
            ? Path.Combine(Path.GetTempPath(), $"libradex-uint64-set-{Guid.NewGuid():N}.lbdxset")
            : null;
        UInt64SetPrototype? set = null;
        try
        {
            set = fileBacked ? UInt64SetPrototype.Create(path!) : UInt64SetPrototype.CreateMemory();
            HashSet<ulong> expected = new(keys.Length);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch write = Stopwatch.StartNew();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!set.TryAdd(keys[i]) || !expected.Add(keys[i]))
                    throw new InvalidDataException($"UInt64 set {set.BackingKind} insertion rejected distinct key 0x{keys[i]:X16} at ordinal {i:N0}.");
            }
            write.Stop();
            long writeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            for (int i = 0; i < keys.Length; i += Math.Max(1, keys.Length / 257))
            {
                if (!set.Contains(keys[i]))
                    throw new InvalidDataException($"UInt64 set {set.BackingKind} could not find inserted key 0x{keys[i]:X16}.");
                if (set.TryAdd(keys[i]))
                    throw new InvalidDataException($"UInt64 set {set.BackingKind} accepted duplicate key 0x{keys[i]:X16}.");
            }

            ValidateUInt64SetPrototypeOrder(set, expected, "initial");

            for (int i = 0; i < keys.Length; i += 7)
            {
                if (!set.Remove(keys[i]) || !expected.Remove(keys[i]))
                    throw new InvalidDataException($"UInt64 set {set.BackingKind} could not remove key 0x{keys[i]:X16}.");
                if (set.Remove(keys[i]))
                    throw new InvalidDataException($"UInt64 set {set.BackingKind} removed key 0x{keys[i]:X16} twice.");
            }

            ValidateUInt64SetPrototypeOrder(set, expected, "after removals");
            long fileBytesBeforeClear = set.FileLength;
            FileAllocationStorageSnapshot allocationBeforeClear = set.GetFileAllocationStorageSnapshot();

            if (fileBacked)
            {
                set.Dispose();
                set = UInt64SetPrototype.Open(path!);
                ValidateUInt64SetPrototypeOrder(set, expected, "after reopen");
            }

            set.Clear();
            FileAllocationStorageSnapshot allocationAfterClear = set.GetFileAllocationStorageSnapshot();
            if (set.Count != 0)
                throw new InvalidDataException($"UInt64 set {set.BackingKind} clear retained {set.Count:N0} keys.");

            expected.Clear();
            ulong generationBeforeBatch = set.Generation;
            if (set.AddMany(keys) != keys.Length || set.Generation != generationBeforeBatch + 1)
                throw new InvalidDataException($"UInt64 set {set.BackingKind} batch repopulation did not publish every key in exactly one generation.");
            expected.UnionWith(keys);
            if (set.AddMany(keys) != 0 || set.Generation != generationBeforeBatch + 1)
                throw new InvalidDataException($"UInt64 set {set.BackingKind} duplicate-only batch changed membership or generation.");

            ValidateUInt64SetPrototypeOrder(set, expected, "after clear/repopulate");
            long finalFileBytes = set.FileLength;
            FileAllocationStorageSnapshot allocationAfterRepopulate = set.GetFileAllocationStorageSnapshot();
            if (fileBacked && finalFileBytes > fileBytesBeforeClear)
            {
                throw new InvalidDataException(
                    $"UInt64 set file grew from {fileBytesBeforeClear:N0} to {finalFileBytes:N0} bytes while repopulating the same shape after clear. " +
                    $"Before clear: {FormatUInt64SetAllocation(allocationBeforeClear)}; " +
                    $"after clear: {FormatUInt64SetAllocation(allocationAfterClear)}; " +
                    $"after repopulate: {FormatUInt64SetAllocation(allocationAfterRepopulate)}.");
            }

            Console.WriteLine(
                $"uint64-set-prototype-sanity backing={set.BackingKind} keys={keys.Length:N0} " +
                $"write={write.Elapsed.TotalMilliseconds:N3}ms itemsPerSecond={keys.Length / Math.Max(write.Elapsed.TotalSeconds, double.Epsilon):N0} " +
                $"writeAllocated={writeAllocated:N0} bytesPerInput={(double)writeAllocated / keys.Length:N2} " +
                 $"generation={set.Generation:N0} fileBytes={finalFileBytes:N0}");
        }
        finally
        {
            set?.Dispose();
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Formats exact allocator accounting into one compact lifecycle diagnostic.<br/>
    /// </summary>
    /// <param name="snapshot">The allocator snapshot to report.<br/></param>
    /// <returns>A stable compact representation of segment, materialized, occupied, and reusable state.<br/></returns>
    private static string FormatUInt64SetAllocation(FileAllocationStorageSnapshot snapshot) =>
        $"segments={snapshot.SegmentCount:N0}, materialized={snapshot.MaterializedSlotCount:N0}, " +
        $"occupied={snapshot.OccupiedSlotCount:N0}, reusableBytes={snapshot.ReusablePayloadBytes:N0}";

    /// <summary>
    /// Compares exact count plus ascending and descending prototype traversal with one sorted managed reference.<br/>
    /// The output arrays belong to the harness and keep result allocation outside the set reader implementation.<br/>
    /// </summary>
    /// <param name="set">The live or reopened prototype set.<br/></param>
    /// <param name="expected">The exact expected membership reference.<br/></param>
    /// <param name="phase">The lifecycle phase included in failures.<br/></param>
    private static void ValidateUInt64SetPrototypeOrder(UInt64SetPrototype set, HashSet<ulong> expected, string phase)
    {
        if (set.Count != (ulong)expected.Count)
            throw new InvalidDataException($"UInt64 set {set.BackingKind} {phase} count {set.Count:N0} does not match expected {expected.Count:N0}.");

        ulong[] expectedAscending = expected.Order().ToArray();
        ulong[] actual = new ulong[expected.Count];
        int copied = set.CopyTo(actual);
        if (copied != actual.Length || !actual.AsSpan().SequenceEqual(expectedAscending))
            throw new InvalidDataException($"UInt64 set {set.BackingKind} {phase} ascending traversal does not match the reference model.");

        set.CopyTo(actual, descending: true);
        Array.Reverse(expectedAscending);
        if (!actual.AsSpan().SequenceEqual(expectedAscending))
            throw new InvalidDataException($"UInt64 set {set.BackingKind} {phase} descending traversal does not match the reference model.");
    }

    /// <summary>
    /// Proves routed-set membership, duplicate, end-fill removal, router-major traversal, clear, file reopen, and extent-reuse behavior through only the public handle.<br/>
    /// The supplied high-entropy and deep-prefix keys force both parent-byte splits and same-extent shelf-to-router transforms without assuming a total leaf order.<br/>
    /// </summary>
    /// <param name="keys">The deterministic distinct shuffled keys shared with the sorted-set structural fixture.<br/></param>
    private static void RunUInt64RoutedSetPublicSurfaceSanity(ulong[] keys)
    {
        RunUInt64RoutedSetBackingSanity(keys, fileBacked: false);
        RunUInt64RoutedSetBackingSanity(keys, fileBacked: true);

        bool unsupportedRejected = false;
        try
        {
            using LibraDexRoutedSet<int> unsupported = LibraDexRoutedSet<int>.CreateMemory();
        }
        catch (NotSupportedException)
        {
            unsupportedRejected = true;
        }

        if (!unsupportedRejected)
            throw new InvalidDataException("The public routed set accepted an unpromoted Int32 physical key codec.");
        Console.WriteLine("uint64-routed-set-public-surface sanity passed.");
    }

    /// <summary>
    /// Exercises one memory or file routed-set lifecycle while validating physical traversal as a complete permutation rather than as a total ordering.<br/>
    /// File mode additionally proves exact root-kind reopen and same-shape allocator reuse after a fast clear.<br/>
    /// </summary>
    /// <param name="keys">The deterministic distinct shuffled UInt64 input.<br/></param>
    /// <param name="fileBacked">Whether to use a disposable reopenable file rather than process-local memory.<br/></param>
    private static void RunUInt64RoutedSetBackingSanity(ulong[] keys, bool fileBacked)
    {
        string? path = fileBacked
            ? Path.Combine(Path.GetTempPath(), $"libradex-uint64-routed-set-{Guid.NewGuid():N}.lbdxset")
            : null;
        LibraDexRoutedSet<ulong>? set = null;
        try
        {
            set = fileBacked ? LibraDexRoutedSet<ulong>.Create(path!) : LibraDexRoutedSet<ulong>.CreateMemory();
            HashSet<ulong> expected = new(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                if (!set.TryAdd(keys[i]) || !expected.Add(keys[i]))
                    throw new InvalidDataException($"The routed set rejected distinct key 0x{keys[i]:X16} at ordinal {i:N0}.");
            }

            for (int i = 0; i < keys.Length; i += Math.Max(1, keys.Length / 257))
            {
                if (!set.Contains(keys[i]) || set.TryAdd(keys[i]))
                    throw new InvalidDataException($"The routed set violated exact membership or duplicate semantics for 0x{keys[i]:X16}.");
            }

            ValidateUInt64RoutedSetContents(set, expected, "initial");
            for (int i = 0; i < keys.Length; i += 7)
            {
                if (!set.Remove(keys[i]) || !expected.Remove(keys[i]) || set.Remove(keys[i]))
                    throw new InvalidDataException($"The routed set violated single-removal semantics for 0x{keys[i]:X16}.");
            }
            ValidateUInt64RoutedSetContents(set, expected, "after removals");

            long fileBytesBeforeClear = path is null ? 0 : new FileInfo(path).Length;
            if (fileBacked)
            {
                set.Dispose();
                set = LibraDexRoutedSet<ulong>.Open(path!);
                ValidateUInt64RoutedSetContents(set, expected, "after reopen");
            }

            set.Clear();
            if (set.Count != 0)
                throw new InvalidDataException($"The routed set clear retained {set.Count:N0} keys.");
            expected.Clear();
            for (int i = 0; i < keys.Length; i++)
            {
                if (!set.TryAdd(keys[i]))
                    throw new InvalidDataException($"The routed set repopulation rejected distinct key 0x{keys[i]:X16}.");
                expected.Add(keys[i]);
            }
            ValidateUInt64RoutedSetContents(set, expected, "after clear/repopulate");

            long finalFileBytes = path is null ? 0 : new FileInfo(path).Length;
            if (fileBacked && finalFileBytes > fileBytesBeforeClear)
                throw new InvalidDataException($"The routed set file grew from {fileBytesBeforeClear:N0} to {finalFileBytes:N0} bytes while repopulating the same shape after clear.");
        }
        finally
        {
            set?.Dispose();
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Validates exact routed-set cardinality and membership without imposing a total order on router-major physical traversal.<br/>
    /// A managed reference is test-only evidence and is not part of the routed implementation or its measured allocation profile.<br/>
    /// </summary>
    /// <param name="set">The live or reopened routed set.<br/></param>
    /// <param name="expected">The exact expected membership reference.<br/></param>
    /// <param name="phase">The lifecycle phase included in failures.<br/></param>
    private static void ValidateUInt64RoutedSetContents(LibraDexRoutedSet<ulong> set, HashSet<ulong> expected, string phase)
    {
        if (set.Count != (ulong)expected.Count)
            throw new InvalidDataException($"The routed set {phase} count {set.Count:N0} does not match expected {expected.Count:N0}.");
        ulong[] actual = set.ToArray();
        if (actual.Length != expected.Count || actual.Distinct().Count() != actual.Length || !expected.SetEquals(actual))
            throw new InvalidDataException($"The routed set {phase} router-major traversal is not an exact permutation of expected membership.");
        for (int index = 0; index < actual.Length; index++)
        {
            if (!set.Contains(actual[index]))
                throw new InvalidDataException($"The routed set {phase} fingerprint lookup could not recover physical key 0x{actual[index]:X16} at traversal ordinal {index:N0}.");
        }
    }
}
