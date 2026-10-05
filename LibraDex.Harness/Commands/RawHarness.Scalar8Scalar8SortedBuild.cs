using System.Diagnostics;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves the native `SS8-8` sorted builder against ordinary shelves, an oversized exact-key terminal chain, child routers, reopen, contract failures, and the established per-item mutation control.<br/>
    /// The same deterministic globally sorted tuple array feeds both paths so elapsed time and file bytes compare identical logical work.<br/>
    /// </summary>
    /// <param name="args">Harness options including `--path` and optional `--count`.<br/></param>
    /// <returns>Zero when native live/reopen parity, failure isolation, and mutation-control parity all succeed.<br/></returns>
    private static int RunScalar8Scalar8SortedBuildSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "ss8-8-sorted-build-sanity.lbdx"));
        int count = GetIntOption(args, "--count", 50_000);
        if (count < 10_000)
            throw new ArgumentOutOfRangeException(nameof(args), count, "The SS8-8 sorted-build proof requires at least 10,000 tuples.");

        string controlPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + "-control" + Path.GetExtension(path));
        string failurePath = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + "-failure" + Path.GetExtension(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        File.Delete(controlPath);
        File.Delete(failurePath);

        LibraDexSortedTuple<ulong, ulong>[] tuples = CreateScalar8Scalar8SortedBuildTuples(count);
        LibraDexSortedBuildDiagnostics native;
        var nativeWall = Stopwatch.StartNew();
        using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> index = catalog.Indexes.IndexSet("native").Define("value").Create<ulong, ulong>(
                IndexKeys.NonUnique,
                new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
            native = index.BuildFromSorted(tuples);
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, "native live");
        }
        nativeWall.Stop();

        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> index = reopened.Indexes.IndexSet("native").Define("value").Open<ulong, ulong>();
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, "native reopen");
        }

        long nativeBeforeCompactionBytes = new FileInfo(path).Length;
        LibraDexCompactionResult compaction = LibraDexCatalogCompactor.Compact(
            path,
            new LibraDexCompactionOptions { CatalogOptions = CatalogOptions.UInt64Identities },
            CancellationToken.None);
        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> index = reopened.Indexes.IndexSet("native").Define("value").Open<ulong, ulong>();
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, "native compacted reopen");
        }

        var controlWall = Stopwatch.StartNew();
        using (Catalog catalog = Catalog.Create(controlPath, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> index = catalog.Indexes.IndexSet("control").Define("value").Create<ulong, ulong>(
                IndexKeys.NonUnique,
                new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
            for (int i = 0; i < tuples.Length; i++)
            {
                LibraDexSortedTuple<ulong, ulong> tuple = tuples[i];
                LibraDexGenericInsertResult result = index.Insert(tuple.Key, tuple.Identity);
                if (!result.Inserted)
                    throw new InvalidDataException($"The SS8-8 mutation control rejected tuple ordinal {i}.");
            }
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, "mutation live");
        }
        controlWall.Stop();

        using (Catalog reopened = Catalog.Open(controlPath, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> index = reopened.Indexes.IndexSet("control").Define("value").Open<ulong, ulong>();
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, "mutation reopen");
        }

        ProveScalar8Scalar8SortedBuildFailureIsolation(failurePath, tuples);
        ProveScalar8Scalar8SortedBuildRandomizedBoundaries();
        ProveScalar8Scalar8UnorderedRecovery(tuples);
        ProveScalar8Scalar8GrowthCanaryRejection();
        ProveMixedStorageAssessmentBoundary(Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + "-mixed-assessment" + Path.GetExtension(path)));

        long nativeBytes = new FileInfo(path).Length;
        long controlBytes = new FileInfo(controlPath).Length;
        Console.WriteLine(
            $"ss8-8-sorted-build-sanity path={path} tuples={count:N0} " +
            $"ordinaryShelves={native.OrdinaryShelfCount:N0} terminalRoots={native.TerminalRootCount:N0} terminalShelves={native.TerminalShelfCount:N0} routers={native.RouterCount:N0} " +
            $"reachableBytes={native.ReachableTopologyBytes:N0} bytesPerTuple={native.BytesPerTuple:N3} canary={native.GrowthCanaryUtilization:P3} " +
            $"storage={native.StorageBuildTime.TotalMilliseconds:N3}ms publish={native.RootPublicationTime.TotalMilliseconds:N3}ms total={native.TotalTime.TotalMilliseconds:N3}ms wall={nativeWall.Elapsed.TotalMilliseconds:N3}ms " +
            $"controlWall={controlWall.Elapsed.TotalMilliseconds:N3}ms speedup={(controlWall.Elapsed.TotalMilliseconds / nativeWall.Elapsed.TotalMilliseconds):N2}x " +
            $"nativeBeforeCompactBytes={nativeBeforeCompactionBytes:N0} nativeAfterCompactBytes={nativeBytes:N0} compactTuples={compaction.TupleCount:N0} " +
            $"controlBytes={controlBytes:N0} precompactSizeRatio={(double)nativeBeforeCompactionBytes / controlBytes:N3}");

        if (native.TupleCount != count ||
            native.OrdinaryShelfCount == 0 ||
            native.TerminalRootCount == 0 ||
            native.TerminalShelfCount < 2 ||
            native.RouterCount == 0 ||
            native.ReachableTopologyBytes <= 0 ||
            native.ReachableTopologyBytes >= native.GrowthCanaryLimitBytes)
        {
            throw new InvalidDataException("The SS8-8 native sorted-build fixture did not exercise every required topology branch.");
        }

        return 0;
    }

    /// <summary>
    /// Proves the compactor's recovery-only object bridge with a deterministic reversed source that the ordinary sorted contract must reject.<br/>
    /// Recovery must encode and order the source before invoking the native builder, preserve every tuple exactly, and expose the result through the ordinary sorted reader.<br/>
    /// </summary>
    /// <param name="source">The complete sorted control population from which a bounded recovery fixture is derived.<br/></param>
    private static void ProveScalar8Scalar8UnorderedRecovery(
        ReadOnlySpan<LibraDexSortedTuple<ulong, ulong>> source)
    {
        int count = Math.Min(source.Length, 10_000);
        var expected = new LibraDexSortedTuple<ulong, ulong>[count];
        source[..count].CopyTo(expected);
        var unordered = new LibraDexObjectTuple[count];
        for (int i = 0; i < count; i++)
        {
            LibraDexSortedTuple<ulong, ulong> tuple = expected[count - 1 - i];
            unordered[i] = new LibraDexObjectTuple(tuple.Key, tuple.Identity);
        }

        using Catalog catalog = Catalog.CreateMemory(CatalogOptions.UInt64Identities);
        LibraDexIndex<ulong, ulong> index = catalog.Indexes.IndexSet("recovery").Define("unordered").Create<ulong, ulong>(
            IndexKeys.NonUnique,
            new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
        ILibraDexNativeSortedBuild recovery = index;
        if (!recovery.TryBuildFromUnorderedObjects(unordered, CancellationToken.None, out long recoveredCount) ||
            recoveredCount != count)
        {
            throw new InvalidDataException(
                $"The unordered SS8-8 recovery bridge returned count {recoveredCount:N0} instead of {count:N0}.");
        }

        ValidateScalar8Scalar8SortedBuildReader(index, expected, "unordered native recovery");
    }

    /// <summary>
    /// Proves that the shape-specific native-build growth guard rejects a candidate above its resolved ceiling.<br/>
    /// Production builders invoke the same guard after all candidate extents are reserved but before child durability or root/directory publication.<br/>
    /// </summary>
    private static void ProveScalar8Scalar8GrowthCanaryRejection()
    {
        bool rejected = false;
        try
        {
            LibraDexFileSession.ValidateScalar8Scalar8GrowthCanary(
                tupleCount: 50_000,
                reachableTopologyBytes: 25_600_001,
                growthCanaryLimitBytes: 25_600_000);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        if (!rejected)
            throw new InvalidDataException("The SS8-8 native-build growth canary accepted a candidate above its conservative limit.");
    }

    /// <summary>
    /// Proves exact whole-catalog accounting for a Wherzit-shaped fixed/text catalog, including an empty-string key-state route.<br/>
    /// A supported wide-scalar shape must preserve complete accounting; adding one unsupported variable-blob shape must then retain the exact supported subtotal while suppressing catalog-wide unreachable bytes and amplification.<br/>
    /// </summary>
    /// <param name="path">Temporary file-backed catalog path.<br/></param>
    private static void ProveMixedStorageAssessmentBoundary(string path)
    {
        File.Delete(path);
        try
        {
            using Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities);
            using LibraDexIndex<long, ulong> length = catalog.Indexes.IndexSet("mixed").Define("length").Int64Keys<ulong>().Create();
            using LibraDexStringScalar8Index name = catalog.Indexes.IndexSet("mixed").Define("name").String.Create(StringKeys.Exact);
            LibraDexSortedTuple<long, ulong>[] lengths =
            [
                new LibraDexSortedTuple<long, ulong>(10, 1),
                new LibraDexSortedTuple<long, ulong>(20, 2),
                new LibraDexSortedTuple<long, ulong>(30, 3)
            ];
            _ = length.BuildFromSorted(lengths);
            ValidateGenericInsert(name.Insert("alpha", 1), "mixed storage assessment text insert");
            ValidateGenericInsert(name.Insert(string.Empty, 2), "mixed storage assessment empty-key insert");

            LibraDexCatalogStorageAssessment storage = catalog.Maintenance.Assess().Storage;
            if (!storage.IsComplete ||
                storage.UnreachableBytes is not >= 0 ||
                storage.AmplificationRatio is not >= 1 ||
                storage.UnsupportedIndexCount != 0 ||
                storage.FixedTopologyComponents.Count != 1 ||
                storage.VariableTextTopologyComponents.Count != 1 ||
                storage.FixedTopologyComponents[0].TupleCount != lengths.Length ||
                storage.VariableTextTopologyComponents[0].TupleCount != 2 ||
                storage.VariableTextTopologyComponents[0].KeyStateRootCount != 1 ||
                storage.PhysicalBytes != storage.KnownReachableBytes + storage.UnreachableBytes.Value)
            {
                throw new InvalidDataException(
                    $"Mixed fixed/text storage assessment mismatch: complete={storage.IsComplete}, unsupported={storage.UnsupportedIndexCount}, fixed={storage.FixedTopologyComponents.Count}, text={storage.VariableTextTopologyComponents.Count}, physical={storage.PhysicalBytes}, reachable={storage.KnownReachableBytes}, unreachable={storage.UnreachableBytes}, amplification={storage.AmplificationRatio}.");
            }

            using LibraDexIndex<Guid, ulong> supportedWide = catalog.Indexes.IndexSet("mixed").Define("wide").GuidKeys<ulong>().Create();
            ValidateGenericInsert(supportedWide.Insert(Guid.NewGuid(), 99), "mixed storage assessment supported wide insert");
            LibraDexCatalogStorageAssessment completeWide = catalog.Maintenance.Assess().Storage;
            if (!completeWide.IsComplete || completeWide.UnsupportedIndexCount != 0 ||
                completeWide.FixedTopologyComponents.Count != 2 || completeWide.VariableTextTopologyComponents.Count != 1 ||
                completeWide.UnreachableBytes is not >= 0 || completeWide.AmplificationRatio is not >= 1)
                throw new InvalidDataException("Mixed fixed/text/wide storage assessment did not remain complete.");

            using LibraDexVariableBlobScalar8Index<ulong> unsupported = catalog.Indexes.IndexSet("mixed").Define("variableBlob").Blob.Variable<ulong>(maxKeyBytes: 16).Create();
            ValidateGenericInsert(unsupported.Insert(new byte[] { 1 }, 100), "mixed storage assessment unsupported variable-blob insert");
            LibraDexCatalogStorageAssessment partial = catalog.Maintenance.Assess().Storage;
            if (partial.IsComplete ||
                partial.UnreachableBytes.HasValue ||
                partial.AmplificationRatio.HasValue ||
                partial.UnsupportedIndexCount != 1 ||
                partial.FixedTopologyComponents.Count != 2 ||
                partial.VariableTextTopologyComponents.Count != 1 ||
                partial.KnownReachableBytes <= partial.CatalogOverheadReachableBytes)
            {
                throw new InvalidDataException(
                    $"Mixed unsupported storage assessment fabricated completeness or lost its exact subtotal: complete={partial.IsComplete}, unsupported={partial.UnsupportedIndexCount}, fixed={partial.FixedTopologyComponents.Count}, text={partial.VariableTextTopologyComponents.Count}, physical={partial.PhysicalBytes}, reachable={partial.KnownReachableBytes}, unreachable={partial.UnreachableBytes}, amplification={partial.AmplificationRatio}.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Creates one globally sorted tuple array with a 6,000-identity exact key and broad high-byte distribution for ordinary-shelf/root packing.<br/>
    /// Identity values remain unique across the whole array so the fixture also exercises single-key-per-identity validation.<br/>
    /// </summary>
    private static LibraDexSortedTuple<ulong, ulong>[] CreateScalar8Scalar8SortedBuildTuples(int count)
    {
        const int terminalIdentityCount = 6_000;
        var tuples = new LibraDexSortedTuple<ulong, ulong>[count];
        for (int i = 0; i < terminalIdentityCount; i++)
            tuples[i] = new LibraDexSortedTuple<ulong, ulong>(0x1000_0000_0000_0042UL, checked((ulong)i + 1));

        int ordinaryCount = count - terminalIdentityCount;
        for (int i = 0; i < ordinaryCount; i++)
        {
            ulong prefix = checked((ulong)(0x20 + ((long)i * 0xC0 / ordinaryCount)));
            ulong key = (prefix << 56) | checked((ulong)i + 1);
            tuples[terminalIdentityCount + i] = new LibraDexSortedTuple<ulong, ulong>(key, checked((ulong)terminalIdentityCount + (ulong)i + 1));
        }

        Array.Sort(tuples, static (left, right) =>
        {
            int keyOrder = left.Key.CompareTo(right.Key);
            return keyOrder != 0 ? keyOrder : left.Identity.CompareTo(right.Identity);
        });
        return tuples;
    }

    /// <summary>
    /// Reads one complete public index and checks exact ordinal parity with the source sorted array.<br/>
    /// </summary>
    private static void ValidateScalar8Scalar8SortedBuildReader(
        LibraDexIndex<ulong, ulong> index,
        LibraDexSortedTuple<ulong, ulong>[] expected,
        string phase)
    {
        using LibraDexRangeReader<ulong, ulong> reader = index.OpenReader();
        int ordinal = 0;
        while (reader.MoveNext())
        {
            if ((uint)ordinal >= expected.Length)
                throw new InvalidDataException($"The {phase} SS8-8 reader returned an unexpected tuple after ordinal {ordinal:N0}.");
            LibraDexSortedTuple<ulong, ulong> tuple = expected[ordinal];
            if (reader.CurrentKey != tuple.Key || reader.CurrentIdentity != tuple.Identity)
            {
                throw new InvalidDataException(
                    $"The {phase} SS8-8 reader diverged at ordinal {ordinal:N0}: expected 0x{tuple.Key:X16}/0x{tuple.Identity:X16}, got 0x{reader.CurrentKey:X16}/0x{reader.CurrentIdentity:X16}.");
            }
            ordinal++;
        }

        if (ordinal != expected.Length)
            throw new InvalidDataException($"The {phase} SS8-8 reader returned {ordinal:N0} of {expected.Length:N0} tuples.");
    }

    /// <summary>
    /// Proves malformed input, unique-key conflicts, single-key-per-identity conflicts, and non-empty-root reuse fail without making rejected tuples visible.<br/>
    /// The first inverted attempt is followed by a valid build on the same index, proving pending reservations were discarded rather than leaking into the later publication.<br/>
    /// </summary>
    private static void ProveScalar8Scalar8SortedBuildFailureIsolation(
        string path,
        LibraDexSortedTuple<ulong, ulong>[] fullTuples)
    {
        using Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities);
        LibraDexIndex<ulong, ulong> retry = catalog.Indexes.IndexSet("failure").Define("retry").Create<ulong, ulong>();
        LibraDexSortedTuple<ulong, ulong>[] valid = fullTuples.AsSpan(0, 10_000).ToArray();
        LibraDexSortedTuple<ulong, ulong>[] inverted = valid.ToArray();
        (inverted[0], inverted[1]) = (inverted[1], inverted[0]);
        ExpectScalar8Scalar8SortedBuildFailure(
            () => retry.BuildFromSorted(inverted),
            "inverted tuple input");
        _ = retry.BuildFromSorted(valid);
        ValidateScalar8Scalar8SortedBuildReader(retry, valid, "retry after inversion");
        ExpectScalar8Scalar8SortedBuildFailure(
            () => retry.BuildFromSorted(valid),
            "non-empty root reuse");

        LibraDexIndex<ulong, ulong> unique = catalog.Indexes.IndexSet("failure").Define("unique").Create<ulong, ulong>(IndexKeys.Unique);
        ExpectScalar8Scalar8SortedBuildFailure(
            () => unique.BuildFromSorted(
            [
                new LibraDexSortedTuple<ulong, ulong>(7, 1),
                new LibraDexSortedTuple<ulong, ulong>(7, 2)
            ]),
            "unique-key conflict");

        LibraDexIndex<ulong, ulong> oneKey = catalog.Indexes.IndexSet("failure").Define("oneKey").Create<ulong, ulong>(
            options: new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
        ExpectScalar8Scalar8SortedBuildFailure(
            () => oneKey.BuildFromSorted(
            [
                new LibraDexSortedTuple<ulong, ulong>(7, 1),
                new LibraDexSortedTuple<ulong, ulong>(8, 1)
            ]),
            "single-key-per-identity conflict");
    }

    /// <summary>
    /// Exercises deterministic randomized key distributions across one-shelf, split-router, and terminal-identity boundaries using the smaller memory-catalog profile.<br/>
    /// Every fixture is sorted once, built through the public API, and read back in exact ordinal order.<br/>
    /// </summary>
    private static void ProveScalar8Scalar8SortedBuildRandomizedBoundaries()
    {
        int[] counts = [1, 257, 4_096, 10_001];
        for (int fixture = 0; fixture < counts.Length; fixture++)
        {
            int count = counts[fixture];
            var random = new Random(0x5A88 + fixture);
            var tuples = new LibraDexSortedTuple<ulong, ulong>[count];
            for (int i = 0; i < count; i++)
            {
                ulong key = checked((ulong)random.Next(0, fixture + 2));
                tuples[i] = new LibraDexSortedTuple<ulong, ulong>(key, checked((ulong)i + 1));
            }
            Array.Sort(tuples, static (left, right) =>
            {
                int keyOrder = left.Key.CompareTo(right.Key);
                return keyOrder != 0 ? keyOrder : left.Identity.CompareTo(right.Identity);
            });

            using Catalog catalog = Catalog.CreateMemory(CatalogOptions.UInt64Identities);
            LibraDexIndex<ulong, ulong> index = catalog.Indexes.IndexSet("random").Define("value").Create<ulong, ulong>(
                options: new IndexOptions { IdentityKeyMultiplicity = IdentityKeyMultiplicity.SingleKeyPerIdentity });
            LibraDexSortedBuildDiagnostics diagnostics = index.BuildFromSorted(tuples);
            ValidateScalar8Scalar8SortedBuildReader(index, tuples, $"randomized boundary {count:N0}");
            if (diagnostics.TupleCount != count)
                throw new InvalidDataException($"The randomized SS8-8 boundary fixture built {diagnostics.TupleCount:N0} of {count:N0} tuples.");
        }
    }

    /// <summary>
    /// Requires one native sorted-build action to fail before it can publish a root.<br/>
    /// </summary>
    private static void ExpectScalar8Scalar8SortedBuildFailure(Action action, string phase)
    {
        bool failed = false;
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            failed = true;
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        if (!failed)
            throw new InvalidDataException($"The SS8-8 sorted builder accepted the {phase} fixture.");
    }

    /// <summary>
    /// Proves that two populated `SS8-8` indexes can prepare complete detached roots and become visible together through one fixed-directory publication.<br/>
    /// Old handles must retain their prior generation, new handles and reopen must see both replacement generations, and a later-sibling validation failure must leave both current roots unchanged.<br/>
    /// </summary>
    /// <param name="args">Harness options including optional `--path` and `--count`.<br/></param>
    /// <returns>Zero when grouped visibility, old-handle snapshot behavior, reopen parity, and failure isolation pass.<br/></returns>
    private static int RunScalar8Scalar8DetachedReplacementSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "ss8-8-detached-replacement-sanity.lbdx"));
        int count = GetIntOption(args, "--count", 20_000);
        if (count < 1_000)
            throw new ArgumentOutOfRangeException(nameof(args), count, "The detached replacement proof requires at least 1,000 tuples.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        LibraDexSortedTuple<ulong, ulong>[] oldLeft = CreateScalar8Scalar8ReplacementTuples(count, 0x10, 1);
        LibraDexSortedTuple<ulong, ulong>[] oldRight = CreateScalar8Scalar8ReplacementTuples(count + 257, 0x30, 1_000_001);
        LibraDexSortedTuple<ulong, ulong>[] newLeft = CreateScalar8Scalar8ReplacementTuples(count + 509, 0x50, 2_000_001);
        LibraDexSortedTuple<ulong, ulong>[] newRight = CreateScalar8Scalar8ReplacementTuples(count + 1_021, 0x70, 3_000_001);

        Scalar8Scalar8DetachedBuildGroupResult replacement;
        LibraDexCatalogStorageAssessment fragmentedStorage;
        LibraDexCatalogStorageAssessment compactedStorage;
        long priorLeftRoot;
        long priorRightRoot;
        long replacementLeftRoot;
        long replacementRightRoot;
        using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> left = catalog.Indexes.IndexSet("replace").Define("left").Create<ulong, ulong>();
            LibraDexIndex<ulong, ulong> right = catalog.Indexes.IndexSet("replace").Define("right").Create<ulong, ulong>();
            _ = left.BuildFromSorted(oldLeft);
            _ = right.BuildFromSorted(oldRight);
            priorLeftRoot = left.RootRouterOffset;
            priorRightRoot = right.RootRouterOffset;

            Scalar8Scalar8DetachedBuildRequest[] requests =
            [
                left.CreateDetachedSortedBuildRequest(newLeft),
                right.CreateDetachedSortedBuildRequest(newRight)
            ];
            replacement = left.ReplaceFromSortedEncoded(requests);
            if (replacement.Builds.Count != 2)
                throw new InvalidDataException($"Detached replacement returned {replacement.Builds.Count:N0} builds instead of two.");
            replacementLeftRoot = replacement.Builds.Single(build => build.SlotIndex == left.SlotIndex).ReplacementRootRouterOffset;
            replacementRightRoot = replacement.Builds.Single(build => build.SlotIndex == right.SlotIndex).ReplacementRootRouterOffset;
            if (replacementLeftRoot == priorLeftRoot || replacementRightRoot == priorRightRoot)
                throw new InvalidDataException("Detached replacement reused a live root instead of allocating a new generation.");

            ValidateScalar8Scalar8SortedBuildReader(left, oldLeft, "detached old left handle");
            ValidateScalar8Scalar8SortedBuildReader(right, oldRight, "detached old right handle");
            LibraDexIndex<ulong, ulong> currentLeft = catalog.Indexes.IndexSet("replace").Define("left").Open<ulong, ulong>();
            LibraDexIndex<ulong, ulong> currentRight = catalog.Indexes.IndexSet("replace").Define("right").Open<ulong, ulong>();
            if (currentLeft.RootRouterOffset != replacementLeftRoot || currentRight.RootRouterOffset != replacementRightRoot)
                throw new InvalidDataException("New handles did not resolve both replacement directory roots.");
            ValidateScalar8Scalar8SortedBuildReader(currentLeft, newLeft, "detached current left");
            ValidateScalar8Scalar8SortedBuildReader(currentRight, newRight, "detached current right");

            LibraDexSortedTuple<ulong, ulong>[] laterLeft = CreateScalar8Scalar8ReplacementTuples(count + 17, 0x90, 4_000_001);
            LibraDexSortedTuple<ulong, ulong>[] invertedRight = CreateScalar8Scalar8ReplacementTuples(count + 19, 0xB0, 5_000_001);
            (invertedRight[0], invertedRight[1]) = (invertedRight[1], invertedRight[0]);
            bool failed = false;
            try
            {
                Scalar8Scalar8DetachedBuildRequest[] rejected =
                [
                    currentLeft.CreateDetachedSortedBuildRequest(laterLeft),
                    currentRight.CreateDetachedSortedBuildRequest(invertedRight)
                ];
                _ = currentLeft.ReplaceFromSortedEncoded(rejected);
            }
            catch (ArgumentException)
            {
                failed = true;
            }
            if (!failed)
                throw new InvalidDataException("The detached replacement proof did not reject the later sibling's inverted source.");

            LibraDexIndex<ulong, ulong> afterFailureLeft = catalog.Indexes.IndexSet("replace").Define("left").Open<ulong, ulong>();
            LibraDexIndex<ulong, ulong> afterFailureRight = catalog.Indexes.IndexSet("replace").Define("right").Open<ulong, ulong>();
            if (afterFailureLeft.RootRouterOffset != replacementLeftRoot || afterFailureRight.RootRouterOffset != replacementRightRoot)
                throw new InvalidDataException("A failed sibling preparation changed one or more live directory roots.");
            ValidateScalar8Scalar8SortedBuildReader(afterFailureLeft, newLeft, "detached failure-isolated left");
            ValidateScalar8Scalar8SortedBuildReader(afterFailureRight, newRight, "detached failure-isolated right");

            fragmentedStorage = catalog.Maintenance.Assess().Storage;
            long expectedTuples = checked((long)newLeft.Length + newRight.Length);
            if (!fragmentedStorage.IsComplete ||
                fragmentedStorage.UnsupportedIndexCount != 0 ||
                fragmentedStorage.FixedTopologyComponents.Count != 2 ||
                fragmentedStorage.FixedTopologyComponents.Sum(static component => component.TupleCount) != expectedTuples ||
                fragmentedStorage.UnreachableBytes is not > 0 ||
                fragmentedStorage.PhysicalBytes is not long fragmentedPhysical ||
                fragmentedPhysical != fragmentedStorage.KnownReachableBytes + fragmentedStorage.UnreachableBytes.Value ||
                fragmentedStorage.AmplificationRatio is not > 1)
            {
                throw new InvalidDataException(
                    $"Detached storage assessment mismatch: complete={fragmentedStorage.IsComplete}, unsupported={fragmentedStorage.UnsupportedIndexCount}, components={fragmentedStorage.FixedTopologyComponents.Count}, physical={fragmentedStorage.PhysicalBytes}, reachable={fragmentedStorage.KnownReachableBytes}, unreachable={fragmentedStorage.UnreachableBytes}, amplification={fragmentedStorage.AmplificationRatio}.");
            }
        }

        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> left = reopened.Indexes.IndexSet("replace").Define("left").Open<ulong, ulong>();
            LibraDexIndex<ulong, ulong> right = reopened.Indexes.IndexSet("replace").Define("right").Open<ulong, ulong>();
            if (left.RootRouterOffset != replacementLeftRoot || right.RootRouterOffset != replacementRightRoot)
                throw new InvalidDataException("Reopen did not retain both detached replacement roots.");
            ValidateScalar8Scalar8SortedBuildReader(left, newLeft, "detached reopened left");
            ValidateScalar8Scalar8SortedBuildReader(right, newRight, "detached reopened right");
        }

        long beforeCompactionBytes = new FileInfo(path).Length;
        LibraDexCompactionResult compaction = LibraDexCatalogCompactor.Compact(
            path,
            new LibraDexCompactionOptions { CatalogOptions = CatalogOptions.UInt64Identities },
            CancellationToken.None);
        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            ValidateScalar8Scalar8SortedBuildReader(
                reopened.Indexes.IndexSet("replace").Define("left").Open<ulong, ulong>(),
                newLeft,
                "detached compacted left");
            ValidateScalar8Scalar8SortedBuildReader(
                reopened.Indexes.IndexSet("replace").Define("right").Open<ulong, ulong>(),
                newRight,
                "detached compacted right");
            compactedStorage = reopened.Maintenance.Assess().Storage;
            if (!compactedStorage.IsComplete ||
                compactedStorage.UnsupportedIndexCount != 0 ||
                compactedStorage.UnreachableBytes != 0 ||
                compactedStorage.PhysicalBytes != compactedStorage.KnownReachableBytes ||
                compactedStorage.AmplificationRatio != 1)
            {
                throw new InvalidDataException(
                    $"Compacted storage assessment mismatch: complete={compactedStorage.IsComplete}, unsupported={compactedStorage.UnsupportedIndexCount}, physical={compactedStorage.PhysicalBytes}, reachable={compactedStorage.KnownReachableBytes}, unreachable={compactedStorage.UnreachableBytes}, amplification={compactedStorage.AmplificationRatio}.");
            }
        }

        long afterCompactionBytes = new FileInfo(path).Length;
        Console.WriteLine(
            $"ss8-8-detached-replacement-sanity path={path} old={oldLeft.Length + oldRight.Length:N0} replacement={newLeft.Length + newRight.Length:N0} " +
            $"priorRoots={priorLeftRoot}/{priorRightRoot} replacementRoots={replacementLeftRoot}/{replacementRightRoot} " +
            $"builds={replacement.Builds.Count:N0} publish={replacement.DirectoryPublicationTime.TotalMilliseconds:N3}ms " +
            $"beforeCompactBytes={beforeCompactionBytes:N0} reachable={fragmentedStorage.KnownReachableBytes:N0} unreachable={fragmentedStorage.UnreachableBytes:N0} amplification={fragmentedStorage.AmplificationRatio:N3} " +
            $"afterCompactBytes={afterCompactionBytes:N0} compactReachable={compactedStorage.KnownReachableBytes:N0} compactTuples={compaction.TupleCount:N0}");
        return 0;
    }

    /// <summary>
    /// Creates one deterministic globally sorted unique-identity replacement population inside a selected high-byte key band.<br/>
    /// </summary>
    /// <param name="count">Tuple count.<br/></param>
    /// <param name="prefixBase">First high-byte key prefix for the population.<br/></param>
    /// <param name="identityBase">First public identity value.<br/></param>
    /// <returns>Typed tuples in exact key-then-identity order.<br/></returns>
    private static LibraDexSortedTuple<ulong, ulong>[] CreateScalar8Scalar8ReplacementTuples(
        int count,
        byte prefixBase,
        ulong identityBase)
    {
        var tuples = new LibraDexSortedTuple<ulong, ulong>[count];
        for (int i = 0; i < count; i++)
        {
            byte prefix = checked((byte)(prefixBase + (i & 0x0F)));
            ulong key = ((ulong)prefix << 56) | (checked((ulong)i) >> 4);
            tuples[i] = new LibraDexSortedTuple<ulong, ulong>(key, checked(identityBase + (ulong)i));
        }
        Array.Sort(tuples, static (left, right) =>
        {
            int keyOrder = left.Key.CompareTo(right.Key);
            return keyOrder != 0 ? keyOrder : left.Identity.CompareTo(right.Identity);
        });
        return tuples;
    }

    /// <summary>
    /// Proves one scalar and two exact-string index generations remain hidden until a shared catalog-directory publication succeeds.<br/>
    /// A deliberately invalid final text sibling and a pre-cancelled replacement must leave every previously published slot unchanged live and after reopen.<br/>
    /// </summary>
    /// <param name="args">Harness options including optional <c>--path</c> and <c>--count</c>.<br/></param>
    /// <returns>Zero when mixed publication, old-handle isolation, failure isolation, cancellation, and reopen parity pass.<br/></returns>
    private static int RunMixedNativeDetachedReplacementSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "mixed-native-detached-replacement-sanity.lbdx"));
        int count = GetIntOption(args, "--count", 12_000);
        if (count < 1_000)
            throw new ArgumentOutOfRangeException(nameof(args), count, "The mixed detached replacement proof requires at least 1,000 tuples.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        LibraDexSortedTuple<ulong, ulong>[] oldScalar = CreateScalar8Scalar8ReplacementTuples(count, 0x10, 1);
        LibraDexSortedTuple<ulong, ulong>[] newScalar = CreateScalar8Scalar8ReplacementTuples(count + 131, 0x30, 1_000_001);
        VarKeyScalar8SortedTuple[] oldPath;
        VarKeyScalar8SortedTuple[] oldName;
        VarKeyScalar8SortedTuple[] newPath;
        VarKeyScalar8SortedTuple[] newName;
        long[] publishedRoots;

        using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> scalar = catalog.Indexes.IndexSet("mixed").Define("length").Create<ulong, ulong>();
            LibraDexStringScalar8Index pathIndex = catalog.Indexes.IndexSet("mixed").Define("path").StringKeys().CreateOrOpen(StringKeys.Exact);
            LibraDexStringScalar8Index nameIndex = catalog.Indexes.IndexSet("mixed").Define("name").StringKeys().CreateOrOpen(StringKeys.Exact);
            _ = scalar.BuildFromSorted(oldScalar);
            oldPath = CreateVarKeyScalar8ReplacementTuples(pathIndex, count + 17, "old-path", 2_000_001);
            oldName = CreateVarKeyScalar8ReplacementTuples(nameIndex, count + 29, "old-name", 3_000_001);
            _ = pathIndex.ReplaceExactFromSortedEncoded(new VarKeyScalar8SortedArraySource(oldPath), [], [], true, true, CancellationToken.None);
            _ = nameIndex.ReplaceExactFromSortedEncoded(new VarKeyScalar8SortedArraySource(oldName), [], [], true, true, CancellationToken.None);

            newPath = CreateVarKeyScalar8ReplacementTuples(pathIndex, count + 257, "new-path", 4_000_001);
            newName = CreateVarKeyScalar8ReplacementTuples(nameIndex, count + 509, "new-name", 5_000_001);
            NativeSortedDetachedBuildGroupResult replacement = pathIndex.ReplaceNativeSortedRootsAtomically(
                [scalar.CreateDetachedSortedBuildRequest(newScalar)],
                [
                    pathIndex.CreateDetachedExactSortedBuildRequest(new VarKeyScalar8SortedArraySource(newPath), true, true),
                    nameIndex.CreateDetachedExactSortedBuildRequest(new VarKeyScalar8SortedArraySource(newName), true, true)
                ]);
            if (replacement.ScalarBuilds.Count != 1 || replacement.VariableBuilds.Count != 2)
                throw new InvalidDataException("Mixed detached replacement did not return one scalar and two variable-key builds.");

            ValidateScalar8Scalar8SortedBuildReader(scalar, oldScalar, "mixed detached old scalar handle");
            ValidateVarKeyScalar8ReplacementReader(pathIndex, oldPath, "mixed detached old path handle");
            ValidateVarKeyScalar8ReplacementReader(nameIndex, oldName, "mixed detached old name handle");

            LibraDexIndex<ulong, ulong> currentScalar = catalog.Indexes.IndexSet("mixed").Define("length").Open<ulong, ulong>();
            LibraDexStringScalar8Index currentPath = catalog.Indexes.IndexSet("mixed").Define("path").StringKeys().CreateOrOpen(StringKeys.Exact);
            LibraDexStringScalar8Index currentName = catalog.Indexes.IndexSet("mixed").Define("name").StringKeys().CreateOrOpen(StringKeys.Exact);
            ValidateScalar8Scalar8SortedBuildReader(currentScalar, newScalar, "mixed detached current scalar");
            ValidateVarKeyScalar8ReplacementReader(currentPath, newPath, "mixed detached current path");
            ValidateVarKeyScalar8ReplacementReader(currentName, newName, "mixed detached current name");
            publishedRoots = [currentScalar.RootRouterOffset, replacement.VariableBuilds[0].ReplacementRootRouterOffset, replacement.VariableBuilds[1].ReplacementRootRouterOffset];

            VarKeyScalar8SortedTuple[] rejectedName = CreateVarKeyScalar8ReplacementTuples(currentName, count + 43, "rejected-name", 6_000_001);
            (rejectedName[0], rejectedName[1]) = (rejectedName[1], rejectedName[0]);
            bool invalidFailed = false;
            try
            {
                _ = currentPath.ReplaceNativeSortedRootsAtomically(
                    [currentScalar.CreateDetachedSortedBuildRequest(CreateScalar8Scalar8ReplacementTuples(count + 31, 0x50, 7_000_001))],
                    [
                        currentPath.CreateDetachedExactSortedBuildRequest(new VarKeyScalar8SortedArraySource(CreateVarKeyScalar8ReplacementTuples(currentPath, count + 37, "rejected-path", 8_000_001)), true, true),
                        currentName.CreateDetachedExactSortedBuildRequest(new VarKeyScalar8SortedArraySource(rejectedName), true, true)
                    ]);
            }
            catch (ArgumentException)
            {
                invalidFailed = true;
            }
            if (!invalidFailed)
                throw new InvalidDataException("Mixed detached replacement accepted the invalid final sibling.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool cancellationFailed = false;
            try
            {
                _ = currentPath.ReplaceNativeSortedRootsAtomically(
                    [currentScalar.CreateDetachedSortedBuildRequest(newScalar)],
                    [currentPath.CreateDetachedExactSortedBuildRequest(new VarKeyScalar8SortedArraySource(newPath), true, true)],
                    cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationFailed = true;
            }
            if (!cancellationFailed)
                throw new InvalidDataException("Mixed detached replacement ignored pre-publication cancellation.");

            LibraDexIndex<ulong, ulong> afterFailureScalar = catalog.Indexes.IndexSet("mixed").Define("length").Open<ulong, ulong>();
            LibraDexStringScalar8Index afterFailurePath = catalog.Indexes.IndexSet("mixed").Define("path").StringKeys().CreateOrOpen(StringKeys.Exact);
            LibraDexStringScalar8Index afterFailureName = catalog.Indexes.IndexSet("mixed").Define("name").StringKeys().CreateOrOpen(StringKeys.Exact);
            ValidateScalar8Scalar8SortedBuildReader(afterFailureScalar, newScalar, "mixed detached failure-isolated scalar");
            ValidateVarKeyScalar8ReplacementReader(afterFailurePath, newPath, "mixed detached failure-isolated path");
            ValidateVarKeyScalar8ReplacementReader(afterFailureName, newName, "mixed detached failure-isolated name");
        }

        using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
        {
            LibraDexIndex<ulong, ulong> scalar = reopened.Indexes.IndexSet("mixed").Define("length").Open<ulong, ulong>();
            LibraDexStringScalar8Index pathIndex = reopened.Indexes.IndexSet("mixed").Define("path").StringKeys().CreateOrOpen(StringKeys.Exact);
            LibraDexStringScalar8Index nameIndex = reopened.Indexes.IndexSet("mixed").Define("name").StringKeys().CreateOrOpen(StringKeys.Exact);
            ValidateScalar8Scalar8SortedBuildReader(scalar, newScalar, "mixed detached reopened scalar");
            ValidateVarKeyScalar8ReplacementReader(pathIndex, newPath, "mixed detached reopened path");
            ValidateVarKeyScalar8ReplacementReader(nameIndex, newName, "mixed detached reopened name");
            if (scalar.RootRouterOffset != publishedRoots[0])
                throw new InvalidDataException("Mixed detached reopen resolved an unexpected scalar root.");
        }

        Console.WriteLine($"mixed-native-detached-replacement-sanity ok scalar={newScalar.Length:N0} path={newPath.Length:N0} name={newName.Length:N0} failure=isolated cancellation=isolated reopen=exact");
        return 0;
    }

    /// <summary>
    /// Creates deterministic path-like encoded keys with several root/deep-prefix partitions and returns canonical key-identity order.<br/>
    /// </summary>
    private static VarKeyScalar8SortedTuple[] CreateVarKeyScalar8ReplacementTuples(
        LibraDexStringScalar8Index index,
        int count,
        string generation,
        ulong identityBase)
    {
        var tuples = new VarKeyScalar8SortedTuple[count];
        for (int i = 0; i < count; i++)
        {
            string key = $@"{(char)('C' + i % 3)}:\fixture\{generation}\branch-{i % 257:D3}\component-{i % 31:D2}\file-{i:D8}-{i % 13:D2}.txt";
            tuples[i] = new VarKeyScalar8SortedTuple(index.EncodeExactUtf8KeyForNativeBuild(System.Text.Encoding.UTF8.GetBytes(key)), checked(identityBase + (ulong)i));
        }
        Array.Sort(tuples, static (left, right) =>
        {
            int keyOrder = left.Key.AsSpan().SequenceCompareTo(right.Key);
            return keyOrder != 0 ? keyOrder : left.Identity.CompareTo(right.Identity);
        });
        return tuples;
    }

    /// <summary>Requires exact ascending identities from one string facade to match its canonical encoded source.<br/></summary>
    private static void ValidateVarKeyScalar8ReplacementReader(
        LibraDexStringScalar8Index index,
        IReadOnlyList<VarKeyScalar8SortedTuple> expected,
        string phase)
    {
        ulong[] actual = index.IterateExactIdentities(QueryDirection.Ascending).ToArray();
        if (actual.Length != expected.Count)
            throw new InvalidDataException($"{phase} returned {actual.Length:N0} identities instead of {expected.Count:N0}.");
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i].Identity)
                throw new InvalidDataException($"{phase} diverged at ordinal {i:N0}: expected {expected[i].Identity:N0}, actual {actual[i]:N0}.");
        }
    }
}
