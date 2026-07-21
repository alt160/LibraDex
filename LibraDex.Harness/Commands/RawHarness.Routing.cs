using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using Microsoft.Data.Sqlite;

internal static partial class RawHarness
{

    /// <summary>
    /// Measures one router arena read-cache scenario over a deterministic key pattern.<br/>
    /// The checksum prevents the route walk loop from becoming observationally irrelevant while keeping output compact.<br/>
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="session">The active file session.</param>
    /// <param name="setup">The route setup keys and root offset.</param>
    /// <param name="iterations">The number of route walks to perform.</param>
    /// <param name="pattern">The route key pattern to use.</param>
    /// <param name="useArenaCache">Whether to call the explicit arena-aware walker.</param>
    /// <returns>The measured perf scenario result.</returns>
    private static RouterArenaReadCachePerfResult MeasureRouterArenaReadCacheScenario(
        string name,
        LibraDexFileSession session,
        RouterArenaReadCachePerfSetup setup,
        int iterations,
        RouterArenaReadCachePerfPattern pattern,
        bool useArenaCache)
    {
        session.ClearRouterArenaReadCacheForValidation();
        _ = session.GetAndResetReadTelemetry();
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            ulong key = pattern switch
            {
                RouterArenaReadCachePerfPattern.SameArena => setup.FirstArenaKey,
                RouterArenaReadCachePerfPattern.AlternatingArenas => (i & 1) == 0 ? setup.FirstArenaKey : setup.SecondArenaKey,
                RouterArenaReadCachePerfPattern.MixedDirectAndArena => (i & 1) == 0 ? setup.FirstArenaKey : setup.DirectKey,
                _ => throw new InvalidOperationException($"Unsupported router arena read-cache perf pattern {pattern}.")
            };

            Scalar8Scalar8RouteReadPolicy readPolicy = useArenaCache
                ? Scalar8Scalar8RouteReadPolicy.PreferArenaCache
                : Scalar8Scalar8RouteReadPolicy.Uncached;
            Scalar8Scalar8RouteTarget target = session.WalkScalar8Scalar8RouteTarget(setup.RootRouterOffset, key, maxRouterHops: 8, readPolicy);
            checksum ^= target.Offset + target.RouterDepth + (int)target.Kind;
        }

