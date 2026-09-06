using System.Diagnostics;

internal static partial class RawHarness
{
    /// <summary>
    /// Runs neutral UInt64 presence/counting comparisons for the two high-cardinality duplicate distributions that motivated the LibraDex set work.<br/>
    /// Every row is correctness-gated before it is printed, and input generation occurs arithmetically inside each engine loop so no million-element source array is retained or charged to one candidate.<br/>
    /// Managed baselines intentionally receive no cardinality hint because an Abraxas result producer normally does not know final distinct cardinality before deduplication completes.<br/>
    /// </summary>
    /// <param name="args">Command arguments; element one may specify the total input count and defaults to one million.<br/></param>
    /// <returns>Zero after every comparison passes its semantic checks.<br/></returns>
    private static int RunUInt64SetComparison(string[] args)
    {
        int inputCount = args.Length > 1
            ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
            : 1_000_000;
        if (inputCount < 4)
            throw new ArgumentOutOfRangeException(nameof(args), inputCount, "The UInt64 set comparison requires at least four inputs.");

        UInt64SetComparisonScenario[] scenarios =
        [
            new("unique-plus-two", inputCount, inputCount - 2, ExpectedDuplicateInputs: 2),
            new("half-repeated-two-pass", inputCount & ~1, (inputCount & ~1) / 2, ExpectedDuplicateInputs: (inputCount & ~1) / 2)
        ];

        Console.WriteLine("UInt64 ordered-set comparison");
        Console.WriteLine("Managed baselines: default growth, no distinct-cardinality hint");
        Console.WriteLine("Retained bytes: GC.GetTotalMemory delta while the populated owner remains alive; treat as an approximation");
        foreach (UInt64SetComparisonScenario scenario in scenarios)
        {
            Console.WriteLine();
            Console.WriteLine($"Scenario={scenario.Name} Inputs={scenario.InputCount:N0} Distinct={scenario.DistinctCount:N0} DuplicateInputs={scenario.ExpectedDuplicateInputs:N0}");
            PrintUInt64SetComparisonResult(RunLibraDexPresenceComparison(scenario));
            PrintUInt64SetComparisonResult(RunLibraDexRoutedPresenceComparison(scenario));
            PrintUInt64SetComparisonResult(RunLibraDexPresenceBatchComparison(scenario));
            PrintUInt64SetComparisonResult(RunBufferedHashSetComparison(scenario, preSized: false));
            PrintUInt64SetComparisonResult(RunBufferedHashSetComparison(scenario, preSized: true));
            PrintUInt64SetComparisonResult(RunHashSetComparison(scenario));
            PrintUInt64SetComparisonResult(RunSortedSetComparison(scenario));
            PrintUInt64SetComparisonResult(RunLibraDexCountedComparison(scenario, saturating: false));
            PrintUInt64SetComparisonResult(RunLibraDexCountedComparison(scenario, saturating: true));
            PrintUInt64SetComparisonResult(RunDictionaryCountedComparison(scenario));
        }

        return 0;
    }

    /// <summary>
    /// Measures one memory-backed <see cref="LibraDex.LibraDexSortedSet{TKey}"/> population and its native ordered export.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunLibraDexPresenceComparison(UInt64SetComparisonScenario scenario)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using LibraDex.LibraDexSortedSet<ulong> set = LibraDex.LibraDexSortedSet<ulong>.CreateMemory();
        int duplicateInputs = 0;
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
        {
            if (!set.TryAdd(GetUInt64SetComparisonKey(scenario, i)))
                duplicateInputs++;
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        ValidateUInt64SetComparisonCardinality("LibraDexSortedSet", scenario, checked((int)set.Count), duplicateInputs);
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        ulong[] ordered = new ulong[scenario.DistinctCount];
        Stopwatch export = Stopwatch.StartNew();
        int copied = set.CopyTo(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered.AsSpan(0, copied), scenario.DistinctCount, "LibraDexSortedSet");
        LibraDex.UInt64SetTopologyStorageSnapshot storage = set.GetTopologyStorageSnapshot();
        return CompleteUInt64SetComparisonResult(
            "LibraDexSortedSet<ulong>",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum,
            FormatUInt64SetStorageShape(storage));
    }

