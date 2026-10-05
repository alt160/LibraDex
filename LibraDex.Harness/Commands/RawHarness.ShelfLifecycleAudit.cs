using System.Buffers.Binary;
using System.Numerics;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Proves the repaired shelf-local lifecycle behavior across fixed-N exhausted keys and all five variable-entry mutable shelf shapes.<br/>
    /// Fixed-N routes must preserve 20,000 same-key identities across reopen, while variable-entry shelves must automatically reclaim deleted payload before reporting `Full`.<br/>
    /// </summary>
    /// <param name="args">Harness arguments; `--directory` selects the isolated diagnostic output directory.<br/></param>
    /// <returns>Zero when every shelf-local lifecycle gate passes.<br/></returns>
    private static int RunShelfLifecycleAudit(string[] args)
    {
        string directory = GetOption(
            args,
            "--directory",
            Path.Combine(Path.GetTempPath(), "LibraDex", $"shelf-lifecycle-audit-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);

        try
        {
            FixedNExhaustedKeyDiagnostic[] fixedN =
            [
                DiagnoseFixedNScalar8ExhaustedKey(Path.Combine(directory, "fsn-8.lbdx")),
                DiagnoseFixedNScalar16ExhaustedKey(Path.Combine(directory, "fsn-16.lbdx")),
                DiagnoseFixedNVarIdentityExhaustedKey(Path.Combine(directory, "fsn-v.lbdx"))
            ];
            FixedNRouterArenaDiagnostic arena = DiagnoseFixedNRouterArenaReopen(Path.Combine(directory, "fsn-router-arena.lbdx"));

            VariablePayloadReuseDiagnostic[] payload =
            [
                DiagnoseVarKeyScalar8PayloadReuse(),
                DiagnoseVarKeyScalar16PayloadReuse(),
                DiagnoseVarKeyVarIdentityPayloadReuse(),
                DiagnoseScalar8VarIdentityPayloadReuse(),
                DiagnoseScalar16VarIdentityPayloadReuse()
            ];

            Console.WriteLine("shape,diagnostic,rows,deleted_payload_bytes,insert_result,reopen_count");
            foreach (FixedNExhaustedKeyDiagnostic row in fixedN)
            {
                Console.WriteLine($"{row.Shape},exhausted-key-terminal,{row.InsertedCount},0,Inserted,{row.ReopenedCount}");
            }

            foreach (VariablePayloadReuseDiagnostic row in payload)
            {
                Console.WriteLine($"{row.Shape},deleted-payload-reuse,{row.InitialCount},{row.DeletedPayloadBytes},{row.BeforeCompaction},not-applicable");
            }

            Console.WriteLine($"FSN-8,router-arena-reopen,{arena.InsertedCount},0,page-{arena.ReusedPageIndex},{arena.FileLengthAfterReuse}");

            Console.WriteLine("shelf-lifecycle-audit passed");
            return 0;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Inserts 20,000 distinct scalar-eight identities under one fixed-N key and proves exact count after reopen.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the diagnostic.<br/></param>
    /// <returns>The shape name plus inserted and reopened tuple counts.<br/></returns>
    private static FixedNExhaustedKeyDiagnostic DiagnoseFixedNScalar8ExhaustedKey(string path)
    {
        File.Delete(path);
        try
        {
            {
                using Catalog catalog = Catalog.Create(path);
                using LibraDexBigIntScalar8Index<long> index = catalog.Indexes.IndexSet("audit").Define("fsn-8").BigIntKeys<long>(32).Create(IndexKeys.NonUnique);
                BigInteger key = new(42);
                for (int i = 0; i < 20_000; i++)
                    ValidateGenericInsert(index.Insert(key, i + 1L), $"FSN-8 exhausted-key diagnostic insert {i}");
                if (index.Count() != 20_000)
                    throw new InvalidDataException($"FSN-8 terminal count before reopen was {index.Count():n0}, expected 20,000.");
            }

            using Catalog reopened = Catalog.Open(path);
            using LibraDexBigIntScalar8Index<long> reopenedIndex = reopened.Indexes.IndexSet("audit").Define("fsn-8").BigIntKeys<long>(32).Open();
            long reopenedCount = reopenedIndex.Count();
            if (reopenedCount != 20_000)
                throw new InvalidDataException($"FSN-8 terminal count after reopen was {reopenedCount:n0}, expected 20,000.");
            ValidateFixedNTerminalStorageAssessment(reopened, "fsn-8", 20_000);
            if (!reopenedIndex.Delete(new BigInteger(42), 1L) || reopenedIndex.Count() != 19_999)
                throw new InvalidDataException("FSN-8 terminal exact delete after reopen did not remove exactly one identity.");
            return new FixedNExhaustedKeyDiagnostic("FSN-8", 20_000, reopenedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Inserts 20,000 distinct scalar-sixteen identities under one fixed-N key and proves exact count after reopen.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the diagnostic.<br/></param>
    /// <returns>The shape name plus inserted and reopened tuple counts.<br/></returns>
    private static FixedNExhaustedKeyDiagnostic DiagnoseFixedNScalar16ExhaustedKey(string path)
    {
        File.Delete(path);
        try
        {
            {
                using Catalog catalog = Catalog.Create(path);
                using LibraDexBigIntScalar8Index<Guid> index = catalog.Indexes.IndexSet("audit").Define("fsn-16").BigIntKeys<Guid>(32).Create(IndexKeys.NonUnique);
                BigInteger key = new(42);
                for (int i = 0; i < 20_000; i++)
                    ValidateGenericInsert(index.Insert(key, CreateStableGuid(i + 1)), $"FSN-16 exhausted-key diagnostic insert {i}");
                if (index.Count() != 20_000)
                    throw new InvalidDataException($"FSN-16 terminal count before reopen was {index.Count():n0}, expected 20,000.");
            }

            using Catalog reopened = Catalog.Open(path);
            using LibraDexBigIntScalar8Index<Guid> reopenedIndex = reopened.Indexes.IndexSet("audit").Define("fsn-16").BigIntKeys<Guid>(32).Open();
            long reopenedCount = reopenedIndex.Count();
            if (reopenedCount != 20_000)
                throw new InvalidDataException($"FSN-16 terminal count after reopen was {reopenedCount:n0}, expected 20,000.");
            ValidateFixedNTerminalStorageAssessment(reopened, "fsn-16", 20_000);
            if (!reopenedIndex.Delete(new BigInteger(42), CreateStableGuid(1)) || reopenedIndex.Count() != 19_999)
                throw new InvalidDataException("FSN-16 terminal exact delete after reopen did not remove exactly one identity.");
            return new FixedNExhaustedKeyDiagnostic("FSN-16", 20_000, reopenedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Inserts 20,000 distinct variable identities under one fixed-N key and proves exact count after reopen.<br/>
    /// </summary>
    /// <param name="path">Isolated catalog path used by the diagnostic.<br/></param>
    /// <returns>The shape name plus inserted and reopened tuple counts.<br/></returns>
    private static FixedNExhaustedKeyDiagnostic DiagnoseFixedNVarIdentityExhaustedKey(string path)
    {
        File.Delete(path);
        try
        {
            {
                using Catalog catalog = Catalog.Create(path);
                using LibraDexBigIntVarIdentityIndex index = catalog.Indexes.IndexSet("audit").Define("fsn-v").BigIntVarIdentityKeys(32, 64).Create(IndexKeys.NonUnique);
                BigInteger key = new(42);
                for (int i = 0; i < 20_000; i++)
                {
                    byte[] identity = new byte[24];
                    BinaryPrimitives.WriteInt32BigEndian(identity, i + 1);
                    ValidateGenericInsert(index.Insert(key, identity), $"FSN-V exhausted-key diagnostic insert {i}");
                }
                if (index.Count() != 20_000)
                    throw new InvalidDataException($"FSN-V terminal count before reopen was {index.Count():n0}, expected 20,000.");
            }

            using Catalog reopened = Catalog.Open(path);
            using LibraDexBigIntVarIdentityIndex reopenedIndex = reopened.Indexes.IndexSet("audit").Define("fsn-v").BigIntVarIdentityKeys(32, 64).Open();
            long reopenedCount = reopenedIndex.Count();
            if (reopenedCount != 20_000)
                throw new InvalidDataException($"FSN-V terminal count after reopen was {reopenedCount:n0}, expected 20,000.");
            ValidateFixedNTerminalStorageAssessment(reopened, "fsn-v", 20_000);
            return new FixedNExhaustedKeyDiagnostic("FSN-V", 20_000, reopenedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Proves that a fixed-N shelf transform consumes intermediate router pages inside the former 64 KiB shelf extent and that the persisted arena map is recovered after reopen.<br/>
    /// Seven shared key bytes force a six-page local router stem; the post-reopen allocation must therefore claim page seven without extending the file.<br/>
    /// </summary>
    /// <param name="path">The isolated file-backed catalog path.<br/></param>
    /// <returns>The inserted count, reused arena page, and unchanged file length.<br/></returns>
    private static FixedNRouterArenaDiagnostic DiagnoseFixedNRouterArenaReopen(string path)
    {
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        const int keySize = 8;
        FixedNScalar8Profile profile = FixedNScalar8Profile.Default64KiB(keySize);
        int insertedCount = checked(profile.MaxItemCount + 1_024);
        long transformedShelfOffset;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9911), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "fsn8arena", 0));
            FixedNScalar8IndexHandle handle = new(root.Offset, profile, IsRouted: true);
            using FixedNScalar8Index index = new(session, handle, slotIndex: 0);
            byte[] key = new byte[keySize];
            key.AsSpan(0, keySize - 1).Fill(0x41);
            for (int i = 0; i < insertedCount; i++)
            {
                key[keySize - 1] = checked((byte)(i & byte.MaxValue));
                (FixedNScalarInsertResult result, _) = index.Insert(key, checked((ulong)i + 1), allowDuplicateKeys: true);
                if (result != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-8 router-arena insert {i} returned {result}.");
            }

            transformedShelfOffset = session.FindRouterTarget(root.Offset, 0x41);
            if (transformedShelfOffset == 0 || session.FindRouterTarget(transformedShelfOffset, 0x41) != transformedShelfOffset + RouterLayout.Size)
                throw new InvalidDataException("FSN-8 transformed shelf did not retain its first intermediate router in local arena page one.");
        }

        long lengthBeforeReuse = new FileInfo(path).Length;
        RouterArenaAllocationResult allocation;
        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            _ = reopened.FindRouterTarget(transformedShelfOffset, 0x41);
            allocation = reopened.CreateExpandedOneByteRouterUsingArena(
                keyDepth: 7,
                allocationClassId: 1,
                preferredArenaBaseOffset: transformedShelfOffset);
        }

        long lengthAfterReuse = new FileInfo(path).Length;
        if (allocation.Kind != RouterArenaAllocationKind.LocalArena ||
            allocation.ArenaBaseOffset != transformedShelfOffset ||
            allocation.ArenaPageIndex != 7 ||
            lengthAfterReuse != lengthBeforeReuse)
        {
            throw new InvalidDataException(
                $"FSN-8 reopened router arena expected page 7 without growth, got {allocation.Kind}/base={allocation.ArenaBaseOffset}/page={allocation.ArenaPageIndex}/length={lengthBeforeReuse}->{lengthAfterReuse}.");
        }

        return new FixedNRouterArenaDiagnostic(insertedCount, allocation.ArenaPageIndex, lengthAfterReuse);
    }

    /// <summary>
    /// Validates that maintenance accounting recognizes a fixed-N terminal root as complete authoritative topology.<br/>
    /// </summary>
    /// <param name="catalog">The reopened catalog containing the terminal index.<br/></param>
    /// <param name="indexName">The fixed-N index name expected in the topology components.<br/></param>
    /// <param name="expectedTupleCount">The exact terminal identity count.<br/></param>
    private static void ValidateFixedNTerminalStorageAssessment(Catalog catalog, string indexName, long expectedTupleCount)
    {
        LibraDexCatalogStorageAssessment storage = catalog.Maintenance.Assess().Storage;
        if (!storage.IsComplete || storage.UnsupportedIndexCount != 0)
            throw new InvalidDataException($"{indexName} terminal storage assessment was incomplete.");

        foreach (LibraDexFixedTopologyStorageComponent component in storage.FixedTopologyComponents)
        {
            if (string.Equals(component.IndexName, indexName, StringComparison.Ordinal))
            {
                if (component.TupleCount != expectedTupleCount)
                    throw new InvalidDataException($"{indexName} terminal topology counted {component.TupleCount:n0}, expected {expectedTupleCount:n0}.");
                return;
            }
        }

        throw new InvalidDataException($"{indexName} terminal storage assessment did not expose a fixed topology component.");
    }

    /// <summary>
    /// Proves that a full `VS8` shelf cannot consume deleted payload until its explicit repack helper runs.<br/>
    /// The same-sized insertion is attempted before and after repack so slot capacity and requested payload size remain controlled.<br/>
    /// </summary>
    /// <returns>The observed before/after insertion results and exact reclaimable byte count.<br/></returns>
    private static VariablePayloadReuseDiagnostic DiagnoseVarKeyScalar8PayloadReuse()
    {
        const int keyLength = 128;
        VarKeyScalar8Profile profile = VarKeyScalar8Profile.DefaultInitial;
        byte[] bytes = VarKeyScalar8.CreateEmpty(profile);
        int count = 0;
        while (true)
        {
            byte[] key = CreateLifecycleAuditBytes(count, keyLength);
            VarKeyScalar8InsertResult result = VarKeyScalar8.Insert(bytes, profile, key, checked((ulong)count + 1), true, out byte[] rewritten);
            if (result == VarKeyScalar8InsertResult.Full)
                break;
            if (result != VarKeyScalar8InsertResult.Inserted)
                throw new InvalidDataException($"VS8 lifecycle fill returned {result} at {count}.");
            bytes = rewritten;
            count++;
        }

        if (!VarKeyScalar8MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf shelf))
            throw new InvalidDataException("VS8 lifecycle diagnostic could not decode its full shelf.");
        _ = shelf.MarkSlotRangeDeleted(0, Math.Min(4, Math.Max(1, count / 4)));
        int deletedBytes = shelf.PayloadBytesDeleted;
        byte[] incoming = CreateLifecycleAuditBytes(count + 1, keyLength);
        VarKeyScalar8InsertResult automatic = shelf.InsertWithMutationHint(
            incoming,
            checked((ulong)count + 2),
            true,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _);
        if (automatic != VarKeyScalar8InsertResult.Inserted || deletedBytes < VarKeyScalar8Layout.GetNewRecordLength(keyLength))
            throw new InvalidDataException($"VS8 automatic payload reuse returned {automatic} with {deletedBytes} reclaimable bytes.");
        return new VariablePayloadReuseDiagnostic("VS8", count, deletedBytes, automatic.ToString(), "automatic");
    }

    /// <summary>
    /// Proves that a full `VS16` shelf cannot consume deleted payload until its explicit repack helper runs.<br/>
    /// Identity width changes only the fixed tuple lane; the key payload and delete pattern match the `VS8` fixture.<br/>
    /// </summary>
    /// <returns>The observed before/after insertion results and exact reclaimable byte count.<br/></returns>
    private static VariablePayloadReuseDiagnostic DiagnoseVarKeyScalar16PayloadReuse()
    {
        const int keyLength = 128;
        VarKeyScalar16Profile profile = VarKeyScalar16Profile.DefaultInitial;
        byte[] bytes = VarKeyScalar16.CreateEmpty(profile);
        int count = 0;
        while (true)
        {
            byte[] key = CreateLifecycleAuditBytes(count, keyLength);
            VarKeyScalar16InsertResult result = VarKeyScalar16.Insert(bytes, profile, key, 0, checked((ulong)count + 1), true, out byte[] rewritten);
            if (result == VarKeyScalar16InsertResult.Full)
                break;
            if (result != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException($"VS16 lifecycle fill returned {result} at {count}.");
            bytes = rewritten;
            count++;
        }

        if (!VarKeyScalar16MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyScalar16MutableShelf shelf))
            throw new InvalidDataException("VS16 lifecycle diagnostic could not decode its full shelf.");
        _ = shelf.MarkSlotRangeDeleted(0, Math.Min(4, Math.Max(1, count / 4)));
        int deletedBytes = shelf.PayloadBytesDeleted;
        byte[] incoming = CreateLifecycleAuditBytes(count + 1, keyLength);
        VarKeyScalar16InsertResult automatic = shelf.InsertWithMutationHint(
            incoming,
            0,
            checked((ulong)count + 2),
            true,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _);
        if (automatic != VarKeyScalar16InsertResult.Inserted || deletedBytes < VarKeyScalar16Layout.GetNewRecordLength(keyLength))
            throw new InvalidDataException($"VS16 automatic payload reuse returned {automatic} with {deletedBytes} reclaimable bytes.");
        return new VariablePayloadReuseDiagnostic("VS16", count, deletedBytes, automatic.ToString(), "automatic");
    }

    /// <summary>
    /// Proves that a full `VV` shelf cannot consume deleted key/identity payload until explicit repack runs.<br/>
    /// Equal fixed lengths keep the diagnostic focused on the local arena policy rather than a best-fit size decision.<br/>
    /// </summary>
    /// <returns>The observed before/after insertion results and exact reclaimable byte count.<br/></returns>
    private static VariablePayloadReuseDiagnostic DiagnoseVarKeyVarIdentityPayloadReuse()
    {
        const int keyLength = 64;
        const int identityLength = 64;
        VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.DefaultInitial;
        byte[] bytes = VarKeyVarIdentity.CreateEmpty(profile);
        int count = 0;
        while (true)
        {
            byte[] key = CreateLifecycleAuditBytes(count, keyLength);
            byte[] identity = CreateLifecycleAuditBytes(count + 1, identityLength);
            VarKeyVarIdentityInsertResult result = VarKeyVarIdentity.Insert(bytes, profile, key, identity, true, out byte[] rewritten);
            if (result == VarKeyVarIdentityInsertResult.Full)
                break;
            if (result != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException($"VV lifecycle fill returned {result} at {count}.");
            bytes = rewritten;
            count++;
        }

        if (!VarKeyVarIdentityMutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf shelf))
            throw new InvalidDataException("VV lifecycle diagnostic could not decode its full shelf.");
        _ = shelf.MarkSlotRangeDeleted(0, Math.Min(4, Math.Max(1, count / 4)));
        int deletedBytes = shelf.PayloadBytesDeleted;
        byte[] incomingKey = CreateLifecycleAuditBytes(count + 1, keyLength);
        byte[] incomingIdentity = CreateLifecycleAuditBytes(count + 2, identityLength);
        VarKeyVarIdentityInsertResult automatic = shelf.InsertWithMutationHint(
            incomingKey,
            incomingIdentity,
            true,
            hintStartDepth: 0,
            maxHintBytes: 0,
            out _);
        if (automatic != VarKeyVarIdentityInsertResult.Inserted || deletedBytes < VarKeyVarIdentityLayout.GetNewRecordLength(keyLength, identityLength))
            throw new InvalidDataException($"VV automatic payload reuse returned {automatic} with {deletedBytes} reclaimable bytes.");
        return new VariablePayloadReuseDiagnostic("VV", count, deletedBytes, automatic.ToString(), "automatic");
    }

    /// <summary>
    /// Proves that a full `SV8` shelf cannot consume deleted variable-identity payload until a compact replacement image is built.<br/>
    /// The replacement is reopened through the normal mutable decoder before the controlled insertion is retried.<br/>
    /// </summary>
    /// <returns>The observed before/after insertion results and exact reclaimable byte count.<br/></returns>
    private static VariablePayloadReuseDiagnostic DiagnoseScalar8VarIdentityPayloadReuse()
    {
        const int identityLength = 128;
        Scalar8VarIdentityProfile profile = Scalar8VarIdentityProfile.DefaultInitial;
        byte[] bytes = Scalar8VarIdentity.CreateEmpty(profile);
        int count = 0;
        while (true)
        {
            byte[] identity = CreateLifecycleAuditBytes(count + 1, identityLength);
            Scalar8VarIdentityInsertResult result = Scalar8VarIdentity.Insert(bytes, profile, checked((ulong)count + 1), identity, true, out byte[] rewritten);
            if (result == Scalar8VarIdentityInsertResult.Full)
                break;
            if (result != Scalar8VarIdentityInsertResult.Inserted)
                throw new InvalidDataException($"SV8 lifecycle fill returned {result} at {count}.");
            bytes = rewritten;
            count++;
        }

        if (!Scalar8VarIdentityMutableShelfView.TryCreate(bytes, profile, out Scalar8VarIdentityMutableShelfView shelf))
            throw new InvalidDataException("SV8 lifecycle diagnostic could not decode its full shelf.");
        _ = shelf.MarkSlotRangeDeleted(0, Math.Min(4, Math.Max(1, count / 4)));
        int deletedBytes = shelf.PayloadBytesDeleted;
        byte[] incoming = CreateLifecycleAuditBytes(count + 2, identityLength);
        Scalar8VarIdentityInsertResult automatic = shelf.Insert(checked((ulong)count + 2), incoming, true);
        if (automatic != Scalar8VarIdentityInsertResult.Inserted || deletedBytes < Scalar8VarIdentityLayout.GetNewRecordLength(identityLength))
            throw new InvalidDataException($"SV8 automatic payload reuse returned {automatic} with {deletedBytes} reclaimable bytes.");
        return new VariablePayloadReuseDiagnostic("SV8", count, deletedBytes, automatic.ToString(), "automatic");
    }

    /// <summary>
    /// Proves that a full `SV16` shelf cannot consume deleted variable-identity payload until a compact replacement image is built.<br/>
    /// The fixture mirrors `SV8` with widened scalar keys so both reverse varlen layouts receive independent evidence.<br/>
    /// </summary>
    /// <returns>The observed before/after insertion results and exact reclaimable byte count.<br/></returns>
    private static VariablePayloadReuseDiagnostic DiagnoseScalar16VarIdentityPayloadReuse()
    {
        const int identityLength = 128;
        Scalar16VarIdentityProfile profile = Scalar16VarIdentityProfile.DefaultInitial;
        byte[] bytes = Scalar16VarIdentity.CreateEmpty(profile);
        int count = 0;
        while (true)
        {
            byte[] identity = CreateLifecycleAuditBytes(count + 1, identityLength);
            Scalar16VarIdentityInsertResult result = Scalar16VarIdentity.Insert(bytes, profile, 0, checked((ulong)count + 1), identity, true, out byte[] rewritten);
            if (result == Scalar16VarIdentityInsertResult.Full)
                break;
            if (result != Scalar16VarIdentityInsertResult.Inserted)
                throw new InvalidDataException($"SV16 lifecycle fill returned {result} at {count}.");
            bytes = rewritten;
            count++;
        }

        if (!Scalar16VarIdentityMutableShelfView.TryCreate(bytes, profile, out Scalar16VarIdentityMutableShelfView shelf))
            throw new InvalidDataException("SV16 lifecycle diagnostic could not decode its full shelf.");
        _ = shelf.MarkSlotRangeDeleted(0, Math.Min(4, Math.Max(1, count / 4)));
        int deletedBytes = shelf.PayloadBytesDeleted;
        byte[] incoming = CreateLifecycleAuditBytes(count + 2, identityLength);
        Scalar16VarIdentityInsertResult automatic = shelf.Insert(0, checked((ulong)count + 2), incoming, true);
        if (automatic != Scalar16VarIdentityInsertResult.Inserted || deletedBytes < Scalar16VarIdentityLayout.GetNewRecordLength(identityLength))
            throw new InvalidDataException($"SV16 automatic payload reuse returned {automatic} with {deletedBytes} reclaimable bytes.");
        return new VariablePayloadReuseDiagnostic("SV16", count, deletedBytes, automatic.ToString(), "automatic");
    }

    /// <summary>
    /// Creates a stable byte-ordinal value whose first four bytes preserve the supplied non-negative ordinal.<br/>
    /// Remaining bytes use a deterministic pattern so equal-length fixture records do not accidentally alias while payload size remains controlled.<br/>
    /// </summary>
    /// <param name="ordinal">Non-negative fixture ordinal written in big-endian order.<br/></param>
    /// <param name="length">Requested byte-array length, which must be at least four.<br/></param>
    /// <returns>A deterministic sortable fixture value.<br/></returns>
    private static byte[] CreateLifecycleAuditBytes(int ordinal, int length)
    {
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (length < sizeof(int))
            throw new ArgumentOutOfRangeException(nameof(length));

        byte[] value = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(value, ordinal);
        for (int i = sizeof(int); i < value.Length; i++)
            value[i] = checked((byte)((ordinal + i) % 251));
        return value;
    }

    private readonly record struct FixedNExhaustedKeyDiagnostic(
        string Shape,
        int InsertedCount,
        long ReopenedCount);

    private readonly record struct FixedNRouterArenaDiagnostic(
        int InsertedCount,
        ushort ReusedPageIndex,
        long FileLengthAfterReuse);

    private readonly record struct VariablePayloadReuseDiagnostic(
        string Shape,
        int InitialCount,
        int DeletedPayloadBytes,
        string BeforeCompaction,
        string AfterCompaction);
}
