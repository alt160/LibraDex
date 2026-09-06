using System.Numerics;
using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves that one complete index set, including maintained projection companions, disappears through one public drop terminal and remains absent after reopen.<br/>
    /// The fixture also proves that unrelated sets survive and an active set-level durability batch rejects the structural mutation without changing directory state.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when atomic set removal, batch rejection, idempotence, and reopen behavior all match the public contract.<br/></returns>
    private static int RunCatalogIndexSetDropSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-index-set-drop-{Guid.NewGuid():N}.lbdx"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            using (Catalog catalog = Catalog.Create(path, CatalogOptions.UInt64Identities))
            {
                using LibraDexIndex<int, ulong> age = catalog.Indexes["people"]["age"].Int32Keys<ulong>().Create();
                using LibraDexStringScalar8Index name = catalog.Indexes["people"]["name"].String.Create(
                    stringKeys: StringKeys.ExactFoldedAndSortKey,
                    directions: LibraDexProjectionDirectionSet.ForwardAndReversed,
                    sortKeyCulture: "en-US");
                using LibraDexIndex<int, ulong> retained = catalog.Indexes["orders"]["number"].Int32Keys<ulong>().Create();
                ValidateGenericInsert(age.Insert(42, 1UL), "index-set drop age insert");
                ValidateGenericInsert(name.Insert("Eric", 1UL), "index-set drop name insert");
                ValidateGenericInsert(retained.Insert(100, 2UL), "index-set drop retained insert");

                CatalogIdentityGroupIndexes people = catalog.IndexSet("people");
                people.Batch.Enable();
                var rejected = false;
                try
                {
                    _ = people.Drop();
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                finally
                {
                    _ = people.Batch.AbortAndDisable();
                }

                if (!rejected ||
                    !catalog.Indexes.TryGetInfo("people", "age", out _) ||
                    !catalog.Indexes.TryGetInfo("people", "name", out _))
                {
                    throw new InvalidDataException("Active-batch index-set drop did not reject without changing the complete set.");
                }

                if (!people.Drop() ||
                    people.Drop() ||
                    catalog.Indexes.IndexSetNames().Contains("people", StringComparer.Ordinal) ||
                    catalog.Indexes.TryGetInfo("people", "age", out _) ||
                    catalog.Indexes.TryGetInfo("people", "name", out _) ||
                    !catalog.Indexes.TryGetInfo("orders", "number", out _))
                {
                    throw new InvalidDataException("Index-set drop did not atomically remove the selected set while retaining unrelated indexes.");
                }
            }

            using (Catalog reopened = Catalog.Open(path, CatalogOptions.UInt64Identities))
            {
                using LibraDexIndex<int, ulong> retained = reopened.Indexes["orders"]["number"].Int32Keys<ulong>().Open();
                if (reopened.Indexes.IndexSetNames().Contains("people", StringComparer.Ordinal) ||
                    reopened.Indexes.TryGetInfo("people", "age", out _) ||
                    reopened.Indexes.TryGetInfo("people", "name", out _) ||
                    !retained.GetIdentities(retained.Where.EqualTo(100).EndCondition).SequenceEqual(new[] { 2UL }))
                {
                    throw new InvalidDataException("Reopened catalog restored a dropped index set or lost an unrelated index.");
                }
            }

            Console.WriteLine("catalog-index-set-drop-sanity ok");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Proves that catalog repack removes persisted orphaned string payload without changing file length or live tuple semantics.<br/>
    /// The fixture distributes sub-threshold deletes across routed shelves so explicit maintenance, rather than automatic dirty-shelf policy, owns the observed reclamation.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when exact reclaim evidence falls to zero after a completed in-place repack.<br/></returns>
    private static int RunCatalogRepackSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-repack-{Guid.NewGuid():N}.lbdx"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            using Catalog catalog = Catalog.Create(path);
            using LibraDexStringScalar8Index text = catalog.Indexes["items"]["text"].String.Create(StringKeys.Exact);
            using LibraDexUInt64VarIdentityIndex raw = catalog.Indexes["items"]["raw"].UInt64VarIdentityKeys(256).Create();
            List<string> keys = new();
            for (int i = 0; i < 96; i++)
            {
                string key = string.Concat((char)(0x20 + i), new string((char)('a' + (i % 26)), 700), i.ToString("D3"));
                keys.Add(key);
                ValidateGenericInsert(text.Insert(key, (ulong)(50_000 + i)), $"repack text insert {i}");
            }
            for (int i = 0; i < 24; i++)
            {
                byte[] identity = Enumerable.Repeat((byte)i, 200).ToArray();
                ValidateGenericInsert(raw.Insert((ulong)i, identity), $"repack raw identity insert {i}");
            }

            for (int i = 0; i < keys.Count; i += 10)
            {
                LibraDexIdentityMutationResult deleted = catalog.Indexes["items"]["text"].Delete(
                    LibraDexCondition.ForGroup("items").Index("text").AsString.EqualTo(keys[i]).EndCondition);
                if (deleted.ChangedCount != 1)
                    throw new InvalidDataException($"Repack fixture delete {i} changed {deleted.ChangedCount:n0} tuples.");
            }
            for (int i = 0; i < 24; i += 6)
            {
                long deleted = raw.DeleteExactKeyForDiagnostics((ulong)i);
                if (deleted != 1)
                    throw new InvalidDataException($"Repack raw-identity fixture delete {i} changed {deleted:n0} tuples.");
            }

            LibraDexMaintenanceAssessment before = catalog.Maintenance.Assess();
            long beforeBytes = before.RepackCandidates.Sum(static candidate => candidate.ReclaimableBytes);
            long fileBytes = new FileInfo(path).Length;
            LibraDexMaintenanceResult repack = catalog.Maintenance.Repack(
                new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
            LibraDexMaintenanceAssessment after = catalog.Maintenance.Assess();
            long afterBytes = after.RepackCandidates.Sum(static candidate => candidate.ReclaimableBytes);
            long repackedFileBytes = new FileInfo(path).Length;
            LibraDexMaintenanceResult optimize = catalog.Maintenance.Optimize(
                new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
            IReadOnlyList<ulong> retained = LibraDexCondition.ForGroup("items").Index("text").AsString
                .EqualTo(keys[1]).EndCondition.ToList<ulong>(_ => text);
            IReadOnlyList<ulong> deletedKey = LibraDexCondition.ForGroup("items").Index("text").AsString
                .EqualTo(keys[0]).EndCondition.ToList<ulong>(_ => text);
            if (beforeBytes <= 0 ||
                !repack.Completed ||
                repack.ChangedCount < 2 ||
                afterBytes != 0 ||
                repackedFileBytes != fileBytes ||
                !optimize.Completed ||
                optimize.IncompleteReasons != LibraDexMaintenanceIncompleteReason.None ||
                optimize.UnsupportedCount != 0 ||
                optimize.ChangedCount != 2 ||
                !retained.SequenceEqual(new[] { 50_001UL }) ||
                deletedKey.Count != 0)
            {
                throw new InvalidDataException(
                    $"Unexpected maintenance result: before={beforeBytes:n0}, after={afterBytes:n0}, repackCompleted={repack.Completed}, repackChanged={repack.ChangedCount}, optimizeCompleted={optimize.Completed}, optimizeReasons={optimize.IncompleteReasons}, optimizeUnsupported={optimize.UnsupportedCount}, optimizeChanged={optimize.ChangedCount}.");
            }

            Console.WriteLine(
                $"catalog-repack-sanity ok reclaimableBefore={beforeBytes:n0} shelvesChanged={repack.ChangedCount:n0} optimizedSubtrees={optimize.ChangedCount:n0} preOptimizeFileBytes={fileBytes:n0}");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Proves that closed-file catalog compaction preserves live logical tuples while reclaiming dropped-index storage.<br/>
    /// The fixture covers scalar, projected string, variable blob, raw identity, and every public BigInteger storage shape, validates the installed replacement through typed reopen paths, and leaves no generated catalog behind after success.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when compaction preserves the live indexes and reduces the physical file.<br/></returns>
    private static int RunCatalogCompactionSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-compaction-{Guid.NewGuid():N}.lbdx"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);
        CatalogOptions catalogOptions = new()
        {
            StringComparisonPolicy = LibraDexStringComparisonPolicy.OrdinalIgnoreCase
        };

        try
        {
            using (Catalog catalog = Catalog.Create(path, catalogOptions))
            {
                LibraDexIndex<int, ulong> age = catalog.Indexes["people"]["age"].Int32Keys<ulong>().Create();
                for (int i = 0; i < 500; i++)
                    ValidateGenericInsert(age.Insert(20 + (i % 60), (ulong)(10_000 + i)), $"compaction age insert {i}");

                using LibraDexStringScalar8Index name = catalog.Indexes["people"]["name"].String.Create(
                    stringKeys: StringKeys.ExactFoldedAndSortKey,
                    directions: LibraDexProjectionDirectionSet.ForwardAndReversed,
                    sortKeyCulture: "en-US");
                ValidateGenericInsert(name.Insert("Eric", 10_001UL), "compaction name Eric insert");
                ValidateGenericInsert(name.Insert("ALICE", 10_002UL), "compaction name ALICE insert");
                ValidateGenericInsert(name.Insert("Zoë", 10_003UL), "compaction name Zoë insert");

                using LibraDexVariableBlobScalar8Index<ulong> liveBlob = catalog.Indexes["live"]["blob"].Blob
                    .Variable<ulong>(maxKeyBytes: 128)
                    .Create();
                ValidateGenericInsert(liveBlob.Insert(new byte[] { 1, 2, 3 }, 20_001UL), "compaction live blob 1 insert");
                ValidateGenericInsert(liveBlob.Insert(new byte[] { 9, 8, 7, 6 }, 20_002UL), "compaction live blob 2 insert");

                using LibraDexUInt64VarIdentityIndex rawIdentity = catalog.Indexes["live"]["rawIdentity"]
                    .UInt64VarIdentityKeys(maxIdentityBytes: 64)
                    .Create();
                ValidateGenericInsert(rawIdentity.Insert(42UL, new byte[] { 4, 2 }), "compaction live raw identity insert");

                using LibraDexBigIntScalar8Index<long> bigFixed8 = catalog.Indexes["live"]["bigFixed8"]
                    .BigIntKeys<long>(maxBytes: 32)
                    .Create();
                BigInteger bigBoundary = (BigInteger.One << 255) - BigInteger.One;
                ValidateGenericInsert(bigFixed8.Insert(-bigBoundary, 30_001L), "compaction fixed BigInt scalar-8 negative boundary insert");
                ValidateGenericInsert(bigFixed8.Insert(new BigInteger(-7), 30_002L), "compaction fixed BigInt scalar-8 negative insert");
                ValidateGenericInsert(bigFixed8.Insert(BigInteger.Zero, 30_003L), "compaction fixed BigInt scalar-8 zero insert");
                ValidateGenericInsert(bigFixed8.Insert(new BigInteger(7), 30_004L), "compaction fixed BigInt scalar-8 positive insert");
                ValidateGenericInsert(bigFixed8.Insert(new BigInteger(7), 30_005L), "compaction fixed BigInt scalar-8 duplicate-key insert");
                ValidateGenericInsert(bigFixed8.Insert(bigBoundary, 30_006L), "compaction fixed BigInt scalar-8 positive boundary insert");
                ValidateGenericInsert(bigFixed8.Insert(ScalarNull.Null, 30_007L), "compaction fixed BigInt scalar-8 null insert 1");
                ValidateGenericInsert(bigFixed8.Insert(ScalarNull.Null, 30_008L), "compaction fixed BigInt scalar-8 null insert 2");
                for (int i = 0; i < 1700; i++)
                {
                    ValidateGenericInsert(
                        bigFixed8.Insert(new BigInteger(10_000 + i), 40_000L + i),
                        $"compaction routed fixed BigInt scalar-8 insert {i}");
                }

                using LibraDexBigIntScalar8Index<Guid> bigFixed16 = catalog.Indexes["live"]["bigFixed16"]
                    .BigIntKeys<Guid>(maxBytes: 32)
                    .Create();
                Guid bigGuid1 = new("11111111-1111-1111-1111-111111111111");
                Guid bigGuid2 = new("22222222-2222-2222-2222-222222222222");
                Guid bigGuid3 = new("33333333-3333-3333-3333-333333333333");
                Guid bigGuid4 = new("44444444-4444-4444-4444-444444444444");
                Guid bigGuidNull = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
                ValidateGenericInsert(bigFixed16.Insert(new BigInteger(-1), bigGuid1), "compaction fixed BigInt scalar-16 negative insert");
                ValidateGenericInsert(bigFixed16.Insert(BigInteger.Zero, bigGuid2), "compaction fixed BigInt scalar-16 zero insert");
                ValidateGenericInsert(bigFixed16.Insert(BigInteger.One, bigGuid3), "compaction fixed BigInt scalar-16 positive insert");
                ValidateGenericInsert(bigFixed16.Insert(BigInteger.One, bigGuid4), "compaction fixed BigInt scalar-16 duplicate-key insert");
                ValidateGenericInsert(bigFixed16.Insert(ScalarNull.Null, bigGuidNull), "compaction fixed BigInt scalar-16 null insert");
                for (int i = 0; i < 1700; i++)
                {
                    ValidateGenericInsert(
                        bigFixed16.Insert(new BigInteger(20_000 + i), CreateStableGuid(50_000 + i)),
                        $"compaction routed fixed BigInt scalar-16 insert {i}");
                }

                using LibraDexBigIntScalar8Index<long> bigVariable = catalog.Indexes["live"]["bigVariable"]
                    .BigIntVarLenKeys<long>(maxBytes: 32)
                    .Create();
                ValidateGenericInsert(bigVariable.Insert(new BigInteger(-1000), 31_001L), "compaction variable BigInt negative insert");
                ValidateGenericInsert(bigVariable.Insert(BigInteger.Zero, 31_002L), "compaction variable BigInt zero insert");
                ValidateGenericInsert(bigVariable.Insert(new BigInteger(1000), 31_003L), "compaction variable BigInt positive insert");
                ValidateGenericInsert(bigVariable.Insert(bigBoundary, 31_004L), "compaction variable BigInt boundary insert");

                using LibraDexBigIntVarIdentityIndex bigVarIdentity = catalog.Indexes["live"]["bigVarIdentity"]
                    .BigIntVarIdentityKeys(maxBytes: 32, maxIdentityBytes: 64)
                    .Create();
                ValidateGenericInsert(bigVarIdentity.Insert(new BigInteger(-9), new byte[] { 9, 1 }), "compaction BigInt variable identity negative insert");
                ValidateGenericInsert(bigVarIdentity.Insert(BigInteger.Zero, new byte[] { 0, 2 }), "compaction BigInt variable identity zero insert");
                ValidateGenericInsert(bigVarIdentity.Insert(new BigInteger(9), new byte[] { 9, 3 }), "compaction BigInt variable identity positive insert");
                ValidateGenericInsert(bigVarIdentity.Insert(new BigInteger(9), new byte[] { 9, 4 }), "compaction BigInt variable identity duplicate-key insert");
                for (int i = 0; i < 1700; i++)
                {
                    ValidateGenericInsert(
                        bigVarIdentity.Insert(new BigInteger(30_000 + i), new byte[] { (byte)i, (byte)(i >> 8), 0x55 }),
                        $"compaction routed BigInt variable identity insert {i}");
                }

                using LibraDexVariableBlobScalar8Index<ulong> discarded = catalog.Indexes["discarded"]["payload"].Blob
                    .Variable<ulong>(maxKeyBytes: 1023)
                    .Create();
                byte[] payload = new byte[900];
                for (int i = 0; i < 3000; i++)
                {
                    payload[0] = (byte)i;
                    payload[1] = (byte)(i >> 8);
                    ValidateGenericInsert(
                        discarded.Insert(payload.AsSpan().ToArray(), (ulong)(100_000 + i)),
                        $"compaction discarded insert {i}");
                }

                if (!catalog.Indexes.Drop("discarded", "payload"))
                    throw new InvalidDataException("Compaction fixture could not drop its fragmentation index.");
            }

            long fragmentedBytes = new FileInfo(path).Length;
            LibraDexCompactionResult result = Catalog.Compact(
                path,
                new LibraDexCompactionOptions { CatalogOptions = catalogOptions });
            if (result.SourceBytes != fragmentedBytes ||
                result.CompactedBytes >= fragmentedBytes ||
                result.ReclaimedBytes <= 0 ||
                result.LogicalIndexCount != 8 ||
                result.TupleCount != 5627)
            {
                throw new InvalidDataException(
                    $"Unexpected compaction result: source={result.SourceBytes:n0}, compacted={result.CompactedBytes:n0}, reclaimed={result.ReclaimedBytes:n0}, indexes={result.LogicalIndexCount}, tuples={result.TupleCount:n0}.");
            }

            using (Catalog reopened = Catalog.Open(path, catalogOptions))
            {
                using LibraDexIndex<int, ulong> age = reopened.Indexes["people"]["age"].Int32Keys<ulong>().Open();
                using LibraDexStringScalar8Index name = reopened.Indexes["people"]["name"].String.Open();
                using LibraDexVariableBlobScalar8Index<ulong> liveBlob = reopened.Indexes["live"]["blob"].Blob.Variable<ulong>(128).Open();
                using LibraDexUInt64VarIdentityIndex rawIdentity = reopened.Indexes["live"]["rawIdentity"].UInt64VarIdentityKeys(64).Open();
                using LibraDexBigIntScalar8Index<long> bigFixed8 = reopened.Indexes["live"]["bigFixed8"].BigIntKeys<long>(32).Open();
                using LibraDexBigIntScalar8Index<Guid> bigFixed16 = reopened.Indexes["live"]["bigFixed16"].BigIntKeys<Guid>(32).Open();
                using LibraDexBigIntScalar8Index<long> bigVariable = reopened.Indexes["live"]["bigVariable"].BigIntVarLenKeys<long>(32).Open();
                using LibraDexBigIntVarIdentityIndex bigVarIdentity = reopened.Indexes["live"]["bigVarIdentity"].BigIntVarIdentityKeys(32, 64).Open();
                Func<string, IIndex> resolver = indexName => indexName switch
                {
                    "age" => age,
                    "name" => name,
                    _ => throw new KeyNotFoundException(indexName)
                };
                IReadOnlyList<ulong> eric = LibraDexCondition.ForGroup("people").Index("name").AsString
                    .EqualTo("eric").EndCondition.ToList<ulong>(resolver);
                IReadOnlyList<ulong> alice = LibraDexCondition.ForGroup("people").Index("name").AsString
                    .StartsWith("al").EndCondition.ToList<ulong>(resolver);
                if (((IIndex)age).Count() != 500 ||
                    liveBlob.Count() != 2 ||
                    !eric.SequenceEqual(new[] { 10_001UL }) ||
                    !alice.SequenceEqual(new[] { 10_002UL }) ||
                    reopened.Indexes.TryGetInfo("discarded", "payload", out _))
                {
                    throw new InvalidDataException("Compacted catalog did not preserve scalar/string semantics or retained the dropped index.");
                }

                BigInteger bigBoundary = (BigInteger.One << 255) - BigInteger.One;
                if (!bigFixed8.GetIdentities(-bigBoundary).SequenceEqual(new[] { 30_001L }) ||
                    !bigFixed8.GetIdentities(new BigInteger(7)).SequenceEqual(new[] { 30_004L, 30_005L }) ||
                    !bigFixed8.GetIdentities(bigBoundary).SequenceEqual(new[] { 30_006L }) ||
                    !bigFixed8.GetIdentities(new BigInteger(11_699)).SequenceEqual(new[] { 41_699L }) ||
                    bigFixed8.Count() != 1708 ||
                    !bigFixed8.Delete(ScalarNull.Null, 30_007L) ||
                    !bigFixed8.Delete(ScalarNull.Null, 30_008L))
                {
                    throw new InvalidDataException("Compacted catalog did not preserve fixed BigInt scalar-8 ordinary or scalar-null tuples.");
                }

                Guid bigGuid3 = new("33333333-3333-3333-3333-333333333333");
                Guid bigGuid4 = new("44444444-4444-4444-4444-444444444444");
                Guid bigGuidNull = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
                if (!bigFixed16.GetIdentities(BigInteger.One).SequenceEqual(new[] { bigGuid3, bigGuid4 }) ||
                    !bigFixed16.GetIdentities(new BigInteger(21_699)).SequenceEqual(new[] { CreateStableGuid(51_699) }) ||
                    bigFixed16.Count() != 1705 ||
                    !bigFixed16.Delete(ScalarNull.Null, bigGuidNull))
                {
                    throw new InvalidDataException("Compacted catalog did not preserve fixed BigInt scalar-16 ordinary or scalar-null tuples.");
                }

                IReadOnlyList<long> variableNegative = bigVariable.GetIdentities(new BigInteger(-1000));
                IReadOnlyList<long> variableBoundary = bigVariable.GetIdentities(bigBoundary);
                long variableCount = bigVariable.Count();
                if (!variableNegative.SequenceEqual(new[] { 31_001L }) ||
                    !variableBoundary.SequenceEqual(new[] { 31_004L }) ||
                    variableCount != 4)
                {
                    throw new InvalidDataException(
                        $"Compacted catalog did not preserve variable-width BigInt scalar tuples: negative=[{string.Join(',', variableNegative)}], boundary=[{string.Join(',', variableBoundary)}], count={variableCount}.");
                }

                IReadOnlyList<byte[]> duplicateRawIdentities = bigVarIdentity.GetIdentities(new BigInteger(9));
                IReadOnlyList<byte[]> routedRawIdentity = bigVarIdentity.GetIdentities(new BigInteger(31_699));
                if (bigVarIdentity.Count() != 1704 ||
                    duplicateRawIdentities.Count != 2 ||
                    !duplicateRawIdentities[0].AsSpan().SequenceEqual(new byte[] { 9, 3 }) ||
                    !duplicateRawIdentities[1].AsSpan().SequenceEqual(new byte[] { 9, 4 }) ||
                    routedRawIdentity.Count != 1 ||
                    !routedRawIdentity[0].AsSpan().SequenceEqual(new byte[] { 0xA3, 0x06, 0x55 }))
                {
                    throw new InvalidDataException("Compacted catalog did not preserve BigInt variable-identity tuples.");
                }
            }

            Console.WriteLine(
                $"catalog-compaction-sanity ok source={result.SourceBytes:n0} compacted={result.CompactedBytes:n0} reclaimed={result.ReclaimedBytes:n0}");
            return 0;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Proves operational mode defaults, exact cumulative limits across multiple logical indexes, and structured unsupported-topology reporting.<br/>
    /// Each fixture is isolated so an incomplete optimizer attempt cannot affect a later assertion through unreachable replacement extents.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every mode and incomplete-result invariant holds.<br/></returns>
    private static int RunCatalogMaintenanceBoundSanity(string[] args)
    {
        string root = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-maintenance-bound-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        string modePath = Path.Combine(root, "modes.lbdx");
        string exactPath = Path.Combine(root, "exact.lbdx");
        string unsupportedPath = Path.Combine(root, "unsupported.lbdx");
        string vvPath = Path.Combine(root, "vv.lbdx");
        try
        {
            using (Catalog catalog = Catalog.Create(modePath))
            using (LibraDexStringScalar8Index index = catalog.Indexes["mode"]["text"].String.Create(StringKeys.Exact))
            {
                for (int i = 0; i < 300; i++)
                    ValidateGenericInsert(index.Insert($"key-{i:D4}", (ulong)i), $"maintenance mode insert {i}");

                LibraDexMaintenanceResult light = catalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Light });
                LibraDexMaintenanceResult bounded = catalog.Maintenance.Optimize();
                LibraDexMaintenanceResult explicitOverride = catalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions
                    {
                        Mode = LibraDexMaintenanceMode.Light,
                        MaxWorkItems = 32
                    });
                LibraDexMaintenanceResult full = catalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
                if (light.Completed ||
                    light.ConsideredCount != LibraDexMaintenancePolicy.LightWorkItems ||
                    light.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    bounded.Completed ||
                    bounded.ConsideredCount != LibraDexMaintenancePolicy.BoundedWorkItems ||
                    bounded.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    explicitOverride.Completed ||
                    explicitOverride.ConsideredCount != 32 ||
                    explicitOverride.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    !full.Completed ||
                    full.ConsideredCount != 300 ||
                    full.IncompleteReasons != LibraDexMaintenanceIncompleteReason.None)
                {
                    throw new InvalidDataException(
                        $"Unexpected mode budgets: light={light.ConsideredCount}/{light.Completed}/{light.IncompleteReasons}, bounded={bounded.ConsideredCount}/{bounded.Completed}/{bounded.IncompleteReasons}, override={explicitOverride.ConsideredCount}/{explicitOverride.Completed}/{explicitOverride.IncompleteReasons}, full={full.ConsideredCount}/{full.Completed}/{full.IncompleteReasons}.");
                }

                try
                {
                    _ = catalog.Maintenance.Optimize(new LibraDexMaintenanceOptions { MaxWorkItems = 0 });
                    throw new InvalidDataException("A zero maintenance work limit was accepted.");
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            using (Catalog catalog = Catalog.Create(exactPath))
            using (LibraDexStringScalar8Index first = catalog.Indexes["exact"]["first"].String.Create(StringKeys.Exact))
            using (LibraDexStringScalar8Index second = catalog.Indexes["exact"]["second"].String.Create(StringKeys.Exact))
            using (LibraDexStringScalar8Index projected = catalog.Indexes["exact"]["projected"].String.Create(StringKeys.ExactAndFolded))
            {
                ValidateGenericInsert(first.Insert("first", 1UL), "maintenance exact first insert");
                ValidateGenericInsert(second.Insert("second", 2UL), "maintenance exact second insert");
                ValidateGenericInsert(projected.Insert("Projected", 3UL), "maintenance projected insert");
                LibraDexMaintenanceOptions one = new()
                {
                    Mode = LibraDexMaintenanceMode.Full,
                    MaxWorkItems = 1
                };
                LibraDexMaintenanceResult optimize = catalog.Maintenance.Optimize(one);
                LibraDexMaintenanceResult repack = catalog.Maintenance.Repack(one);
                LibraDexMaintenanceResult completeProjectedRepack = catalog.Maintenance.Repack(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
                if (optimize.Completed ||
                    optimize.ConsideredCount != 1 ||
                    optimize.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    repack.Completed ||
                    repack.ConsideredCount != 1 ||
                    repack.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    !completeProjectedRepack.Completed ||
                    completeProjectedRepack.IncompleteReasons != LibraDexMaintenanceIncompleteReason.None)
                {
                    throw new InvalidDataException(
                        $"Exact-bound completion defect: optimize={optimize.ConsideredCount}/{optimize.Completed}/{optimize.IncompleteReasons}, repack={repack.ConsideredCount}/{repack.Completed}/{repack.IncompleteReasons}, projected={completeProjectedRepack.ConsideredCount}/{completeProjectedRepack.Completed}/{completeProjectedRepack.IncompleteReasons}.");
                }
            }

            using (Catalog catalog = Catalog.Create(unsupportedPath))
            using (LibraDexUInt64VarIdentityIndex index = catalog.Indexes["unsupported"]["identity"]
                .UInt64VarIdentityKeys(32)
                .Create())
            {
                ValidateGenericInsert(index.Insert(7UL, new byte[] { 7, 0 }), "maintenance unsupported insert");
                LibraDexMaintenanceResult sv8 = catalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
                if (!sv8.Completed ||
                    sv8.ConsideredCount != 1 ||
                    sv8.ChangedCount != 1 ||
                    sv8.UnsupportedCount != 0 ||
                    sv8.IncompleteReasons != LibraDexMaintenanceIncompleteReason.None)
                {
                    throw new InvalidDataException(
                        $"SV8 topology was not optimized: completed={sv8.Completed}, considered={sv8.ConsideredCount}, changed={sv8.ChangedCount}, count={sv8.UnsupportedCount}, reasons={sv8.IncompleteReasons}, message={sv8.Message}");
                }
            }

            using (VarKeyScalar16Index index = Indexes.VS16.Create(
                backingKind: DataKernelBackingKind.Memory,
                name: "maintenance-vs16",
                maxKeyLength: 32))
            {
                for (int i = 0; i < 300; i++)
                {
                    byte[] key = System.Text.Encoding.ASCII.GetBytes($"vs16-{i:D4}");
                    VarKeyScalar16InsertOutcome inserted = index.Insert(
                        key,
                        encodedIdentityHigh: (ulong)(i / 17),
                        encodedIdentityLow: (ulong)(100_000 + i));
                    if (!inserted.Inserted)
                        throw new InvalidDataException($"VS16 maintenance insert {i} did not publish.");
                }
                byte[] deletedKey = System.Text.Encoding.ASCII.GetBytes("vs16-0000");
                if (!index.DeleteExactTuple(deletedKey, encodedIdentityHigh: 0, encodedIdentityLow: 100_000))
                    throw new InvalidDataException("VS16 maintenance fixture could not delete its excluded tuple.");

                LibraDexMaintenanceWalkResult limited = index.Session.OptimizeVarKeyScalar16Topology(
                    index.RootRouterOffset,
                    index.Handle.MaxKeyLength,
                    requestedRouteCount: 16,
                    maxWorkItems: 16);
                LibraDexMaintenanceWalkResult full = index.Session.OptimizeVarKeyScalar16Topology(
                    index.RootRouterOffset,
                    index.Handle.MaxKeyLength,
                    requestedRouteCount: 16,
                    maxWorkItems: null);
                int retained = 0;
                bool foundDeleted = false;
                using (VarKeyScalar16RangeReader reader = index.OpenRangeReader(
                    System.Text.Encoding.ASCII.GetBytes("vs16-0000"),
                    System.Text.Encoding.ASCII.GetBytes("vs16-9999")))
                {
                    while (reader.MoveNext())
                    {
                        retained++;
                        foundDeleted |= reader.CurrentKey.SequenceEqual(deletedKey);
                    }
                }

                if (limited.Completed ||
                    limited.ConsideredCount != 16 ||
                    limited.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    !full.Completed ||
                    full.ConsideredCount != 299 ||
                    full.ChangedCount != 1 ||
                    retained != 299 ||
                    foundDeleted)
                {
                    throw new InvalidDataException(
                        $"VS16 maintenance mismatch: limited={limited.ConsideredCount}/{limited.Completed}/{limited.IncompleteReasons}, full={full.ConsideredCount}/{full.ChangedCount}/{full.Completed}, retained={retained}, foundDeleted={foundDeleted}.");
                }
            }

            using (Scalar16VarIdentityIndex index = Indexes.SV16.Create(
                backingKind: DataKernelBackingKind.Memory,
                name: "maintenance-sv16",
                maxIdentityLength: 32))
            {
                const ulong keyHigh = 0x3000_0000_0000_0000UL;
                for (int i = 0; i < 128; i++)
                {
                    byte[] identity = [(byte)i, (byte)(i >> 8), 0x5A];
                    Scalar16VarIdentityInsertOutcome inserted = index.Insert(keyHigh, (ulong)i, identity);
                    if (!inserted.Inserted)
                        throw new InvalidDataException($"SV16 maintenance insert {i} did not publish.");
                }
                byte[] deletedIdentity = [0, 0, 0x5A];
                if (!index.DeleteExactTuple(keyHigh, encodedKeyLow: 0, identity: deletedIdentity))
                    throw new InvalidDataException("SV16 maintenance fixture could not delete its excluded tuple.");

                LibraDexMaintenanceWalkResult limited = index.Session.OptimizeScalar16VarIdentityTopology(
                    index.RootRouterOffset,
                    index.MaxIdentityLength,
                    maxWorkItems: 16);
                LibraDexMaintenanceWalkResult full = index.Session.OptimizeScalar16VarIdentityTopology(
                    index.RootRouterOffset,
                    index.MaxIdentityLength,
                    maxWorkItems: null);
                int retained = 0;
                bool foundDeleted = false;
                using (Scalar16VarIdentityRangeReader reader = index.OpenRangeReader(
                    lowerEncodedKeyHigh: keyHigh,
                    lowerEncodedKeyLow: 0,
                    upperEncodedKeyHigh: keyHigh,
                    upperEncodedKeyLow: ulong.MaxValue))
                {
                    while (reader.MoveNext())
                    {
                        retained++;
                        foundDeleted |= reader.CurrentEncodedKeyLow == 0;
                    }
                }

                if (limited.Completed ||
                    limited.ConsideredCount != 16 ||
                    limited.IncompleteReasons != LibraDexMaintenanceIncompleteReason.WorkLimit ||
                    !full.Completed ||
                    full.ConsideredCount != 127 ||
                    full.ChangedCount != 1 ||
                    retained != 127 ||
                    foundDeleted)
                {
                    throw new InvalidDataException(
                        $"SV16 maintenance mismatch: limited={limited.ConsideredCount}/{limited.Completed}/{limited.IncompleteReasons}, full={full.ConsideredCount}/{full.ChangedCount}/{full.Completed}, retained={retained}, foundDeleted={foundDeleted}.");
                }
            }

            using (Catalog catalog = Catalog.Create(vvPath))
            using (VarKeyVarIdentityIndex index = catalog.Indexes["vv"]["raw"]
                .VarKeyVarIdentityKeys(maxKeyBytes: 64, maxIdentityBytes: 32)
                .Create())
            {
                for (int i = 0; i < 200; i++)
                {
                    byte[] key = System.Text.Encoding.ASCII.GetBytes($"vv-{i:D4}");
                    byte[] identity = [(byte)i, (byte)(i >> 8), 0xA5];
                    VarKeyVarIdentityIndexInsertResult inserted = index.Insert(key, identity);
                    if (!inserted.Inserted)
                        throw new InvalidDataException($"VV maintenance insert {i} did not publish.");
                }
                byte[] deletedKey = System.Text.Encoding.ASCII.GetBytes("vv-0000");
                byte[] deletedIdentity = [0, 0, 0xA5];
                if (!index.DeleteExactTuple(deletedKey, deletedIdentity))
                    throw new InvalidDataException("VV maintenance fixture could not delete its excluded tuple.");

                LibraDexMaintenanceResult optimized = catalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
                int retained = 0;
                bool foundDeleted = false;
                using (VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(
                    System.Text.Encoding.ASCII.GetBytes("vv-0000"),
                    System.Text.Encoding.ASCII.GetBytes("vv-9999")))
                {
                    while (reader.MoveNext())
                    {
                        retained++;
                        foundDeleted |= reader.CurrentKey.SequenceEqual(deletedKey);
                    }
                }

                if (!optimized.Completed ||
                    optimized.ConsideredCount != 199 ||
                    optimized.ChangedCount != 1 ||
                    optimized.UnsupportedCount != 0 ||
                    retained != 199 ||
                    foundDeleted)
                {
                    throw new InvalidDataException(
                        $"VV maintenance mismatch: optimized={optimized.ConsideredCount}/{optimized.ChangedCount}/{optimized.Completed}/{optimized.IncompleteReasons}, unsupported={optimized.UnsupportedCount}, retained={retained}, foundDeleted={foundDeleted}.");
                }
            }

            Console.WriteLine("catalog-maintenance-bound-sanity ok light=16 bounded=256 override=32 full=300 exactLimit=1 sv8=1 vs16=299 vv=199 sv16=127");
            return 0;
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Proves that deterministic failures immediately before replacement and immediately after replacement both preserve the exact original catalog bytes and reopen semantics.<br/>
    /// The proof also rejects leaked shadow or rollback siblings so cleanup is part of the executable contract.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when both failure boundaries restore the original catalog exactly.<br/></returns>
    private static int RunCatalogCompactionFailureSanity(string[] args)
    {
        string path = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-compaction-failure-{Guid.NewGuid():N}.lbdx"));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);

        try
        {
            using (Catalog catalog = Catalog.Create(path))
            using (LibraDexIndex<int, ulong> index = catalog.Indexes["proof"]["value"].Int32Keys<ulong>().Create())
            {
                for (int i = 0; i < 64; i++)
                    ValidateGenericInsert(index.Insert(i, (ulong)(10_000 + i)), $"compaction failure insert {i}");
            }

            byte[] originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            ProveFailure(LibraDexCompactionFaultPoint.BeforeReplacement);
            ProveFailure(LibraDexCompactionFaultPoint.AfterReplacementBeforeValidation);
            Console.WriteLine("catalog-compaction-failure-sanity ok beforeReplacement=restored afterReplacement=restored");
            return 0;

            void ProveFailure(LibraDexCompactionFaultPoint faultPoint)
            {
                try
                {
                    _ = LibraDexCatalogCompactor.Compact(path, options: null, CancellationToken.None, faultPoint);
                    throw new InvalidDataException($"Compaction fault {faultPoint} did not throw.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("Injected LibraDex compaction failure", StringComparison.Ordinal))
                {
                }

                byte[] restoredHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
                if (!restoredHash.AsSpan().SequenceEqual(originalHash))
                    throw new InvalidDataException($"Compaction fault {faultPoint} did not restore the exact original catalog bytes.");

                using Catalog reopened = Catalog.Open(path);
                using LibraDexIndex<int, ulong> index = reopened.Indexes["proof"]["value"].Int32Keys<ulong>().Open();
                LibraDexConditionEndCondition exact = LibraDexCondition.ForGroup("proof").Index("value")
                    .AsInt32.EqualTo(63).EndCondition;
                if (((IIndex)index).Count() != 64 || !index.GetIdentities(exact).SequenceEqual(new[] { 10_063UL }))
                    throw new InvalidDataException($"Compaction fault {faultPoint} did not preserve reopen semantics.");

                string sourceName = Path.GetFileName(path);
                string siblingDirectory = Path.GetDirectoryName(path)!;
                string[] leaked = Directory.GetFiles(siblingDirectory, $".{sourceName}.compact-*");
                if (leaked.Length != 0)
                    throw new InvalidDataException($"Compaction fault {faultPoint} leaked sibling files: {string.Join(", ", leaked)}");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            string? cleanupDirectory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(cleanupDirectory) && Directory.Exists(cleanupDirectory))
            {
                string sourceName = Path.GetFileName(path);
                foreach (string sibling in Directory.GetFiles(cleanupDirectory, $".{sourceName}.compact-*"))
                    File.Delete(sibling);
            }
        }
    }

    /// <summary>
    /// Proves exact multi-block live-file backup, post-snapshot source independence, active-batch rejection, publication exclusion, between-block cancellation cleanup, same-path rejection, overwrite policy, and memory-catalog rejection.<br/>
    /// The deterministic publication probe pauses the physical copy while its storage lock is held, starts a writer, and verifies that the writer cannot publish into the captured image.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when every live-backup boundary preserves an exact reopenable committed image and leaves no staging files.<br/></returns>
    private static int RunCatalogLiveBackupSanity(string[] args)
    {
        string root = GetOption(
            args,
            "--path",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"catalog-live-backup-{Guid.NewGuid():N}"));
        string sourcePath = Path.Combine(root, "source.lbdx");
        string firstBackupPath = Path.Combine(root, "first.backup.lbdx");
        string concurrentBackupPath = Path.Combine(root, "concurrent.backup.lbdx");
        string batchBackupPath = Path.Combine(root, "batch.backup.lbdx");
        string cancelledBackupPath = Path.Combine(root, "cancelled.backup.lbdx");
        Directory.CreateDirectory(root);

        try
        {
            using Catalog catalog = Catalog.Create(sourcePath);
            using LibraDexIndex<long, long> index = catalog.Indexes["proof"]["value"].Int64Keys<long>().Create();
            for (int i = 0; i < 64; i++)
                ValidateGenericInsert(index.Insert(i, 10_000L + i), $"live backup initial insert {i}");

            using LibraDexVariableBlobScalar8Index<long> payloadIndex = catalog.Indexes["proof"]["payload"]
                .Blob.Variable<long>(maxKeyBytes: 1023)
                .Create();
            byte[] payload = new byte[1023];
            payload.AsSpan().Fill(0xA5);
            for (int i = 0; i < 3072; i++)
            {
                BitConverter.TryWriteBytes(payload.AsSpan(0, sizeof(int)), i);
                ValidateGenericInsert(payloadIndex.Insert(payload, i), $"live backup payload insert {i}");
            }

            long sourceLength = new FileInfo(sourcePath).Length;
            if (sourceLength <= 2L * 1024 * 1024)
                throw new InvalidDataException($"The live-backup fixture must span multiple 1 MiB copy blocks; actual source length was {sourceLength:n0} bytes.");

            string normalizedSourceAlias = Path.Combine(root, ".", Path.GetFileName(sourcePath));
            try
            {
                _ = catalog.Backup(normalizedSourceAlias);
                throw new InvalidDataException("A live backup accepted a normalized alias of its source path.");
            }
            catch (ArgumentException ex) when (ex.ParamName == "path")
            {
            }

            LibraDexBackupResult first = catalog.Backup(firstBackupPath);
            string installedHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(firstBackupPath)));
            if (first.Path != Path.GetFullPath(firstBackupPath) ||
                first.Bytes != new FileInfo(firstBackupPath).Length ||
                first.ContentHash != installedHash ||
                first.IndexCount != 2)
            {
                throw new InvalidDataException(
                    $"Unexpected first live backup result: path={first.Path}, bytes={first.Bytes:n0}, hash={first.ContentHash}, indexes={first.IndexCount}.");
            }

            ValidateGenericInsert(index.Insert(64, 10_064L), "live backup post-snapshot source insert");
            using (Catalog firstBackup = Catalog.Open(firstBackupPath))
            using (LibraDexIndex<long, long> backupIndex = firstBackup.Indexes["proof"]["value"].Int64Keys<long>().Open())
            {
                LibraDexConditionEndCondition key64 = LibraDexCondition.ForGroup("proof")
                    .Index("value").AsInt64.EqualTo(64L).EndCondition;
                if (((IIndex)backupIndex).Count() != 64 || backupIndex.Count(key64) != 0)
                    throw new InvalidDataException("The first backup changed after a later live-source mutation.");
            }

            using (LibraDexBatch<long, long> batch = index.BeginBatch())
            {
                _ = batch.Insert(65, 10_065L);
                try
                {
                    _ = catalog.Backup(batchBackupPath);
                    throw new InvalidDataException("A live backup incorrectly started while a durability batch was active.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("durability batch is active", StringComparison.Ordinal))
                {
                }

                _ = batch.Abort();
            }

            if (File.Exists(batchBackupPath))
                throw new InvalidDataException("An active-batch backup failure installed a destination file.");

            using ManualResetEventSlim copyEntered = new(false);
            using ManualResetEventSlim releaseCopy = new(false);
            LibraDexFileSession.LiveBackupCopyEnteredForValidation = () =>
            {
                copyEntered.Set();
                if (!releaseCopy.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The live-backup publication proof was not released.");
            };
            try
            {
                Task<LibraDexBackupResult> backupTask = Task.Run(
                    () => catalog.Backup(concurrentBackupPath));
                if (!copyEntered.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The live-backup copy did not enter its publication boundary.");

                Task<LibraDexGenericInsertResult> writerTask = Task.Run(
                    () => index.Insert(66, 10_066L));
                if (writerTask.Wait(TimeSpan.FromMilliseconds(150)))
                    throw new InvalidDataException("A writer published while the live-backup physical copy held the storage boundary.");

                releaseCopy.Set();
                LibraDexBackupResult concurrent = backupTask.GetAwaiter().GetResult();
                LibraDexGenericInsertResult writer = writerTask.GetAwaiter().GetResult();
                ValidateGenericInsert(writer, "live backup concurrent writer insert");
                if (concurrent.IndexCount != 2)
                    throw new InvalidDataException("The concurrent live backup did not reopen with its expected index definition.");
            }
            finally
            {
                releaseCopy.Set();
                LibraDexFileSession.LiveBackupCopyEnteredForValidation = null;
            }

            using (Catalog concurrentBackup = Catalog.Open(concurrentBackupPath))
            using (LibraDexIndex<long, long> backupIndex = concurrentBackup.Indexes["proof"]["value"].Int64Keys<long>().Open())
            {
                LibraDexConditionEndCondition key64 = LibraDexCondition.ForGroup("proof")
                    .Index("value").AsInt64.EqualTo(64L).EndCondition;
                LibraDexConditionEndCondition key66 = LibraDexCondition.ForGroup("proof")
                    .Index("value").AsInt64.EqualTo(66L).EndCondition;
                if (((IIndex)backupIndex).Count() != 65 ||
                    backupIndex.Count(key64) != 1 ||
                    backupIndex.Count(key66) != 0 ||
                    ((IIndex)index).Count() != 66)
                {
                    throw new InvalidDataException("The publication-boundary backup did not preserve the exact pre-writer state.");
                }
            }

            using CancellationTokenSource cancellation = new();
            long cancellationCopiedBytes = 0;
            long cancellationTotalBytes = 0;
            LibraDexFileSession.LiveBackupCopyBlockCompletedForValidation = (copiedBytes, totalBytes) =>
            {
                if (copiedBytes < totalBytes &&
                    Interlocked.CompareExchange(ref cancellationCopiedBytes, copiedBytes, 0) == 0)
                {
                    cancellationTotalBytes = totalBytes;
                    cancellation.Cancel();
                }
            };
            try
            {
                try
                {
                    _ = catalog.Backup(cancelledBackupPath, cancellationToken: cancellation.Token);
                    throw new InvalidDataException("A cancelled live backup unexpectedly completed.");
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                LibraDexFileSession.LiveBackupCopyBlockCompletedForValidation = null;
            }

            if (cancellationCopiedBytes <= 0 || cancellationCopiedBytes >= cancellationTotalBytes)
                throw new InvalidDataException("The live-backup cancellation proof did not cancel strictly between physical copy blocks.");
            if (File.Exists(cancelledBackupPath))
                throw new InvalidDataException("A cancelled live backup installed a destination file.");
            if (Directory.GetFiles(root, ".*.backup-*.tmp").Length != 0)
                throw new InvalidDataException("A live-backup failure leaked a destination staging file.");

            try
            {
                _ = catalog.Backup(firstBackupPath);
                throw new InvalidDataException("A live backup overwrote an existing destination without explicit permission.");
            }
            catch (IOException)
            {
            }

            LibraDexBackupResult overwritten = catalog.Backup(
                firstBackupPath,
                new LibraDexBackupOptions { Overwrite = true });
            if (overwritten.Bytes != new FileInfo(firstBackupPath).Length)
                throw new InvalidDataException("The explicit overwrite backup returned an incorrect installed length.");

            using (Catalog memory = Catalog.CreateMemory())
            {
                try
                {
                    _ = memory.Backup(Path.Combine(root, "memory.backup.lbdx"));
                    throw new InvalidDataException("A memory-backed catalog unexpectedly produced a live file backup.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("file-backed", StringComparison.Ordinal))
                {
                }
            }

            Console.WriteLine(
                $"catalog-live-backup-sanity ok bytes={overwritten.Bytes:n0} indexes={overwritten.IndexCount} activeBatch=rejected concurrentWriter=excluded cancellation=between-block samePath=rejected overwrite=explicit");
            return 0;
        }
        finally
        {
            LibraDexFileSession.LiveBackupCopyEnteredForValidation = null;
            LibraDexFileSession.LiveBackupCopyBlockCompletedForValidation = null;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