    /// <summary>
    /// Measures one memory-backed <see cref="LibraDex.LibraDexRoutedSet{TKey}"/> immediate population and router-major physical export.<br/>
    /// Correctness validation sorts only after the export timer has stopped, so the reported traversal cost does not charge RoutedSet for a total-order contract it intentionally does not provide.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunLibraDexRoutedPresenceComparison(UInt64SetComparisonScenario scenario)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using LibraDex.LibraDexRoutedSet<ulong> set = LibraDex.LibraDexRoutedSet<ulong>.CreateMemory();
        int duplicateInputs = 0;
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
        {
            if (!set.TryAdd(GetUInt64SetComparisonKey(scenario, i)))
                duplicateInputs++;
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        ValidateUInt64SetComparisonCardinality("LibraDexRoutedSet", scenario, checked((int)set.Count), duplicateInputs);
        for (int i = 0; i < scenario.InputCount; i += Math.Max(1, scenario.InputCount / 257))
        {
            if (!set.Contains(GetUInt64SetComparisonKey(scenario, i)))
                throw new InvalidDataException($"LibraDexRoutedSet could not find sampled input ordinal {i:N0} for {scenario.Name}.");
        }

        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        ulong[] physical = new ulong[scenario.DistinctCount];
        Stopwatch export = Stopwatch.StartNew();
        int copied = set.CopyTo(physical);
        export.Stop();
        Array.Sort(physical, 0, copied);
        ulong checksum = ValidateUInt64SetComparisonOrder(physical.AsSpan(0, copied), scenario.DistinctCount, "LibraDexRoutedSet");
        LibraDex.UInt64SetTopologyStorageSnapshot storage = set.GetTopologyStorageSnapshot();
        return CompleteUInt64SetComparisonResult(
            "LibraDexRoutedSet<ulong>",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum,
            FormatUInt64SetStorageShape(storage));
    }

