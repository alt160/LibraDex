using System.Diagnostics;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves the native exact-string `VS8` builder with a Wherzit-shaped low-cardinality value distribution, null/empty key states, live/reopen ordering, and topology assessment.<br/>
    /// One repeated key intentionally spans many 4 KB terminal identity shelves so assessment also validates the persisted first/tail chain rather than only ordinary leaf construction.<br/>
    /// </summary>
    /// <param name="args">Harness options including `--path` and optional `--count`.<br/></param>
    /// <returns>Zero when native population, ascending/descending order, reopen, and assessed terminal topology all agree.<br/></returns>
    private static int RunVarKeyScalar8SortedBuildSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "vs8-sorted-build-sanity.lbdx"));
        int count = GetIntOption(args, "--count", 50_000);
        if (count < 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(args), count, "The VS8 native sorted-build proof requires at least 10,000 tuples.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        List<(string? Key, ulong Identity)> entries = CreateVarKeyScalar8SortedBuildEntries(count);
        ulong[] expectedAscending = entries
            .OrderBy(static entry => GetVarKeyScalar8LogicalKeyStateOrder(entry.Key))
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Identity)
            .Select(static entry => entry.Identity)
            .ToArray();
        ulong[] expectedDescending = expectedAscending.Reverse().ToArray();

        LibraDexVariableTextTopologyStorageComponent liveTopology;
        TimeSpan liveAscendingTime;
        TimeSpan liveDescendingTime;
        var nativeTimer = Stopwatch.StartNew();
        using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
        {
            using LibraDexStringScalar8Index index = catalog.Indexes.IndexSet("native").Define("attributes").String.Create(
                stringKeys: StringKeys.Exact,
                directions: LibraDexProjectionDirectionSet.Forward,
                sortOrder: LibraDexIndexSortOrder.Ascending,
                identityLookupMode: IdentityLookupMode.Disabled);
            long retained = index.ReplaceExactFromUnordered(entries, CancellationToken.None);
            if (retained != entries.Count)
            {
                throw new InvalidDataException($"The VS8 native string builder retained {retained:N0} of {entries.Count:N0} tuples.");
            }

            ValidateVarKeyScalar8SortedBuildOrder(index, expectedAscending, QueryDirection.Ascending, "native live ascending");
            ValidateVarKeyScalar8SortedBuildOrder(index, expectedDescending, QueryDirection.Descending, "native live descending");
            liveAscendingTime = MeasureVarKeyScalar8SortedBuildTraversal(index, QueryDirection.Ascending, entries.Count);
            liveDescendingTime = MeasureVarKeyScalar8SortedBuildTraversal(index, QueryDirection.Descending, entries.Count);
            LibraDexCatalogStorageAssessment storage = catalog.Maintenance.Assess().Storage;
            liveTopology = storage.VariableTextTopologyComponents.Single(component =>
                component.Group == "native" && component.IndexName == "attributes");
            if (liveTopology.TupleCount != entries.Count ||
                liveTopology.TerminalRootCount <= 0 ||
                liveTopology.TerminalShelfCount <= 1)
            {
                throw new InvalidDataException(
                    $"The VS8 native string topology was not the expected multi-shelf terminal shape: tuples={liveTopology.TupleCount:N0}, roots={liveTopology.TerminalRootCount:N0}, shelves={liveTopology.TerminalShelfCount:N0}.");
            }
        }
        nativeTimer.Stop();

        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            using LibraDexStringScalar8Index index = reopened.Indexes.IndexSet("native").Define("attributes").String.Open();
            ValidateVarKeyScalar8SortedBuildOrder(index, expectedAscending, QueryDirection.Ascending, "native reopen ascending");
            ValidateVarKeyScalar8SortedBuildOrder(index, expectedDescending, QueryDirection.Descending, "native reopen descending");
            LibraDexVariableTextTopologyStorageComponent reopenedTopology = reopened.Maintenance.Assess().Storage.VariableTextTopologyComponents.Single(component =>
                component.Group == "native" && component.IndexName == "attributes");
            if (reopenedTopology != liveTopology)
            {
                throw new InvalidDataException("The VS8 native string topology assessment changed after reopen.");
            }
        }

        Console.WriteLine(
            $"vs8-sorted-build-sanity ok count={count:N0} elapsed={nativeTimer.Elapsed} throughput={count / Math.Max(nativeTimer.Elapsed.TotalSeconds, 0.000001):N0}/s " +
            $"ascending={liveAscendingTime} descending={liveDescendingTime} " +
            $"file={new FileInfo(path).Length:N0} reachable={liveTopology.ReachableBytes:N0} routers={liveTopology.RouterCount:N0} ordinary={liveTopology.OrdinaryShelfCount:N0} " +
            $"terminalRoots={liveTopology.TerminalRootCount:N0} terminalShelves={liveTopology.TerminalShelfCount:N0} keyStates={liveTopology.KeyStateRootCount:N0}");
        return 0;
    }

    /// <summary>
    /// Creates a deterministic unordered exact-string population resembling a heavily repeated file-attributes column.<br/>
    /// Most tuples share one exhausted key, the remainder cover a small set of nearby values, and the final two identities exercise null and empty key-state routes.<br/>
    /// </summary>
    /// <param name="count">Total tuple count to create.<br/></param>
    /// <returns>An in-place deterministically shuffled tuple list.<br/></returns>
    private static List<(string? Key, ulong Identity)> CreateVarKeyScalar8SortedBuildEntries(int count)
    {
        int repeatedCount = Math.Max(8_000, count * 7 / 10);
        var entries = new List<(string? Key, ulong Identity)>(count);
        for (int i = 0; i < count; i++)
        {
            string? key = i switch
            {
                _ when i == count - 2 => null,
                _ when i == count - 1 => string.Empty,
                _ when i < repeatedCount => "Archive",
                _ => $"Attributes-{i % 32:D2}"
            };
            entries.Add((key, checked((ulong)i + 1)));
        }

        var random = new Random(0x5A8_2026);
        for (int i = entries.Count - 1; i > 0; i--)
        {
            int swap = random.Next(i + 1);
            (entries[i], entries[swap]) = (entries[swap], entries[i]);
        }

        return entries;
    }

    /// <summary>
    /// Returns the logical key-state ordering used by exact string traversal before ordinary ordinal text values.<br/>
    /// Null sorts first, empty sorts second, and all non-empty strings share the ordinary third state.<br/>
    /// </summary>
    /// <param name="key">Logical string key.<br/></param>
    /// <returns>Zero for null, one for empty, or two for ordinary text.<br/></returns>
    private static int GetVarKeyScalar8LogicalKeyStateOrder(string? key)
        => key is null ? 0 : key.Length == 0 ? 1 : 2;

    /// <summary>
    /// Compares one exact-string identity traversal with the canonical expected identity sequence.<br/>
    /// The validation materializes only the identity result so no source keys are decoded during the physical-order proof.<br/>
    /// </summary>
    /// <param name="index">Completed exact-string index.<br/></param>
    /// <param name="expected">Expected identity order.<br/></param>
    /// <param name="direction">Requested physical traversal direction.<br/></param>
    /// <param name="label">Failure-attribution label.<br/></param>
    private static void ValidateVarKeyScalar8SortedBuildOrder(
        LibraDexStringScalar8Index index,
        ulong[] expected,
        QueryDirection direction,
        string label)
    {
        ulong[] actual = index.IterateExactIdentities(direction).ToArray();
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            int mismatch = 0;
            while (mismatch < actual.Length && mismatch < expected.Length && actual[mismatch] == expected[mismatch])
            {
                mismatch++;
            }

            throw new InvalidDataException(
                $"The VS8 sorted-build {label} order differs at ordinal {mismatch:N0}; actual count={actual.Length:N0}, expected count={expected.Length:N0}.");
        }
    }

    /// <summary>
    /// Measures one complete exact-string identity traversal without materializing result tuples or decoded keys.<br/>
    /// The count and an order-sensitive rolling checksum are consumed so the benchmark cannot accidentally measure an unopened or abandoned iterator.<br/>
    /// The preceding parity proof remains authoritative for exact order; this helper attributes only the steady-state directional reader cost.<br/>
    /// </summary>
    /// <param name="index">The completed exact-string index to enumerate.<br/></param>
    /// <param name="direction">The requested physical traversal direction.<br/></param>
    /// <param name="expectedCount">The exact number of identities that must be consumed.<br/></param>
    /// <returns>The elapsed time for complete identity enumeration.<br/></returns>
    private static TimeSpan MeasureVarKeyScalar8SortedBuildTraversal(
        LibraDexStringScalar8Index index,
        QueryDirection direction,
        int expectedCount)
    {
        int count = 0;
        ulong checksum = 1469598103934665603UL;
        long started = Stopwatch.GetTimestamp();
        foreach (ulong identity in index.IterateExactIdentities(direction))
        {
            checksum = unchecked((checksum ^ identity) * 1099511628211UL);
            count++;
        }
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        if (count != expectedCount)
        {
            throw new InvalidDataException($"The VS8 {direction} traversal consumed {count:N0} identities instead of {expectedCount:N0}.");
        }

        GC.KeepAlive(checksum);
        return elapsed;
    }
}