        stopwatch.Stop();
        DataKernelReadTelemetry telemetry = session.GetAndResetReadTelemetry();
        return new RouterArenaReadCachePerfResult(name, iterations, stopwatch.Elapsed, telemetry.ReadCallCount, telemetry.BackingReadCallCount, telemetry.BytesRead, checksum);
    }


    /// <summary>
    /// Validates that catalog metadata can durably carry root-level null and empty key-state route offsets across reopen.<br/>
    /// The proof covers metadata anchoring, compact identity-only route shelves, and condition materialization for scalar null presence.<br/>
    /// </summary>
    private static void ValidateKeyRouteMetadataContract()
    {
        static IndexDirectorySlotSnapshot FindSlot(Catalog catalog, int slotIndex)
        {
            ReadOnlySpan<IndexDirectorySlotSnapshot> activeSlots = catalog.Session.IndexDirectory.ActiveSlots;
            for (int i = 0; i < activeSlots.Length; i++)
            {
                if (activeSlots[i].SlotIndex == slotIndex)
                {
                    return activeSlots[i];
                }
            }

            throw new InvalidDataException($"Catalog slot {slotIndex} was not active.");
        }

        string path = Path.Combine("artifacts", "key-route-metadata-contract-sanity.lbdx");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.Delete(path);
        int slotIndex;
        int scalar16SlotIndex;
        Guid scalar16NullA = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Guid scalar16NullB = Guid.Parse("00112233-4455-6677-1111-222222222222");
        LibraDexGenericScalarCodec<Guid>.Encode16(scalar16NullA, out ulong scalar16NullAHigh, out ulong scalar16NullALow);
        LibraDexGenericScalarCodec<Guid>.Encode16(scalar16NullB, out ulong scalar16NullBHigh, out ulong scalar16NullBLow);
        using (Catalog catalog = Catalog.Create(path))
        {
            LibraDexIndex<long, long> index = catalog.Indexes["routes"]["value"].Int64Keys<long>().Create();
            LibraDexIndex<long, Guid> scalar16Index = catalog.Indexes["routes"]["guidValue"].Int64Keys<Guid>().Create();
            ValidateGenericInsert(index.Insert(10, 444L), "key-state non-null scalar insert");
            if (!catalog.Indexes.TryGetInfo("routes", "value", out CatalogIndexInfo info))
            {
                throw new InvalidDataException("Created key-route metadata proof index was not discoverable.");
            }

            if (!catalog.Indexes.TryGetInfo("routes", "guidValue", out CatalogIndexInfo scalar16Info))
            {
                throw new InvalidDataException("Created scalar-16 key-route metadata proof index was not discoverable.");
            }

            slotIndex = info.SlotIndex;
            scalar16SlotIndex = scalar16Info.SlotIndex;
            IndexDirectorySlotSnapshot slot = FindSlot(catalog, slotIndex);
            if (!catalog.Session.TryReadKeyRouteOffsets(slot, out KeyRouteOffsets initialOffsets) ||
                initialOffsets.Null != 0 ||
                initialOffsets.Empty != 0)
            {
                throw new InvalidDataException("Fresh catalog metadata did not expose zeroed key-state route offsets.");
            }

            _ = index;
            ulong encodedNull111 = LibraDexGenericScalarCodec<long>.Encode8(111L);
            ulong encodedNull333 = LibraDexGenericScalarCodec<long>.Encode8(333L);
            ulong encodedNull555 = LibraDexGenericScalarCodec<long>.Encode8(555L);
            ulong encodedEmpty222 = LibraDexGenericScalarCodec<long>.Encode8(222L);
            if (!catalog.Session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull333) ||
                !catalog.Session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull111) ||
                catalog.Session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull333) ||
                !catalog.Session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Empty, encodedEmpty222) ||
                !catalog.Session.InsertScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull555))
            {
                throw new InvalidDataException("Key-state scalar-8 identity insertion did not report expected insert/no-op results.");
            }

            if (!catalog.Session.ContainsScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull555) ||
                !catalog.Session.DeleteScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull555) ||
                catalog.Session.ContainsScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull555) ||
                catalog.Session.DeleteScalar8KeyStateIdentity(slotIndex, KeyStateRoute.Null, encodedNull555))
            {
                throw new InvalidDataException("Key-state scalar-8 identity route did not support exact identity contains/delete.");
            }

            if (!index.Insert(ScalarNull.Null, 777L).Inserted ||
                !((IIndex)index).Insert(null, 999L).Inserted ||
                index.Insert(ScalarNull.Null, 777L).Inserted)
            {
                throw new InvalidDataException("Public scalar-null insert paths did not report expected insert/no-op results.");
            }

            if (!index.Insert(ScalarNull.Null, 555L).Inserted ||
                !index.Delete(ScalarNull.Null, 555L) ||
                index.Delete(ScalarNull.Null, 555L) ||
                !((IIndex)index).Insert(null, 555L).Inserted ||
                !((IIndex)index).Delete(null, 555L) ||
                ((IIndex)index).Delete(null, 555L))
            {
                throw new InvalidDataException("Public scalar-null exact delete paths did not report expected delete/no-op results.");
            }

            IndexDirectorySlotSnapshot updatedSlot = FindSlot(catalog, slotIndex);
            if (!catalog.Session.TryReadKeyRouteOffsets(updatedSlot, out KeyRouteOffsets updatedOffsets) ||
                updatedOffsets.Null <= 0 ||
                updatedOffsets.Empty <= 0)
            {
                throw new InvalidDataException("Updated catalog metadata did not expose published key-state route offsets.");
            }

            ulong[] nullIdentities = catalog.Session.ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Null);
            ulong[] emptyIdentities = catalog.Session.ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Empty);
            if (!nullIdentities.SequenceEqual(new[] { encodedNull111, encodedNull333, LibraDexGenericScalarCodec<long>.Encode8(777L), LibraDexGenericScalarCodec<long>.Encode8(999L) }) ||
                !emptyIdentities.SequenceEqual(new[] { encodedEmpty222 }))
            {
                throw new InvalidDataException("Key-state scalar-8 identity shelves did not preserve sorted route identities.");
            }

            Func<string, IIndex> routeResolver = indexName => string.Equals(indexName, "value", StringComparison.Ordinal)
                ? index
                : throw new KeyNotFoundException(indexName);
            LibraDexConditionEndCondition scalarNullCondition = LibraDexCondition
                .ForGroup("routes")
                .Index("value").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition;
            LibraDexConditionEndCondition scalarNonNullCondition = LibraDexCondition
                .ForGroup("routes")
                .Index("value").AsInt64.NotEqualTo(ScalarNull.Null)
                .EndCondition;
            IReadOnlyList<long> scalarNullIds = scalarNullCondition.ToList<long>(routeResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> scalarNonNullIds = scalarNonNullCondition.ToList<long>(routeResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> scalarAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("value").AsInt64.All()
                .EndCondition
                .ToList<long>(routeResolver, deduplication: IdentityDeduplication.Preserve);
            if (!scalarNullIds.SequenceEqual(new[] { 111L, 333L, 777L, 999L }) ||
                !scalarNonNullIds.SequenceEqual(new[] { 444L }) ||
                !scalarAllIds.SequenceEqual(new[] { 111L, 333L, 777L, 999L, 444L }) ||
                scalarNullCondition.Count(routeResolver, deduplication: IdentityDeduplication.Preserve) != 4 ||
                LibraDexCondition.ForGroup("routes").Index("value").AsInt64.All().EndCondition.Count(routeResolver, deduplication: IdentityDeduplication.Preserve) != 5 ||
                !scalarNullCondition.Exists(routeResolver, deduplication: IdentityDeduplication.Preserve))
            {
                throw new InvalidDataException("ScalarNull condition materialization did not route through expected null/non-null identities.");
            }

            LibraDexIndex<long, long> deleteIndex = catalog.Indexes["routes"]["deleteValue"].Int64Keys<long>().Create();
            ValidateGenericInsert(deleteIndex.Insert(10, 610L), "key-state delete proof non-null insert");
            ValidateGenericInsert(deleteIndex.Insert(ScalarNull.Null, 611L), "key-state delete proof null 611 insert");
            ValidateGenericInsert(deleteIndex.Insert(ScalarNull.Null, 612L), "key-state delete proof null 612 insert");
            Func<string, IIndex> deleteResolver = indexName => string.Equals(indexName, "deleteValue", StringComparison.Ordinal)
                ? deleteIndex
                : throw new KeyNotFoundException(indexName);
            LibraDexIdentityMutationResult deleteNullResult = catalog["routes"]["deleteValue"].Delete(LibraDexCondition
                .ForGroup("routes")
                .Index("deleteValue").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition);
            IReadOnlyList<long> deleteAfterNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("deleteValue").AsInt64.All()
                .EndCondition
                .ToList<long>(deleteResolver, deduplication: IdentityDeduplication.Preserve);
            if (deleteNullResult.ChangedCount != 2 ||
                !deleteAfterNullIds.SequenceEqual(new[] { 610L }))
            {
                throw new InvalidDataException("ScalarNull condition delete did not remove only null-route identities.");
            }

            ValidateGenericInsert(deleteIndex.Insert(ScalarNull.Null, 613L), "key-state all delete proof null insert");
            LibraDexIdentityMutationResult deleteAllResult = catalog["routes"]["deleteValue"].DeleteAll();
            if (deleteAllResult.ChangedCount != 2 ||
                LibraDexCondition.ForGroup("routes").Index("deleteValue").AsInt64.All().EndCondition.Count(deleteResolver, deduplication: IdentityDeduplication.Preserve) != 0)
            {
                throw new InvalidDataException("Scalar all-condition delete did not remove null-route and ordinary identities.");
            }

            LibraDexIndex<long, long> rekeyIndex = catalog.Indexes["routes"]["rekeyValue"].Int64Keys<long>().Create();
            ValidateGenericInsert(rekeyIndex.Insert(ScalarNull.Null, 701L), "key-state rekey proof null 701 insert");
            ValidateGenericInsert(rekeyIndex.Insert(20, 702L), "key-state rekey proof non-null 702 insert");
            ValidateGenericInsert(rekeyIndex.Insert(ScalarNull.Null, 703L), "key-state rekey proof null 703 insert");
            bool rekeyNullToValue = rekeyIndex.Rekey(701L, ScalarNull.Null, 30L);
            bool rekeyValueToNull = rekeyIndex.Rekey(702L, 20L, ScalarNull.Null);
            bool rekeyNonGenericNullToValue = ((IIndex)rekeyIndex).Rekey(703L, null, 40L);
            bool rekeyInsert704 = rekeyIndex.Insert(ScalarNull.Null, 704L).Inserted;
            long rekeyIdentityOnlyNullToValue = ((IIndex)rekeyIndex).Rekey(704L, 50L);
            if (!rekeyNullToValue ||
                !rekeyValueToNull ||
                !rekeyNonGenericNullToValue ||
                !rekeyInsert704 ||
                rekeyIdentityOnlyNullToValue != 1)
            {
                throw new InvalidDataException($"Scalar-null rekey paths did not report expected changes. nullToValue={rekeyNullToValue} valueToNull={rekeyValueToNull} nonGenericNullToValue={rekeyNonGenericNullToValue} insert704={rekeyInsert704} identityOnly={rekeyIdentityOnlyNullToValue}");
            }

            Func<string, IIndex> rekeyResolver = indexName => string.Equals(indexName, "rekeyValue", StringComparison.Ordinal)
                ? rekeyIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> rekeyNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("rekeyValue").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition
                .ToList<long>(rekeyResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> rekeyAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("rekeyValue").AsInt64.All()
                .EndCondition
                .ToList<long>(rekeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (!rekeyNullIds.SequenceEqual(new[] { 702L }) ||
                !rekeyAllIds.SequenceEqual(new[] { 702L, 701L, 703L, 704L }))
            {
                throw new InvalidDataException("Scalar-null rekey paths did not leave expected null-first all-scan identities.");
            }

            LibraDexIndex<long, long> setKeyIndex = catalog.Indexes["routes"]["setKeyValue"].Int64Keys<long>().Create();
            ValidateGenericInsert(setKeyIndex.Insert(60, 801L), "key-state SetKey proof non-null 801 insert");
            ValidateGenericInsert(setKeyIndex.Insert(ScalarNull.Null, 802L), "key-state SetKey proof null 802 insert");
            Func<string, IIndex> setKeyResolver = indexName => string.Equals(indexName, "setKeyValue", StringComparison.Ordinal)
                ? setKeyIndex
                : throw new KeyNotFoundException(indexName);
            LibraDexIdentityMutationResult setKeyToNullResult = catalog["routes"]["setKeyValue"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("setKeyValue").AsInt64.EqualTo(60)
                .EndCondition, null);
            LibraDexIdentityMutationResult setKeyFromNullResult = catalog["routes"]["setKeyValue"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("setKeyValue").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition, 70L);
            IReadOnlyList<long> setKeyNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("setKeyValue").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition
                .ToList<long>(setKeyResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> setKeyAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("setKeyValue").AsInt64.All()
                .EndCondition
                .ToList<long>(setKeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (setKeyToNullResult.ChangedCount != 1 ||
                setKeyFromNullResult.ChangedCount != 2 ||
                setKeyNullIds.Count != 0 ||
                !setKeyAllIds.SequenceEqual(new[] { 801L, 802L }))
            {
                throw new InvalidDataException($"Scalar-null SetKey did not move identities between null and ordinary routes. toNull={setKeyToNullResult.ChangedCount} fromNull={setKeyFromNullResult.ChangedCount} nullIds={string.Join(",", setKeyNullIds)} allIds={string.Join(",", setKeyAllIds)}");
            }

            LibraDexStringScalar8Index stringRouteIndex = catalog.Indexes["routes"]["code"].String.Create(stringKeys: StringKeys.Exact);
            ValidateGenericInsert(stringRouteIndex.Insert(null, 901UL), "string key-state null insert");
            ValidateGenericInsert(stringRouteIndex.Insert(string.Empty, 902UL), "string key-state empty insert");
            ValidateGenericInsert(stringRouteIndex.Insert("A", 903UL), "string key-state normal insert");
            Func<string, IIndex> stringRouteResolver = indexName => string.Equals(indexName, "code", StringComparison.Ordinal)
                ? stringRouteIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<ulong> stringNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("code").AsString.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<ulong>(stringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> stringEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("code").AsString.EqualTo(string.Empty)
                .EndCondition
                .ToList<ulong>(stringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> stringNullOrEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("code").AsString.EqualTo(NullKey.NullOrEmpty)
                .EndCondition
                .ToList<ulong>(stringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> stringAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("code").AsString.All()
                .EndCondition
                .ToList<ulong>(stringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            if (!stringNullIds.SequenceEqual(new[] { 901UL }) ||
                !stringEmptyIds.SequenceEqual(new[] { 902UL }) ||
                !stringNullOrEmptyIds.SequenceEqual(new[] { 901UL, 902UL }) ||
                !stringAllIds.SequenceEqual(new[] { 901UL, 902UL, 903UL }) ||
                !((IIndex)stringRouteIndex).Delete(string.Empty, 902UL) ||
                ((IIndex)stringRouteIndex).Delete(string.Empty, 902UL))
            {
                throw new InvalidDataException("String NullKey route reads, all-scan ordering, or exact deletes did not match expected route semantics.");
            }

            LibraDexIndex<byte[], long> binaryRouteIndex = catalog.Indexes["routes"]["fingerprint"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
            ValidateGenericInsert(((IIndex)binaryRouteIndex).Insert(null, 911L), "binary key-state null insert");
            ValidateGenericInsert(binaryRouteIndex.Insert(Array.Empty<byte>(), 912L), "binary key-state empty insert");
            ValidateGenericInsert(binaryRouteIndex.Insert(Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"), 913L), "binary key-state normal insert");
            Func<string, IIndex> binaryRouteResolver = indexName => string.Equals(indexName, "fingerprint", StringComparison.Ordinal)
                ? binaryRouteIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> binaryNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprint").AsBinary.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<long>(binaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> binaryEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprint").AsBinary.EqualTo(Array.Empty<byte>())
                .EndCondition
                .ToList<long>(binaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> binaryNullOrEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprint").AsBinary.EqualTo(NullKey.NullOrEmpty)
                .EndCondition
                .ToList<long>(binaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> binaryAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprint").AsBinary.All()
                .EndCondition
                .ToList<long>(binaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            if (!binaryNullIds.SequenceEqual(new[] { 911L }) ||
                !binaryEmptyIds.SequenceEqual(new[] { 912L }) ||
                !binaryNullOrEmptyIds.SequenceEqual(new[] { 911L, 912L }) ||
                !binaryAllIds.SequenceEqual(new[] { 911L, 912L, 913L }) ||
                !binaryRouteIndex.Delete(NullKey.Empty, 912L) ||
                binaryRouteIndex.Delete(NullKey.Empty, 912L))
            {
                throw new InvalidDataException("Binary NullKey route reads, all-scan ordering, or exact deletes did not match expected route semantics.");
            }

            LibraDexStringScalar8Index stringRekeyIndex = catalog.Indexes["routes"]["codeRekey"].String.Create(stringKeys: StringKeys.Exact);
            ValidateGenericInsert(stringRekeyIndex.Insert(null, 921UL), "string key-state rekey null insert");
            ValidateGenericInsert(stringRekeyIndex.Insert(string.Empty, 922UL), "string key-state rekey empty insert");
            ValidateGenericInsert(stringRekeyIndex.Insert("A", 923UL), "string key-state rekey normal insert");
            bool stringRekeyNullToValue = stringRekeyIndex.Rekey(921UL, null, "B");
            bool stringRekeyValueToEmpty = stringRekeyIndex.Rekey(923UL, "A", string.Empty);
            long stringRekeyEmptyToNull = stringRekeyIndex.Rekey(922UL, null);
            Func<string, IIndex> stringRekeyResolver = indexName => string.Equals(indexName, "codeRekey", StringComparison.Ordinal)
                ? stringRekeyIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<ulong> stringRekeyAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("codeRekey").AsString.All()
                .EndCondition
                .ToList<ulong>(stringRekeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (!stringRekeyNullToValue ||
                !stringRekeyValueToEmpty ||
                stringRekeyEmptyToNull != 1 ||
                !stringRekeyAllIds.SequenceEqual(new[] { 922UL, 923UL, 921UL }))
            {
                throw new InvalidDataException("String NullKey direct rekey paths did not leave expected null-first route ordering.");
            }

            LibraDexIdentityMutationResult stringSetKeyToEmpty = catalog["routes"]["codeRekey"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("codeRekey").AsString.EqualTo("B")
                .EndCondition, string.Empty);
            LibraDexIdentityMutationResult stringSetKeyToNull = catalog["routes"]["codeRekey"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("codeRekey").AsString.EqualTo(NullKey.Empty)
                .EndCondition, null);
            IReadOnlyList<ulong> stringSetKeyNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("codeRekey").AsString.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<ulong>(stringRekeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (stringSetKeyToEmpty.ChangedCount != 1 ||
                stringSetKeyToNull.ChangedCount != 2 ||
                !stringSetKeyNullIds.SequenceEqual(new[] { 921UL, 922UL, 923UL }))
            {
                throw new InvalidDataException("String NullKey condition SetKey did not move identities through null and empty routes.");
            }

            LibraDexIndex<byte[], long> binaryRekeyIndex = catalog.Indexes["routes"]["fingerprintRekey"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
            byte[] binaryRekeyA = Convert.FromHexString("100102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            byte[] binaryRekeyB = Convert.FromHexString("200102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            ValidateGenericInsert(binaryRekeyIndex.Insert(NullKey.Null, 931L), "binary key-state rekey null insert");
            ValidateGenericInsert(binaryRekeyIndex.Insert(NullKey.Empty, 932L), "binary key-state rekey empty insert");
            ValidateGenericInsert(binaryRekeyIndex.Insert(binaryRekeyA, 933L), "binary key-state rekey normal insert");
            bool binaryRekeyNullToValue = binaryRekeyIndex.Rekey(931L, NullKey.Null, binaryRekeyB);
            bool binaryRekeyValueToEmpty = binaryRekeyIndex.Rekey(933L, binaryRekeyA, NullKey.Empty);
            long binaryRekeyEmptyToNull = ((IIndex)binaryRekeyIndex).Rekey(932L, null);
            Func<string, IIndex> binaryRekeyResolver = indexName => string.Equals(indexName, "fingerprintRekey", StringComparison.Ordinal)
                ? binaryRekeyIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> binaryRekeyAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintRekey").AsBinary.All()
                .EndCondition
                .ToList<long>(binaryRekeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (!binaryRekeyNullToValue ||
                !binaryRekeyValueToEmpty ||
                binaryRekeyEmptyToNull != 1 ||
                !binaryRekeyAllIds.SequenceEqual(new[] { 932L, 933L, 931L }))
            {
                throw new InvalidDataException("Binary NullKey direct rekey paths did not leave expected null-first route ordering.");
            }

            LibraDexIdentityMutationResult binarySetKeyToEmpty = catalog["routes"]["fingerprintRekey"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintRekey").AsBinary.EqualTo(binaryRekeyB)
                .EndCondition, Array.Empty<byte>());
            LibraDexIdentityMutationResult binarySetKeyToNull = catalog["routes"]["fingerprintRekey"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintRekey").AsBinary.EqualTo(NullKey.Empty)
                .EndCondition, null);
            IReadOnlyList<long> binarySetKeyNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintRekey").AsBinary.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<long>(binaryRekeyResolver, deduplication: IdentityDeduplication.Preserve);
            if (binarySetKeyToEmpty.ChangedCount != 1 ||
                binarySetKeyToNull.ChangedCount != 2 ||
                !binarySetKeyNullIds.SequenceEqual(new[] { 931L, 932L, 933L }))
            {
                throw new InvalidDataException("Binary NullKey condition SetKey did not move identities through null and empty routes.");
            }

            LibraDexStringScalar8Index stringMembershipIndex = catalog.Indexes["routes"]["codeMembership"].String.Create(stringKeys: StringKeys.Exact);
            ValidateGenericInsert(stringMembershipIndex.Insert(null, 941UL), "string key-state membership null insert");
            ValidateGenericInsert(stringMembershipIndex.Insert(string.Empty, 942UL), "string key-state membership empty insert");
            ValidateGenericInsert(stringMembershipIndex.Insert("A", 943UL), "string key-state membership normal A insert");
            ValidateGenericInsert(stringMembershipIndex.Insert("B", 944UL), "string key-state membership normal B insert");
            Func<string, IIndex> stringMembershipResolver = indexName => string.Equals(indexName, "codeMembership", StringComparison.Ordinal)
                ? stringMembershipIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<ulong> stringMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("codeMembership").AsString.InSet(new[] { null!, string.Empty, "B" })
                .EndCondition
                .ToList<ulong>(stringMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> stringNotMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("codeMembership").AsString.NotInSet(new[] { null!, string.Empty, "B" })
                .EndCondition
                .ToList<ulong>(stringMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            LibraDexIdentityMutationResult stringMembershipDelete = catalog["routes"]["codeMembership"].Delete(LibraDexCondition
                .ForGroup("routes")
                .Index("codeMembership").AsString.InSet(new[] { string.Empty, "B" })
                .EndCondition);
            LibraDexIdentityMutationResult stringMembershipSetKey = catalog["routes"]["codeMembership"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("codeMembership").AsString.InSet(new[] { null!, "A" })
                .EndCondition, string.Empty);
            IReadOnlyList<ulong> stringMembershipEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("codeMembership").AsString.EqualTo(NullKey.Empty)
                .EndCondition
                .ToList<ulong>(stringMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            if (!stringMembershipIds.SequenceEqual(new[] { 941UL, 942UL, 944UL }) ||
                !stringNotMembershipIds.SequenceEqual(new[] { 943UL }) ||
                stringMembershipDelete.ChangedCount != 2 ||
                stringMembershipSetKey.ChangedCount != 2 ||
                !stringMembershipEmptyIds.SequenceEqual(new[] { 941UL, 943UL }))
            {
                throw new InvalidDataException("String NullKey membership, NotInSet, delete, or SetKey did not route null and empty members correctly.");
            }

            LibraDexIndex<byte[], long> binaryMembershipIndex = catalog.Indexes["routes"]["fingerprintMembership"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
            byte[] binaryMembershipA = Convert.FromHexString("300102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            byte[] binaryMembershipB = Convert.FromHexString("400102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            ValidateGenericInsert(binaryMembershipIndex.Insert(NullKey.Null, 951L), "binary key-state membership null insert");
            ValidateGenericInsert(binaryMembershipIndex.Insert(NullKey.Empty, 952L), "binary key-state membership empty insert");
            ValidateGenericInsert(binaryMembershipIndex.Insert(binaryMembershipA, 953L), "binary key-state membership normal A insert");
            ValidateGenericInsert(binaryMembershipIndex.Insert(binaryMembershipB, 954L), "binary key-state membership normal B insert");
            Func<string, IIndex> binaryMembershipResolver = indexName => string.Equals(indexName, "fingerprintMembership", StringComparison.Ordinal)
                ? binaryMembershipIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> binaryMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintMembership").AsBinary.InSet(new byte[][] { null!, Array.Empty<byte>(), binaryMembershipB })
                .EndCondition
                .ToList<long>(binaryMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> binaryNotMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintMembership").AsBinary.NotInSet(new byte[][] { null!, Array.Empty<byte>(), binaryMembershipB })
                .EndCondition
                .ToList<long>(binaryMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            LibraDexPreparedObjectSet binaryPreparedMembership = ((IIndex)binaryMembershipIndex).PrepareInSet(new object[] { null!, Array.Empty<byte>(), binaryMembershipB });
            IReadOnlyList<object> binaryPreparedIds = ((IIdentityPrimitiveExecutor)binaryMembershipIndex).ExecuteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.InSet, new object?[] { binaryPreparedMembership }));
            LibraDexIdentityMutationResult binaryMembershipDelete = catalog["routes"]["fingerprintMembership"].Delete(LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintMembership").AsBinary.InSet(new byte[][] { Array.Empty<byte>(), binaryMembershipB })
                .EndCondition);
            LibraDexIdentityMutationResult binaryMembershipSetKey = catalog["routes"]["fingerprintMembership"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintMembership").AsBinary.InSet(new byte[][] { null!, binaryMembershipA })
                .EndCondition, Array.Empty<byte>());
            IReadOnlyList<long> binaryMembershipEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("fingerprintMembership").AsBinary.EqualTo(NullKey.Empty)
                .EndCondition
                .ToList<long>(binaryMembershipResolver, deduplication: IdentityDeduplication.Preserve);
            if (!binaryMembershipIds.SequenceEqual(new[] { 951L, 952L, 954L }) ||
                !binaryNotMembershipIds.SequenceEqual(new[] { 953L }) ||
                !binaryPreparedIds.Cast<long>().SequenceEqual(new[] { 951L, 952L, 954L }) ||
                binaryMembershipDelete.ChangedCount != 2 ||
                binaryMembershipSetKey.ChangedCount != 2 ||
                !binaryMembershipEmptyIds.SequenceEqual(new[] { 951L, 953L }))
            {
                throw new InvalidDataException("Binary NullKey membership, prepared membership, NotInSet, delete, or SetKey did not route null and empty members correctly.");
            }

            LibraDexStringScalar8Index reopenStringRouteIndex = catalog.Indexes["routes"]["reopenCode"].String.Create(stringKeys: StringKeys.Exact);
            ValidateGenericInsert(reopenStringRouteIndex.Insert(null, 961UL), "reopen string key-state null insert");
            ValidateGenericInsert(reopenStringRouteIndex.Insert(string.Empty, 962UL), "reopen string key-state empty insert");
            ValidateGenericInsert(reopenStringRouteIndex.Insert("A", 963UL), "reopen string key-state normal A insert");
            ValidateGenericInsert(reopenStringRouteIndex.Insert("B", 964UL), "reopen string key-state normal B insert");

            LibraDexStringScalar8Index reopenStringMutationIndex = catalog.Indexes["routes"]["reopenCodeMutation"].String.Create(stringKeys: StringKeys.Exact);
            ValidateGenericInsert(reopenStringMutationIndex.Insert(null, 965UL), "reopen string mutation null insert");
            ValidateGenericInsert(reopenStringMutationIndex.Insert(string.Empty, 966UL), "reopen string mutation empty insert");
            ValidateGenericInsert(reopenStringMutationIndex.Insert("A", 967UL), "reopen string mutation normal insert");

            LibraDexIndex<byte[], long> reopenBinaryRouteIndex = catalog.Indexes["routes"]["reopenFingerprint"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
            byte[] reopenBinaryA = Convert.FromHexString("500102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            byte[] reopenBinaryB = Convert.FromHexString("600102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            ValidateGenericInsert(reopenBinaryRouteIndex.Insert(NullKey.Null, 971L), "reopen binary key-state null insert");
            ValidateGenericInsert(reopenBinaryRouteIndex.Insert(NullKey.Empty, 972L), "reopen binary key-state empty insert");
            ValidateGenericInsert(reopenBinaryRouteIndex.Insert(reopenBinaryA, 973L), "reopen binary key-state normal A insert");
            ValidateGenericInsert(reopenBinaryRouteIndex.Insert(reopenBinaryB, 974L), "reopen binary key-state normal B insert");

            LibraDexIndex<byte[], long> reopenBinaryMutationIndex = catalog.Indexes["routes"]["reopenFingerprintMutation"].Blob.Scalar<long>(LibraDexScalarWidth.Bytes32).Create();
            byte[] reopenBinaryMutationA = Convert.FromHexString("700102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            ValidateGenericInsert(reopenBinaryMutationIndex.Insert(NullKey.Null, 975L), "reopen binary mutation null insert");
            ValidateGenericInsert(reopenBinaryMutationIndex.Insert(NullKey.Empty, 976L), "reopen binary mutation empty insert");
            ValidateGenericInsert(reopenBinaryMutationIndex.Insert(reopenBinaryMutationA, 977L), "reopen binary mutation normal insert");

            _ = scalar16Index;
            if (!catalog.Session.InsertScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow) ||
                !catalog.Session.InsertScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullBHigh, scalar16NullBLow) ||
                catalog.Session.InsertScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow))
            {
                throw new InvalidDataException("Key-state scalar-16 identity insertion did not report expected insert/no-op results.");
            }

            if (!catalog.Session.ContainsScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow) ||
                !catalog.Session.DeleteScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow) ||
                catalog.Session.ContainsScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow) ||
                !catalog.Session.InsertScalar16KeyStateIdentity(scalar16SlotIndex, KeyStateRoute.Null, scalar16NullAHigh, scalar16NullALow))
            {
                throw new InvalidDataException("Key-state scalar-16 identity route did not support exact identity contains/delete.");
            }

            (ulong[] scalar16Highs, ulong[] scalar16Lows) = catalog.Session.ReadScalar16KeyStateIdentities(scalar16SlotIndex, KeyStateRoute.Null);
            if (scalar16Highs.Length != 2 ||
                scalar16Lows.Length != 2)
            {
                throw new InvalidDataException("Key-state scalar-16 identity shelf did not preserve sorted route identities.");
            }

            Func<string, IIndex> scalar16Resolver = indexName => string.Equals(indexName, "guidValue", StringComparison.Ordinal)
                ? scalar16Index
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<Guid> scalar16NullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("guidValue").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition
                .ToList<Guid>(scalar16Resolver, deduplication: IdentityDeduplication.Preserve);
            if (!scalar16NullIds.OrderBy(static value => value).SequenceEqual(new[] { scalar16NullA, scalar16NullB }.OrderBy(static value => value)))
            {
                throw new InvalidDataException("ScalarNull condition materialization did not decode scalar-16 route identities.");
            }
        }

        using (Catalog reopened = Catalog.Open(path))
        {
            IndexDirectorySlotSnapshot reopenedSlot = FindSlot(reopened, slotIndex);
            if (!reopened.Session.TryReadKeyRouteOffsets(reopenedSlot, out KeyRouteOffsets reopenedOffsets) ||
                reopenedOffsets.Null <= 0 ||
                reopenedOffsets.Empty <= 0)
            {
                throw new InvalidDataException("Catalog metadata key-state route offsets did not survive reopen.");
            }

            ulong[] reopenedNullIdentities = reopened.Session.ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Null);
            ulong[] reopenedEmptyIdentities = reopened.Session.ReadScalar8KeyStateIdentities(slotIndex, KeyStateRoute.Empty);
            if (!reopenedNullIdentities.SequenceEqual(new[] { LibraDexGenericScalarCodec<long>.Encode8(111L), LibraDexGenericScalarCodec<long>.Encode8(333L), LibraDexGenericScalarCodec<long>.Encode8(777L), LibraDexGenericScalarCodec<long>.Encode8(999L) }) ||
                !reopenedEmptyIdentities.SequenceEqual(new[] { LibraDexGenericScalarCodec<long>.Encode8(222L) }))
            {
                throw new InvalidDataException("Key-state scalar-8 identity shelves did not survive reopen.");
            }

            (ulong[] reopenedScalar16Highs, ulong[] reopenedScalar16Lows) = reopened.Session.ReadScalar16KeyStateIdentities(scalar16SlotIndex, KeyStateRoute.Null);
            if (reopenedScalar16Highs.Length != 2 ||
                reopenedScalar16Lows.Length != 2)
            {
                throw new InvalidDataException("Key-state scalar-16 identity shelf did not survive reopen.");
            }

            if (!reopened.Indexes.TryGetInfo("routes", "value", out CatalogIndexInfo reopenedInfo))
            {
                throw new InvalidDataException("Reopened key-route metadata proof index was not discoverable.");
            }

            IIndex reopenedIndex = reopened.OpenIndex(reopenedInfo);
            Func<string, IIndex> reopenedRouteResolver = indexName => string.Equals(indexName, "value", StringComparison.Ordinal)
                ? reopenedIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> reopenedScalarNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("value").AsInt64.EqualTo(ScalarNull.Null)
                .EndCondition
                .ToList<long>(reopenedRouteResolver, deduplication: IdentityDeduplication.Preserve);
            if (!reopenedScalarNullIds.SequenceEqual(new[] { 111L, 333L, 777L, 999L }))
            {
                throw new InvalidDataException("ScalarNull condition route identities did not survive reopen.");
            }

            LibraDexStringScalar8Index reopenedStringRouteIndex = reopened.Indexes["routes"]["reopenCode"].String.Open();
            Func<string, IIndex> reopenedStringRouteResolver = indexName => string.Equals(indexName, "reopenCode", StringComparison.Ordinal)
                ? reopenedStringRouteIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<ulong> reopenedStringNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCode").AsString.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<ulong>(reopenedStringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> reopenedStringEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCode").AsString.EqualTo(NullKey.Empty)
                .EndCondition
                .ToList<ulong>(reopenedStringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> reopenedStringMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCode").AsString.InSet(new[] { null!, string.Empty, "B" })
                .EndCondition
                .ToList<ulong>(reopenedStringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<ulong> reopenedStringAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCode").AsString.All()
                .EndCondition
                .ToList<ulong>(reopenedStringRouteResolver, deduplication: IdentityDeduplication.Preserve);
            if (!reopenedStringNullIds.SequenceEqual(new[] { 961UL }) ||
                !reopenedStringEmptyIds.SequenceEqual(new[] { 962UL }) ||
                !reopenedStringMembershipIds.SequenceEqual(new[] { 961UL, 962UL, 964UL }) ||
                !reopenedStringAllIds.SequenceEqual(new[] { 961UL, 962UL, 963UL, 964UL }))
            {
                throw new InvalidDataException("Reopened string NullKey route equality, membership, or all-scan did not use persisted route offsets.");
            }

            LibraDexStringScalar8Index reopenedStringMutationIndex = reopened.Indexes["routes"]["reopenCodeMutation"].String.Open();
            Func<string, IIndex> reopenedStringMutationResolver = indexName => string.Equals(indexName, "reopenCodeMutation", StringComparison.Ordinal)
                ? reopenedStringMutationIndex
                : throw new KeyNotFoundException(indexName);
            LibraDexIdentityMutationResult reopenedStringDelete = reopened["routes"]["reopenCodeMutation"].Delete(LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCodeMutation").AsString.EqualTo(NullKey.Empty)
                .EndCondition);
            LibraDexIdentityMutationResult reopenedStringSetKey = reopened["routes"]["reopenCodeMutation"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCodeMutation").AsString.EqualTo("A")
                .EndCondition, null);
            bool reopenedStringRekey = reopenedStringMutationIndex.Rekey(965UL, null, "B");
            IReadOnlyList<ulong> reopenedStringMutationAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenCodeMutation").AsString.All()
                .EndCondition
                .ToList<ulong>(reopenedStringMutationResolver, deduplication: IdentityDeduplication.Preserve);
            if (reopenedStringDelete.ChangedCount != 1 ||
                reopenedStringSetKey.ChangedCount != 1 ||
                !reopenedStringRekey ||
                !reopenedStringMutationAllIds.SequenceEqual(new[] { 967UL, 965UL }))
            {
                throw new InvalidDataException("Reopened string NullKey delete, SetKey, or rekey did not use persisted route offsets.");
            }

            byte[] reopenedBinaryA = Convert.FromHexString("500102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            byte[] reopenedBinaryB = Convert.FromHexString("600102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            if (!reopened.Indexes.TryGetInfo("routes", "reopenFingerprint", out CatalogIndexInfo reopenedBinaryInfo))
            {
                throw new InvalidDataException("Reopened binary NullKey route proof index was not discoverable.");
            }

            LibraDexIndex<byte[], long> reopenedBinaryRouteIndex = (LibraDexIndex<byte[], long>)reopened.OpenIndex(reopenedBinaryInfo);
            Func<string, IIndex> reopenedBinaryRouteResolver = indexName => string.Equals(indexName, "reopenFingerprint", StringComparison.Ordinal)
                ? reopenedBinaryRouteIndex
                : throw new KeyNotFoundException(indexName);
            IReadOnlyList<long> reopenedBinaryNullIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprint").AsBinary.EqualTo(NullKey.Null)
                .EndCondition
                .ToList<long>(reopenedBinaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> reopenedBinaryEmptyIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprint").AsBinary.EqualTo(NullKey.Empty)
                .EndCondition
                .ToList<long>(reopenedBinaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> reopenedBinaryMembershipIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprint").AsBinary.InSet(new byte[][] { null!, Array.Empty<byte>(), reopenedBinaryB })
                .EndCondition
                .ToList<long>(reopenedBinaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            IReadOnlyList<long> reopenedBinaryAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprint").AsBinary.All()
                .EndCondition
                .ToList<long>(reopenedBinaryRouteResolver, deduplication: IdentityDeduplication.Preserve);
            if (!reopenedBinaryNullIds.SequenceEqual(new[] { 971L }) ||
                !reopenedBinaryEmptyIds.SequenceEqual(new[] { 972L }) ||
                !reopenedBinaryMembershipIds.SequenceEqual(new[] { 971L, 972L, 974L }) ||
                !reopenedBinaryAllIds.SequenceEqual(new[] { 971L, 972L, 973L, 974L }))
            {
                throw new InvalidDataException("Reopened binary NullKey route equality, membership, or all-scan did not use persisted route offsets.");
            }

            byte[] reopenedBinaryMutationA = Convert.FromHexString("700102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            byte[] reopenedBinaryMutationB = Convert.FromHexString("800102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
            if (!reopened.Indexes.TryGetInfo("routes", "reopenFingerprintMutation", out CatalogIndexInfo reopenedBinaryMutationInfo))
            {
                throw new InvalidDataException("Reopened binary NullKey mutation proof index was not discoverable.");
            }

            LibraDexIndex<byte[], long> reopenedBinaryMutationIndex = (LibraDexIndex<byte[], long>)reopened.OpenIndex(reopenedBinaryMutationInfo);
            Func<string, IIndex> reopenedBinaryMutationResolver = indexName => string.Equals(indexName, "reopenFingerprintMutation", StringComparison.Ordinal)
                ? reopenedBinaryMutationIndex
                : throw new KeyNotFoundException(indexName);
            LibraDexIdentityMutationResult reopenedBinaryDelete = reopened["routes"]["reopenFingerprintMutation"].Delete(LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprintMutation").AsBinary.EqualTo(NullKey.Empty)
                .EndCondition);
            LibraDexIdentityMutationResult reopenedBinarySetKey = reopened["routes"]["reopenFingerprintMutation"].SetKey(LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprintMutation").AsBinary.EqualTo(reopenedBinaryMutationA)
                .EndCondition, null);
            bool reopenedBinaryRekey = reopenedBinaryMutationIndex.Rekey(975L, NullKey.Null, reopenedBinaryMutationB);
            IReadOnlyList<long> reopenedBinaryMutationAllIds = LibraDexCondition
                .ForGroup("routes")
                .Index("reopenFingerprintMutation").AsBinary.All()
                .EndCondition
                .ToList<long>(reopenedBinaryMutationResolver, deduplication: IdentityDeduplication.Preserve);
            if (reopenedBinaryDelete.ChangedCount != 1 ||
                reopenedBinarySetKey.ChangedCount != 1 ||
                !reopenedBinaryRekey ||
                !reopenedBinaryMutationAllIds.SequenceEqual(new[] { 977L, 975L }))
            {
                throw new InvalidDataException("Reopened binary NullKey delete, SetKey, or rekey did not use persisted route offsets.");
            }
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf key set that can split at the root-router prefix byte.<br/>
    /// Half the keys live under prefix `0x00` and half under prefix `0x80`, so adding another `0x80` key forces a visible root route update.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-8` shelf profile that determines full capacity.</param>
    /// <param name="keys">The generated encoded keys.</param>
    /// <param name="identities">The generated encoded identities.</param>
    /// <param name="leftCount">The number of low-prefix tuples.</param>
    /// <param name="rightCount">The number of high-prefix tuples.</param>
    private static void CreateRootPrefixSplitVectors(
        Scalar8Scalar8Profile profile,
        out ulong[] keys,
        out ulong[] identities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        keys = new ulong[profile.MaxItemCount];
        identities = new ulong[profile.MaxItemCount];
        const ulong rightPrefixBase = 0x8000_0000_0000_0000UL;
        for (int i = 0; i < leftCount; i++)
        {
            keys[i] = (ulong)i;
            identities[i] = keys[i];
        }

        for (int i = 0; i < rightCount; i++)
        {
            int target = leftCount + i;
            keys[target] = rightPrefixBase + (ulong)i;
            identities[target] = keys[target];
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf key set that shares the root prefix and can split at the second prefix byte.<br/>
    /// Half the keys live under child prefix `0x00` and half under child prefix `0x80`, so the old shelf can transform into a child router.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-8` shelf profile that determines full capacity.</param>
    /// <param name="keys">The generated encoded keys.</param>
    /// <param name="identities">The generated encoded identities.</param>
    /// <param name="leftCount">The number of low-child-prefix tuples.</param>
    /// <param name="rightCount">The number of high-child-prefix tuples.</param>
    private static void CreateTransformSplitVectors(
        Scalar8Scalar8Profile profile,
        out ulong[] keys,
        out ulong[] identities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        keys = new ulong[profile.MaxItemCount];
        identities = new ulong[profile.MaxItemCount];
        const ulong secondByteRightBase = 0x0080_0000_0000_0000UL;
        for (int i = 0; i < leftCount; i++)
        {
            keys[i] = (ulong)i;
            identities[i] = keys[i];
        }

        for (int i = 0; i < rightCount; i++)
        {
            int target = leftCount + i;
            keys[target] = secondByteRightBase + (ulong)i;
            identities[target] = keys[target];
        }
    }


    /// <summary>
    /// Creates a deterministic full-shelf key set that shares root and child prefixes and can split at the third prefix byte.<br/>
    /// This is used to validate a walked deeper transform where the full shelf is already below a child router.<br/>
    /// </summary>
    /// <param name="profile">The `SS8-8` shelf profile that determines full capacity.</param>
    /// <param name="keys">The generated encoded keys.</param>
    /// <param name="identities">The generated encoded identities.</param>
    /// <param name="leftCount">The number of low-third-prefix tuples.</param>
    /// <param name="rightCount">The number of high-third-prefix tuples.</param>
    private static void CreateDeeperTransformSplitVectors(
        Scalar8Scalar8Profile profile,
        out ulong[] keys,
        out ulong[] identities,
        out int leftCount,
        out int rightCount)
    {
        leftCount = profile.MaxItemCount / 2;
        rightCount = profile.MaxItemCount - leftCount;
        keys = new ulong[profile.MaxItemCount];
        identities = new ulong[profile.MaxItemCount];
        const ulong thirdByteRightBase = 0x0000_8000_0000_0000UL;
        for (int i = 0; i < leftCount; i++)
        {
            keys[i] = (ulong)i;
            identities[i] = keys[i];
        }

        for (int i = 0; i < rightCount; i++)
        {
            int target = leftCount + i;
            keys[target] = thirdByteRightBase + (ulong)i;
            identities[target] = keys[target];
        }
    }


    private static void ValidateRootRouter(LibraDexFileSession session, RouterSnapshot router)
    {
        if (router.Offset <= 0)
        {
            throw new InvalidDataException("Root router offset must be nonzero.");
        }

        if (router.PrefixByteCount != 1 || router.KeyDepth != 0)
        {
            throw new InvalidDataException("Root router prefix shape mismatch.");
        }

        if (router.RouteCount != RouterLayout.MaxOneByteRouteCount || router.MaxRouteCount != RouterLayout.MaxOneByteRouteCount || !router.HasDirectIndex)
        {
            throw new InvalidDataException("Root router direct-index shape mismatch.");
        }

        if (session.ReadDirectRouterTarget(router.Offset, 0) != 0 || session.ReadDirectRouterTarget(router.Offset, 255) != 0)
        {
            throw new InvalidDataException("New root router routes should be present but unset.");
        }
    }


    private static void ValidateRouteVector(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> key, long expectedTarget)
    {
        long actual = RoutePersistedKey(session, rootOffset, key);
        if (actual != expectedTarget)
        {
            throw new InvalidDataException($"Route vector failed. Expected {expectedTarget}, actual {actual}.");
        }
    }


    private static long RoutePersistedKey(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> key)
    {
        if (key.Length < 1)
        {
            return 0;
        }

        long childOffset = session.FindRouterTarget(rootOffset, key[0]);
        if (childOffset == 0 || key.Length < 2)
        {
            return 0;
        }

        return session.FindRouterTarget(childOffset, key[1]);
    }


    private static void ValidateInvalidRouteDefinitions()
    {
        ExpectInvalidCompressedRoutes(16, [new RouterRouteSnapshot(0x80, 0x40, 1)]);
        ExpectInvalidCompressedRoutes(16, [new RouterRouteSnapshot(0x40, 0x7F, 1), new RouterRouteSnapshot(0x00, 0x3F, 2)]);
        ExpectInvalidCompressedRoutes(16, [new RouterRouteSnapshot(0x00, 0x7F, 1), new RouterRouteSnapshot(0x40, 0xFF, 2)]);
        ExpectInvalidCompressedRoutes(1, [new RouterRouteSnapshot(0x00, 0x3F, 1), new RouterRouteSnapshot(0x40, 0xFF, 2)]);
    }


    private static void ExpectInvalidCompressedRoutes(ushort maxRouteCount, RouterRouteSnapshot[] routes)
    {
        byte[] bytes = new byte[RouterLayout.Size];
        RouterWriter writer = new(bytes);
        try
        {
            writer.InitializeCompressed(1, 1, maxRouteCount, 1, routes);
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidDataException("Expected invalid route definition to be rejected.");
    }


    private static long RoutePersistedChain(LibraDexFileSession session, long rootOffset, ReadOnlySpan<byte> key, int maxHops)
    {
        long routerOffset = rootOffset;
        for (int depth = 0; depth < key.Length && depth < maxHops; depth++)
        {
            long target = session.FindRouterTarget(routerOffset, key[depth]);
            if (target == 0)
            {
                return 0;
            }

            if (depth == key.Length - 1)
            {
                return target;
            }

            routerOffset = target;
        }

        return 0;
    }


    /// <summary>
    /// Creates one encoded key for the public-wrapper routed bulk-write benchmark.<br/>
    /// The key's high byte selects the root prefix, while the low bytes carry a batch-local ordinal that remains unique across warmup and measured batches.<br/>
    /// </summary>
    /// <param name="batch">The logical batch ordinal.</param>
    /// <param name="itemOrdinal">The item ordinal before insertion-order permutation.</param>
    /// <param name="itemsPerBatch">The number of generated items in each batch.</param>
    /// <param name="prefixCount">The number of root prefixes covered by the generated key set.</param>
    /// <returns>The encoded sortable `SS8-8` key.</returns>
    private static ulong CreateRoutedBulkWriteKey(
        int batch,
        int itemOrdinal,
        int itemsPerBatch,
        int prefixCount)
    {
        const int itemsPerSecondByteBucket = 128;
        int itemsPerPrefix = (itemsPerBatch + prefixCount - 1) / prefixCount;
        int prefix = Math.Min(itemOrdinal / itemsPerPrefix, prefixCount - 1);
        int localOrdinal = itemOrdinal - (prefix * itemsPerPrefix);
        ulong absoluteLocalOrdinal = checked(((ulong)batch * (ulong)itemsPerPrefix) + (ulong)localOrdinal);
        ulong secondByte = (absoluteLocalOrdinal / itemsPerSecondByteBucket) & 0xFFUL;
        ulong thirdByte = secondByte;
        ulong lowValue = absoluteLocalOrdinal & 0x0000_FFFF_FFFF_FFFFUL;
        return Scalar8Scalar8Layout.EncodeUnsignedScalar8(((ulong)(byte)prefix << 56) | (secondByte << 48) | (thirdByte << 40) | lowValue);
    }


    private static RoutePerfResult MeasureRootRoutePerf(byte[] rootBytes, int iterations)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            RouterReader root = new(rootBytes);
            byte prefix = (byte)(0x40 + (i & 3));
            checksum += root.GetDirectTarget(prefix);
        }

        watch.Stop();
        return new RoutePerfResult(iterations, watch.Elapsed, checksum);
    }


    private static RoutePerfResult MeasureTwoHopRoutePerf(byte[] rootBytes, byte[] childBytes, int iterations, byte secondByte)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            RouterReader root = new(rootBytes);
            byte prefix = (byte)(0x40 + (i & 3));
            long childOffset = root.GetDirectTarget(prefix);
            if (childOffset != 0)
            {
                RouterReader child = new(childBytes);
                checksum += child.FindTarget(secondByte);
            }
        }

        watch.Stop();
        return new RoutePerfResult(iterations, watch.Elapsed, checksum);
    }


    private static RoutePerfResult MeasureRootMissRoutePerf(byte[] rootBytes, int iterations)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            RouterReader root = new(rootBytes);
            byte prefix = (byte)(0x50 + (i & 3));
            checksum += root.GetDirectTarget(prefix);
        }

        watch.Stop();
        return new RoutePerfResult(iterations, watch.Elapsed, checksum);
    }


    private static long[] BuildExpandedTargets(RouterRouteSnapshot[] routes)
    {
        long[] targets = new long[RouterLayout.MaxOneByteRouteCount];
        for (int i = 0; i < routes.Length; i++)
        {
            RouterRouteSnapshot route = routes[i];
            for (int prefix = route.PrefixStart; prefix <= route.PrefixEnd; prefix++)
            {
                targets[prefix] = route.TargetOffset;
            }
        }

        return targets;
    }


    private static bool VerifyRouteEquivalence(byte[] compressedBytes, byte[] expandedBytes)
    {
        RouterReader compressed = new(compressedBytes);
        RouterReader expanded = new(expandedBytes);
        for (int i = 0; i < RouterLayout.MaxOneByteRouteCount; i++)
        {
            byte prefix = (byte)i;
            if (compressed.FindTarget(prefix) != expanded.FindTarget(prefix))
            {
                return false;
            }
        }

        return true;
    }


    private static RoutePerfResult MeasureShapeRoutePerf(byte[] routerBytes, int iterations)
    {
        long checksum = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            byte prefix = (byte)i;
            RouterReader router = new(routerBytes);
            checksum += router.FindTarget(prefix);
        }

        watch.Stop();
        return new RoutePerfResult(iterations, watch.Elapsed, checksum);
    }


    private static double GetNsPerRoute(RoutePerfResult result)
    {
        double seconds = Math.Max(result.Elapsed.TotalSeconds, 0.000001);
        return (seconds * 1_000_000_000d) / result.Iterations;
    }


    private readonly record struct RoutePerfResult(
        int Iterations,
        TimeSpan Elapsed,
        long Checksum);


    private readonly record struct RouteShapeResult(
        string Name,
        bool Equivalent,
        int CompressedRouteCount,
        int CompressedRouteBytes,
        int ExpandedRouteBytes,
        int SavedRouteBytes,
        RoutePerfResult CompressedPerf,
        RoutePerfResult ExpandedPerf);


    private readonly record struct RouteStressResult(
        int Commits,
        long Writes,
        long Bytes,
        long SetLength,
        TimeSpan Elapsed,
        long FinalTarget);


    private readonly record struct RouterArenaReadCachePerfSetup(
        long RootRouterOffset,
        ulong FirstArenaKey,
        ulong SecondArenaKey,
        ulong DirectKey);


    private readonly record struct RouterArenaReadCachePerfResult(
        string Name,
        int Iterations,
        TimeSpan Elapsed,
        long ReadCallCount,
        long BackingReadCallCount,
        long BytesRead,
        long Checksum);


    private struct RoutedBulkWriteLoopTelemetry
    {
        public long CommitCount;
        public long DeferredCommitRequests;
        public long WriteCallCount;
        public long BytesWritten;
        public long SetLengthCallCount;
        public long InsertedCount;
        public long Checksum;
        public long RootLookupTicks;
        public long RouteTargetCacheHitCount;
        public long RouteTargetCacheMissCount;
        public long RouteWalkCount;
        public long RouteWalkTicks;
        public long ShelfInsertTicks;
        public long ShelfCacheHitCount;
        public long ShelfCacheMissCount;
        public long CachedInsertCount;
        public long WalkedFallbackCount;
        public long CacheFullFallbackCount;
        public long NoSplitFallbackCount;
        public long TransformSplitFallbackCount;
        public long ParentRouteSplitFallbackCount;
        public long RootRouteCreateCount;
        public long FullShelfStageCount;
        public long DeltaShelfStageCount;
        public long DeltaChangedSpanCount;
        public long DeltaChangedBytes;
    }


    private enum RouterArenaReadCachePerfPattern
    {
        SameArena,
        AlternatingArenas,
        MixedDirectAndArena
    }

}