    /// <summary>
    /// Measures the explicit span batch route after preparing the caller-owned input outside the insertion timer and allocation counter.<br/>
    /// The input array remains live across the retained-memory baseline and measurement so its bytes are not attributed to the set; this mirrors a caller that already owns a result-identity buffer.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated batch timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunLibraDexPresenceBatchComparison(UInt64SetComparisonScenario scenario)
    {
        ulong[] inputs = new ulong[scenario.InputCount];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = GetUInt64SetComparisonKey(scenario, i);

        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        using LibraDex.LibraDexSortedSet<ulong> set = LibraDex.LibraDexSortedSet<ulong>.CreateMemory();
        Stopwatch insert = Stopwatch.StartNew();
        int added = set.AddMany(inputs);
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        int duplicateInputs = checked(scenario.InputCount - added);
        ValidateUInt64SetComparisonCardinality("LibraDexSortedSet AddMany", scenario, checked((int)set.Count), duplicateInputs);
        if (set.Generation != 1)
            throw new InvalidDataException($"LibraDexSortedSet AddMany generation mismatch for {scenario.Name}: expected 1, got {set.Generation:N0}.");
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        GC.KeepAlive(inputs);
        ulong[] ordered = new ulong[scenario.DistinctCount];
        Stopwatch export = Stopwatch.StartNew();
        int copied = set.CopyTo(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered.AsSpan(0, copied), scenario.DistinctCount, "LibraDexSortedSet AddMany");
        return CompleteUInt64SetComparisonResult(
            "LibraDexSortedSet<ulong> AddMany",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Measures the managed exact-membership baseline with ordinary default-capacity growth and a separate sort for ordered output.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunHashSetComparison(UInt64SetComparisonScenario scenario)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        HashSet<ulong> set = [];
        int duplicateInputs = 0;
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
        {
            if (!set.Add(GetUInt64SetComparisonKey(scenario, i)))
                duplicateInputs++;
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        ValidateUInt64SetComparisonCardinality("HashSet", scenario, set.Count, duplicateInputs);
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        ulong[] ordered = new ulong[set.Count];
        Stopwatch export = Stopwatch.StartNew();
        set.CopyTo(ordered);
        Array.Sort(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered, scenario.DistinctCount, "HashSet");
        return CompleteUInt64SetComparisonResult(
            "HashSet<ulong>",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Measures managed hash-set insertion from the same pre-existing caller-owned input-buffer shape used by the LibraDex <c>AddMany</c> row.<br/>
    /// The optional upper-bound capacity is fair for an explicit span API because its input length is known even though its final distinct cardinality is not.<br/>
    /// Input construction stays outside the timer, allocation counter, and retained-memory delta while the buffer is kept alive through measurement.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <param name="preSized">Whether to reserve the known input-count upper bound before insertion.<br/></param>
    /// <returns>The correctness-gated buffered hash-set timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunBufferedHashSetComparison(UInt64SetComparisonScenario scenario, bool preSized)
    {
        ulong[] inputs = new ulong[scenario.InputCount];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = GetUInt64SetComparisonKey(scenario, i);

        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        HashSet<ulong> set = preSized ? new HashSet<ulong>(scenario.InputCount) : [];
        int duplicateInputs = 0;
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < inputs.Length; i++)
        {
            if (!set.Add(inputs[i]))
                duplicateInputs++;
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        ValidateUInt64SetComparisonCardinality("Buffered HashSet", scenario, set.Count, duplicateInputs);
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        GC.KeepAlive(inputs);
        ulong[] ordered = new ulong[set.Count];
        Stopwatch export = Stopwatch.StartNew();
        set.CopyTo(ordered);
        Array.Sort(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered, scenario.DistinctCount, "Buffered HashSet");
        return CompleteUInt64SetComparisonResult(
            preSized ? "HashSet<ulong> buffered/pre-sized" : "HashSet<ulong> buffered/default",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Measures the managed naturally ordered membership baseline.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunSortedSetComparison(UInt64SetComparisonScenario scenario)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        SortedSet<ulong> set = [];
        int duplicateInputs = 0;
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
        {
            if (!set.Add(GetUInt64SetComparisonKey(scenario, i)))
                duplicateInputs++;
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        ValidateUInt64SetComparisonCardinality("SortedSet", scenario, set.Count, duplicateInputs);
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        ulong[] ordered = new ulong[set.Count];
        Stopwatch export = Stopwatch.StartNew();
        set.CopyTo(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered, scenario.DistinctCount, "SortedSet");
        return CompleteUInt64SetComparisonResult(
            "SortedSet<ulong>",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Measures exact or two-or-more saturating LibraDex occurrence retention.<br/>
    /// The correctness gate validates every expected key count after population so saturation cannot masquerade as an insertion-only success.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <param name="saturating">Whether to use the compact two-or-more counter layout rather than exact UInt64 counters.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunLibraDexCountedComparison(UInt64SetComparisonScenario scenario, bool saturating)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        LibraDex.LibraDexCountedSetOptions options = saturating
            ? LibraDex.LibraDexCountedSetOptions.Saturating(2)
            : LibraDex.LibraDexCountedSetOptions.Exact;
        using LibraDex.LibraDexCountedSet<ulong> set = LibraDex.LibraDexCountedSet<ulong>.CreateMemory(options);
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
            _ = set.AddOccurrence(GetUInt64SetComparisonKey(scenario, i));
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        if (set.DistinctCount != (ulong)scenario.DistinctCount)
            throw new InvalidDataException($"LibraDex counted-set distinct cardinality mismatch for {scenario.Name}: expected {scenario.DistinctCount:N0}, got {set.DistinctCount:N0}.");
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        LibraDex.LibraDexCountedSetEntry<ulong>[] ordered = new LibraDex.LibraDexCountedSetEntry<ulong>[scenario.DistinctCount];
        Stopwatch export = Stopwatch.StartNew();
        int copied = set.CopyTo(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64CountedSetComparisonOrder(ordered.AsSpan(0, copied), scenario);
        return CompleteUInt64SetComparisonResult(
            saturating ? "LibraDexCountedSet<ulong> saturating-2" : "LibraDexCountedSet<ulong> exact",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Measures the ordinary managed exact-counting baseline and a key-sorted export.<br/>
    /// </summary>
    /// <param name="scenario">The deterministic input distribution and expected semantic result.<br/></param>
    /// <returns>The correctness-gated timing and allocation row.<br/></returns>
    private static UInt64SetComparisonResult RunDictionaryCountedComparison(UInt64SetComparisonScenario scenario)
    {
        PrepareUInt64SetComparisonSample(out long baselineManagedBytes, out long baselineWorkingSetBytes);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        Dictionary<ulong, ulong> counts = [];
        Stopwatch insert = Stopwatch.StartNew();
        for (int i = 0; i < scenario.InputCount; i++)
        {
            ulong key = GetUInt64SetComparisonKey(scenario, i);
            counts.TryGetValue(key, out ulong prior);
            counts[key] = checked(prior + 1);
        }
        insert.Stop();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        gen0Before = GC.CollectionCount(0) - gen0Before;
        gen1Before = GC.CollectionCount(1) - gen1Before;
        gen2Before = GC.CollectionCount(2) - gen2Before;

        if (counts.Count != scenario.DistinctCount)
            throw new InvalidDataException($"Dictionary counted cardinality mismatch for {scenario.Name}: expected {scenario.DistinctCount:N0}, got {counts.Count:N0}.");
        ValidateDictionaryCountedComparison(counts, scenario);
        long retainedManagedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - baselineManagedBytes);
        long workingSetDelta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - baselineWorkingSetBytes);
        ulong[] ordered = counts.Keys.ToArray();
        Stopwatch export = Stopwatch.StartNew();
        Array.Sort(ordered);
        export.Stop();
        ulong checksum = ValidateUInt64SetComparisonOrder(ordered, scenario.DistinctCount, "Dictionary");
        return CompleteUInt64SetComparisonResult(
            "Dictionary<ulong,ulong>",
            scenario,
            insert.Elapsed,
            export.Elapsed,
            allocatedBefore,
            retainedManagedBytes,
            workingSetDelta,
            gen0Before,
            gen1Before,
            gen2Before,
            checksum);
    }

    /// <summary>
    /// Forces a stable managed baseline immediately before one engine is created.<br/>
    /// Working-set deltas remain advisory because the operating system controls page reclamation independently of managed liveness.<br/>
    /// </summary>
    /// <param name="managedBytes">Receives the managed heap size before engine creation.<br/></param>
    /// <param name="workingSetBytes">Receives the current process working set before engine creation.<br/></param>
    private static void PrepareUInt64SetComparisonSample(out long managedBytes, out long workingSetBytes)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
    }

    /// <summary>
    /// Maps one logical ordinal to a full-domain UInt64 key through a bijective SplitMix64 finalizer.<br/>
    /// Because every transform step is invertible modulo two to the sixty-fourth power, distinct ordinals remain distinct without storing a shuffled input array.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based distinct-key ordinal.<br/></param>
    /// <returns>A deterministic sparse UInt64 key.<br/></returns>
    private static ulong PermuteUInt64SetComparisonKey(int ordinal)
    {
        ulong value = unchecked((ulong)ordinal + 0x9E37_79B9_7F4A_7C15UL);
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// Resolves the input key for one scenario position without allocating source storage.<br/>
    /// </summary>
    /// <param name="scenario">The selected duplicate distribution.<br/></param>
    /// <param name="inputIndex">The zero-based input position.<br/></param>
    /// <returns>The deterministic key for that position.<br/></returns>
    private static ulong GetUInt64SetComparisonKey(UInt64SetComparisonScenario scenario, int inputIndex)
    {
        int ordinal;
        if (scenario.Name == "unique-plus-two")
        {
            ordinal = inputIndex < scenario.DistinctCount
                ? inputIndex
                : inputIndex == scenario.DistinctCount
                    ? scenario.DistinctCount / 3
                    : (scenario.DistinctCount * 2) / 3;
        }
        else
        {
            ordinal = inputIndex % scenario.DistinctCount;
        }

        return PermuteUInt64SetComparisonKey(ordinal);
    }

    /// <summary>
    /// Verifies distinct and repeated-input counts for a presence owner.<br/>
    /// </summary>
    private static void ValidateUInt64SetComparisonCardinality(
        string owner,
        UInt64SetComparisonScenario scenario,
        int actualDistinctCount,
        int actualDuplicateInputs)
    {
        if (actualDistinctCount != scenario.DistinctCount || actualDuplicateInputs != scenario.ExpectedDuplicateInputs)
        {
            throw new InvalidDataException(
                $"{owner} semantic mismatch for {scenario.Name}: distinct {actualDistinctCount:N0}/{scenario.DistinctCount:N0}, duplicates {actualDuplicateInputs:N0}/{scenario.ExpectedDuplicateInputs:N0}.");
        }
    }

    /// <summary>
    /// Verifies strict ascending order and computes one order-sensitive checksum outside the measured export duration.<br/>
    /// </summary>
    private static ulong ValidateUInt64SetComparisonOrder(ReadOnlySpan<ulong> ordered, int expectedCount, string owner)
    {
        if (ordered.Length != expectedCount)
            throw new InvalidDataException($"{owner} ordered export count mismatch: expected {expectedCount:N0}, got {ordered.Length:N0}.");
        ulong checksum = 14_695_981_039_346_656_037UL;
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i != 0 && ordered[i - 1] >= ordered[i])
                throw new InvalidDataException($"{owner} ordered export is not strictly ascending at position {i:N0}.");
            checksum = unchecked((checksum ^ ordered[i]) * 1_099_511_628_211UL);
        }
        return checksum;
    }

    /// <summary>
    /// Verifies managed exact-counting values for every deterministic key.<br/>
    /// </summary>
    private static void ValidateDictionaryCountedComparison(
        Dictionary<ulong, ulong> counts,
        UInt64SetComparisonScenario scenario)
    {
        for (int ordinal = 0; ordinal < scenario.DistinctCount; ordinal++)
        {
            ulong key = PermuteUInt64SetComparisonKey(ordinal);
            ulong expected = GetUInt64SetComparisonExpectedCount(scenario, ordinal);
            if (!counts.TryGetValue(key, out ulong actual) || actual != expected)
                throw new InvalidDataException($"Dictionary counted value mismatch for {scenario.Name} ordinal {ordinal:N0}: expected {expected:N0}, got {actual:N0}.");
        }
    }

    /// <summary>
    /// Returns the expected exact or saturation-two count for one logical key ordinal.<br/>
    /// </summary>
    private static ulong GetUInt64SetComparisonExpectedCount(UInt64SetComparisonScenario scenario, int ordinal)
    {
        if (scenario.Name != "unique-plus-two")
            return 2;
        return ordinal == scenario.DistinctCount / 3 || ordinal == (scenario.DistinctCount * 2) / 3 ? 2UL : 1UL;
    }

    /// <summary>
    /// Verifies counted-entry order/counts and computes the same key-order checksum used by presence rows.<br/>
    /// </summary>
    private static ulong ValidateUInt64CountedSetComparisonOrder(
        ReadOnlySpan<LibraDex.LibraDexCountedSetEntry<ulong>> ordered,
        UInt64SetComparisonScenario scenario)
    {
        if (ordered.Length != scenario.DistinctCount)
            throw new InvalidDataException($"LibraDex counted ordered export count mismatch: expected {scenario.DistinctCount:N0}, got {ordered.Length:N0}.");
        ulong checksum = 14_695_981_039_346_656_037UL;
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i != 0 && ordered[i - 1].Key >= ordered[i].Key)
                throw new InvalidDataException($"LibraDex counted ordered export is not strictly ascending at position {i:N0}.");
            ulong expected = GetUInt64SetComparisonExpectedCountForKey(scenario, ordered[i].Key);
            if (ordered[i].Count != expected)
                throw new InvalidDataException($"LibraDex counted ordered export value mismatch at position {i:N0}: expected {expected:N0}, got {ordered[i].Count:N0}.");
            checksum = unchecked((checksum ^ ordered[i].Key) * 1_099_511_628_211UL);
        }
        return checksum;
    }

    /// <summary>
    /// Resolves the expected count for an exported key without constructing a managed reference map.<br/>
    /// </summary>
    private static ulong GetUInt64SetComparisonExpectedCountForKey(UInt64SetComparisonScenario scenario, ulong key)
    {
        if (scenario.Name != "unique-plus-two")
            return 2;
        ulong firstDuplicate = PermuteUInt64SetComparisonKey(scenario.DistinctCount / 3);
        ulong secondDuplicate = PermuteUInt64SetComparisonKey((scenario.DistinctCount * 2) / 3);
        return key == firstDuplicate || key == secondDuplicate ? 2UL : 1UL;
    }

    /// <summary>
    /// Completes allocation/collection counters after correctness validation.<br/>
    /// </summary>
    private static UInt64SetComparisonResult CompleteUInt64SetComparisonResult(
        string engine,
        UInt64SetComparisonScenario scenario,
        TimeSpan insertDuration,
        TimeSpan orderedExportDuration,
        long allocatedBytes,
        long retainedManagedBytes,
        long workingSetDelta,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections,
        ulong checksum,
        string? storageShape = null)
    {
        return new UInt64SetComparisonResult(
            engine,
            insertDuration,
            orderedExportDuration,
            scenario.InputCount / Math.Max(insertDuration.TotalSeconds, double.Epsilon),
            Math.Max(0, allocatedBytes),
            retainedManagedBytes,
            workingSetDelta,
            gen0Collections,
            gen1Collections,
            gen2Collections,
            checksum,
            storageShape);
    }

    /// <summary>
    /// Formats one internal set topology snapshot as compact benchmark evidence.<br/>
    /// Occupancy is derived from live item count and physical leaf capacity, while arena counters expose whether retained-heap changes come from reachable pages, reusable extents, or managed overhead.<br/>
    /// </summary>
    /// <param name="storage">The correctness-gated post-timing topology snapshot.<br/></param>
    /// <returns>A single-line storage-shape description suitable for comparison output.<br/></returns>
    private static string FormatUInt64SetStorageShape(LibraDex.UInt64SetTopologyStorageSnapshot storage)
    {
        double occupancy = storage.LeafPageCount == 0
            ? 0
            : storage.LeafItemCount / (double)(storage.LeafPageCount * storage.LeafCapacity);
        return $"routers={storage.RouterPageCount:N0},leaves={storage.LeafPageCount:N0},leafCapacity={storage.LeafCapacity:N0}," +
               $"occupancy={occupancy:P1},arenaPages={storage.MemoryPageCount:N0},arena={storage.MemoryArenaBytes:N0}," +
               $"liveRanges={storage.MemoryLiveRangeCount:N0},liveBytes={storage.MemoryLiveBytes:N0}," +
               $"freeExtents={storage.MemoryFreeExtentCount:N0},freeBytes={storage.MemoryFreeBytes:N0},end={storage.MemoryEndOffset:N0}";
    }

    /// <summary>
    /// Prints one comparison row after all measured state and correctness evidence have been captured.<br/>
    /// </summary>
    private static void PrintUInt64SetComparisonResult(UInt64SetComparisonResult result)
    {
        Console.WriteLine(
            $"{result.Engine} | insert={result.InsertDuration.TotalMilliseconds:N3}ms | {result.InputsPerSecond:N0} inputs/s | " +
            $"export={result.OrderedExportDuration.TotalMilliseconds:N3}ms | insertAllocated={result.AllocatedBytes:N0} | " +
            $"retainedApprox={result.RetainedManagedBytes:N0} | workingSetDelta={result.WorkingSetDelta:N0} | " +
            $"GC={result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections} | checksum=0x{result.OrderChecksum:X16}" +
            (string.IsNullOrEmpty(result.StorageShape) ? string.Empty : $" | {result.StorageShape}"));
    }

    private readonly record struct UInt64SetComparisonScenario(
        string Name,
        int InputCount,
        int DistinctCount,
        int ExpectedDuplicateInputs);

    private readonly record struct UInt64SetComparisonResult(
        string Engine,
        TimeSpan InsertDuration,
        TimeSpan OrderedExportDuration,
        double InputsPerSecond,
        long AllocatedBytes,
        long RetainedManagedBytes,
        long WorkingSetDelta,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        ulong OrderChecksum,
        string? StorageShape);
}
