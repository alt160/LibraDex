using System.Numerics;
using System.Buffers;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Verifies descending tuple order across the public index families with deliberately large equal-key runs.<br/>
    /// The first-result limits exercise reverse physical traversal rather than a caller-side whole-result reversal.<br/>
    /// </summary>
    /// <param name="args">Unused validation-command arguments.<br/></param>
    /// <returns>Zero when every descending index family preserves key then identity order.<br/></returns>
    private static int RunDescendingSortOrderSanity(string[] args)
    {
        bool verifyPhysicalSv8 = args.Contains("--verify-physical-sv8", StringComparer.OrdinalIgnoreCase);
        bool verifyPhysicalSv16 = args.Contains("--verify-physical-sv16", StringComparer.OrdinalIgnoreCase);
        bool verifySs88Shelf = args.Contains("--verify-physical-ss88-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifySs168Shelf = args.Contains("--verify-physical-ss168-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifySs816Shelf = args.Contains("--verify-physical-ss816-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifySs1616Shelf = args.Contains("--verify-physical-ss1616-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyFs328Shelf = args.Contains("--verify-physical-fs328-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyFs3216Shelf = args.Contains("--verify-physical-fs3216-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyFsn8Shelf = args.Contains("--verify-physical-fsn8-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyFsn16Shelf = args.Contains("--verify-physical-fsn16-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyFsnvShelf = args.Contains("--verify-physical-fsnv-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyVs8Shelf = args.Contains("--verify-physical-vs8-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyVs8Split = args.Contains("--verify-physical-vs8-split", StringComparer.OrdinalIgnoreCase);
        bool verifyVs8Terminal = args.Contains("--verify-physical-vs8-terminal", StringComparer.OrdinalIgnoreCase);
        bool verifyVs8Optimizer = args.Contains("--verify-physical-vs8-optimizer", StringComparer.OrdinalIgnoreCase);
        bool verifyVs16Shelf = args.Contains("--verify-physical-vs16-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyVs16Split = args.Contains("--verify-physical-vs16-split", StringComparer.OrdinalIgnoreCase);
        bool verifyVs16Terminal = args.Contains("--verify-physical-vs16-terminal", StringComparer.OrdinalIgnoreCase);
        bool verifyVs16Api = args.Contains("--verify-physical-vs16-api", StringComparer.OrdinalIgnoreCase);
        bool verifyVs16Optimizer = args.Contains("--verify-physical-vs16-optimizer", StringComparer.OrdinalIgnoreCase);
        bool verifyVvShelf = args.Contains("--verify-physical-vv-shelf", StringComparer.OrdinalIgnoreCase);
        bool verifyVvApi = args.Contains("--verify-physical-vv-api", StringComparer.OrdinalIgnoreCase);
        bool verifyVvCatalog = args.Contains("--verify-physical-vv-catalog", StringComparer.OrdinalIgnoreCase);
        bool verifyVvSplit = args.Contains("--verify-physical-vv-split", StringComparer.OrdinalIgnoreCase);
        bool verifyVvTerminal = args.Contains("--verify-physical-vv-terminal", StringComparer.OrdinalIgnoreCase);
        bool verifyComposite = args.Contains("--verify-physical-composite", StringComparer.OrdinalIgnoreCase);
        bool verifyShapeSortOverride = args.Contains("--verify-shape-sort-override", StringComparer.OrdinalIgnoreCase);
        if (verifyPhysicalSv16)
        {
            Scalar16VarIdentityProfile profile = Scalar16VarIdentityProfile.Create(4 * 1024, 160, descending: true);
            byte[] shelfBytes = Scalar16VarIdentity.CreateEmpty(profile);
            byte[] a = [0x61];
            byte[] aa = [0x61, 0x61];
            byte[] b = [0x62];
            if (Scalar16VarIdentity.InsertInPlace(shelfBytes, profile, 1, 1, a, true, out shelfBytes) != Scalar16VarIdentityInsertResult.Inserted ||
                Scalar16VarIdentity.InsertInPlace(shelfBytes, profile, 2, 0, a, true, out shelfBytes) != Scalar16VarIdentityInsertResult.Inserted ||
                Scalar16VarIdentity.InsertInPlace(shelfBytes, profile, 1, 1, aa, true, out shelfBytes) != Scalar16VarIdentityInsertResult.Inserted ||
                Scalar16VarIdentity.InsertInPlace(shelfBytes, profile, 1, 1, b, true, out shelfBytes) != Scalar16VarIdentityInsertResult.Inserted)
                throw new InvalidDataException("SV16 descending shelf fixture insert failed.");
            Scalar16VarIdentityReadOnly physical = new(shelfBytes, profile);
            if (!physical.IsValid || !physical.IsDescending || physical.ItemCount != 4 ||
                physical.ReadKeyHighAt(0) != 2 || !physical.ReadIdentityAt(1).SequenceEqual(b) ||
                !physical.ReadIdentityAt(2).SequenceEqual(aa) || !physical.ReadIdentityAt(3).SequenceEqual(a) ||
                physical.LowerBoundKey(1, 1) != 1 || physical.LowerBound(1, 1, aa) != 2 ||
                !physical.Contains(1, 1, aa))
                throw new InvalidDataException("SV16 descending shelf physical order or binary bounds failed.");
            if (!Scalar16VarIdentityMutableShelfView.TryCreate(shelfBytes, profile, out Scalar16VarIdentityMutableShelfView mutable) ||
                mutable.MarkKeyRangeDeleted(1, 1, 1, 1) != 3 ||
                (Scalar16VarIdentityLayout.ReadFlags(shelfBytes) & Scalar16VarIdentityLayout.DescendingFlag) == 0)
                throw new InvalidDataException("SV16 descending shelf range deletion or direction flag failed.");

            DataKernelOptions options = new(DefaultAppendBufferSize, 0, false, 512);
            using (LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                options, new SuperblockDeveloperMetadata("DescendingSV16Split", "descending physical split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "descsv16split", 0));
                _ = splitSession.CreateScalar16VarIdentityShelfAndLinkRootRoute(root.Offset, 0, profile);
                for (ulong key = 120; key >= 1; key--)
                {
                    Scalar16VarIdentityRoutedInsertResult inserted = splitSession.InsertWalkedRoutedScalar16VarIdentity(
                        root.Offset, profile.MaxIdentityLength, key << 48, 0, a, true,
                        maxRouterHops: 32, descending: true);
                    if (inserted.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                        throw new InvalidDataException($"SV16 descending split fixture insert failed at {key}: {inserted.Kind}/{inserted.InsertResult}.");
                }
                using Scalar16VarIdentityRangeReader reader = splitSession.OpenScalar16VarIdentityRangeReader(
                    root.Offset, profile.MaxIdentityLength, 1UL << 48, 0, 120UL << 48, 0, descending: true);
                for (ulong expected = 120; expected >= 1; expected--)
                {
                    if (!reader.MoveNext() || reader.CurrentEncodedKeyHigh != expected << 48)
                        throw new InvalidDataException($"SV16 descending split reader lost key {expected}.");
                }
                if (reader.MoveNext() ||
                    !splitSession.TryWalkScalar16VarIdentityExactShelf(root.Offset, 120UL << 48, 0, 32, out long highShelfOffset))
                    throw new InvalidDataException("SV16 descending split route did not terminate correctly.");
                byte[] highShelfBytes = splitSession.ReadScalar16VarIdentityShelfBytes(highShelfOffset, profile.MaxIdentityLength, out Scalar16VarIdentityProfile highProfile);
                Scalar16VarIdentityReadOnly highShelf = new(highShelfBytes, highProfile);
                if (!highShelf.IsValid || !highProfile.Descending || highShelf.ReadKeyHighAt(0) != 120UL << 48)
                    throw new InvalidDataException("SV16 descending split high-side shelf is not physically highest-first.");
            }

            using (LibraDexFileSession chainSession = LibraDexFileSession.InitializeMemory(
                options, new SuperblockDeveloperMetadata("DescendingSV16Chain", "descending physical equal-key chain", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = chainSession.CreateRootRouterIndex(CreateHarnessSlot(0, "descsv16chain", 0));
                _ = chainSession.CreateScalar16VarIdentityShelfAndLinkRootRoute(root.Offset, 0, profile);
                for (int number = 100; number >= 2; number -= 2)
                {
                    byte[] identity = new byte[160];
                    identity[1] = checked((byte)number);
                    Scalar16VarIdentityRoutedInsertResult inserted = chainSession.InsertWalkedRoutedScalar16VarIdentity(
                        root.Offset, profile.MaxIdentityLength, 1, 7, identity, true,
                        maxRouterHops: 32, descending: true);
                    if (inserted.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                        throw new InvalidDataException($"SV16 descending chain fixture insert failed at {number}.");
                }
                foreach (byte number in new byte[] { 101, 53, 1 })
                {
                    byte[] identity = new byte[160];
                    identity[1] = number;
                    Scalar16VarIdentityRoutedInsertResult inserted = chainSession.InsertWalkedRoutedScalar16VarIdentity(
                        root.Offset, profile.MaxIdentityLength, 1, 7, identity, true,
                        maxRouterHops: 32, descending: true);
                    if (inserted.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                        throw new InvalidDataException($"SV16 descending chain non-tail insert failed at {number}.");
                }
                if (!chainSession.TryWalkScalar16VarIdentityExactShelf(root.Offset, 1, 7, 32, out long headOffset))
                    throw new InvalidDataException("SV16 descending chain route is missing.");
                byte[] headBytes = chainSession.ReadScalar16VarIdentityShelfBytes(headOffset, profile.MaxIdentityLength, out Scalar16VarIdentityProfile headProfile);
                Scalar16VarIdentityReadOnly head = new(headBytes, headProfile);
                if (!head.IsValid || !headProfile.Descending || head.ReadIdentityAt(0)[1] != 101 || head.NextShelfOffset == 0)
                    throw new InvalidDataException("SV16 descending chain head is not the physical highest identity.");
                using Scalar16VarIdentityRangeReader reader = chainSession.OpenScalar16VarIdentityRangeReader(
                    root.Offset, profile.MaxIdentityLength, 1, 7, 1, 7, descending: true);
                if (!reader.MoveNext() || reader.CurrentIdentity[1] != 101)
                    throw new InvalidDataException("SV16 descending chain first result is not the physical head.");
                object? loadedShelves = typeof(Scalar16VarIdentityRangeReader)
                    .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(reader);
                if (loadedShelves is not int loadedCount || loadedCount != 1)
                    throw new InvalidDataException("SV16 descending chain first result loaded more than one shelf.");
                int count = 1;
                int previous = 101;
                while (reader.MoveNext())
                {
                    int current = reader.CurrentIdentity[1];
                    if (current >= previous)
                        throw new InvalidDataException("SV16 descending chain returned identities out of physical order.");
                    previous = current;
                    count++;
                }
                if (count != 53 || previous != 1)
                    throw new InvalidDataException($"SV16 descending chain returned {count} rows; final identity {previous}.");
                if (chainSession.CountScalar16VarIdentityRange(root.Offset, profile.MaxIdentityLength, 1, 7, 1, 7, descending: true) != 53)
                    throw new InvalidDataException("SV16 descending chain key-range count missed linked shelves.");
                LibraDexMaintenanceWalkResult optimized = chainSession.OptimizeScalar16VarIdentityTopology(
                    root.Offset, profile.MaxIdentityLength, maxWorkItems: null, descending: true);
                if (!optimized.Completed || optimized.ChangedCount != 1)
                    throw new InvalidDataException("SV16 descending optimizer did not publish its rebuilt equal-key route.");
                using Scalar16VarIdentityRangeReader maintained = chainSession.OpenScalar16VarIdentityRangeReader(
                    root.Offset, profile.MaxIdentityLength, 1, 7, 1, 7, descending: true);
                int maintainedCount = maintained.Count;
                bool maintainedHasFirst = maintained.MoveNext();
                int maintainedFirst = maintainedHasFirst ? maintained.CurrentIdentity[1] : -1;
                if (maintainedCount != 53 || maintainedFirst != 101)
                {
                    _ = chainSession.TryWalkScalar16VarIdentityExactShelf(root.Offset, 1, 7, 32, out long rebuiltHeadOffset);
                    byte[] rebuiltHeadBytes = chainSession.ReadScalar16VarIdentityShelfBytes(rebuiltHeadOffset, profile.MaxIdentityLength, out _);
                    throw new InvalidDataException($"SV16 descending optimizer lost the physical high-first chain: considered={optimized.ConsideredCount}, count={maintainedCount}, first={maintainedFirst}, headCount={Scalar16VarIdentityLayout.ReadItemCount(rebuiltHeadBytes)}, next={Scalar16VarIdentityLayout.ReadNextShelfOffset(rebuiltHeadBytes)}, tail={Scalar16VarIdentityLayout.ReadTailShelfOffset(rebuiltHeadBytes)}.");
                }
                _ = chainSession.TryWalkScalar16VarIdentityExactShelf(root.Offset, 1, 7, 32, out long expectedBatchHeadOffset);
                Scalar16VarIdentityRoutedInsertResult batchInserted = default;
                int visibleDuringBatch = -1;
                using (LibraDexFileSessionDurabilityBatch batch = chainSession.BeginDurabilityBatch())
                {
                    byte[] higher = new byte[160];
                    higher[1] = 102;
                    batchInserted = chainSession.InsertWalkedRoutedScalar16VarIdentity(
                        root.Offset, profile.MaxIdentityLength, 1, 7, higher, true,
                        maxRouterHops: 32, descending: true);
                    if (batchInserted.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                        throw new InvalidDataException("SV16 descending batched chain head insert failed.");
                    using Scalar16VarIdentityRangeReader duringBatch = chainSession.OpenScalar16VarIdentityRangeReader(
                        root.Offset, profile.MaxIdentityLength, 1, 7, 1, 7, descending: true);
                    visibleDuringBatch = duringBatch.Count;
                    _ = batch.Commit();
                }
                using Scalar16VarIdentityRangeReader afterBatch = chainSession.OpenScalar16VarIdentityRangeReader(
                    root.Offset, profile.MaxIdentityLength, 1, 7, 1, 7, descending: true);
                int afterBatchCount = afterBatch.Count;
                bool afterBatchHasFirst = afterBatch.MoveNext();
                int afterBatchFirst = afterBatchHasFirst ? afterBatch.CurrentIdentity[1] : -1;
                if (afterBatchCount != 54 || afterBatchFirst != 102)
                    throw new InvalidDataException($"SV16 descending batched chain rewrite lost highest-first order or a linked identity: before={visibleDuringBatch}, count={afterBatchCount}, first={afterBatchFirst}, kind={batchInserted.Kind}, primary={batchInserted.PrimaryOffset}, expectedHead={expectedBatchHeadOffset}.");
            }

            string path = Path.GetFullPath(Path.Combine("artifacts", $"descending-sv16-api-{Guid.NewGuid():N}.lbdx"));
            const ulong highRootKey = 0x8000_0000_0000_0000UL;
            try
            {
                using (Scalar16VarIdentityIndex index = Indexes.SV16.Create(path: path, descending: true))
                {
                    if (!index.Insert(1, 1, a).Inserted || !index.Insert(2, 0, a).Inserted ||
                        !index.Insert(1, 1, aa).Inserted || !index.Insert(1, 1, b).Inserted ||
                        !index.Insert(highRootKey, 0, a).Inserted)
                        throw new InvalidDataException("SV16 descending API fixture insert failed.");
                }
                using Scalar16VarIdentityIndex reopened = Indexes.SV16.Open(path);
                if (!reopened.Descending)
                    throw new InvalidDataException("SV16 descending API reopen lost directory sort order.");
                using (Scalar16VarIdentityRangeReader reader = reopened.OpenRangeReader(1, 1, highRootKey, 0))
                {
                    if (!reader.MoveNext() || reader.CurrentEncodedKeyHigh != highRootKey ||
                        !reader.MoveNext() || reader.CurrentEncodedKeyHigh != 2 ||
                        !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(b) ||
                        !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(aa) ||
                        !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(a) || reader.MoveNext())
                        throw new InvalidDataException("SV16 descending API reader did not preserve physical tuple order.");
                }
                if (reopened.CountIdentityRange(1, 1, highRootKey, 0) != 5 ||
                    !reopened.DeleteExactTuple(1, 1, aa) ||
                    reopened.CountIdentityRange(1, 1, highRootKey, 0) != 4 ||
                    reopened.DeleteRange(1, 1, 1, 1) != 2 ||
                    reopened.CountIdentityRange(1, 1, highRootKey, 0) != 2)
                    throw new InvalidDataException("SV16 descending API count or delete lost its inclusive key range.");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        if (verifyVvShelf)
        {
            VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.DefaultInitial with { Descending = true };
            byte[] bytes = VarKeyVarIdentity.CreateEmpty(profile);
            if (!VarKeyVarIdentityMutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf mutable))
                throw new InvalidDataException("VV descending shelf fixture could not open its mutable shelf.");
            byte[] lowKey = [0x61];
            byte[] highKey = [0x62];
            byte[] shortIdentity = [0x61];
            byte[] longIdentity = [0x61, 0x61];
            byte[] highIdentity = [0x62];
            if (mutable.InsertWithMutationHint(lowKey, shortIdentity, true, 0, 0, out _) != VarKeyVarIdentityInsertResult.Inserted ||
                mutable.InsertWithMutationHint(highKey, shortIdentity, true, 0, 0, out _) != VarKeyVarIdentityInsertResult.Inserted ||
                mutable.InsertWithMutationHint(lowKey, longIdentity, true, 0, 0, out _) != VarKeyVarIdentityInsertResult.Inserted ||
                mutable.InsertWithMutationHint(lowKey, highIdentity, true, 0, 0, out _) != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException("VV descending shelf fixture insert failed.");
            VarKeyVarIdentityReadOnly physical = new(bytes, profile);
            if (!physical.IsValid || !physical.IsDescending || physical.ItemCount != 4 ||
                !physical.ReadKeyAt(0).SequenceEqual(highKey) ||
                !physical.ReadIdentityAt(1).SequenceEqual(highIdentity) ||
                !physical.ReadIdentityAt(2).SequenceEqual(longIdentity) ||
                !physical.ReadIdentityAt(3).SequenceEqual(shortIdentity) ||
                physical.LowerBoundKey(highKey) != 0 || physical.LowerBoundKey(lowKey) != 1 ||
                !physical.Contains(lowKey, longIdentity) || physical.CountItemsInKeyRange(lowKey, lowKey) != 3)
                throw new InvalidDataException("VV descending shelf lost raw-byte tuple order or binary bounds.");
            using (LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVVShelf", "descending raw-byte shelf", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvdescending", 0));
                (long shelfOffset, _) = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(root.Offset, 0x61, profile, bytes);
                VarKeyVarIdentityReadOnly reloaded = session.ReadVarKeyVarIdentityReadOnlyShelf(shelfOffset, profile.MaxKeyLength, profile.MaxIdentityLength);
                if (!reloaded.IsValid || !reloaded.IsDescending || !reloaded.ReadKeyAt(0).SequenceEqual(highKey))
                    throw new InvalidDataException("VV descending shelf reload lost physical direction.");
            }
            if (mutable.MarkKeyRangeDeleted(lowKey, lowKey) != 3 ||
                mutable.CountLiveItemsInKeyRange(lowKey, highKey) != 1 ||
                (VarKeyVarIdentityLayout.ReadFlags(bytes) & VarKeyVarIdentityLayout.DescendingFlag) == 0)
                throw new InvalidDataException("VV descending shelf range deletion or header direction failed.");
            mutable.Release(clearShelfBytes: false);
        }
        if (verifyVvApi)
        {
            string path = Path.GetFullPath(Path.Combine("artifacts", $"descending-vv-api-{Guid.NewGuid():N}.lbdx"));
            using (VarKeyVarIdentityIndex index = Indexes.VV.Create(path: path, descending: true))
            {
                if (!index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x61 }).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x62 }, new byte[] { 0x61 }).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x61, 0x61 }).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x62 }).Inserted)
                    throw new InvalidDataException("VV descending API fixture insert failed.");
            }
            using (VarKeyVarIdentityIndex reopened = Indexes.VV.Open(path))
            {
                if (!reopened.Descending)
                    throw new InvalidDataException("VV descending API reopen lost its directory direction.");
                using VarKeyVarIdentityRangeReader reader = reopened.OpenRangeReader(
                    (ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x62 });
                if (!reader.MoveNext() || !reader.CurrentKey.SequenceEqual(new byte[] { 0x62 }) ||
                    !reader.MoveNext() || !reader.CurrentKey.SequenceEqual(new byte[] { 0x61 }) || !reader.CurrentIdentity.SequenceEqual(new byte[] { 0x62 }) ||
                    !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(new byte[] { 0x61, 0x61 }) ||
                    !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(new byte[] { 0x61 }) ||
                    reader.MoveNext())
                    throw new InvalidDataException("VV descending API reopen lost first-result or equal-key order.");
                reader.Dispose();
                LibraDexMaintenanceWalkResult maintenance = reopened.Session.OptimizeVarKeyVarIdentityTopology(
                    reopened.RootRouterOffset, reopened.MaxPhysicalKeyLength, reopened.MaxIdentityLength,
                    maxWorkItems: null, descending: true);
                if (!maintenance.Completed || maintenance.ConsideredCount != 4)
                    throw new InvalidDataException("VV descending maintenance did not rebuild all tuples.");
                using VarKeyVarIdentityRangeReader maintained = reopened.OpenRangeReader(
                    (ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x62 });
                if (!maintained.MoveNext() || !maintained.CurrentKey.SequenceEqual(new byte[] { 0x62 }) ||
                    !maintained.MoveNext() || !maintained.CurrentIdentity.SequenceEqual(new byte[] { 0x62 }) ||
                    maintained.Count != 4 || reopened.CountIdentityRange(new byte[] { 0x61 }, new byte[] { 0x62 }) != 4)
                    throw new InvalidDataException("VV descending maintenance changed physical order or range count.");
            }
        }
        if (verifyVvCatalog)
        {
            string path = Path.GetFullPath(Path.Combine("artifacts", $"descending-vv-catalog-{Guid.NewGuid():N}.lbdx"));
            using (Catalog fixtureCatalog = Catalog.Create(path))
            using (VarKeyVarIdentityIndex index = fixtureCatalog.Indexes.IndexSet("vv").Define("raw")
                .VarKeyVarIdentityKeys(maxKeyBytes: 64, maxIdentityBytes: 64)
                .Create(options: new IndexOptions { SortOrder = LibraDexIndexSortOrder.Descending }))
            {
                if (!index.Descending ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x61 }).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x62 }, new byte[] { 0x61 }).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x62 }).Inserted)
                    throw new InvalidDataException("VV descending catalog creation or fixture insert failed.");
                LibraDexMaintenanceResult optimized = fixtureCatalog.Maintenance.Optimize(
                    new LibraDexMaintenanceOptions { Mode = LibraDexMaintenanceMode.Full });
                if (!optimized.Completed)
                    throw new InvalidDataException("VV descending catalog optimization did not complete.");
            }
            using (Catalog reopened = Catalog.Open(path))
            using (VarKeyVarIdentityIndex index = reopened.Indexes.IndexSet("vv").Define("raw")
                .VarKeyVarIdentityKeys(maxKeyBytes: 64, maxIdentityBytes: 64).Open())
            {
                if (!index.Descending)
                    throw new InvalidDataException("VV descending catalog reopen lost directory direction.");
                using VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(
                    (ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x62 });
                if (!reader.MoveNext() || !reader.CurrentKey.SequenceEqual(new byte[] { 0x62 }) ||
                    !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(new byte[] { 0x62 }) ||
                    reader.Count != 3)
                    throw new InvalidDataException("VV descending catalog optimization or reopen lost physical order.");
            }
        }
        if (verifyVvSplit)
        {
            VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Default128KiB with { Descending = true };
            byte[] bytes = VarKeyVarIdentity.CreateEmpty(profile);
            if (!VarKeyVarIdentityMutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf mutable))
                throw new InvalidDataException("VV descending split fixture could not open its mutable shelf.");
            int inserted = 0;
            while (true)
            {
                byte[] fixtureKey = new byte[1000];
                fixtureKey.AsSpan().Fill(0x40);
                fixtureKey[0] = 0x43;
                fixtureKey[1] = (inserted & 1) == 0 ? (byte)0x61 : (byte)0x7A;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(fixtureKey.AsSpan(996), inserted);
                byte[] fixtureIdentity = [0x69, (byte)(inserted >> 8), (byte)inserted];
                VarKeyVarIdentityInsertResult result = mutable.InsertWithMutationHint(
                    fixtureKey, fixtureIdentity, true, 0, 0, out _);
                if (result == VarKeyVarIdentityInsertResult.Full)
                    break;
                if (result != VarKeyVarIdentityInsertResult.Inserted || inserted++ > 1000)
                    throw new InvalidDataException($"VV descending split fixture insert returned {result}.");
            }
            if (inserted < 4)
                throw new InvalidDataException("VV descending split fixture has too few source rows.");
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVVSplit", "descending raw-byte split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvsplit", 0));
            _ = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(root.Offset, 0x43, profile, bytes);
            byte[] incomingKey = new byte[1000];
            incomingKey.AsSpan().Fill(0xFF);
            incomingKey[0] = 0x43;
            incomingKey[1] = 0x7A;
            VarKeyVarIdentityRoutedInsertResult split = session.InsertWalkedRoutedVarKeyVarIdentity(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, incomingKey, new byte[] { 0x69, 0xFF }, true,
                VarKeyVarIdentityIndex.DefaultMaxRouterHops, descending: true);
            if (split.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException($"VV descending split insert returned {split.InsertResult}.");
            long childOffset = session.FindRouterTarget(root.Offset, 0x43);
            if (session.ClassifyVarKeyVarIdentityRouteTarget(childOffset) != VarKeyVarIdentityRouteTargetKind.Router)
                throw new InvalidDataException("VV descending full shelf did not split into a router.");
            long highOffset = session.FindRouterTarget(childOffset, 0x7A);
            long lowOffset = session.FindRouterTarget(childOffset, 0x61);
            if (highOffset == 0 || lowOffset == 0 || highOffset == lowOffset)
                throw new InvalidDataException("VV descending split did not separate key groups.");
            VarKeyVarIdentityReadOnly high = session.ReadVarKeyVarIdentityReadOnlyShelf(highOffset, profile.MaxKeyLength, profile.MaxIdentityLength);
            VarKeyVarIdentityReadOnly low = session.ReadVarKeyVarIdentityReadOnlyShelf(lowOffset, profile.MaxKeyLength, profile.MaxIdentityLength);
            if (!high.IsValid || !low.IsValid || !high.IsDescending || !low.IsDescending ||
                !high.ReadKeyAt(0).SequenceEqual(incomingKey) ||
                high.ReadKeyAt(high.ItemCount - 1).SequenceCompareTo(low.ReadKeyAt(0)) <= 0 ||
                high.ItemCount + low.ItemCount != inserted + 1)
                throw new InvalidDataException("VV descending split shelves lost physical tuple order.");
            using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, new byte[] { 0x43 }, new byte[] { 0x43, 0xFF }, descending: true);
            if (!reader.MoveNext() || !reader.CurrentKey.SequenceEqual(incomingKey) || reader.Count != inserted + 1)
                throw new InvalidDataException("VV descending split first-result traversal failed.");
            mutable.Release(clearShelfBytes: false);
        }
        if (verifyVvTerminal)
        {
            VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Default128KiB with { Descending = true };
            byte[] key = [0x43, 0x61];
            byte[] bytes = VarKeyVarIdentity.CreateEmpty(profile);
            if (!VarKeyVarIdentityMutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyVarIdentityMutableShelf mutable))
                throw new InvalidDataException("VV descending terminal fixture could not open its mutable shelf.");
            int inserted = 0;
            while (true)
            {
                byte[] currentIdentity = new byte[1000];
                currentIdentity.AsSpan().Fill(0x20);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(currentIdentity.AsSpan(996), inserted);
                VarKeyVarIdentityInsertResult result = mutable.InsertWithMutationHint(key, currentIdentity, true, 0, 0, out _);
                if (result == VarKeyVarIdentityInsertResult.Full)
                    break;
                if (result != VarKeyVarIdentityInsertResult.Inserted || inserted++ > 1000)
                    throw new InvalidDataException($"VV descending terminal fixture insert returned {result}.");
            }
            if (inserted < 16)
                throw new InvalidDataException("VV descending terminal fixture has too few identities.");
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVVTerminal", "descending raw-byte duplicate terminal", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvterminal", 0));
            _ = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(root.Offset, key[0], profile, bytes);
            byte[] incomingIdentity = new byte[1000];
            incomingIdentity.AsSpan().Fill(0xFF);
            VarKeyVarIdentityRoutedInsertResult conversion = session.InsertWalkedRoutedVarKeyVarIdentity(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, key, incomingIdentity, true,
                VarKeyVarIdentityIndex.DefaultMaxRouterHops, descending: true);
            if (conversion.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException($"VV descending terminal conversion returned {conversion.InsertResult}.");
            VarKeyVarIdentityRoutePathTarget path = session.WalkVarKeyVarIdentityRoutePathTarget(
                root.Offset, key, VarKeyVarIdentityIndex.DefaultMaxRouterHops);
            if (path.Target.Kind != VarKeyVarIdentityRouteTargetKind.TerminalVarIdentityRoot ||
                session.ReadTerminalIdentityRootBytes(path.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                throw new InvalidDataException("VV descending duplicate-key route did not persist terminal direction.");
            byte[] newestIdentity = new byte[1000];
            newestIdentity.AsSpan().Fill(0xFE);
            VarKeyVarIdentityRoutedInsertResult update = session.InsertWalkedRoutedVarKeyVarIdentity(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, key, newestIdentity, true,
                VarKeyVarIdentityIndex.DefaultMaxRouterHops, descending: true);
            if (update.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException("VV descending terminal update failed.");
            using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, key, key, descending: true);
            if (!reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(incomingIdentity) ||
                !reader.MoveNext() || !reader.CurrentIdentity.SequenceEqual(newestIdentity) ||
                reader.Count != inserted + 2)
                throw new InvalidDataException("VV descending terminal did not stream highest identity first.");
            reader.Dispose();
            byte[] higherKey = [0x43, 0x7A];
            byte[] lowerKey = [0x43, 0x60];
            if (session.InsertWalkedRoutedVarKeyVarIdentity(
                    root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, higherKey, new byte[] { 0x68 }, true,
                    VarKeyVarIdentityIndex.DefaultMaxRouterHops, descending: true).InsertResult != VarKeyVarIdentityInsertResult.Inserted ||
                session.InsertWalkedRoutedVarKeyVarIdentity(
                    root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength, lowerKey, new byte[] { 0x6C }, true,
                    VarKeyVarIdentityIndex.DefaultMaxRouterHops, descending: true).InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                throw new InvalidDataException("VV descending terminal sibling insert failed.");
            using VarKeyVarIdentityRangeReader siblings = session.OpenVarKeyVarIdentityRangeReader(
                root.Offset, profile.MaxKeyLength, profile.MaxIdentityLength,
                new byte[] { 0x43, 0x00 }, new byte[] { 0x43, 0xFF }, descending: true);
            if (!siblings.MoveNext() || !siblings.CurrentKey.SequenceEqual(higherKey) ||
                !siblings.MoveNext() || !siblings.CurrentKey.SequenceEqual(key) ||
                siblings.Count != inserted + 4)
                throw new InvalidDataException("VV descending terminal sibling routes lost high-first order.");
            mutable.Release(clearShelfBytes: false);
        }
        if (verifyVs16Optimizer)
        {
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS16Optimizer", "descending scalar-16 replacement", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs16optimizer", 0));
            byte[][] keys = [new byte[] { 0x43, 0x61 }, new byte[] { 0x43, 0x61 }, new byte[] { 0x43, 0x7A }];
            ulong[] highs = [1, 1, 0];
            ulong[] lows = [1, 2, 3];
            VarLenOptimizerReplacementSubtree shelf = session.CreateVarKeyScalar16OptimizerReplacementSubtree(
                keys, highs, lows, 1024, 16, descending: true);
            _ = session.UpdateRouterRoute(root.Offset, 0x43, 0x43, 0x43, shelf.TargetOffset);
            VarKeyScalar16ReadOnly physical = session.ReadVarKeyScalar16ReadOnlyShelf(shelf.TargetOffset, 1024);
            physical.ReadIdentityAt(0, out ulong firstHigh, out ulong firstLow);
            physical.ReadIdentityAt(1, out ulong secondHigh, out ulong secondLow);
            if (!physical.IsValid || !physical.IsDescending || !physical.ReadKeyAt(0).SequenceEqual(keys[2]) ||
                firstHigh != 0 || firstLow != 3 || secondHigh != 1 || secondLow != 2)
                throw new InvalidDataException("VS16 descending optimizer ordinary leaf is not physically highest-first.");
            byte[] repeatedKey = new byte[1024];
            repeatedKey.AsSpan().Fill(0x61);
            repeatedKey[0] = 0x44;
            byte[][] repeatedKeys = Enumerable.Repeat(repeatedKey, 600).ToArray();
            ulong[] repeatedHighs = new ulong[600];
            ulong[] repeatedLows = Enumerable.Range(1, 600).Select(value => (ulong)value).ToArray();
            (RouterSnapshot terminalRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vs16optterminal", 0));
            VarLenOptimizerReplacementSubtree terminal = session.CreateVarKeyScalar16OptimizerReplacementSubtree(
                repeatedKeys, repeatedHighs, repeatedLows, 1024, 16, descending: true);
            _ = session.UpdateRouterRoute(terminalRoot.Offset, 0x44, 0x44, 0x44, terminal.TargetOffset);
            VarKeyScalar16RoutePathTarget terminalPath = session.WalkVarKeyScalar16RoutePathTarget(
                terminalRoot.Offset, repeatedKey, LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
            if (terminalPath.Target.Kind != VarKeyScalar16RouteTargetKind.TerminalIdentityRoot ||
                session.ReadTerminalIdentityRootBytes(terminalPath.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                throw new InvalidDataException("VS16 descending optimizer terminal lost its root direction.");
            using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(
                terminalRoot.Offset, 1024, repeatedKey, repeatedKey, descending: true);
            if (!reader.MoveNext() || reader.CurrentEncodedIdentityLow != 600 ||
                !reader.MoveNext() || reader.CurrentEncodedIdentityLow != 599 || reader.Count != 600)
                throw new InvalidDataException("VS16 descending optimizer terminal did not stream highest-first.");
        }
        if (verifyVs16Api)
        {
            string path = Path.GetFullPath(Path.Combine("artifacts", $"descending-vs16-api-{Guid.NewGuid():N}.lbdx"));
            using (VarKeyScalar16Index index = Indexes.VS16.Create(path: path, descending: true))
            {
                if (!index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, 1, 1).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x7A }, 0, 1).Inserted ||
                    !index.Insert((ReadOnlySpan<byte>)new byte[] { 0x61 }, 1, 2).Inserted)
                    throw new InvalidDataException("VS16 descending API fixture insert failed.");
            }
            using (VarKeyScalar16Index reopened = Indexes.VS16.Open(path))
            {
                if (!reopened.Handle.Descending)
                    throw new InvalidDataException("VS16 descending API reopen lost its slot-level direction.");
                using VarKeyScalar16RangeReader reader = reopened.OpenRangeReader(
                    (ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x7A });
                if (!reader.MoveNext() || !reader.CurrentKey.SequenceEqual(new byte[] { 0x7A }) ||
                    reader.CurrentEncodedIdentityHigh != 0 || reader.CurrentEncodedIdentityLow != 1 ||
                    !reader.MoveNext() || !reader.CurrentKey.SequenceEqual(new byte[] { 0x61 }) ||
                    reader.CurrentEncodedIdentityHigh != 1 || reader.CurrentEncodedIdentityLow != 2 ||
                    !reader.MoveNext() || reader.CurrentEncodedIdentityHigh != 1 || reader.CurrentEncodedIdentityLow != 1 ||
                    reader.MoveNext())
                    throw new InvalidDataException("VS16 descending API reopen lost physical range order.");
                reader.Dispose();
                LibraDexMaintenanceWalkResult maintenance = reopened.Session.OptimizeVarKeyScalar16Topology(
                    reopened.RootRouterOffset, reopened.Handle.MaxKeyLength, 16, maxWorkItems: null, descending: true);
                if (!maintenance.Completed || maintenance.ConsideredCount != 3)
                    throw new InvalidDataException("VS16 descending API maintenance did not consume its persisted tuples.");
                using VarKeyScalar16RangeReader maintained = reopened.OpenRangeReader(
                    (ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x7A });
                if (!maintained.MoveNext() || !maintained.CurrentKey.SequenceEqual(new byte[] { 0x7A }) ||
                    !maintained.MoveNext() || maintained.CurrentEncodedIdentityLow != 2)
                    throw new InvalidDataException("VS16 descending API maintenance changed first-result order.");
                ulong[] rangeHighs = new ulong[3];
                ulong[] rangeLows = new ulong[3];
                if (reopened.ReadRange((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x7A }, rangeHighs, rangeLows) != 3 ||
                    !rangeHighs.SequenceEqual(new ulong[] { 0, 1, 1 }) ||
                    !rangeLows.SequenceEqual(new ulong[] { 1, 2, 1 }))
                    throw new InvalidDataException("VS16 descending API span-copy range lost physical order.");
                if (reopened.DeleteRange(new byte[] { 0x61 }, new byte[] { 0x61 }) != 2 ||
                    reopened.CountIdentityRange((ReadOnlySpan<byte>)new byte[] { 0x61 }, new byte[] { 0x7A }) != 1)
                    throw new InvalidDataException("VS16 descending API range deletion lost its surviving high key.");
            }
        }
        if (verifyVs16Terminal)
        {
            VarKeyScalar16Profile profile = VarKeyScalar16Profile.Default128KiB with { Descending = true };
            byte[] repeatedKey = new byte[1024];
            repeatedKey.AsSpan().Fill(0x61);
            repeatedKey[0] = 0x43;
            byte[] bytes = VarKeyScalar16.CreateEmpty(profile);
            if (!VarKeyScalar16MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false,
                out VarKeyScalar16MutableShelf mutable))
                throw new InvalidDataException("VS16 descending terminal fixture could not open its shelf.");
            ulong inserted = 0;
            while (true)
            {
                VarKeyScalar16InsertResult result = mutable.InsertWithMutationHint(
                    repeatedKey, 0, inserted + 1, true, 0, 0, out _);
                if (result == VarKeyScalar16InsertResult.Full)
                    break;
                if (result != VarKeyScalar16InsertResult.Inserted || ++inserted > 1000)
                    throw new InvalidDataException($"VS16 descending terminal fixture insert returned {result}.");
            }
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS16Terminal", "descending variable-key scalar-16 terminal", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs16terminal", 0));
            _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(root.Offset, repeatedKey[0], profile, bytes);
            VarKeyScalar16RoutedInsertResult conversion = session.InsertWalkedRoutedVarKeyScalar16(
                root.Offset, profile.MaxKeyLength, repeatedKey, 1, 1, true,
                LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops, descending: true);
            if (conversion.InsertResult != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException($"VS16 descending terminal conversion returned {conversion.InsertResult}.");
            VarKeyScalar16RoutePathTarget path = session.WalkVarKeyScalar16RoutePathTarget(
                root.Offset, repeatedKey, LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
            if (path.Target.Kind != VarKeyScalar16RouteTargetKind.TerminalIdentityRoot ||
                session.ReadTerminalIdentityRootBytes(path.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                throw new InvalidDataException("VS16 descending equal-key route did not persist its terminal direction.");
            VarKeyScalar16RoutedInsertResult update = session.InsertWalkedRoutedVarKeyScalar16(
                root.Offset, profile.MaxKeyLength, repeatedKey, 1, 2, true,
                LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops, descending: true);
            if (update.InsertResult != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException("VS16 descending terminal update failed.");
            using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(
                root.Offset, profile.MaxKeyLength, repeatedKey, repeatedKey, descending: true);
            if (!reader.MoveNext() || reader.CurrentEncodedIdentityHigh != 1 || reader.CurrentEncodedIdentityLow != 2 ||
                !reader.MoveNext() || reader.CurrentEncodedIdentityHigh != 1 || reader.CurrentEncodedIdentityLow != 1 ||
                reader.Count != (int)inserted + 2)
                throw new InvalidDataException("VS16 descending terminal first-result order failed.");
            byte[] siblingKey = (byte[])repeatedKey.Clone();
            siblingKey[^1] = 0x7A;
            byte[] secondSiblingKey = (byte[])repeatedKey.Clone();
            secondSiblingKey[^1] = 0x62;
            if (session.InsertWalkedRoutedVarKeyScalar16(root.Offset, profile.MaxKeyLength, siblingKey, 0, 1, true,
                    LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops, descending: true).InsertResult != VarKeyScalar16InsertResult.Inserted ||
                session.InsertWalkedRoutedVarKeyScalar16(root.Offset, profile.MaxKeyLength, secondSiblingKey, 0, 2, true,
                    LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops, descending: true).InsertResult != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException("VS16 descending terminal mismatch or cold sibling insert failed.");
            using VarKeyScalar16RangeReader siblings = session.OpenVarKeyScalar16RangeReader(
                root.Offset, profile.MaxKeyLength, repeatedKey, siblingKey, descending: true);
            if (!siblings.MoveNext() || !siblings.CurrentKey.SequenceEqual(siblingKey) ||
                !siblings.MoveNext() || !siblings.CurrentKey.SequenceEqual(secondSiblingKey) ||
                !siblings.MoveNext() || !siblings.CurrentKey.SequenceEqual(repeatedKey))
                throw new InvalidDataException("VS16 descending terminal sibling route lost high-first order.");
            mutable.Release(clearShelfBytes: false);
        }
        if (verifyVs16Split)
        {
            VarKeyScalar16Profile profile = VarKeyScalar16Profile.Default128KiB with { Descending = true };
            byte[] bytes = VarKeyScalar16.CreateEmpty(profile);
            if (!VarKeyScalar16MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false,
                out VarKeyScalar16MutableShelf mutable))
                throw new InvalidDataException("VS16 descending split fixture could not open its shelf.");
            int inserted = 0;
            while (true)
            {
                byte[] fixtureKey = new byte[1000];
                fixtureKey.AsSpan().Fill(0x40);
                fixtureKey[0] = 0x43;
                fixtureKey[1] = (inserted & 1) == 0 ? (byte)0x61 : (byte)0x7A;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(fixtureKey.AsSpan(996), inserted);
                VarKeyScalar16InsertResult result = mutable.InsertWithMutationHint(
                    fixtureKey, 0, (ulong)inserted + 1, true, 0, 0, out _);
                if (result == VarKeyScalar16InsertResult.Full)
                    break;
                if (result != VarKeyScalar16InsertResult.Inserted || inserted++ > 1000)
                    throw new InvalidDataException($"VS16 descending split fixture insert returned {result}.");
            }
            if (inserted < 4)
                throw new InvalidDataException("VS16 descending split fixture had too few source rows.");
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS16Split", "descending variable-key scalar-16 split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs16split", 0));
            _ = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(root.Offset, 0x43, profile, bytes);
            byte[] incomingKey = new byte[1000];
            incomingKey.AsSpan().Fill(0xFF);
            incomingKey[0] = 0x43;
            incomingKey[1] = 0x7A;
            VarKeyScalar16RoutedInsertResult split = session.InsertWalkedRoutedVarKeyScalar16(
                root.Offset, profile.MaxKeyLength, incomingKey, 1, 1, true,
                LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops, descending: true);
            if (split.InsertResult != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException($"VS16 descending split insert returned {split.InsertResult}.");
            long childOffset = session.FindRouterTarget(root.Offset, 0x43);
            if (session.ClassifyVarKeyScalar16RouteTarget(childOffset) != VarKeyScalar16RouteTargetKind.Router)
                throw new InvalidDataException("VS16 descending full shelf did not split into a router.");
            long highOffset = session.FindRouterTarget(childOffset, 0x7A);
            long lowOffset = session.FindRouterTarget(childOffset, 0x61);
            if (highOffset == 0 || lowOffset == 0 || highOffset == lowOffset)
                throw new InvalidDataException("VS16 descending split did not separate key groups.");
            VarKeyScalar16ReadOnly high = session.ReadVarKeyScalar16ReadOnlyShelf(highOffset, profile.MaxKeyLength);
            VarKeyScalar16ReadOnly low = session.ReadVarKeyScalar16ReadOnlyShelf(lowOffset, profile.MaxKeyLength);
            if (!high.IsValid || !low.IsValid || !high.IsDescending || !low.IsDescending ||
                !high.ReadKeyAt(0).SequenceEqual(incomingKey) ||
                high.ReadKeyAt(high.ItemCount - 1).SequenceCompareTo(low.ReadKeyAt(0)) <= 0 ||
                high.ItemCount + low.ItemCount != inserted + 1)
                throw new InvalidDataException("VS16 descending split shelves lost physical tuple order.");
            using VarKeyScalar16RangeReader reader = session.OpenVarKeyScalar16RangeReader(
                root.Offset, profile.MaxKeyLength, new byte[] { 0x43 }, new byte[] { 0x43, 0xFF }, descending: true);
            if (!reader.MoveNext() || !reader.CurrentKey.SequenceEqual(incomingKey) || reader.Count != inserted + 1)
                throw new InvalidDataException("VS16 descending split first-result traversal failed.");
            mutable.Release(clearShelfBytes: false);
        }
        if (verifyVs16Shelf)
        {
            VarKeyScalar16Profile profile = VarKeyScalar16Profile.DefaultInitial with { Descending = true };
            byte[] bytes = VarKeyScalar16.CreateEmpty(profile);
            if (!VarKeyScalar16MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false,
                out VarKeyScalar16MutableShelf mutable))
                throw new InvalidDataException("VS16 descending fixture could not open its mutable shelf.");
            byte[] lowKey = [0x43, 0x61];
            byte[] highKey = [0x43, 0x62];
            if (mutable.InsertWithMutationHint(lowKey, 1, 1, true, 0, 0, out _) != VarKeyScalar16InsertResult.Inserted ||
                mutable.InsertWithMutationHint(highKey, 0, 1, true, 0, 0, out _) != VarKeyScalar16InsertResult.Inserted ||
                mutable.InsertWithMutationHint(lowKey, 1, 2, true, 0, 0, out _) != VarKeyScalar16InsertResult.Inserted)
                throw new InvalidDataException("VS16 descending fixture could not insert its three tuples.");
            VarKeyScalar16ReadOnly physical = new(bytes, profile);
            physical.ReadIdentityAt(0, out ulong firstHigh, out ulong firstLow);
            physical.ReadIdentityAt(1, out ulong secondHigh, out ulong secondLow);
            physical.ReadIdentityAt(2, out ulong thirdHigh, out ulong thirdLow);
            if (!physical.IsValid || !physical.IsDescending || physical.ItemCount != 3 ||
                !physical.ReadKeyAt(0).SequenceEqual(highKey) || firstHigh != 0 || firstLow != 1 ||
                !physical.ReadKeyAt(1).SequenceEqual(lowKey) || secondHigh != 1 || secondLow != 2 ||
                thirdHigh != 1 || thirdLow != 1 ||
                physical.LowerBoundKey(highKey) != 0 || physical.LowerBoundKey(lowKey) != 1 ||
                physical.CountItemsInKeyRange(lowKey, lowKey) != 2 ||
                !physical.Contains(lowKey, 1, 2))
                throw new InvalidDataException("VS16 descending shelf lost key/identity ordering or bounds.");
            ulong[] highs = new ulong[3];
            ulong[] lows = new ulong[3];
            if (physical.CopyIdentitiesInKeyRange(lowKey, highKey, highs, lows) != 3 ||
                !highs.SequenceEqual(new ulong[] { 0, 1, 1 }) ||
                !lows.SequenceEqual(new ulong[] { 1, 2, 1 }))
                throw new InvalidDataException("VS16 descending shelf range lost physical tuple order.");
            using (LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS16Shelf", "descending variable-key scalar-16 shelf", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs16descending", 0));
                (long shelfOffset, _) = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(root.Offset, 0x43, profile, bytes);
                VarKeyScalar16ReadOnly reloaded = session.ReadVarKeyScalar16ReadOnlyShelf(shelfOffset, profile.MaxKeyLength);
                if (!reloaded.IsValid || !reloaded.IsDescending || !reloaded.ReadKeyAt(0).SequenceEqual(highKey))
                    throw new InvalidDataException("VS16 descending session read did not recover the physical shelf direction.");
            }
            if (mutable.MarkKeyRangeDeleted(lowKey, lowKey) != 2 ||
                mutable.CountLiveItemsInKeyRange(lowKey, highKey) != 1 ||
                (VarKeyScalar16Layout.ReadFlags(bytes) & VarKeyScalar16Layout.DescendingFlag) == 0)
                throw new InvalidDataException("VS16 descending shelf range deletion or direction flag failed.");
        }
        if (verifyVs8Optimizer)
        {
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS8Optimizer", "descending replacement planner", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8optimizer", 0));
            VarKeyScalar8SortedTuple[] ordinary =
            [
                new(new byte[] { 0x43, 0x61 }, 1),
                new(new byte[] { 0x43, 0x61 }, 2),
                new(new byte[] { 0x43, 0x62 }, 3)
            ];
            VarLenOptimizerReplacementSubtree shelf = session.CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
                ordinary, 1024, 16, descending: true);
            VarKeyScalar8ReadOnly physical = session.ReadVarKeyScalar8ReadOnlyShelf(shelf.TargetOffset, 1024);
            if (!physical.IsValid || !physical.IsDescending || physical.ItemCount != 3 ||
                !physical.ReadKeyAt(0).SequenceEqual(ordinary[2].Key) || physical.ReadIdentityAt(0) != 3 ||
                physical.ReadIdentityAt(1) != 2 || physical.ReadIdentityAt(2) != 1)
                throw new InvalidDataException("VS8 descending optimizer shelf is not physically highest-first.");
            _ = session.UpdateRouterRoute(root.Offset, 0x43, 0x43, 0x43, shelf.TargetOffset);
            using (VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(
                root.Offset, 1024, new byte[] { 0x43 }, new byte[] { 0x43, 0xFF }, direction: QueryDirection.Descending))
            {
                if (!reader.MovePrevious() || reader.CurrentEncodedIdentity != 3 ||
                    !reader.MovePrevious() || reader.CurrentEncodedIdentity != 2)
                    throw new InvalidDataException("VS8 descending optimizer shelf read lost physical order.");
            }

            (RouterSnapshot terminalRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vs8optimizerterminal", 0));
            byte[] repeatedKey = new byte[1024];
            repeatedKey[0] = 0x44;
            repeatedKey.AsSpan(1).Fill(0x61);
            VarKeyScalar8SortedTuple[] repeated = Enumerable.Range(1, 600)
                .Select(identity => new VarKeyScalar8SortedTuple(repeatedKey, (ulong)identity)).ToArray();
            VarLenOptimizerReplacementSubtree terminal = session.CreateVarKeyScalar8OptimizerReplacementSubtreeFromSorted(
                repeated, 1024, 16, descending: true);
            _ = session.UpdateRouterRoute(terminalRoot.Offset, 0x44, 0x44, 0x44, terminal.TargetOffset);
            using (VarKeyScalar8RangeReader terminalReader = session.OpenVarKeyScalar8RangeReader(
                terminalRoot.Offset, 1024, repeatedKey, repeatedKey, captureDiagnostics: true, direction: QueryDirection.Descending))
            {
                if (!terminalReader.MovePrevious() || terminalReader.CurrentEncodedIdentity != 600 ||
                    terminalReader.Diagnostics.TerminalIdentityShelvesVisited != 1 ||
                    !terminalReader.MovePrevious() || terminalReader.CurrentEncodedIdentity != 599)
                    throw new InvalidDataException("VS8 descending optimizer terminal read lost highest-first identity order.");
                for (ulong expected = 598; expected >= 1; expected--)
                    if (!terminalReader.MovePrevious() || terminalReader.CurrentEncodedIdentity != expected)
                        throw new InvalidDataException("VS8 descending optimizer terminal crossed a shelf out of order.");
                if (terminalReader.MovePrevious())
                    throw new InvalidDataException("VS8 descending optimizer terminal returned an extra identity.");
            }
            using (VarKeyScalar8RangeReader ascendingReader = session.OpenVarKeyScalar8RangeReader(
                terminalRoot.Offset, 1024, repeatedKey, repeatedKey, direction: QueryDirection.Ascending))
            {
                for (ulong expected = 1; expected <= 600; expected++)
                    if (!ascendingReader.MoveNext() || ascendingReader.CurrentEncodedIdentity != expected)
                        throw new InvalidDataException("VS8 descending optimizer terminal reverse traversal crossed a shelf out of order.");
                if (ascendingReader.MoveNext())
                    throw new InvalidDataException("VS8 descending optimizer terminal reverse traversal returned an extra identity.");
            }

            (RouterSnapshot sortedRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(2, "vs8sorted", 0));
            _ = session.BuildVarKeyScalar8FromUnordered(
                sortedRoot.Offset, 1024, 16, ordinary, allowDuplicateKeys: true,
                singleKeyPerIdentity: true, CancellationToken.None, descending: true);
            long sortedShelfOffset = session.FindRouterTarget(sortedRoot.Offset, 0x43);
            VarKeyScalar8ReadOnly sortedShelf = session.ReadVarKeyScalar8ReadOnlyShelf(sortedShelfOffset, 1024);
            if (!sortedShelf.IsValid || !sortedShelf.IsDescending ||
                !sortedShelf.ReadKeyAt(0).SequenceEqual(ordinary[2].Key) || sortedShelf.ReadIdentityAt(0) != 3)
                throw new InvalidDataException("VS8 descending native population lost physical tuple order.");
        }
        if (verifyVs8Terminal)
        {
            VarKeyScalar8Profile profile = VarKeyScalar8Profile.Default128KiB with { Descending = true };
            byte[] key = new byte[] { 0x43, 0x61 };
            int capacity = VarKeyScalar8Layout.CalculateSlotCapacityBytes(profile.ShelfExtentSize) / VarKeyScalar8Layout.SlotSize;
            byte[][] keys = Enumerable.Repeat(key, capacity).ToArray();
            ulong[] fixtureIdentities = Enumerable.Range(1, capacity).Reverse().Select(value => (ulong)value).ToArray();
            if (!VarKeyScalar8.TryBuildFromSorted(keys, fixtureIdentities, profile, out byte[] bytes))
                throw new InvalidDataException("VS8 descending terminal fixture could not fill its ordinary shelf.");
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS8Terminal", "descending variable-key equal run", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8terminal", 0));
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(root.Offset, key[0], profile, bytes);
            VarKeyScalar8RoutedInsertResult conversion = session.InsertWalkedRoutedVarKeyScalar8(
                root.Offset, profile.MaxKeyLength, key, (ulong)capacity + 1, allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops,
                requestedRouteCount: 16);
            if (conversion.InsertResult != VarKeyScalar8InsertResult.Inserted)
                throw new InvalidDataException("VS8 descending equal-key terminal conversion failed.");
            long terminalOffset = session.FindRouterTarget(root.Offset, key[0]);
            if (session.ClassifyVarKeyScalar8RouteTarget(terminalOffset) != VarKeyScalar8RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException("VS8 descending equal-key route did not become a terminal root.");
            byte[] terminalRoot = session.ReadTerminalIdentityRootBytes(terminalOffset);
            if (terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] == 0)
                throw new InvalidDataException("VS8 descending terminal root lost its physical direction.");
            List<ulong> identities = session.ReadTerminalIdentity8RouteIdentities(terminalOffset, key, profile.ShelfExtentSize);
            if (identities.Count != capacity + 1 || identities[0] != (ulong)capacity + 1 || identities[^1] != 1)
                throw new InvalidDataException("VS8 descending terminal conversion lost equal-key identity order.");
            VarKeyScalar8RoutedInsertResult next = session.InsertWalkedRoutedVarKeyScalar8(
                root.Offset, profile.MaxKeyLength, key, (ulong)capacity + 2, allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops,
                requestedRouteCount: 16);
            identities = session.ReadTerminalIdentity8RouteIdentities(terminalOffset, key, profile.ShelfExtentSize);
            if (next.InsertResult != VarKeyScalar8InsertResult.Inserted || identities[0] != (ulong)capacity + 2 || identities[^1] != 1)
                throw new InvalidDataException("VS8 descending terminal update lost equal-key identity order.");
            using (VarKeyScalar8RangeReader descendingReader = session.OpenVarKeyScalar8RangeReader(
                root.Offset, profile.MaxKeyLength, key, key, captureDiagnostics: true, direction: QueryDirection.Descending))
            {
                if (!descendingReader.MovePrevious() || descendingReader.CurrentEncodedIdentity != (ulong)capacity + 2 ||
                    descendingReader.Diagnostics.TerminalIdentityShelvesVisited != 1)
                    throw new InvalidDataException("VS8 descending terminal first-result traversal failed.");
                if (descendingReader.Skip(2) != 2 || descendingReader.CurrentEncodedIdentity != (ulong)capacity ||
                    !descendingReader.MovePrevious() || descendingReader.CurrentEncodedIdentity != (ulong)capacity - 1)
                    throw new InvalidDataException("VS8 descending terminal skip lost physical cursor orientation.");
            }
            using (VarKeyScalar8RangeReader ascendingReader = session.OpenVarKeyScalar8RangeReader(
                root.Offset, profile.MaxKeyLength, key, key, direction: QueryDirection.Ascending))
            {
                if (!ascendingReader.MoveNext() || ascendingReader.CurrentEncodedIdentity != 1)
                    throw new InvalidDataException("VS8 explicit ascending terminal override failed.");
                if (ascendingReader.Skip(2) != 2 || ascendingReader.CurrentEncodedIdentity != 3 ||
                    !ascendingReader.MoveNext() || ascendingReader.CurrentEncodedIdentity != 4)
                    throw new InvalidDataException("VS8 explicit ascending terminal skip lost reverse physical orientation.");
            }
        }
        if (verifyVs8Split)
        {
            VarKeyScalar8Profile profile = VarKeyScalar8Profile.Default128KiB with { Descending = true };
            byte[] bytes = VarKeyScalar8.CreateEmpty(profile);
            if (!VarKeyScalar8MutableShelf.TryCreate(bytes, profile, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf shelf))
                throw new InvalidDataException("VS8 descending split fixture could not open its mutable shelf.");
            List<(byte[] Key, ulong Identity)> expected = new();
            ulong identity = 1;
            for (int i = 0; i < 90; i++)
                InsertVarKeyScalar8VariablePayloadFixtureTuple(shelf, expected, new byte[] { 0x43, 0x61 }, identity++);
            for (int group = 0; group < 2; group++)
                for (int i = 0; i < 50; i++)
                    InsertVarKeyScalar8VariablePayloadFixtureTuple(shelf, expected,
                        CreateVarKeyScalar8VariablePayloadFixtureKey(
                            group == 0 ? (byte)0x6D : (byte)0x7A,
                            i,
                            i < 30 ? 1013 : 1012,
                            0x20), identity++);
            if (VarKeyScalar8Layout.ReadRecordArenaEnd(bytes) != profile.ShelfExtentSize)
                throw new InvalidDataException("VS8 descending split fixture did not fill its shelf exactly.");
            using LibraDexFileSession session = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingVS8Split", "descending variable-key split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8descending", 0));
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(root.Offset, 0x43, profile, bytes);
            byte[] incomingKey = CreateVarKeyScalar8VariablePayloadFixtureKey(0x7A, int.MaxValue, 1024, 0xFF);
            VarKeyScalar8RoutedInsertResult split = session.InsertWalkedRoutedVarKeyScalar8(
                root.Offset, profile.MaxKeyLength, incomingKey, identity, allowDuplicateKeys: true,
                maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops,
                requestedRouteCount: 16);
            if (split.InsertResult != VarKeyScalar8InsertResult.Inserted)
                throw new InvalidDataException($"VS8 descending split insert returned {split.InsertResult}.");
            long childOffset = session.FindRouterTarget(root.Offset, 0x43);
            if (session.ClassifyVarKeyScalar8RouteTarget(childOffset) != VarKeyScalar8RouteTargetKind.Router)
                throw new InvalidDataException("VS8 descending full-shelf split did not publish a child router.");
            long highOffset = session.FindRouterTarget(childOffset, 0x7A);
            long middleOffset = session.FindRouterTarget(childOffset, 0x6D);
            if (highOffset == 0 || middleOffset == 0 || highOffset == middleOffset ||
                session.ClassifyVarKeyScalar8RouteTarget(highOffset) != VarKeyScalar8RouteTargetKind.Shelf ||
                session.ClassifyVarKeyScalar8RouteTarget(middleOffset) != VarKeyScalar8RouteTargetKind.Shelf)
                throw new InvalidDataException("VS8 descending split did not separate the high and middle key groups.");
            VarKeyScalar8ReadOnly highShelf = session.ReadVarKeyScalar8ReadOnlyShelf(highOffset, profile.MaxKeyLength);
            VarKeyScalar8ReadOnly middleShelf = session.ReadVarKeyScalar8ReadOnlyShelf(middleOffset, profile.MaxKeyLength);
            if (!highShelf.IsValid || !middleShelf.IsValid ||
                !highShelf.ReadKeyAt(0).SequenceEqual(incomingKey) ||
                highShelf.ReadKeyAt(highShelf.ItemCount - 1).SequenceCompareTo(middleShelf.ReadKeyAt(0)) <= 0)
                throw new InvalidDataException("VS8 descending split shelves lost physical key order.");
            byte[] lowerBound = new byte[] { 0x43, 0x61 };
            byte[] upperBound = new byte[profile.MaxKeyLength];
            upperBound.AsSpan().Fill(byte.MaxValue);
            upperBound[0] = 0x43;
            using (VarKeyScalar8RangeReader descendingReader = session.OpenVarKeyScalar8RangeReader(
                root.Offset, profile.MaxKeyLength, lowerBound, upperBound, direction: QueryDirection.Descending))
            {
                if (!descendingReader.MovePrevious() || !descendingReader.CurrentKey.SequenceEqual(incomingKey))
                    throw new InvalidDataException("VS8 descending split routed first result failed.");
            }
        }
        if (verifyVs8Shelf)
        {
            VarKeyScalar8Profile profile = VarKeyScalar8Profile.Default16KiB with { Descending = true };
            byte[] bytes = VarKeyScalar8.CreateEmpty(profile);
            foreach ((string key, ulong identity) in new[] { ("a", 1UL), ("b", 9UL), ("a", 3UL), ("a", 2UL) })
            {
                if (VarKeyScalar8.Insert(bytes, profile, Encoding.ASCII.GetBytes(key), identity, true, out bytes) != VarKeyScalar8InsertResult.Inserted)
                    throw new InvalidDataException("VS8 descending shelf insertion failed.");
            }
            VarKeyScalar8ReadOnly shelf = new(bytes, profile);
            byte[] equalKey = Encoding.ASCII.GetBytes("a");
            byte[] highKey = Encoding.ASCII.GetBytes("b");
            ulong[] identities = new ulong[4];
            if (!shelf.IsValid || shelf.ItemCount != 4 ||
                (VarKeyScalar8Layout.ReadFlags(bytes) & VarKeyScalar8Layout.DescendingFlag) == 0 ||
                shelf.LowerBoundKey(highKey) != 0 || shelf.LowerBoundKey(equalKey) != 1 ||
                shelf.LowerBound(equalKey, 2) != 2 || !shelf.Contains(equalKey, 2) ||
                shelf.ReadIdentityAt(0) != 9 || shelf.ReadIdentityAt(1) != 3 ||
                shelf.ReadIdentityAt(2) != 2 || shelf.ReadIdentityAt(3) != 1 ||
                shelf.CountItemsInKeyRange(equalKey, highKey) != 4 ||
                shelf.CopyIdentitiesInKeyRange(equalKey, highKey, identities) != 4 ||
                !identities.SequenceEqual(new ulong[] { 9, 3, 2, 1 }))
                throw new InvalidDataException("VS8 descending shelf order, bound, or range failed.");
            if (!VarKeyScalar8MutableShelf.TryCreate((byte[])bytes.Clone(), profile, ownsBytes: false, rentSidecars: false, out VarKeyScalar8MutableShelf mutable))
                throw new InvalidDataException("VS8 descending mutable shelf decode failed.");
            try
            {
                if (mutable.CountLiveItemsInKeyRange(equalKey, equalKey) != 3 ||
                    mutable.MarkKeyRangeDeleted(equalKey, equalKey) != 3 ||
                    mutable.CountLiveItemsInKeyRange(equalKey, highKey) != 1)
                    throw new InvalidDataException("VS8 descending mutable key-range handling failed.");
                VarKeyScalar8Layout.WriteReclaimablePayloadBytes(mutable.Bytes, 41);
                if ((VarKeyScalar8Layout.ReadFlags(mutable.Bytes) & VarKeyScalar8Layout.DescendingFlag) == 0 ||
                    VarKeyScalar8Layout.ReadReclaimablePayloadBytes(mutable.Bytes) != 41)
                    throw new InvalidDataException("VS8 descending payload-flag accounting failed.");
            }
            finally
            {
                mutable.Release(clearShelfBytes: false);
            }
            byte[] runBytes = VarKeyScalar8.CreateEmpty(profile);
            VarKeyScalar8Layout.WriteFlags(runBytes, VarKeyScalar8Layout.DescendingFlag | VarKeyScalar8Layout.DuplicateRunFlag);
            VarKeyScalar8Layout.WriteItemCount(runBytes, 3);
            VarKeyScalar8Layout.WriteDuplicateRunKeyLength(runBytes, equalKey.Length);
            equalKey.CopyTo(runBytes.AsSpan(VarKeyScalar8Layout.DuplicateRunKeyOffset));
            VarKeyScalar8Layout.WriteDuplicateRunIdentity(runBytes, equalKey.Length, 0, 3);
            VarKeyScalar8Layout.WriteDuplicateRunIdentity(runBytes, equalKey.Length, 1, 2);
            VarKeyScalar8Layout.WriteDuplicateRunIdentity(runBytes, equalKey.Length, 2, 1);
            VarKeyScalar8ReadOnly run = new(runBytes, profile);
            ulong[] runIdentities = new ulong[3];
            if (!run.IsValid || run.LowerBound(equalKey, 2) != 1 ||
                run.CopyIdentitiesInKeyRange(equalKey, equalKey, runIdentities) != 3 ||
                !runIdentities.SequenceEqual(new ulong[] { 3, 2, 1 }))
                throw new InvalidDataException("VS8 descending duplicate-run shelf bounds or range failed.");
        }
        if (verifyFsnvShelf)
        {
            FixedNVarIdentityProfile profile = FixedNVarIdentityProfile.Default64KiB(8, 256) with { Descending = true };
            byte[] shelfBytes = FixedNVarIdentity.CreateEmpty(profile);
            byte[] key = new byte[8];
            byte[] highKey = new byte[8];
            key[^1] = 1;
            highKey[^1] = 2;
            foreach (byte[] identity in new[] { Encoding.ASCII.GetBytes("a"), Encoding.ASCII.GetBytes("b"), Encoding.ASCII.GetBytes("aa") })
            {
                if (FixedNVarIdentity.InsertInPlace(shelfBytes, profile, key, identity, allowDuplicateKeys: true, out shelfBytes) != FixedNVarIdentityInsertResult.Inserted)
                    throw new InvalidDataException("FSN-V descending shelf insertion failed.");
            }
            if (FixedNVarIdentity.InsertInPlace(shelfBytes, profile, highKey, Encoding.ASCII.GetBytes("z"), allowDuplicateKeys: true, out shelfBytes) != FixedNVarIdentityInsertResult.Inserted)
                throw new InvalidDataException("FSN-V descending high-key shelf insertion failed.");
            FixedNVarIdentityReadOnly shelf = new(shelfBytes, profile);
            if (!shelf.IsValid || shelf.ItemCount != 4 || shelf.LowerBoundKey(highKey) != 0 || shelf.LowerBoundKey(key) != 1 ||
                !shelf.ReadIdentityAt(0).SequenceEqual(Encoding.ASCII.GetBytes("z")) ||
                !shelf.ReadIdentityAt(1).SequenceEqual(Encoding.ASCII.GetBytes("b")) ||
                !shelf.ReadIdentityAt(2).SequenceEqual(Encoding.ASCII.GetBytes("aa")) ||
                !shelf.ReadIdentityAt(3).SequenceEqual(Encoding.ASCII.GetBytes("a")) ||
                !shelf.Contains(key, Encoding.ASCII.GetBytes("aa")) ||
                shelf.CountItemsInKeyRange(key, highKey) != 4)
                throw new InvalidDataException("FSN-V descending shelf order, bound, or range failed.");
            List<byte[]> ranged = [];
            shelf.CopyIdentitiesInKeyRange(key, highKey, ranged);
            if (!ranged.Select(item => Encoding.ASCII.GetString(item)).SequenceEqual(new[] { "z", "b", "aa", "a" }))
                throw new InvalidDataException("FSN-V descending shelf bulk range failed.");
        }
        if (verifyFsn16Shelf)
        {
            FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(515) with { Descending = true };
            byte[] bytes = new byte[profile.ShelfExtentSize];
            FixedNScalar16 shelf = new(bytes, profile);
            shelf.Initialize();
            byte[] equalKey = new byte[profile.KeySize];
            byte[] highKey = new byte[profile.KeySize];
            equalKey[^1] = 7;
            highKey[^1] = 8;
            byte[] identityBytes = new byte[16];
            for (ulong identity = 1; identity <= 80; identity++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(identityBytes.AsSpan(8), identity);
                if (shelf.Insert(equalKey, identityBytes, allowDuplicateKeys: true) != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-16 descending shelf insert failed at {identity}.");
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(identityBytes.AsSpan(8), 900);
            if (shelf.Insert(highKey, identityBytes, allowDuplicateKeys: true) != FixedNScalarInsertResult.Inserted)
                throw new InvalidDataException("FSN-16 descending high-key insert failed.");
            FixedNScalar16ReadOnly physical = new(bytes, profile);
            byte[] firstKey = new byte[profile.KeySize];
            byte[] firstIdentity = new byte[16];
            physical.CopyKeyAt(0, firstKey);
            physical.CopyIdentityAt(1, firstIdentity);
            byte[] lowerIdentity = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(lowerIdentity.AsSpan(8), 79);
            if (!physical.IsValid || !physical.IsDescending || !firstKey.AsSpan().SequenceEqual(highKey) ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(firstIdentity.AsSpan(8)) != 80 ||
                physical.LowerBound(equalKey, lowerIdentity) != 2 || physical.LowerBoundKey(equalKey) != 1)
                throw new InvalidDataException("FSN-16 descending physical shelf order or bounds failed.");
            byte[] range = new byte[80 * 16];
            if (physical.CopyIdentitiesInKeyRange(equalKey, equalKey, range) != 80 ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(range.AsSpan(8)) != 80 ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(range.AsSpan(range.Length - 8)) != 1 ||
                physical.CountItemsInKeyRange(equalKey, equalKey) != 80)
                throw new InvalidDataException("FSN-16 descending shelf inclusive range failed.");

            const int smallExtent = 4096;
            const int smallKeySize = 24;
            int smallItemSize = smallKeySize + FixedNScalar16Layout.IdentitySize;
            int smallMaxItems = (smallExtent - FixedNScalar16Layout.HeaderSize) / (FixedNScalar16Layout.SlotSize + smallItemSize);
            int slotRegionSize = smallMaxItems * FixedNScalar16Layout.SlotSize;
            int itemRegionOffset = FixedNScalar16Layout.HeaderSize + slotRegionSize;
            FixedNScalar16Profile smallProfile = new(
                smallExtent, smallKeySize, checked((ushort)smallMaxItems),
                FixedNScalar16Layout.HeaderSize, slotRegionSize, itemRegionOffset,
                smallMaxItems * smallItemSize,
                smallExtent - itemRegionOffset - (smallMaxItems * smallItemSize),
                Descending: true);
            using LibraDexFileSession terminalSession = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingFSN16Terminal", "descending fixed-N scalar-16 terminal", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            CatalogIndexMetadata metadata = CreateFixedNRootClaimProbeMetadata<UInt128>("descending", "fsn16terminal", smallKeySize) with
            {
                SortOrder = LibraDexIndexSortOrder.Descending,
                Projections = [new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.BigIntFixed, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Descending)]
            };
            (FixedNScalar16IndexHandle handle, _) = terminalSession.CreateFixedNScalar16RootRouterIndex(
                CreateHarnessSlot(0, "fsn16terminal", 0), metadata, smallProfile);
            using FixedNScalar16Index terminalIndex = new(terminalSession, handle, 0);
            byte[] terminalKey = new byte[smallKeySize];
            terminalKey[^1] = 7;
            const ulong terminalLastIdentity = 530;
            for (ulong identity = 1; identity <= terminalLastIdentity; identity++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(identityBytes.AsSpan(8), identity);
                if (terminalIndex.Insert(terminalKey, identityBytes, allowDuplicateKeys: true).Result != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-16 descending multi-shelf terminal insert failed at {identity}.");
            }
            long terminalOffset = terminalSession.FindRouterTarget(handle.RootOffset, terminalKey[0]);
            byte[] root = terminalSession.ReadTerminalIdentityRootBytes(terminalOffset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(root);
            byte[] head = terminalSession.ReadTerminalVarIdentityShelfBytes(headOffset, smallExtent);
            try
            {
                ReadOnlySpan<byte> headIdentity = TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0);
                if (root[TerminalIdentityRootLayout.SortDirectionOffset] != 1 || headIdentity.Length != 16 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(headIdentity.Slice(8)) != terminalLastIdentity ||
                    TerminalVarIdentityShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("FSN-16 descending multi-shelf terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            ulong[] firstThree = terminalIndex.IterateTuples(QueryDirection.Descending).Take(3)
                .Select(tuple => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tuple.EncodedIdentity.AsSpan(8))).ToArray();
            ulong[] lastThree = terminalIndex.IterateTuples(QueryDirection.Ascending).Take(3)
                .Select(tuple => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tuple.EncodedIdentity.AsSpan(8))).ToArray();
            if (!firstThree.SequenceEqual(new ulong[] { 530, 529, 528 }) ||
                !lastThree.SequenceEqual(new ulong[] { 1, 2, 3 }))
                throw new InvalidDataException("FSN-16 descending terminal tuple traversal failed across its physical chain.");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(identityBytes.AsSpan(8), terminalLastIdentity);
            if (!terminalIndex.Delete(terminalKey, identityBytes).Deleted)
                throw new InvalidDataException("FSN-16 descending terminal head delete failed.");
            if (terminalIndex.Insert(terminalKey, identityBytes, allowDuplicateKeys: true).Result != FixedNScalarInsertResult.Inserted)
                throw new InvalidDataException("FSN-16 descending terminal head reinsert failed.");
        }
        if (verifyFsn8Shelf)
        {
            FixedNScalar8Profile profile = FixedNScalar8Profile.Create(4096, 24) with { Descending = true };
            byte[] bytes = new byte[profile.ShelfExtentSize];
            FixedNScalar8 shelf = new(bytes, profile);
            shelf.Initialize();
            byte[] equalKey = new byte[profile.KeySize];
            byte[] highKey = new byte[profile.KeySize];
            equalKey[^1] = 7;
            highKey[^1] = 8;
            for (ulong identity = 1; identity <= 80; identity++)
            {
                if (shelf.Insert(equalKey, identity, allowDuplicateKeys: true) != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-8 descending shelf insert failed at {identity}.");
            }
            if (shelf.Insert(highKey, 900, allowDuplicateKeys: true) != FixedNScalarInsertResult.Inserted)
                throw new InvalidDataException("FSN-8 descending high-key insert failed.");
            FixedNScalar8ReadOnly physical = new(bytes, profile);
            byte[] firstKey = new byte[profile.KeySize];
            physical.CopyKeyAt(0, firstKey);
            if (!physical.IsValid || !physical.IsDescending || !firstKey.AsSpan().SequenceEqual(highKey) ||
                physical.ReadIdentityAt(0) != 900 || physical.ReadIdentityAt(1) != 80 ||
                physical.ReadIdentityAt(80) != 1 || physical.LowerBound(equalKey, 79) != 2 ||
                physical.LowerBoundKey(equalKey) != 1)
                throw new InvalidDataException("FSN-8 descending physical shelf order or bounds failed.");
            ulong[] range = new ulong[80];
            if (physical.CopyIdentitiesInKeyRange(equalKey, equalKey, range) != 80 || range[0] != 80 || range[^1] != 1 ||
                physical.CountItemsInKeyRange(equalKey, equalKey) != 80)
                throw new InvalidDataException("FSN-8 descending shelf inclusive range failed.");

            using LibraDexFileSession terminalSession = LibraDexFileSession.InitializeMemory(
                new DataKernelOptions(DefaultAppendBufferSize, 0, false, 512),
                new SuperblockDeveloperMetadata("DescendingFSN8Terminal", "descending fixed-N terminal", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions);
            CatalogIndexMetadata metadata = CreateFixedNRootClaimProbeMetadata<long>("descending", "fsn8terminal", profile.KeySize) with
            {
                SortOrder = LibraDexIndexSortOrder.Descending,
                Projections = [new LibraDexIndexProjectionSpec(LibraDexIndexProjectionKind.BigIntFixed, LibraDexIndexByteDirection.Forward, LibraDexIndexSortOrder.Descending)]
            };
            (FixedNScalar8IndexHandle handle, _) = terminalSession.CreateFixedNScalar8RootRouterIndex(
                CreateHarnessSlot(0, "fsn8terminal", 0), metadata, profile);
            using FixedNScalar8Index terminalIndex = new(terminalSession, handle, 0);
            const ulong terminalLastIdentity = 530;
            for (ulong identity = 1; identity <= terminalLastIdentity; identity++)
            {
                if (terminalIndex.Insert(equalKey, identity, allowDuplicateKeys: true).Result != FixedNScalarInsertResult.Inserted)
                    throw new InvalidDataException($"FSN-8 descending multi-shelf terminal insert failed at {identity}.");
            }
            long terminalOffset = terminalSession.FindRouterTarget(handle.RootOffset, equalKey[0]);
            byte[] terminalRoot = terminalSession.ReadTerminalIdentityRootBytes(terminalOffset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
            byte[] head = terminalSession.ReadTerminalIdentity8ShelfBytes(headOffset, profile.ShelfExtentSize);
            try
            {
                if (terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    TerminalIdentity8ShelfLayout.ReadIdentity(head, 0) != terminalLastIdentity ||
                    TerminalIdentity8ShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("FSN-8 descending multi-shelf terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            ulong[] firstThree = terminalIndex.IterateTuples(QueryDirection.Descending).Take(3).Select(tuple => tuple.EncodedIdentity).ToArray();
            ulong[] lastThree = terminalIndex.IterateTuples(QueryDirection.Ascending).Take(3).Select(tuple => tuple.EncodedIdentity).ToArray();
            if (!firstThree.SequenceEqual(new ulong[] { 530, 529, 528 }) ||
                !lastThree.SequenceEqual(new ulong[] { 1, 2, 3 }))
                throw new InvalidDataException("FSN-8 descending terminal tuple traversal failed across its physical chain.");
        }
        if (verifyFs3216Shelf)
        {
            Fixed32Scalar16Profile profile = Fixed32Scalar16Profile.Default40KiB with { Descending = true };
            byte[] bytes = new byte[profile.ShelfExtentSize];
            Fixed32Scalar16 shelf = new(bytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 700; identity++)
            {
                if (shelf.Insert(0, 0, 0, 7, 0, identity, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.Inserted)
                    throw new InvalidDataException($"FS32-16 descending shelf insert failed at {identity}.");
            }
            if (shelf.Insert(0, 0, 0, 8, 0, 900, allowDuplicateKeys: true) != Fixed32Scalar16InsertResult.Inserted)
                throw new InvalidDataException("FS32-16 descending high-key insert failed.");
            Fixed32Scalar16ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyPart3At(0) != 8 ||
                physical.ReadIdentityLowAt(0) != 900 || physical.ReadIdentityLowAt(1) != 700 ||
                physical.ReadIdentityLowAt(700) != 1 ||
                physical.LowerBound(0, 0, 0, 7, 0, 699) != 2 ||
                physical.LowerBoundKey(0, 0, 0, 7) != 1 ||
                !physical.Contains(0, 0, 0, 7, 0, 699))
                throw new InvalidDataException("FS32-16 descending physical shelf order or bounds failed.");
            ulong[] highs = new ulong[700];
            ulong[] lows = new ulong[700];
            if (physical.CopyIdentitiesInKeyRange(0, 0, 0, 7, 0, 0, 0, 7, highs, lows) != 700 ||
                highs[0] != 0 || lows[0] != 700 || lows[^1] != 1)
                throw new InvalidDataException("FS32-16 descending shelf inclusive range failed.");

            Fixed32Scalar16Profile splitProfile = Fixed32Scalar16Profile.Default4KiB with { Descending = true };
            DataKernelOptions splitOptions = new(DefaultAppendBufferSize, 0, false, 512);
            int leftCount = splitProfile.MaxItemCount / 2;
            ulong[] key0s = new ulong[splitProfile.MaxItemCount];
            ulong[] key1s = new ulong[splitProfile.MaxItemCount];
            ulong[] key2s = new ulong[splitProfile.MaxItemCount];
            ulong[] key3s = new ulong[splitProfile.MaxItemCount];
            ulong[] identities = new ulong[splitProfile.MaxItemCount];
            for (int i = 0; i < splitProfile.MaxItemCount; i++)
            {
                key0s[i] = i < leftCount ? 0 : 0x8000_0000_0000_0000UL;
                key3s[i] = (ulong)(i < leftCount ? i : i - leftCount);
                identities[i] = (ulong)i;
            }
            using (LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                splitOptions, new SuperblockDeveloperMetadata("DescendingFS3216Split", "descending physical split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc3216split", 0));
                _ = splitSession.CreateFixed32Scalar16ShelfAndLinkRootRoutes(
                    root.Offset, splitProfile, key0s, key1s, key2s, key3s, identities, [0x00, 0x80]);
                (_, _, int splitLeftCount, int splitRightCount, Fixed32Scalar16InsertResult result, _) =
                    splitSession.SplitRoutedFixed32Scalar16ByRootPrefix(
                        root.Offset, 0x00, 0x80, splitProfile,
                        0x8000_0000_0000_0000UL, 0, 0, 10_000, 10_000);
                if (result != Fixed32Scalar16InsertResult.Inserted ||
                    splitLeftCount != leftCount || splitRightCount != splitProfile.MaxItemCount - leftCount + 1)
                    throw new InvalidDataException("FS32-16 descending root split counts failed.");
                byte[] leftBytes = new byte[splitProfile.ShelfExtentSize];
                byte[] rightBytes = new byte[splitProfile.ShelfExtentSize];
                _ = splitSession.ReadRoutedFixed32Scalar16Shelf(root.Offset, 0x00, splitProfile, leftBytes);
                _ = splitSession.ReadRoutedFixed32Scalar16Shelf(root.Offset, 0x80, splitProfile, rightBytes);
                Fixed32Scalar16ReadOnly left = new(leftBytes, splitProfile);
                Fixed32Scalar16ReadOnly right = new(rightBytes, splitProfile);
                if (!left.IsDescending || !right.IsDescending ||
                    left.ReadKeyPart3At(0) != (ulong)(leftCount - 1) || left.ReadKeyPart3At(left.ItemCount - 1) != 0 ||
                    right.ReadKeyPart3At(0) != 10_000 || right.ReadKeyPart3At(right.ItemCount - 1) != 0)
                    throw new InvalidDataException("FS32-16 descending root split physical order failed.");
                using Fixed32Scalar16RangeReader reader = splitSession.OpenFixed32Scalar16RangeReader(
                    root.Offset, splitProfile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue,
                    ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
                if (!reader.TryReadPreviousEncodedTuple(out ulong firstKey0, out _, out _, out ulong firstKey3, out ulong firstIdentityHigh, out ulong firstIdentityLow) ||
                    firstKey0 != 0x8000_0000_0000_0000UL || firstKey3 != 10_000 ||
                    firstIdentityHigh != 0 || firstIdentityLow != 10_000)
                    throw new InvalidDataException("FS32-16 descending root split routed first row failed.");
            }

            for (int i = 0; i < splitProfile.MaxItemCount; i++)
                key0s[i] = i < leftCount ? 0 : 0x0080_0000_0000_0000UL;
            using (LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                splitOptions, new SuperblockDeveloperMetadata("DescendingFS3216Transform", "descending physical transform", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc3216xform", 0));
                _ = transformSession.CreateFixed32Scalar16ShelfAndLinkRootRoutes(
                    root.Offset, splitProfile, key0s, key1s, key2s, key3s, identities, [0x00]);
                (long childRouter, long leftOffset, long rightOffset, int transformedLeftCount, int transformedRightCount,
                    Fixed32Scalar16InsertResult result, _) = transformSession.SplitRoutedFixed32Scalar16ByShelfTransform(
                        root.Offset, 0x00, 0x80, splitProfile, 0x0080_0000_0000_0000UL, 0, 0, 10_000, 10_000);
                if (childRouter == 0 || leftOffset == 0 || rightOffset == 0 ||
                    result != Fixed32Scalar16InsertResult.Inserted || transformedLeftCount != leftCount ||
                    transformedRightCount != splitProfile.MaxItemCount - leftCount + 1)
                    throw new InvalidDataException("FS32-16 descending transform split structure failed.");
                Fixed32Scalar16ReadOnly left = new(transformSession.ReadFixed32Scalar16ShelfBytesForBatch(leftOffset, splitProfile), splitProfile);
                Fixed32Scalar16ReadOnly right = new(transformSession.ReadFixed32Scalar16ShelfBytesForBatch(rightOffset, splitProfile), splitProfile);
                if (!left.IsDescending || !right.IsDescending || left.ReadKeyPart3At(0) != (ulong)(leftCount - 1) ||
                    right.ReadKeyPart3At(0) != 10_000)
                    throw new InvalidDataException("FS32-16 descending transformed shelves were not physically highest-first.");

                int fillCount = splitProfile.MaxItemCount - transformedRightCount;
                for (int i = 0; i < fillCount; i++)
                {
                    Fixed32Scalar16RoutedInsertResult fill = transformSession.InsertWalkedRoutedFixed32Scalar16(
                        root.Offset, splitProfile, 0x00C0_0000_0000_0000UL, 0, 0, (ulong)i, (ulong)i,
                        allowDuplicateKeys: true, maxRouterHops: 8);
                    if (fill.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                        throw new InvalidDataException($"FS32-16 descending parent-route fill failed at {i}: {fill.Kind}/{fill.InsertResult}.");
                }
                Fixed32Scalar16RoutedInsertResult parentSplit = transformSession.InsertWalkedRoutedFixed32Scalar16(
                    root.Offset, splitProfile, 0x00C0_0000_0000_0000UL, 0, 0, 10_000, 10_000,
                    allowDuplicateKeys: true, maxRouterHops: 8);
                if (parentSplit.Kind != Fixed32Scalar16RoutedInsertKind.WalkedParentRouteSplit ||
                    parentSplit.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                    throw new InvalidDataException($"FS32-16 descending parent-route split failed: {parentSplit.Kind}/{parentSplit.InsertResult}.");
                using Fixed32Scalar16RangeReader parentReader = transformSession.OpenFixed32Scalar16RangeReader(
                    root.Offset, splitProfile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue,
                    ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
                if (!parentReader.TryReadPreviousEncodedTuple(out ulong firstKey0, out _, out _, out ulong firstKey3, out ulong firstIdentityHigh, out ulong firstIdentityLow) ||
                    firstKey0 != 0x00C0_0000_0000_0000UL || firstKey3 != 10_000 ||
                    firstIdentityHigh != 0 || firstIdentityLow != 10_000)
                    throw new InvalidDataException("FS32-16 descending parent-route split did not preserve global first tuple.");
            }
        }
        if (verifyFs328Shelf)
        {
            Fixed32Scalar8Profile profile = Fixed32Scalar8Profile.Default40KiB with { Descending = true };
            byte[] shelfBytes = new byte[profile.ShelfExtentSize];
            Fixed32Scalar8 shelf = new(shelfBytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 700; identity++)
            {
                if (shelf.Insert(0, 0, 0, 7, identity, allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"FS32-8 descending shelf insert failed for identity {identity}.");
            }
            if (shelf.Insert(0, 0, 0, 8, 900, allowDuplicateKeys: true) != Fixed32Scalar8InsertResult.Inserted)
                throw new InvalidDataException("FS32-8 descending shelf high-key insert failed.");
            Fixed32Scalar8ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyPart3At(0) != 8 || physical.ReadIdentityAt(0) != 900 ||
                physical.ReadIdentityAt(1) != 700 || physical.ReadIdentityAt(700) != 1 ||
                physical.LowerBound(0, 0, 0, 7, 699) != 2 || physical.LowerBoundKey(0, 0, 0, 7) != 1 ||
                !physical.Contains(0, 0, 0, 7, 699) || physical.Contains(0, 0, 0, 7, 701))
                throw new InvalidDataException("FS32-8 descending shelf physical order or bounds failed.");
            ulong[] copied = new ulong[700];
            if (physical.CopyIdentitiesInKeyRange(0, 0, 0, 7, 0, 0, 0, 7, copied) != 700 ||
                copied[0] != 700 || copied[^1] != 1)
                throw new InvalidDataException("FS32-8 descending shelf inclusive range failed.");

            Fixed32Scalar8Profile splitProfile = Fixed32Scalar8Profile.Default4KiB with { Descending = true };
            DataKernelOptions splitOptions = new(DefaultAppendBufferSize, 0, false, 512);
            int leftCount = splitProfile.MaxItemCount / 2;
            ulong[] key0s = new ulong[splitProfile.MaxItemCount];
            ulong[] key1s = new ulong[splitProfile.MaxItemCount];
            ulong[] key2s = new ulong[splitProfile.MaxItemCount];
            ulong[] key3s = new ulong[splitProfile.MaxItemCount];
            ulong[] identities = new ulong[splitProfile.MaxItemCount];
            for (int i = 0; i < splitProfile.MaxItemCount; i++)
            {
                key0s[i] = i < leftCount ? 0 : 0x8000_0000_0000_0000UL;
                key3s[i] = (ulong)(i < leftCount ? i : i - leftCount);
                identities[i] = (ulong)i;
            }
            using (LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                splitOptions, new SuperblockDeveloperMetadata("DescendingFS328Split", "descending physical split", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc328split", 0));
                _ = splitSession.CreateFixed32Scalar8ShelfAndLinkRootRoutes(
                    root.Offset, splitProfile, key0s, key1s, key2s, key3s, identities, [0x00, 0x80]);
                (_, _, int splitLeftCount, int splitRightCount, Fixed32Scalar8InsertResult result, _) =
                    splitSession.SplitRoutedFixed32Scalar8ByRootPrefix(
                        root.Offset, 0x00, 0x80, splitProfile,
                        0x8000_0000_0000_0000UL, 0, 0, 10_000, 10_000);
                if (result != Fixed32Scalar8InsertResult.Inserted ||
                    splitLeftCount != leftCount || splitRightCount != splitProfile.MaxItemCount - leftCount + 1)
                    throw new InvalidDataException("FS32-8 descending root split counts failed.");
                byte[] leftBytes = new byte[splitProfile.ShelfExtentSize];
                byte[] rightBytes = new byte[splitProfile.ShelfExtentSize];
                _ = splitSession.ReadRoutedFixed32Scalar8Shelf(root.Offset, 0x00, splitProfile, leftBytes);
                _ = splitSession.ReadRoutedFixed32Scalar8Shelf(root.Offset, 0x80, splitProfile, rightBytes);
                Fixed32Scalar8ReadOnly left = new(leftBytes, splitProfile);
                Fixed32Scalar8ReadOnly right = new(rightBytes, splitProfile);
                if (!left.IsDescending || !right.IsDescending ||
                    left.ReadKeyPart3At(0) != (ulong)(leftCount - 1) || left.ReadKeyPart3At(left.ItemCount - 1) != 0 ||
                    right.ReadKeyPart3At(0) != 10_000 || right.ReadKeyPart3At(right.ItemCount - 1) != 0)
                    throw new InvalidDataException("FS32-8 descending root split physical order failed.");
                using Fixed32Scalar8RangeReader reader = splitSession.OpenFixed32Scalar8RangeReader(
                    root.Offset, splitProfile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue,
                    ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
                if (!reader.TryReadPreviousEncodedTuple(out ulong firstKey0, out _, out _, out ulong firstKey3, out ulong firstIdentity) ||
                    firstKey0 != 0x8000_0000_0000_0000UL || firstKey3 != 10_000 || firstIdentity != 10_000)
                    throw new InvalidDataException("FS32-8 descending root split routed first row failed.");
            }

            for (int i = 0; i < splitProfile.MaxItemCount; i++)
                key0s[i] = i < leftCount ? 0 : 0x0080_0000_0000_0000UL;
            using (LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                splitOptions, new SuperblockDeveloperMetadata("DescendingFS328Transform", "descending physical transform", Guid.NewGuid(), 1, 2, 3),
                DataKernelTelemetryOptions.EnabledOptions))
            {
                (RouterSnapshot root, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc328xform", 0));
                _ = transformSession.CreateFixed32Scalar8ShelfAndLinkRootRoutes(
                    root.Offset, splitProfile, key0s, key1s, key2s, key3s, identities, [0x00]);
                (long childRouter, long leftOffset, long rightOffset, int transformedLeftCount, int transformedRightCount,
                    Fixed32Scalar8InsertResult result, _) = transformSession.SplitRoutedFixed32Scalar8ByShelfTransform(
                        root.Offset, 0x00, 0x80, splitProfile, 0x0080_0000_0000_0000UL, 0, 0, 10_000, 10_000);
                if (childRouter == 0 || leftOffset == 0 || rightOffset == 0 ||
                    result != Fixed32Scalar8InsertResult.Inserted || transformedLeftCount != leftCount ||
                    transformedRightCount != splitProfile.MaxItemCount - leftCount + 1)
                    throw new InvalidDataException("FS32-8 descending transform split structure failed.");
                Fixed32Scalar8ReadOnly left = new(transformSession.ReadFixed32Scalar8ShelfBytesForBatch(leftOffset, splitProfile), splitProfile);
                Fixed32Scalar8ReadOnly right = new(transformSession.ReadFixed32Scalar8ShelfBytesForBatch(rightOffset, splitProfile), splitProfile);
                if (!left.IsDescending || !right.IsDescending || left.ReadKeyPart3At(0) != (ulong)(leftCount - 1) ||
                    right.ReadKeyPart3At(0) != 10_000)
                    throw new InvalidDataException("FS32-8 descending transformed shelves were not physically highest-first.");
                using Fixed32Scalar8RangeReader reader = transformSession.OpenFixed32Scalar8RangeReader(
                    root.Offset, splitProfile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue,
                    ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
                if (!reader.TryReadPreviousEncodedTuple(out ulong firstKey0, out _, out _, out ulong firstKey3, out ulong firstIdentity) ||
                    firstKey0 != 0x0080_0000_0000_0000UL || firstKey3 != 10_000 || firstIdentity != 10_000)
                    throw new InvalidDataException("FS32-8 descending transform routed first row failed.");

                int fillCount = splitProfile.MaxItemCount - transformedRightCount;
                for (int i = 0; i < fillCount; i++)
                {
                    Fixed32Scalar8RoutedInsertResult fill = transformSession.InsertWalkedRoutedFixed32Scalar8(
                        root.Offset, splitProfile, 0x00C0_0000_0000_0000UL, 0, 0, (ulong)i, (ulong)i,
                        allowDuplicateKeys: true, maxRouterHops: 8);
                    if (fill.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                        throw new InvalidDataException($"FS32-8 descending parent-route fill failed at {i}: {fill.Kind}/{fill.InsertResult}.");
                }
                Fixed32Scalar8RoutedInsertResult parentSplit = transformSession.InsertWalkedRoutedFixed32Scalar8(
                    root.Offset, splitProfile, 0x00C0_0000_0000_0000UL, 0, 0, 10_000, 10_000,
                    allowDuplicateKeys: true, maxRouterHops: 8);
                if (parentSplit.Kind != Fixed32Scalar8RoutedInsertKind.WalkedParentRouteSplit ||
                    parentSplit.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"FS32-8 descending parent-route split failed: {parentSplit.Kind}/{parentSplit.InsertResult}.");
                using Fixed32Scalar8RangeReader parentReader = transformSession.OpenFixed32Scalar8RangeReader(
                    root.Offset, splitProfile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue,
                    ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
                if (!parentReader.TryReadPreviousEncodedTuple(out firstKey0, out _, out _, out firstKey3, out firstIdentity) ||
                    firstKey0 != 0x00C0_0000_0000_0000UL || firstKey3 != 10_000 || firstIdentity != 10_000)
                    throw new InvalidDataException("FS32-8 descending parent-route split did not preserve global first tuple.");
            }
        }
        if (verifySs88Shelf)
        {
            Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB with { Descending = true };
            byte[] shelfBytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar8 shelf = new(shelfBytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 512; identity++)
            {
                if (shelf.Insert(1, identity, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS8-8 descending shelf insert failed for identity {identity}.");
            }
            if (shelf.Insert(2, 900, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS8-8 descending shelf high-key insert failed.");
            Scalar8Scalar8ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyAt(0) != 2 || physical.ReadIdentityAt(0) != 900 ||
                physical.ReadIdentityAt(1) != 512 || physical.ReadIdentityAt(512) != 1 ||
                physical.LowerBound(1, 500) != 13 || physical.LowerBoundKey(1) != 1 ||
                !physical.Contains(1, 500) || physical.Contains(1, 513))
                throw new InvalidDataException("SS8-8 descending shelf physical order or bounds failed.");
            ulong[] copied = new ulong[512];
            if (physical.CopyIdentitiesInKeyRange(1, 1, copied) != 512 ||
                copied[0] != 512 || copied[511] != 1)
                throw new InvalidDataException("SS8-8 descending shelf inclusive key range failed.");

            byte[] duplicateBytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar8Layout.WriteMagic(duplicateBytes, Scalar8Scalar8Layout.Magic);
            Scalar8Scalar8Layout.WriteFormatVersion(duplicateBytes, Scalar8Scalar8Layout.FormatVersion);
            Scalar8Scalar8Layout.WriteHeaderSize(duplicateBytes, Scalar8Scalar8Layout.HeaderSize);
            Scalar8Scalar8Layout.WriteFlags(duplicateBytes, Scalar8Scalar8Layout.DuplicateRunFlag | Scalar8Scalar8Layout.DescendingFlag);
            Scalar8Scalar8Layout.WriteDuplicateRunKey(duplicateBytes, 1);
            Scalar8Scalar8 duplicate = new(duplicateBytes, profile);
            if (duplicate.InsertIntoSingleShelfDuplicateRun(1, 3, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted ||
                duplicate.InsertIntoSingleShelfDuplicateRun(1, 1, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted ||
                duplicate.InsertIntoSingleShelfDuplicateRun(1, 2, allowDuplicateKeys: true) != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS8-8 descending duplicate-run insert failed.");
            Scalar8Scalar8ReadOnly duplicatePhysical = duplicate.AsReadOnly();
            if (!duplicatePhysical.IsDescending || !duplicatePhysical.IsDuplicateRun ||
                duplicatePhysical.ReadIdentityAt(0) != 3 || duplicatePhysical.ReadIdentityAt(1) != 2 ||
                duplicatePhysical.ReadIdentityAt(2) != 1 || duplicatePhysical.LowerBound(1, 2) != 1)
                throw new InvalidDataException("SS8-8 descending duplicate-run physical order failed.");

            Scalar8Scalar8Profile splitProfile = Scalar8Scalar8Profile.Default4KiB with { Descending = true };
            DataKernelOptions splitOptions = new(DefaultAppendBufferSize, 0, false, 512);
            SuperblockDeveloperMetadata splitMetadata = new(
                "DescendingSS88Split", "descending physical split", Guid.NewGuid(), 1, 2, 3);
            using LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                splitOptions, splitMetadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc88", 0));
            ulong[] splitKeys = new ulong[splitProfile.MaxItemCount];
            ulong[] splitIdentities = new ulong[splitProfile.MaxItemCount];
            int leftCount = splitKeys.Length / 2;
            for (int i = 0; i < splitKeys.Length; i++)
            {
                ulong key = i < leftCount ? (ulong)i : 0x8000_0000_0000_0000UL + (ulong)(i - leftCount);
                splitKeys[i] = key;
                splitIdentities[i] = key;
            }
            _ = splitSession.CreateScalar8Scalar8ShelfAndLinkRootRoutes(
                root.Offset, splitProfile, splitKeys, splitIdentities, [0x00, 0x80]);
            (_, _, int splitLeftCount, int splitRightCount, Scalar8Scalar8InsertResult splitResult, _) =
                splitSession.SplitRoutedScalar8Scalar8ByRootPrefix(
                    root.Offset, 0x00, 0x80, splitProfile,
                    0x8000_0000_0000_0000UL + 10_000UL,
                    0x8000_0000_0000_0000UL + 10_000UL);
            if (splitResult != Scalar8Scalar8InsertResult.Inserted ||
                splitLeftCount != leftCount || splitRightCount != splitKeys.Length - leftCount + 1)
                throw new InvalidDataException("SS8-8 descending root split counts failed.");
            byte[] splitLeftBytes = new byte[splitProfile.ShelfExtentSize];
            byte[] splitRightBytes = new byte[splitProfile.ShelfExtentSize];
            _ = splitSession.ReadRoutedScalar8Scalar8Shelf(root.Offset, 0x00, splitProfile, splitLeftBytes);
            _ = splitSession.ReadRoutedScalar8Scalar8Shelf(root.Offset, 0x80, splitProfile, splitRightBytes);
            Scalar8Scalar8ReadOnly splitLeft = new(splitLeftBytes, splitProfile);
            Scalar8Scalar8ReadOnly splitRight = new(splitRightBytes, splitProfile);
            if (!splitLeft.IsDescending || !splitRight.IsDescending ||
                splitLeft.ReadKeyAt(0) != (ulong)(leftCount - 1) || splitLeft.ReadKeyAt(leftCount - 1) != 0 ||
                splitRight.ReadKeyAt(0) != 0x8000_0000_0000_0000UL + 10_000UL ||
                splitRight.ReadKeyAt(splitRight.ItemCount - 1) != 0x8000_0000_0000_0000UL)
                throw new InvalidDataException("SS8-8 descending root split physical order failed.");
            using (Scalar8Scalar8RangeReader descendingSplit = splitSession.OpenScalar8Scalar8RangeReader(
                root.Offset, splitProfile, 0, ulong.MaxValue, QueryDirection.Descending))
            {
                if (!descendingSplit.TryReadPreviousEncodedTuple(out ulong firstKey, out ulong firstIdentity) ||
                    firstKey != 0x8000_0000_0000_0000UL + 10_000UL || firstIdentity != firstKey)
                    throw new InvalidDataException("SS8-8 descending routed first row failed.");
            }
            using (Scalar8Scalar8RangeReader ascendingSplit = splitSession.OpenScalar8Scalar8RangeReader(
                root.Offset, splitProfile, 0, ulong.MaxValue, QueryDirection.Ascending))
            {
                if (!ascendingSplit.TryReadNextEncodedIdentity(out ulong firstIdentity) || firstIdentity != 0)
                    throw new InvalidDataException("SS8-8 explicit ascending routed first row failed.");
            }

            using LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                splitOptions, splitMetadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot transformRoot, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc88x", 0));
            CreateDeeperTransformSplitVectors(splitProfile, out ulong[] transformKeys, out ulong[] transformIdentities,
                out int transformLeftCount, out int transformRightCount, includeIntermediateStem: true);
            _ = transformSession.CreateScalar8Scalar8ShelfBehindChildRouter(
                transformRoot.Offset, 0x00, 0x00, splitProfile, transformKeys, transformIdentities);
            const ulong transformHighKey = 0x0000_2280_0000_0000UL + 10_000UL;
            Scalar8Scalar8RoutedInsertResult transformed = transformSession.InsertWalkedRoutedScalar8Scalar8(
                transformRoot.Offset, splitProfile, transformHighKey, transformHighKey,
                allowDuplicateKeys: true, maxRouterHops: 8);
            if (transformed.Kind != Scalar8Scalar8RoutedInsertKind.WalkedShelfTransformSplit ||
                transformed.InsertResult != Scalar8Scalar8InsertResult.Inserted ||
                transformed.LeftShelfOffset == 0 || transformed.RightShelfOffset == 0)
                throw new InvalidDataException("SS8-8 descending transformed split did not publish two shelves.");
            Scalar8Scalar8ReadOnly transformLeft = new(
                transformSession.ReadScalar8Scalar8ShelfBytesForBatch(transformed.LeftShelfOffset, splitProfile), splitProfile);
            Scalar8Scalar8ReadOnly transformRight = new(
                transformSession.ReadScalar8Scalar8ShelfBytesForBatch(transformed.RightShelfOffset, splitProfile), splitProfile);
            if (!transformLeft.IsDescending || !transformRight.IsDescending ||
                transformLeft.ItemCount != transformLeftCount || transformRight.ItemCount != transformRightCount + 1 ||
                transformLeft.ReadKeyAt(0) != 0x0000_2200_0000_0000UL + (ulong)(transformLeftCount - 1) ||
                transformRight.ReadKeyAt(0) != transformHighKey)
                throw new InvalidDataException("SS8-8 descending transformed split physical order failed.");

            using LibraDexFileSession terminalSession = LibraDexFileSession.InitializeMemory(
                splitOptions, splitMetadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot terminalRouter, _) = terminalSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc88t", 0));
            _ = terminalSession.CreateScalar8Scalar8ShelfAndLinkRootRoute(
                terminalRouter.Offset, 0x00, splitProfile, itemCount: 0);
            for (ulong identity = 1; identity <= 700; identity++)
            {
                Scalar8Scalar8RoutedInsertResult inserted = terminalSession.InsertWalkedRoutedScalar8Scalar8(
                    terminalRouter.Offset, splitProfile, 1, identity,
                    allowDuplicateKeys: true, maxRouterHops: 16);
                if (inserted.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS8-8 descending terminal insert failed for identity {identity}.");
            }
            Scalar8Scalar8RoutePathTarget terminalPath = terminalSession.WalkScalar8Scalar8RoutePathTarget(
                terminalRouter.Offset, 1, maxRouterHops: 16,
                readPolicy: Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (terminalPath.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"SS8-8 equal-key run did not reach a terminal root: {terminalPath.Target.Kind}.");
            byte[] terminalRoot = terminalSession.ReadTerminalIdentityRootBytes(terminalPath.Target.Offset);
            if (terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                throw new InvalidDataException("SS8-8 terminal root did not persist descending order.");
            long terminalHeadOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
            byte[] terminalHead = terminalSession.ReadTerminalIdentity8ShelfBytes(terminalHeadOffset, splitProfile.ShelfExtentSize);
            if (TerminalIdentity8ShelfLayout.ReadIdentity(terminalHead, 0) != 700)
                throw new InvalidDataException("SS8-8 descending terminal head did not hold the highest equal-key identity.");
            byte[] terminalKeyBytes = new byte[sizeof(ulong)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(terminalKeyBytes, 1);
            List<ulong> terminalRun = terminalSession.ReadTerminalIdentity8RouteIdentities(
                terminalPath.Target.Offset, terminalKeyBytes, splitProfile.ShelfExtentSize);
            if (terminalRun.Count != 700 || terminalRun[0] != 700 || terminalRun[^1] != 1 ||
                !terminalRun.SequenceEqual(Enumerable.Range(1, 700).Reverse().Select(static value => (ulong)value)))
                throw new InvalidDataException("SS8-8 descending terminal chain was not globally ordered.");
            if (terminalSession.DeleteScalar8Scalar8TerminalIdentities(
                terminalPath.Target.Offset, splitProfile, 1, 700) != 1)
                throw new InvalidDataException("SS8-8 descending terminal head delete failed.");
            terminalRun = terminalSession.ReadTerminalIdentity8RouteIdentities(
                terminalPath.Target.Offset, terminalKeyBytes, splitProfile.ShelfExtentSize);
            if (terminalRun[0] != 699)
                throw new InvalidDataException("SS8-8 descending terminal delete did not advance its physical head.");
            if (terminalSession.InsertWalkedRoutedScalar8Scalar8(
                terminalRouter.Offset, splitProfile, 1, 700,
                allowDuplicateKeys: true, maxRouterHops: 16).InsertResult != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS8-8 descending terminal head reinsert failed.");
            using (Scalar8Scalar8RangeReader descendingTerminal = terminalSession.OpenScalar8Scalar8RangeReader(
                terminalRouter.Offset, splitProfile, 1, 1, QueryDirection.Descending, maxRouterHops: 16))
            {
                for (ulong expected = 700; expected >= 1; expected--)
                {
                    if (!descendingTerminal.TryReadPreviousEncodedTuple(out ulong key, out ulong identity) ||
                        key != 1 || identity != expected)
                        throw new InvalidDataException($"SS8-8 descending terminal stream failed at identity {expected}.");
                }
                if (descendingTerminal.MovePrevious())
                    throw new InvalidDataException("SS8-8 descending terminal stream returned an extra row.");
            }
            using (Scalar8Scalar8RangeReader ascendingTerminal = terminalSession.OpenScalar8Scalar8RangeReader(
                terminalRouter.Offset, splitProfile, 1, 1, QueryDirection.Ascending, maxRouterHops: 16))
            {
                for (ulong expected = 1; expected <= 700; expected++)
                {
                    if (!ascendingTerminal.TryReadNextEncodedIdentity(out ulong identity) || identity != expected)
                        throw new InvalidDataException($"SS8-8 explicit ascending terminal stream failed at identity {expected}.");
                }
                if (ascendingTerminal.MoveNext())
                    throw new InvalidDataException("SS8-8 ascending terminal stream returned an extra row.");
            }

            using LibraDexFileSession sortedSession = LibraDexFileSession.InitializeMemory(
                splitOptions, splitMetadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot sortedRoot, _) = sortedSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc88b", 0));
            Scalar8Scalar8SortedTuple[] sortedTuples = new Scalar8Scalar8SortedTuple[701];
            for (ulong identity = 1; identity <= 700; identity++)
                sortedTuples[checked((int)identity - 1)] = new Scalar8Scalar8SortedTuple(1, identity);
            sortedTuples[^1] = new Scalar8Scalar8SortedTuple(2, 900);
            Scalar8Scalar8SortedBuildResult sortedBuild = sortedSession.BuildScalar8Scalar8FromSorted(
                sortedRoot.Offset, splitProfile, sortedTuples,
                allowDuplicateKeys: true, singleKeyPerIdentity: true, cancellationToken: CancellationToken.None);
            if (sortedBuild.TerminalRootCount != 1 || sortedBuild.TupleCount != 701)
                throw new InvalidDataException("SS8-8 descending sorted build did not create the expected terminal route.");
            using (Scalar8Scalar8RangeReader sortedDescending = sortedSession.OpenScalar8Scalar8RangeReader(
                sortedRoot.Offset, splitProfile, 1, 2, QueryDirection.Descending, maxRouterHops: 16))
            {
                if (!sortedDescending.TryReadPreviousEncodedTuple(out ulong firstKey, out ulong firstIdentity) ||
                    firstKey != 2 || firstIdentity != 900 ||
                    !sortedDescending.TryReadPreviousEncodedTuple(out ulong nextKey, out ulong nextIdentity) ||
                    nextKey != 1 || nextIdentity != 700)
                    throw new InvalidDataException("SS8-8 descending sorted-build first rows failed.");
            }
            using (Scalar8Scalar8RangeReader sortedAscending = sortedSession.OpenScalar8Scalar8RangeReader(
                sortedRoot.Offset, splitProfile, 1, 2, QueryDirection.Ascending, maxRouterHops: 16))
            {
                if (!sortedAscending.TryReadNextEncodedIdentity(out ulong firstIdentityAscending) || firstIdentityAscending != 1)
                    throw new InvalidDataException("SS8-8 ascending sorted-build first row failed.");
            }
        }
        if (verifySs168Shelf)
        {
            Scalar16Scalar8Profile profile = Scalar16Scalar8Profile.Default4KiB with { Descending = true };
            byte[] shelfBytes = new byte[profile.ShelfExtentSize];
            Scalar16Scalar8 shelf = new(shelfBytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 100; identity++)
            {
                if (shelf.Insert(1, 7, identity, allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS16-8 descending shelf insert failed for identity {identity}.");
            }
            if (shelf.Insert(2, 0, 900, allowDuplicateKeys: true) != Scalar16Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS16-8 descending shelf high-key insert failed.");
            Scalar16Scalar8ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyHighAt(0) != 2 || physical.ReadIdentityAt(0) != 900 ||
                physical.ReadIdentityAt(1) != 100 || physical.ReadIdentityAt(100) != 1 ||
                physical.LowerBound(1, 7, 90) != 11 || physical.LowerBoundKey(1, 7) != 1 ||
                !physical.Contains(1, 7, 90) || physical.Contains(1, 7, 101))
                throw new InvalidDataException("SS16-8 descending shelf physical order or bounds failed.");
            ulong[] copied = new ulong[100];
            if (physical.CopyIdentitiesInKeyRange(1, 7, 1, 7, copied) != 100 ||
                copied[0] != 100 || copied[^1] != 1)
                throw new InvalidDataException("SS16-8 descending shelf inclusive range failed.");

            DataKernelOptions options = new(DefaultAppendBufferSize, 0, false, 512);
            SuperblockDeveloperMetadata metadata = new(
                "DescendingSS168", "descending physical terminal", Guid.NewGuid(), 1, 2, 3);
            using LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot splitRoot, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc168s", 0));
            ulong[] splitHighs = new ulong[profile.MaxItemCount];
            ulong[] splitLows = new ulong[profile.MaxItemCount];
            ulong[] splitIdentities = new ulong[profile.MaxItemCount];
            int leftCount = splitHighs.Length / 2;
            for (int i = 0; i < splitHighs.Length; i++)
            {
                ulong high = i < leftCount ? (ulong)i : 0x8000_0000_0000_0000UL + (ulong)(i - leftCount);
                splitHighs[i] = high;
                splitIdentities[i] = high;
            }
            _ = splitSession.CreateScalar16Scalar8ShelfAndLinkRootRoutes(
                splitRoot.Offset, profile, splitHighs, splitLows, splitIdentities, [0x00, 0x80]);
            const ulong incomingHigh = 0x8000_0000_0000_0000UL + 10_000UL;
            (_, _, int splitLeftCount, int splitRightCount, Scalar16Scalar8InsertResult splitResult, _) =
                splitSession.SplitRoutedScalar16Scalar8ByRootPrefix(
                    splitRoot.Offset, 0x00, 0x80, profile, incomingHigh, 0, incomingHigh);
            if (splitResult != Scalar16Scalar8InsertResult.Inserted ||
                splitLeftCount != leftCount || splitRightCount != splitHighs.Length - leftCount + 1)
                throw new InvalidDataException("SS16-8 descending root split counts failed.");
            byte[] splitLeftBytes = new byte[profile.ShelfExtentSize];
            byte[] splitRightBytes = new byte[profile.ShelfExtentSize];
            _ = splitSession.ReadRoutedScalar16Scalar8Shelf(splitRoot.Offset, 0x00, profile, splitLeftBytes);
            _ = splitSession.ReadRoutedScalar16Scalar8Shelf(splitRoot.Offset, 0x80, profile, splitRightBytes);
            Scalar16Scalar8ReadOnly splitLeft = new(splitLeftBytes, profile);
            Scalar16Scalar8ReadOnly splitRight = new(splitRightBytes, profile);
            if (!splitLeft.IsDescending || !splitRight.IsDescending ||
                splitLeft.ReadKeyHighAt(0) != (ulong)(leftCount - 1) ||
                splitLeft.ReadKeyHighAt(splitLeft.ItemCount - 1) != 0 ||
                splitRight.ReadKeyHighAt(0) != incomingHigh ||
                splitRight.ReadKeyHighAt(splitRight.ItemCount - 1) != 0x8000_0000_0000_0000UL)
                throw new InvalidDataException("SS16-8 descending root split physical order failed.");
            using (Scalar16Scalar8RangeReader descendingSplit = splitSession.OpenScalar16Scalar8RangeReader(
                splitRoot.Offset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending))
            {
                if (!descendingSplit.TryReadPreviousEncodedTuple(out ulong high, out _, out ulong identity) ||
                    high != incomingHigh || identity != incomingHigh)
                    throw new InvalidDataException("SS16-8 descending root split first row failed.");
            }
            using (Scalar16Scalar8RangeReader ascendingDelete = splitSession.OpenScalar16Scalar8RangeReader(
                splitRoot.Offset, profile, 0, 0, (ulong)(leftCount - 1), 0, QueryDirection.Ascending))
            {
                if (!ascendingDelete.TryReadNextEncodedIdentity(out ulong firstIdentity) || firstIdentity != 0 ||
                    !ascendingDelete.DeleteCurrent() ||
                    !ascendingDelete.TryReadNextEncodedIdentity(out ulong nextIdentity) || nextIdentity != 1)
                    throw new InvalidDataException("SS16-8 descending shelf ascending cursor delete continuation failed.");
            }

            using LibraDexFileSession parentSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot parentRoot, _) = parentSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc168p", 0));
            _ = parentSession.CreateScalar16Scalar8ShelfAndLinkRootRoutes(
                parentRoot.Offset, profile, splitHighs, splitLows, splitIdentities, [0x00, 0x80]);
            if (!parentSession.TrySplitScalar16Scalar8DirectParentRoute(
                parentRoot.Offset, profile, incomingHigh, 0, incomingHigh, maxRouterHops: 16,
                out Scalar16Scalar8RoutedInsertResult parentSplit) ||
                parentSplit.Kind != Scalar16Scalar8RoutedInsertKind.WalkedParentRouteSplit ||
                parentSplit.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS16-8 descending direct parent-route split failed.");
            using (Scalar16Scalar8RangeReader parentReader = parentSession.OpenScalar16Scalar8RangeReader(
                parentRoot.Offset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending))
            {
                if (!parentReader.TryReadPreviousEncodedTuple(out ulong high, out _, out ulong identity) ||
                    high != incomingHigh || identity != incomingHigh)
                    throw new InvalidDataException("SS16-8 descending parent-route split first row failed.");
            }

            using LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot transformRoot, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc168x", 0));
            ulong[] transformHighs = new ulong[profile.MaxItemCount];
            ulong[] transformLows = new ulong[profile.MaxItemCount];
            ulong[] transformIdentities = new ulong[profile.MaxItemCount];
            const ulong transformBase = 0x40F0_0000_0000_0000UL;
            for (int i = 0; i < transformHighs.Length; i++)
            {
                transformHighs[i] = transformBase + (ulong)i * 2;
                transformIdentities[i] = (ulong)i + 1;
            }
            _ = transformSession.CreateScalar16Scalar8ShelfAndLinkRootRoutes(
                transformRoot.Offset, profile, transformHighs, transformLows, transformIdentities, [0x20, 0x40, 0x60]);
            if (!transformSession.TryTransformScalar16Scalar8DirectShelf(
                transformRoot.Offset, profile, transformBase + 1, 0, 10_000,
                maxRouterHops: 40, out Scalar16Scalar8RoutedInsertResult transformed) ||
                transformed.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS16-8 descending direct transform did not execute.");
            using (Scalar16Scalar8RangeReader transformedReader = transformSession.OpenScalar16Scalar8RangeReader(
                transformRoot.Offset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending, maxRouterHops: 40))
            {
                for (int i = transformHighs.Length - 1; i >= 0; i--)
                {
                    if (!transformedReader.TryReadPreviousEncodedTuple(out ulong high, out _, out ulong identity) ||
                        high != transformHighs[i] || identity != transformIdentities[i])
                        throw new InvalidDataException($"SS16-8 descending transformed stream missed item {i}.");
                    if (i == 1 && (!transformedReader.TryReadPreviousEncodedTuple(out high, out _, out identity) ||
                        high != transformBase + 1 || identity != 10_000))
                        throw new InvalidDataException("SS16-8 descending transformed stream missed incoming item.");
                }
                if (transformedReader.MovePrevious())
                    throw new InvalidDataException("SS16-8 descending transformed stream returned an extra row.");
            }
            using LibraDexFileSession terminalSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = terminalSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc168", 0));
            _ = terminalSession.CreateScalar16Scalar8ShelfAndLinkRootRoute(root.Offset, 0, profile, itemCount: 0);
            for (ulong identity = 1; identity <= 700; identity++)
            {
                Scalar16Scalar8RoutedInsertResult inserted = terminalSession.InsertWalkedRoutedScalar16Scalar8(
                    root.Offset, profile, 1, 7, identity, allowDuplicateKeys: true, maxRouterHops: 16);
                if (inserted.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                    throw new InvalidDataException($"SS16-8 descending terminal insert failed for identity {identity}.");
            }
            Scalar16Scalar8RoutePathTarget terminalPath = terminalSession.WalkScalar16Scalar8RoutePathTarget(
                root.Offset, 1, 7, maxRouterHops: 16);
            if (terminalPath.Target.Kind != Scalar16Scalar8RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"SS16-8 equal-key run did not reach a terminal root: {terminalPath.Target.Kind}.");
            byte[] terminalRoot = terminalSession.ReadTerminalIdentityRootBytes(terminalPath.Target.Offset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
            byte[] head = terminalSession.ReadTerminalIdentity8ShelfBytes(headOffset, profile.ShelfExtentSize);
            if (terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                TerminalIdentity8ShelfLayout.ReadIdentity(head, 0) != 700 ||
                TerminalIdentity8ShelfLayout.ReadNextShelfOffset(head) == 0)
                throw new InvalidDataException("SS16-8 terminal head was not physically descending or multi-shelf.");
            Scalar16Scalar8RoutedInsertResult neighboring = terminalSession.InsertWalkedRoutedScalar16Scalar8(
                root.Offset, profile, 1, 8, 900, allowDuplicateKeys: true, maxRouterHops: 40);
            if (neighboring.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                throw new InvalidDataException("SS16-8 descending terminal-neighbor separation insert failed.");
            using (Scalar16Scalar8RangeReader neighboringReader = terminalSession.OpenScalar16Scalar8RangeReader(
                root.Offset, profile, 1, 7, 1, 8, QueryDirection.Descending, maxRouterHops: 40))
            {
                if (!neighboringReader.TryReadPreviousEncodedTuple(out ulong high, out ulong low, out ulong identity) ||
                    high != 1 || low != 8 || identity != 900 ||
                    !neighboringReader.TryReadPreviousEncodedTuple(out high, out low, out identity) ||
                    high != 1 || low != 7 || identity != 700)
                    throw new InvalidDataException("SS16-8 descending terminal-neighbor route ordering failed.");
            }
            using (Scalar16Scalar8RangeReader descendingReader = terminalSession.OpenScalar16Scalar8RangeReader(
                root.Offset, profile, 1, 7, 1, 7, QueryDirection.Descending, maxRouterHops: 40))
            {
                for (ulong expected = 700; expected >= 1; expected--)
                {
                    if (!descendingReader.TryReadPreviousEncodedTuple(out ulong high, out ulong low, out ulong identity) ||
                        high != 1 || low != 7 || identity != expected)
                        throw new InvalidDataException($"SS16-8 descending terminal stream failed at identity {expected}.");
                    if (expected == 700)
                    {
                        object? loadedShelves = typeof(Scalar16Scalar8RangeReader)
                            .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?.GetValue(descendingReader);
                        if (loadedShelves is not int loadedCount || loadedCount != 1)
                            throw new InvalidDataException("SS16-8 descending first result loaded more than one terminal shelf.");
                    }
                }
                if (descendingReader.MovePrevious())
                    throw new InvalidDataException("SS16-8 descending terminal stream returned an extra row.");
            }
            using (Scalar16Scalar8RangeReader ascendingReader = terminalSession.OpenScalar16Scalar8RangeReader(
                root.Offset, profile, 1, 7, 1, 7, QueryDirection.Ascending, maxRouterHops: 40))
            {
                for (ulong expected = 1; expected <= 700; expected++)
                {
                    if (!ascendingReader.TryReadNextEncodedIdentity(out ulong identity) || identity != expected)
                        throw new InvalidDataException($"SS16-8 explicit ascending terminal stream failed at identity {expected}.");
                }
                if (ascendingReader.MoveNext())
                    throw new InvalidDataException("SS16-8 ascending terminal stream returned an extra row.");
            }
            using (Scalar16Scalar8RangeReader deleteReader = terminalSession.OpenScalar16Scalar8RangeReader(
                root.Offset, profile, 1, 7, 1, 7, QueryDirection.Descending, maxRouterHops: 40))
            {
                if (deleteReader.DeleteMatchedRanges() != 700)
                    throw new InvalidDataException("SS16-8 descending terminal range deletion missed identities.");
            }
            using (Scalar16Scalar8RangeReader emptyReader = terminalSession.OpenScalar16Scalar8RangeReader(
                root.Offset, profile, 1, 7, 1, 7, QueryDirection.Descending, maxRouterHops: 40))
            {
                if (emptyReader.MovePrevious())
                    throw new InvalidDataException("SS16-8 terminal range deletion retained an identity.");
            }
        }
        if (verifySs816Shelf)
        {
            Scalar8Scalar16Profile profile = Scalar8Scalar16Profile.Default4KiB with { Descending = true };
            byte[] shelfBytes = new byte[profile.ShelfExtentSize];
            Scalar8Scalar16 shelf = new(shelfBytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 100; identity++)
            {
                if (shelf.Insert(1, 0, identity, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.Inserted)
                    throw new InvalidDataException($"SS8-16 descending shelf insert failed for identity {identity}.");
            }
            if (shelf.Insert(2, 0, 900, allowDuplicateKeys: true) != Scalar8Scalar16InsertResult.Inserted)
                throw new InvalidDataException("SS8-16 descending shelf high-key insert failed.");
            Scalar8Scalar16ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyAt(0) != 2 || physical.ReadIdentityLowAt(0) != 900 ||
                physical.ReadIdentityLowAt(1) != 100 || physical.ReadIdentityLowAt(100) != 1 ||
                physical.LowerBound(1, 0, 90) != 11 || physical.LowerBoundKey(1) != 1 ||
                !physical.Contains(1, 0, 90) || physical.Contains(1, 0, 101))
                throw new InvalidDataException("SS8-16 descending shelf physical order or bounds failed.");
            ulong[] highs = new ulong[100];
            ulong[] lows = new ulong[100];
            if (physical.CopyIdentitiesInKeyRange(1, 1, highs, lows) != 100 ||
                lows[0] != 100 || lows[^1] != 1 || highs.Any(static value => value != 0))
                throw new InvalidDataException("SS8-16 descending shelf inclusive range failed.");

            DataKernelOptions options = new(DefaultAppendBufferSize, 0, false, 512);
            SuperblockDeveloperMetadata metadata = new(
                "DescendingSS816", "descending physical split", Guid.NewGuid(), 1, 2, 3);
            using LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc816", 0));
            ulong[] splitKeys = new ulong[profile.MaxItemCount];
            ulong[] splitHighs = new ulong[profile.MaxItemCount];
            ulong[] splitLows = new ulong[profile.MaxItemCount];
            int leftCount = splitKeys.Length / 2;
            for (int i = 0; i < splitKeys.Length; i++)
            {
                ulong key = i < leftCount ? (ulong)i : 0x8000_0000_0000_0000UL + (ulong)(i - leftCount);
                splitKeys[i] = key;
                splitLows[i] = key;
            }
            _ = splitSession.CreateScalar8Scalar16ShelfAndLinkRootRoutes(
                root.Offset, profile, splitKeys, splitHighs, splitLows, [0x00, 0x80]);
            const ulong incomingKey = 0x8000_0000_0000_0000UL + 10_000UL;
            (_, _, int splitLeftCount, int splitRightCount, Scalar8Scalar16InsertResult splitResult, _) =
                splitSession.SplitRoutedScalar8Scalar16ByRootPrefix(
                    root.Offset, 0x00, 0x80, profile, incomingKey, 0, incomingKey);
            if (splitResult != Scalar8Scalar16InsertResult.Inserted ||
                splitLeftCount != leftCount || splitRightCount != splitKeys.Length - leftCount + 1)
                throw new InvalidDataException("SS8-16 descending root split counts failed.");
            byte[] leftBytes = new byte[profile.ShelfExtentSize];
            byte[] rightBytes = new byte[profile.ShelfExtentSize];
            _ = splitSession.ReadRoutedScalar8Scalar16Shelf(root.Offset, 0x00, profile, leftBytes);
            _ = splitSession.ReadRoutedScalar8Scalar16Shelf(root.Offset, 0x80, profile, rightBytes);
            Scalar8Scalar16ReadOnly left = new(leftBytes, profile);
            Scalar8Scalar16ReadOnly right = new(rightBytes, profile);
            if (!left.IsDescending || !right.IsDescending ||
                left.ReadKeyAt(0) != (ulong)(leftCount - 1) || left.ReadKeyAt(left.ItemCount - 1) != 0 ||
                right.ReadKeyAt(0) != incomingKey || right.ReadKeyAt(right.ItemCount - 1) != 0x8000_0000_0000_0000UL)
                throw new InvalidDataException("SS8-16 descending root split physical order failed.");
            using (Scalar8Scalar16RangeReader reader = splitSession.OpenScalar8Scalar16RangeReader(
                root.Offset, profile, 0, ulong.MaxValue, QueryDirection.Descending))
            {
                if (!reader.TryReadPreviousEncodedTuple(out ulong key, out ulong high, out ulong low) ||
                    key != incomingKey || high != 0 || low != incomingKey)
                    throw new InvalidDataException("SS8-16 descending root split first row failed.");
            }
            using (Scalar8Scalar16RangeReader reader = splitSession.OpenScalar8Scalar16RangeReader(
                root.Offset, profile, 0, (ulong)(leftCount - 1), QueryDirection.Ascending))
            {
                if (!reader.TryReadNextEncodedIdentity(out ulong high, out ulong low) || high != 0 || low != 0 ||
                    !reader.DeleteCurrent() ||
                    !reader.TryReadNextEncodedIdentity(out high, out low) || high != 0 || low != 1)
                    throw new InvalidDataException("SS8-16 descending shelf ascending cursor delete continuation failed.");
            }

            using LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot transformRoot, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc816x", 0));
            ulong[] transformKeys = new ulong[profile.MaxItemCount];
            ulong[] transformHighs = new ulong[profile.MaxItemCount];
            ulong[] transformLows = new ulong[profile.MaxItemCount];
            const ulong transformBase = 0x40F0_0000_0000_0000UL;
            for (int i = 0; i < transformKeys.Length; i++)
            {
                transformKeys[i] = transformBase + (ulong)i * 2;
                transformLows[i] = (ulong)i + 1;
            }
            _ = transformSession.CreateScalar8Scalar16ShelfAndLinkRootRoutes(
                transformRoot.Offset, profile, transformKeys, transformHighs, transformLows, [0x20, 0x40, 0x60]);
            if (!transformSession.TryTransformScalar8Scalar16DirectShelf(
                transformRoot.Offset, profile, transformBase + 1, 0, 10_000,
                maxRouterHops: 40, out Scalar8Scalar16RoutedInsertResult transformed) ||
                transformed.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                throw new InvalidDataException("SS8-16 descending direct transform did not execute.");
            using (Scalar8Scalar16RangeReader reader = transformSession.OpenScalar8Scalar16RangeReader(
                transformRoot.Offset, profile, 0, ulong.MaxValue, QueryDirection.Descending, maxRouterHops: 40))
            {
                for (int i = transformKeys.Length - 1; i >= 0; i--)
                {
                    if (!reader.TryReadPreviousEncodedTuple(out ulong key, out ulong high, out ulong low) ||
                        key != transformKeys[i] || high != 0 || low != transformLows[i])
                        throw new InvalidDataException($"SS8-16 descending transformed stream missed item {i}.");
                    if (i == 1 && (!reader.TryReadPreviousEncodedTuple(out key, out high, out low) ||
                        key != transformBase + 1 || high != 0 || low != 10_000))
                        throw new InvalidDataException("SS8-16 descending transformed stream missed incoming item.");
                }
                if (reader.MovePrevious())
                    throw new InvalidDataException("SS8-16 descending transformed stream returned an extra row.");
            }
        }

        if (verifySs1616Shelf)
        {
            Scalar16Scalar16Profile profile = Scalar16Scalar16Profile.Default4KiB with { Descending = true };
            byte[] shelfBytes = new byte[profile.ShelfExtentSize];
            Scalar16Scalar16 shelf = new(shelfBytes, profile);
            shelf.Initialize();
            for (ulong identity = 1; identity <= 100; identity++)
            {
                if (shelf.Insert(1, 2, 0, identity, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.Inserted)
                    throw new InvalidDataException($"SS16-16 descending shelf insert failed for identity {identity}.");
            }
            if (shelf.Insert(1, 3, 0, 900, allowDuplicateKeys: true) != Scalar16Scalar16InsertResult.Inserted)
                throw new InvalidDataException("SS16-16 descending shelf high-key insert failed.");
            Scalar16Scalar16ReadOnly physical = shelf.AsReadOnly();
            if (!physical.IsDescending || physical.ReadKeyLowAt(0) != 3 || physical.ReadIdentityLowAt(0) != 900 ||
                physical.ReadIdentityLowAt(1) != 100 || physical.ReadIdentityLowAt(100) != 1 ||
                physical.LowerBound(1, 2, 0, 90) != 11 || physical.LowerBoundKey(1, 2) != 1 ||
                !physical.Contains(1, 2, 0, 90) || physical.Contains(1, 2, 0, 101))
                throw new InvalidDataException("SS16-16 descending shelf physical order or bounds failed.");
            ulong[] highs = new ulong[100];
            ulong[] lows = new ulong[100];
            if (physical.CopyIdentitiesInKeyRange(1, 2, 1, 2, highs, lows) != 100 ||
                lows[0] != 100 || lows[^1] != 1 || highs.Any(static value => value != 0))
                throw new InvalidDataException("SS16-16 descending shelf inclusive range failed.");

            DataKernelOptions options = new(DefaultAppendBufferSize, 0, false, 512);
            SuperblockDeveloperMetadata metadata = new(
                "DescendingSS1616", "descending physical split", Guid.NewGuid(), 1, 2, 3);
            using LibraDexFileSession splitSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot root, _) = splitSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc1616", 0));
            ulong[] keyHighs = new ulong[profile.MaxItemCount];
            ulong[] keyLows = new ulong[profile.MaxItemCount];
            ulong[] identityHighs = new ulong[profile.MaxItemCount];
            ulong[] identityLows = new ulong[profile.MaxItemCount];
            int leftCount = keyHighs.Length / 2;
            for (int i = 0; i < keyHighs.Length; i++)
            {
                ulong high = i < leftCount ? (ulong)i : 0x8000_0000_0000_0000UL + (ulong)(i - leftCount);
                keyHighs[i] = high;
                identityLows[i] = high;
            }
            _ = splitSession.CreateScalar16Scalar16ShelfAndLinkRootRoutes(
                root.Offset, profile, keyHighs, keyLows, identityHighs, identityLows, [0x00, 0x80]);
            const ulong incomingHigh = 0x8000_0000_0000_0000UL + 10_000UL;
            (_, _, int splitLeftCount, int splitRightCount, Scalar16Scalar16InsertResult splitResult, _) =
                splitSession.SplitRoutedScalar16Scalar16ByRootPrefix(
                    root.Offset, 0x00, 0x80, profile, incomingHigh, 0, 0, incomingHigh);
            if (splitResult != Scalar16Scalar16InsertResult.Inserted ||
                splitLeftCount != leftCount || splitRightCount != keyHighs.Length - leftCount + 1)
                throw new InvalidDataException("SS16-16 descending root split counts failed.");
            byte[] leftBytes = new byte[profile.ShelfExtentSize];
            byte[] rightBytes = new byte[profile.ShelfExtentSize];
            _ = splitSession.ReadRoutedScalar16Scalar16Shelf(root.Offset, 0x00, profile, leftBytes);
            _ = splitSession.ReadRoutedScalar16Scalar16Shelf(root.Offset, 0x80, profile, rightBytes);
            Scalar16Scalar16ReadOnly left = new(leftBytes, profile);
            Scalar16Scalar16ReadOnly right = new(rightBytes, profile);
            if (!left.IsDescending || !right.IsDescending ||
                left.ReadKeyHighAt(0) != (ulong)(leftCount - 1) || left.ReadKeyHighAt(left.ItemCount - 1) != 0 ||
                right.ReadKeyHighAt(0) != incomingHigh || right.ReadKeyHighAt(right.ItemCount - 1) != 0x8000_0000_0000_0000UL)
                throw new InvalidDataException("SS16-16 descending root split physical order failed.");
            using Scalar16Scalar16RangeReader reader = splitSession.OpenScalar16Scalar16RangeReader(
                root.Offset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, QueryDirection.Descending);
            if (!reader.TryReadPreviousEncodedTuple(out ulong firstHigh, out ulong firstLow, out ulong firstIdentityHigh, out ulong firstIdentityLow) ||
                firstHigh != incomingHigh || firstLow != 0 || firstIdentityHigh != 0 || firstIdentityLow != incomingHigh)
                throw new InvalidDataException("SS16-16 descending root split first row failed.");

            using LibraDexFileSession transformSession = LibraDexFileSession.InitializeMemory(
                options, metadata, DataKernelTelemetryOptions.EnabledOptions);
            (RouterSnapshot transformRoot, _) = transformSession.CreateRootRouterIndex(CreateHarnessSlot(0, "desc1616x", 0));
            ulong[] transformKeyHighs = new ulong[profile.MaxItemCount];
            ulong[] transformKeyLows = new ulong[profile.MaxItemCount];
            ulong[] transformIdentityHighs = new ulong[profile.MaxItemCount];
            ulong[] transformIdentityLows = new ulong[profile.MaxItemCount];
            const ulong transformBase = 0x40F0_0000_0000_0000UL;
            for (int i = 0; i < transformKeyHighs.Length; i++)
            {
                transformKeyHighs[i] = transformBase + (ulong)i * 2;
                transformIdentityLows[i] = (ulong)i + 1;
            }
            _ = transformSession.CreateScalar16Scalar16ShelfAndLinkRootRoutes(
                transformRoot.Offset, profile, transformKeyHighs, transformKeyLows,
                transformIdentityHighs, transformIdentityLows, [0x20, 0x40, 0x60]);
            if (!transformSession.TryTransformScalar16Scalar16DirectShelf(
                transformRoot.Offset, profile, transformBase + 1, 0, 0, 10_000,
                maxRouterHops: 40, out Scalar16Scalar16RoutedInsertResult transformed) ||
                transformed.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                throw new InvalidDataException("SS16-16 descending direct transform did not execute.");
            using Scalar16Scalar16RangeReader transformedReader = transformSession.OpenScalar16Scalar16RangeReader(
                transformRoot.Offset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue,
                QueryDirection.Descending, maxRouterHops: 40);
            for (int i = transformKeyHighs.Length - 1; i >= 0; i--)
            {
                if (!transformedReader.TryReadPreviousEncodedTuple(out ulong keyHigh, out ulong keyLow,
                    out ulong identityHigh, out ulong identityLow) ||
                    keyHigh != transformKeyHighs[i] || keyLow != 0 ||
                    identityHigh != 0 || identityLow != transformIdentityLows[i])
                    throw new InvalidDataException($"SS16-16 descending transformed stream missed item {i}.");
                if (i == 1 && (!transformedReader.TryReadPreviousEncodedTuple(out keyHigh, out keyLow,
                    out identityHigh, out identityLow) || keyHigh != transformBase + 1 || keyLow != 0 ||
                    identityHigh != 0 || identityLow != 10_000))
                    throw new InvalidDataException("SS16-16 descending transformed stream missed incoming item.");
            }
            if (transformedReader.MovePrevious())
                throw new InvalidDataException("SS16-16 descending transformed stream returned an extra row.");
        }

        using Catalog catalog = Catalog.CreateMemory();
        IndexOptions descending = new() { SortOrder = LibraDexIndexSortOrder.Descending };
        LibraDexIdentityPrimitiveRequest all = new(LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 4, Direction: QueryDirection.Descending);

        using LibraDexIndex<long, long> scalar = catalog.Indexes.IndexSet("descending").Define("modified").Int64Keys<long>().Create(options: descending);
        for (long identity = 1; identity <= 128; identity++)
            ValidateGenericInsert(scalar.Insert(1, identity), "descending scalar equal-key insert");
        ValidateGenericInsert(scalar.Insert(2, 900), "descending scalar high-key insert");
        AssertDescendingIds((IIndex)scalar, all, 900, 128, 127, 126);
        AssertDescendingIds(scalar, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { 1L }, TakeLimit: 3, Direction: QueryDirection.Descending), 128, 127, 126);
        LibraDexConditionEndCondition scalarAll = LibraDexCondition.ForGroup("descending").Index("modified").AsInt64.All().EndCondition;
        long[] scalarNatural = catalog.Indexes.IndexSet("descending").GetIdentities<long>(scalarAll, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
        if (!scalarNatural.SequenceEqual(new long[] { 900, 128, 127, 126 }))
            throw new InvalidDataException($"Descending scalar plan-natural order failed: [{string.Join(',', scalarNatural)}].");
        if (verifySs88Shelf)
        {
            Scalar8Scalar8Profile publicProfile = scalar.GetScalar8Scalar8Profile();
            long terminalLastIdentity = 3L * publicProfile.MaxItemCount + 28;
            ulong encodedPublicKey = LibraDexGenericScalarCodec<long>.Encode8(1L);
            for (long identity = 129; identity <= terminalLastIdentity; identity++)
                ValidateGenericInsert(scalar.Insert(1, identity), "descending public SS8-8 terminal insert");
            Scalar8Scalar8RoutePathTarget publicPath = catalog.Session.WalkScalar8Scalar8RoutePathTarget(
                scalar.RootRouterOffset, encodedPublicKey, maxRouterHops: 16,
                readPolicy: Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (!publicProfile.Descending || publicPath.Target.Kind != Scalar8Scalar8RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"Public SS8-8 descending profile did not produce a terminal route: profileMax={publicProfile.MaxItemCount}, target={publicPath.Target.Kind}.");
            byte[] publicRoot = catalog.Session.ReadTerminalIdentityRootBytes(publicPath.Target.Offset);
            long publicHeadOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(publicRoot);
            byte[] publicHead = catalog.Session.ReadTerminalIdentity8ShelfBytes(publicHeadOffset, publicProfile.ShelfExtentSize);
            if (publicRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                TerminalIdentity8ShelfLayout.ReadIdentity(publicHead, 0) != LibraDexGenericScalarCodec<long>.Encode8(terminalLastIdentity))
                throw new InvalidDataException("Public SS8-8 descending terminal head was not physically highest-first.");
            AssertDescendingIds(scalar, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { 1L }, TakeLimit: 3,
                Direction: QueryDirection.Descending), terminalLastIdentity, terminalLastIdentity - 1, terminalLastIdentity - 2);
            AssertDescendingIds(scalar, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { 1L }, TakeLimit: 3,
                Direction: QueryDirection.Ascending), 1, 2, 3);
            using (LibraDexRangeReader<long, long> publicDescending = scalar.OpenRangeReader(1, 2, QueryDirection.Descending))
            {
                if (publicDescending.Count != terminalLastIdentity + 1 || !publicDescending.MoveNext() ||
                    publicDescending.CurrentKey != 2 || publicDescending.CurrentIdentity != 900 ||
                    !publicDescending.MoveNext() || publicDescending.CurrentIdentity != terminalLastIdentity)
                    throw new InvalidDataException("Public SS8-8 descending cursor range or first results failed.");
                if (publicDescending.Skip(10) != 10 || !publicDescending.MoveNext() ||
                    publicDescending.CurrentIdentity != terminalLastIdentity - 11)
                    throw new InvalidDataException("Public SS8-8 descending cursor skip crossed the wrong terminal slots.");
            }
            using (LibraDexRangeReader<long, long> publicAscending = scalar.OpenRangeReader(1, 1, QueryDirection.Ascending))
            {
                if (publicAscending.Skip(10) != 10 || !publicAscending.MoveNext() ||
                    publicAscending.CurrentIdentity != 11)
                    throw new InvalidDataException("Public SS8-8 explicit ascending cursor skip crossed the wrong terminal slots.");
            }
            scalar.Delete(1, terminalLastIdentity);
            AssertDescendingIds(scalar, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { 1L }, TakeLimit: 1,
                Direction: QueryDirection.Descending), terminalLastIdentity - 1);
            ValidateGenericInsert(scalar.Insert(1, terminalLastIdentity), "descending public SS8-8 terminal reinsert");
            long[] completeDescendingRun = ((IIdentityPrimitiveExecutor)scalar).IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Find, new object?[] { 1L },
                    Direction: QueryDirection.Descending)).Select(value => (long)value).ToArray();
            if (completeDescendingRun.Length != terminalLastIdentity ||
                completeDescendingRun[0] != terminalLastIdentity || completeDescendingRun[^1] != 1 ||
                !completeDescendingRun.SequenceEqual(Enumerable.Range(1, checked((int)terminalLastIdentity)).Reverse().Select(static value => (long)value)))
                throw new InvalidDataException("Public SS8-8 descending stream did not cover the complete physical terminal chain.");
            long[] completeAscendingRun = ((IIdentityPrimitiveExecutor)scalar).IterateIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Find, new object?[] { 1L },
                    Direction: QueryDirection.Ascending)).Select(value => (long)value).ToArray();
            if (!completeAscendingRun.SequenceEqual(completeDescendingRun.Reverse()))
                throw new InvalidDataException("Public SS8-8 explicit ascending stream did not reverse the complete terminal chain.");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss88-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<long, long> fileIndex = persisted.Indexes.IndexSet("descending").Define("reopenSS88").Int64Keys<long>().Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(1, 10), "descending SS8-8 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(1, 30), "descending SS8-8 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(1, 20), "descending SS8-8 file insert 20");
                    ValidateGenericInsert(fileIndex.Insert(2, 40), "descending SS8-8 file insert high key");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<long, long> fileIndex = reopened.Indexes.IndexSet("descending").Define("reopenSS88").Int64Keys<long>().Open();
                    Scalar8Scalar8Profile reopenedProfile = fileIndex.GetScalar8Scalar8Profile();
                    Scalar8Scalar8RoutePathTarget reopenedPath = reopened.Session.WalkScalar8Scalar8RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedPublicKey, maxRouterHops: 16,
                        readPolicy: Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Scalar8Scalar8RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened SS8-8 did not retain its descending shelf profile.");
                    Scalar8Scalar8ReadOnly reopenedShelf = new(
                        reopened.Session.ReadScalar8Scalar8ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending ||
                        reopenedShelf.ReadIdentityAt(0) != LibraDexGenericScalarCodec<long>.Encode8(40L) ||
                        reopenedShelf.ReadIdentityAt(1) != LibraDexGenericScalarCodec<long>.Encode8(30L))
                        throw new InvalidDataException("Reopened SS8-8 shelf was not physically descending.");
                    AssertDescendingIds(fileIndex, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { 1L }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), 30, 20, 10);
                    LibraDexIdentityMutationResult removed = ((IIdentityPrimitiveMutator)fileIndex).DeleteIdentityPrimitive(
                        new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Between, new object?[] { 1L, 1L }));
                    if (removed.ChangedCount != 3)
                        throw new InvalidDataException($"Reopened SS8-8 descending inclusive deletion removed {removed.ChangedCount} rows instead of 3.");
                    AssertDescendingIds(fileIndex, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 1,
                        Direction: QueryDirection.Descending), 40);
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        if (verifySs168Shelf)
        {
            using LibraDexIndex<Guid, long> guidIndex = catalog.Indexes.IndexSet("descending").Define("guid168").GuidKeys<long>().Create(options: descending);
            Guid key = Guid.Parse("00000000-0000-0000-0000-000000000007");
            Guid upperKey = Guid.Parse("00000000-0000-0000-0000-000000000008");
            for (long identity = 1; identity <= 700; identity++)
                ValidateGenericInsert(guidIndex.Insert(key, identity), "descending public SS16-8 terminal insert");
            ValidateGenericInsert(guidIndex.Insert(upperKey, 900), "descending public SS16-8 upper-key insert");
            if (!guidIndex.GetScalar16Scalar8Profile().Descending)
                throw new InvalidDataException("Public SS16-8 descending profile was not propagated.");
            AssertDescendingIds(guidIndex, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { key }, TakeLimit: 4,
                Direction: QueryDirection.Descending), 700, 699, 698, 697);
            AssertDescendingIds(guidIndex, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { key }, TakeLimit: 4,
                Direction: QueryDirection.Ascending), 1, 2, 3, 4);
            LibraDexConditionEndCondition guidNatural = LibraDexCondition.ForGroup("descending")
                .Index("guid168").AsGuid.Between(key, key).EndCondition;
            long[] naturalGuids = catalog.Indexes.IndexSet("descending").GetIdentities<long>(
                guidNatural, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalGuids.SequenceEqual(new long[] { 700, 699, 698, 697 }))
                throw new InvalidDataException($"Public SS16-8 natural descending range returned [{string.Join(',', naturalGuids)}].");
            LibraDexConditionEndCondition guidBetween = LibraDexCondition.ForGroup("descending")
                .Index("guid168").AsGuid.Between(key, upperKey).EndCondition;
            long[] naturalBetween = catalog.Indexes.IndexSet("descending").GetIdentities<long>(
                guidBetween, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalBetween.SequenceEqual(new long[] { 900, 700, 699, 698 }))
                throw new InvalidDataException($"Public SS16-8 natural descending between returned [{string.Join(',', naturalBetween)}].");
            long[] bulkGuids = new long[700];
            LibraDexGenericRangeReadResult bulkGuidRead = guidIndex.ReadRange(key, key, bulkGuids);
            if (bulkGuidRead.IdentityCount != 700 || bulkGuids[0] != 700 || bulkGuids[^1] != 1)
                throw new InvalidDataException("Public SS16-8 descending bulk range did not stream the terminal chain in natural order.");
            long[] bulkBetween = new long[701];
            LibraDexGenericRangeReadResult bulkBetweenRead = guidIndex.ReadRange(key, upperKey, bulkBetween);
            if (bulkBetweenRead.IdentityCount != 701 || bulkBetween[0] != 900 || bulkBetween[1] != 700 || bulkBetween[^1] != 1)
                throw new InvalidDataException("Public SS16-8 descending bulk between did not preserve router order.");
            using (LibraDexRangeReader<Guid, long> descendingReader = guidIndex.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!descendingReader.MoveNext() || descendingReader.CurrentIdentity != 700 ||
                    descendingReader.Skip(10) != 10 || !descendingReader.MoveNext() || descendingReader.CurrentIdentity != 689)
                    throw new InvalidDataException("Public SS16-8 descending cursor first result or skip failed.");
            }
            using (LibraDexRangeReader<Guid, long> crossShelfReader = guidIndex.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!crossShelfReader.MoveNext() || crossShelfReader.CurrentIdentity != 700 ||
                    crossShelfReader.Skip(600) != 600 || !crossShelfReader.MoveNext() || crossShelfReader.CurrentIdentity != 99)
                    throw new InvalidDataException("Public SS16-8 descending cursor skip did not cross terminal shelves.");
            }
            using (LibraDexRangeReader<Guid, long> ascendingReader = guidIndex.OpenRangeReader(key, key, QueryDirection.Ascending))
            {
                if (!ascendingReader.MoveNext() || ascendingReader.CurrentIdentity != 1 ||
                    ascendingReader.Skip(600) != 600 || !ascendingReader.MoveNext() || ascendingReader.CurrentIdentity != 602)
                    throw new InvalidDataException("Public SS16-8 ascending cursor skip did not cross terminal shelves.");
            }
            guidIndex.Delete(key, 700);
            AssertDescendingIds(guidIndex, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { key }, TakeLimit: 1,
                Direction: QueryDirection.Descending), 699);
            ValidateGenericInsert(guidIndex.Insert(key, 700), "descending public SS16-8 terminal reinsert");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss168-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<Guid, long> fileIndex = persisted.Indexes.IndexSet("descending").Define("guid168file").GuidKeys<long>().Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(key, 10), "descending SS16-8 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(key, 30), "descending SS16-8 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(key, 20), "descending SS16-8 file insert 20");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<Guid, long> fileIndex = reopened.Indexes.IndexSet("descending").Define("guid168file").GuidKeys<long>().Open();
                    Scalar16Scalar8Profile reopenedProfile = fileIndex.GetScalar16Scalar8Profile();
                    LibraDexGenericScalarCodec<Guid>.Encode16(key, out ulong encodedHigh, out ulong encodedLow);
                    Scalar16Scalar8RoutePathTarget reopenedPath = reopened.Session.WalkScalar16Scalar8RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedHigh, encodedLow, maxRouterHops: 16);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Scalar16Scalar8RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened SS16-8 did not retain its descending shelf profile.");
                    Scalar16Scalar8ReadOnly reopenedShelf = new(
                        reopened.Session.ReadScalar16Scalar8ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending ||
                        reopenedShelf.ReadIdentityAt(0) != LibraDexGenericScalarCodec<long>.Encode8(30L) ||
                        reopenedShelf.ReadIdentityAt(1) != LibraDexGenericScalarCodec<long>.Encode8(20L))
                        throw new InvalidDataException("Reopened SS16-8 shelf was not physically descending.");
                    AssertDescendingIds(fileIndex, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { key }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), 30, 20, 10);
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        if (verifySs816Shelf)
        {
            using LibraDexIndex<long, UInt128> wide = catalog.Indexes.IndexSet("descending").Define("wideIdentity").Int64Keys<UInt128>().Create(options: descending);
            for (ulong identity = 1; identity <= 700; identity++)
                ValidateGenericInsert(wide.Insert(1, (UInt128)identity), "descending public SS8-16 terminal insert");
            ValidateGenericInsert(wide.Insert(2, (UInt128)900), "descending public SS8-16 upper-key insert");
            Scalar8Scalar16Profile publicProfile = wide.GetScalar8Scalar16Profile();
            ulong encodedKey = LibraDexGenericScalarCodec<long>.Encode8(1);
            Scalar8Scalar16RoutePathTarget path = catalog.Session.WalkScalar8Scalar16RoutePathTarget(
                wide.RootRouterOffset, encodedKey, maxRouterHops: 16);
            if (!publicProfile.Descending || path.Target.Kind != Scalar8Scalar16RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"Public SS8-16 descending profile did not produce a terminal route: {path.Target.Kind}.");
            byte[] rootBytes = catalog.Session.ReadTerminalIdentityRootBytes(path.Target.Offset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            byte[] head = catalog.Session.ReadTerminalVarIdentityShelfBytes(headOffset, publicProfile.ShelfExtentSize);
            try
            {
                ReadOnlySpan<byte> first = TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0);
                if (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first) != 0 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first.Slice(sizeof(ulong))) != 700 ||
                    TerminalVarIdentityShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("Public SS8-16 descending terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            using (Scalar8Scalar16RangeReader reader = catalog.Session.OpenScalar8Scalar16RangeReader(
                wide.RootRouterOffset, publicProfile, encodedKey, encodedKey, QueryDirection.Descending, maxRouterHops: 16))
            {
                if (!reader.TryReadPreviousEncodedTuple(out ulong key, out ulong high, out ulong low) ||
                    key != encodedKey || high != 0 || low != 700)
                    throw new InvalidDataException("SS8-16 descending terminal first tuple failed.");
                object? loadedShelves = typeof(Scalar8Scalar16RangeReader)
                    .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(reader);
                if (loadedShelves is not int loadedCount || loadedCount != 1)
                    throw new InvalidDataException("SS8-16 descending first result loaded more than one terminal shelf.");
            }
            LibraDexConditionEndCondition natural = LibraDexCondition.ForGroup("descending")
                .Index("wideIdentity").AsInt64.Between(1, 2).EndCondition;
            UInt128[] naturalIds = catalog.Indexes.IndexSet("descending").GetIdentities<UInt128>(
                natural, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalIds.SequenceEqual(new UInt128[] { 900, 700, 699, 698 }))
                throw new InvalidDataException("Public SS8-16 natural descending condition order failed.");
            using (LibraDexRangeReader<long, UInt128> reader = wide.OpenRangeReader(1, 2, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)900 ||
                    !reader.MoveNext() || reader.CurrentIdentity != (UInt128)700 ||
                    reader.Skip(600) != 600 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)99)
                    throw new InvalidDataException("Public SS8-16 descending cursor first rows or cross-shelf skip failed.");
            }
            using (LibraDexRangeReader<long, UInt128> reader = wide.OpenRangeReader(1, 1, QueryDirection.Ascending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1 ||
                    reader.Skip(600) != 600 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)602)
                    throw new InvalidDataException("Public SS8-16 ascending cursor did not reverse the terminal chain.");
            }
            UInt128[] bulk = new UInt128[701];
            LibraDexGenericRangeReadResult bulkResult = wide.ReadRange(1, 2, bulk);
            if (bulkResult.IdentityCount != 701 || bulk[0] != (UInt128)900 ||
                bulk[1] != (UInt128)700 || bulk[^1] != (UInt128)1)
                throw new InvalidDataException("Public SS8-16 descending bulk range did not preserve physical order.");
            wide.Delete(1, (UInt128)700);
            using (LibraDexRangeReader<long, UInt128> reader = wide.OpenRangeReader(1, 1, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)699)
                    throw new InvalidDataException("Public SS8-16 terminal head delete failed.");
            }
            ValidateGenericInsert(wide.Insert(1, (UInt128)700), "descending public SS8-16 terminal reinsert");
            UInt128 highLaneIdentity = ((UInt128)1 << 64) + 5;
            ValidateGenericInsert(wide.Insert(1, highLaneIdentity), "descending public SS8-16 high-lane identity insert");
            using (LibraDexRangeReader<long, UInt128> reader = wide.OpenRangeReader(1, 1, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != highLaneIdentity ||
                    !reader.MoveNext() || reader.CurrentIdentity != (UInt128)700)
                    throw new InvalidDataException("Public SS8-16 descending high-lane identity ordering failed.");
            }
            wide.Delete(1, highLaneIdentity);

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss816-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<long, UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide816file")
                        .Int64Keys<UInt128>().Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(1, (UInt128)10), "descending SS8-16 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(1, (UInt128)30), "descending SS8-16 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(1, (UInt128)20), "descending SS8-16 file insert 20");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<long, UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide816file")
                        .Int64Keys<UInt128>().Open();
                    Scalar8Scalar16Profile reopenedProfile = fileIndex.GetScalar8Scalar16Profile();
                    Scalar8Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkScalar8Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedKey, maxRouterHops: 16);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Scalar8Scalar16RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened SS8-16 did not retain its descending shelf profile.");
                    Scalar8Scalar16ReadOnly reopenedShelf = new(
                        reopened.Session.ReadScalar8Scalar16ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending || reopenedShelf.ReadIdentityLowAt(0) != 30 ||
                        reopenedShelf.ReadIdentityLowAt(1) != 20 || reopenedShelf.ReadIdentityLowAt(2) != 10)
                        throw new InvalidDataException("Reopened SS8-16 shelf was not physically descending.");
                    using LibraDexRangeReader<long, UInt128> reader = fileIndex.OpenRangeReader(1, 1, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)30 ||
                        !reader.MoveNext() || reader.CurrentIdentity != (UInt128)20)
                        throw new InvalidDataException("Reopened SS8-16 descending cursor order failed.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
            string terminalReopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss816-descending-terminal-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(terminalReopenPath))
                {
                    using LibraDexIndex<long, UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide816terminal")
                        .Int64Keys<UInt128>().Create(options: descending);
                    for (ulong identity = 1; identity <= 1300; identity++)
                        ValidateGenericInsert(fileIndex.Insert(1, (UInt128)identity), "descending SS8-16 file terminal insert");
                }
                using (Catalog reopened = Catalog.Open(terminalReopenPath))
                {
                    using LibraDexIndex<long, UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide816terminal")
                        .Int64Keys<UInt128>().Open();
                    Scalar8Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkScalar8Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedKey, maxRouterHops: 16);
                    if (reopenedPath.Target.Kind != Scalar8Scalar16RouteTargetKind.TerminalIdentityRoot ||
                        reopened.Session.ReadTerminalIdentityRootBytes(reopenedPath.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened SS8-16 terminal did not retain physical descending order.");
                    using LibraDexRangeReader<long, UInt128> reader = fileIndex.OpenRangeReader(1, 1, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1300 ||
                        !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1299)
                        throw new InvalidDataException("Reopened SS8-16 terminal did not stream highest identities first.");
                }
            }
            finally
            {
                if (File.Exists(terminalReopenPath))
                    File.Delete(terminalReopenPath);
            }
            LibraDexIdentityMutationResult removed = ((IIdentityPrimitiveMutator)wide).DeleteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Between, new object?[] { 1L, 1L }));
            if (removed.ChangedCount != 700)
                throw new InvalidDataException($"Public SS8-16 descending terminal range deletion removed {removed.ChangedCount} rows instead of 700.");
            using (LibraDexRangeReader<long, UInt128> reader = wide.OpenRangeReader(1, 2, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)900 || reader.MoveNext())
                    throw new InvalidDataException("Public SS8-16 descending range deletion retained equal-key identities.");
            }
        }

        if (verifySs1616Shelf)
        {
            Guid key = Guid.Parse("00000000-0000-0000-0000-000000000007");
            Guid upperKey = Guid.Parse("00000000-0000-0000-0000-000000000008");
            using LibraDexIndex<Guid, UInt128> wide = catalog.Indexes.IndexSet("descending").Define("wide1616").GuidKeys<UInt128>().Create(options: descending);
            for (ulong identity = 1; identity <= 700; identity++)
                ValidateGenericInsert(wide.Insert(key, (UInt128)identity), "descending public SS16-16 terminal insert");
            ValidateGenericInsert(wide.Insert(upperKey, (UInt128)900), "descending public SS16-16 upper-key insert");
            Scalar16Scalar16Profile publicProfile = wide.GetScalar16Scalar16Profile();
            LibraDexGenericScalarCodec<Guid>.Encode16(key, out ulong encodedKeyHigh, out ulong encodedKeyLow);
            Scalar16Scalar16RoutePathTarget path = catalog.Session.WalkScalar16Scalar16RoutePathTarget(
                wide.RootRouterOffset, encodedKeyHigh, encodedKeyLow, maxRouterHops: 17);
            if (!publicProfile.Descending || path.Target.Kind != Scalar16Scalar16RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"Public SS16-16 descending profile did not produce a terminal route: {path.Target.Kind}.");
            byte[] rootBytes = catalog.Session.ReadTerminalIdentityRootBytes(path.Target.Offset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            byte[] head = catalog.Session.ReadTerminalVarIdentityShelfBytes(headOffset, publicProfile.ShelfExtentSize);
            try
            {
                ReadOnlySpan<byte> first = TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0);
                if (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first) != 0 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first.Slice(sizeof(ulong))) != 700 ||
                    TerminalVarIdentityShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("Public SS16-16 descending terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            using (Scalar16Scalar16RangeReader allRows = catalog.Session.OpenScalar16Scalar16RangeReader(
                wide.RootRouterOffset, publicProfile, encodedKeyHigh, encodedKeyLow, encodedKeyHigh, encodedKeyLow,
                QueryDirection.Descending, maxRouterHops: 17))
            {
                for (ulong expected = 700; expected > 0; expected--)
                {
                    if (!allRows.TryReadPreviousEncodedTuple(out _, out _, out _, out ulong actual) || actual != expected)
                        throw new InvalidDataException($"SS16-16 descending terminal sequence failed at expected={expected}, actual={actual}.");
                }
                if (allRows.MovePrevious())
                    throw new InvalidDataException("SS16-16 descending terminal sequence returned extra rows.");
            }
            using (Scalar16Scalar16RangeReader reader = catalog.Session.OpenScalar16Scalar16RangeReader(
                wide.RootRouterOffset, publicProfile, encodedKeyHigh, encodedKeyLow, encodedKeyHigh, encodedKeyLow,
                QueryDirection.Descending, maxRouterHops: 17))
            {
                if (!reader.TryReadPreviousEncodedTuple(out ulong firstKeyHigh, out ulong firstKeyLow,
                    out ulong firstIdentityHigh, out ulong firstIdentityLow) ||
                    firstKeyHigh != encodedKeyHigh || firstKeyLow != encodedKeyLow ||
                    firstIdentityHigh != 0 || firstIdentityLow != 700)
                    throw new InvalidDataException("SS16-16 descending terminal first tuple failed.");
                object? loadedShelves = typeof(Scalar16Scalar16RangeReader)
                    .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(reader);
                if (loadedShelves is not int loadedCount || loadedCount != 1)
                    throw new InvalidDataException("SS16-16 descending first result loaded more than one terminal shelf.");
                int directSkipped = reader.SkipPrevious(600);
                ulong afterSkip = 0;
                if (directSkipped != 600 || !reader.TryReadPreviousEncodedTuple(out _, out _, out _, out afterSkip) || afterSkip != 99)
                    throw new InvalidDataException($"SS16-16 descending direct terminal skip failed: skipped={directSkipped}, identity={afterSkip}.");
            }
            LibraDexConditionEndCondition natural = LibraDexCondition.ForGroup("descending")
                .Index("wide1616").AsGuid.Between(key, upperKey).EndCondition;
            UInt128[] naturalIds = catalog.Indexes.IndexSet("descending").GetIdentities<UInt128>(
                natural, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalIds.SequenceEqual(new UInt128[] { 900, 700, 699, 698 }))
                throw new InvalidDataException("Public SS16-16 natural descending condition order failed.");
            using (LibraDexRangeReader<Guid, UInt128> reader = wide.OpenRangeReader(key, upperKey, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)900)
                    throw new InvalidDataException("Public SS16-16 descending cursor did not start at the upper key.");
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)700)
                    throw new InvalidDataException("Public SS16-16 descending cursor did not enter the terminal run at its highest identity.");
                int skipped = reader.Skip(600);
                if (skipped != 600 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)99)
                    throw new InvalidDataException($"Public SS16-16 descending cursor cross-shelf skip failed: skipped={skipped}, identity={reader.CurrentIdentity}.");
            }
            using (LibraDexRangeReader<Guid, UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Ascending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1 ||
                    reader.Skip(600) != 600 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)602)
                    throw new InvalidDataException("Public SS16-16 ascending cursor did not reverse the terminal chain.");
            }
            UInt128[] bulk = new UInt128[701];
            LibraDexGenericRangeReadResult bulkResult = wide.ReadRange(key, upperKey, bulk);
            if (bulkResult.IdentityCount != 701 || bulk[0] != (UInt128)900 ||
                bulk[1] != (UInt128)700 || bulk[^1] != (UInt128)1)
                throw new InvalidDataException("Public SS16-16 descending bulk range did not preserve physical order.");
            wide.Delete(key, (UInt128)700);
            using (LibraDexRangeReader<Guid, UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)699)
                    throw new InvalidDataException("Public SS16-16 terminal head delete failed.");
            }
            ValidateGenericInsert(wide.Insert(key, (UInt128)700), "descending public SS16-16 terminal reinsert");
            UInt128 highLaneIdentity = ((UInt128)1 << 64) + 5;
            ValidateGenericInsert(wide.Insert(key, highLaneIdentity), "descending public SS16-16 high-lane identity insert");
            using (LibraDexRangeReader<Guid, UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != highLaneIdentity ||
                    !reader.MoveNext() || reader.CurrentIdentity != (UInt128)700)
                    throw new InvalidDataException("Public SS16-16 descending high-lane identity ordering failed.");
            }
            wide.Delete(key, highLaneIdentity);

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss1616-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<Guid, UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide1616file")
                        .GuidKeys<UInt128>().Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)10), "descending SS16-16 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)30), "descending SS16-16 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)20), "descending SS16-16 file insert 20");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<Guid, UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide1616file")
                        .GuidKeys<UInt128>().Open();
                    Scalar16Scalar16Profile reopenedProfile = fileIndex.GetScalar16Scalar16Profile();
                    Scalar16Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkScalar16Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedKeyHigh, encodedKeyLow, maxRouterHops: 17);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Scalar16Scalar16RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened SS16-16 did not retain its descending shelf profile.");
                    Scalar16Scalar16ReadOnly reopenedShelf = new(
                        reopened.Session.ReadScalar16Scalar16ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending || reopenedShelf.ReadIdentityLowAt(0) != 30 ||
                        reopenedShelf.ReadIdentityLowAt(1) != 20 || reopenedShelf.ReadIdentityLowAt(2) != 10)
                        throw new InvalidDataException("Reopened SS16-16 shelf was not physically descending.");
                    using LibraDexRangeReader<Guid, UInt128> reader = fileIndex.OpenRangeReader(key, key, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)30 ||
                        !reader.MoveNext() || reader.CurrentIdentity != (UInt128)20)
                        throw new InvalidDataException("Reopened SS16-16 descending cursor order failed.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }

            string terminalReopenPath = Path.GetFullPath(Path.Combine("artifacts", $"ss1616-descending-terminal-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(terminalReopenPath))
                {
                    using LibraDexIndex<Guid, UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide1616terminal")
                        .GuidKeys<UInt128>().Create(options: descending);
                    for (ulong identity = 1; identity <= 1300; identity++)
                        ValidateGenericInsert(fileIndex.Insert(key, (UInt128)identity), "descending SS16-16 file terminal insert");
                }
                using (Catalog reopened = Catalog.Open(terminalReopenPath))
                {
                    using LibraDexIndex<Guid, UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide1616terminal")
                        .GuidKeys<UInt128>().Open();
                    Scalar16Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkScalar16Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, encodedKeyHigh, encodedKeyLow, maxRouterHops: 17);
                    if (reopenedPath.Target.Kind != Scalar16Scalar16RouteTargetKind.TerminalIdentityRoot ||
                        reopened.Session.ReadTerminalIdentityRootBytes(reopenedPath.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened SS16-16 terminal did not retain physical descending order.");
                    using LibraDexRangeReader<Guid, UInt128> reader = fileIndex.OpenRangeReader(key, key, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1300 ||
                        !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1299)
                        throw new InvalidDataException("Reopened SS16-16 terminal did not stream highest identities first.");
                }
            }
            finally
            {
                if (File.Exists(terminalReopenPath))
                    File.Delete(terminalReopenPath);
            }

            LibraDexIdentityMutationResult removed = ((IIdentityPrimitiveMutator)wide).DeleteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Between, new object?[] { key, key }));
            if (removed.ChangedCount != 700)
                throw new InvalidDataException($"Public SS16-16 descending terminal range deletion removed {removed.ChangedCount} rows instead of 700.");
        }

        if (verifyFs328Shelf)
        {
            byte[] key = new byte[32];
            key[^1] = 7;
            byte[] upperKey = new byte[32];
            upperKey[^1] = 8;
            using LibraDexIndex<byte[], long> wide = catalog.Indexes.IndexSet("descending").Define("wide328").Blob
                .Scalar<long>(LibraDexScalarWidth.Bytes32).Create(options: descending);
            for (long identity = 1; identity <= 1300; identity++)
                ValidateGenericInsert(wide.Insert(key, identity), "descending public FS32-8 terminal insert");
            ValidateGenericInsert(wide.Insert(upperKey, 900), "descending public FS32-8 upper-key insert");
            Fixed32Scalar8Profile publicProfile = wide.GetFixed32Scalar8Profile();
            Fixed32Scalar8RoutePathTarget path = catalog.Session.WalkFixed32Scalar8RoutePathTarget(
                wide.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
            if (!publicProfile.Descending || path.Target.Kind != Fixed32Scalar8RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"Public FS32-8 descending profile did not produce a terminal route: {path.Target.Kind}.");
            byte[] rootBytes = catalog.Session.ReadTerminalIdentityRootBytes(path.Target.Offset);
            ulong encodedHighest = LibraDexGenericScalarCodec<long>.Encode8(1300L);
            ulong encodedAfterSkip = LibraDexGenericScalarCodec<long>.Encode8(99L);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            byte[] head = catalog.Session.ReadTerminalIdentity8ShelfBytes(headOffset, publicProfile.ShelfExtentSize);
            try
            {
                if (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    TerminalIdentity8ShelfLayout.ReadIdentity(head, 0) != encodedHighest ||
                    TerminalIdentity8ShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException($"Public FS32-8 descending terminal head was not physically highest-first: direction={rootBytes[TerminalIdentityRootLayout.SortDirectionOffset]}, head={TerminalIdentity8ShelfLayout.ReadIdentity(head, 0)}, next={TerminalIdentity8ShelfLayout.ReadNextShelfOffset(head)}.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            using (Fixed32Scalar8RangeReader reader = catalog.Session.OpenFixed32Scalar8RangeReader(
                wide.RootRouterOffset, publicProfile, 0, 0, 0, 7, 0, 0, 0, 7, QueryDirection.Descending, maxRouterHops: 33))
            {
                if (!reader.TryReadPreviousEncodedTuple(out _, out _, out _, out ulong actualKey, out ulong actualIdentity) ||
                    actualKey != 7 || actualIdentity != encodedHighest)
                    throw new InvalidDataException("FS32-8 descending terminal first tuple failed.");
                object? loadedShelves = typeof(Fixed32Scalar8RangeReader)
                    .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(reader);
                if (loadedShelves is not int loadedCount || loadedCount != 1)
                    throw new InvalidDataException("FS32-8 descending first result loaded more than one terminal shelf.");
                if (reader.SkipPrevious(1200) != 1200 || !reader.TryReadPreviousEncodedTuple(out _, out _, out _, out _, out actualIdentity) || actualIdentity != encodedAfterSkip)
                    throw new InvalidDataException("FS32-8 descending terminal skip failed.");
            }
            using (LibraDexRangeReader<byte[], long> reader = wide.OpenRangeReader(key, upperKey, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != 900 ||
                    !reader.MoveNext() || reader.CurrentIdentity != 1300 ||
                    reader.Skip(1200) != 1200 || !reader.MoveNext() || reader.CurrentIdentity != 99)
                    throw new InvalidDataException("Public FS32-8 descending cursor did not stream upper key then terminal head.");
            }
            using (LibraDexRangeReader<byte[], long> reader = wide.OpenRangeReader(key, key, QueryDirection.Ascending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != 1 ||
                    reader.Skip(1200) != 1200 || !reader.MoveNext() || reader.CurrentIdentity != 1202)
                    throw new InvalidDataException("Public FS32-8 explicit ascending cursor did not reverse the terminal chain.");
            }
            LibraDexConditionEndCondition natural = LibraDexCondition.ForGroup("descending")
                .Index("wide328").AsBinary.Between(key, upperKey).EndCondition;
            long[] naturalIds = catalog.Indexes.IndexSet("descending").GetIdentities<long>(
                natural, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalIds.SequenceEqual(new long[] { 900, 1300, 1299, 1298 }))
                throw new InvalidDataException("Public FS32-8 natural condition order failed.");
            long[] bulk = new long[1301];
            LibraDexGenericRangeReadResult bulkResult = wide.ReadRange(key, upperKey, bulk);
            if (bulkResult.IdentityCount != 1301 || bulk[0] != 900 || bulk[1] != 1300 || bulk[^1] != 1)
                throw new InvalidDataException("Public FS32-8 descending bulk range did not preserve physical order.");
            wide.Delete(key, 1300);
            using (LibraDexRangeReader<byte[], long> reader = wide.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != 1299)
                    throw new InvalidDataException("Public FS32-8 descending terminal head delete failed.");
            }
            ValidateGenericInsert(wide.Insert(key, 1300), "descending public FS32-8 terminal reinsert");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fs328-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<byte[], long> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide328file").Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32).Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(key, 10), "descending FS32-8 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(key, 30), "descending FS32-8 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(key, 20), "descending FS32-8 file insert 20");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<byte[], long> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide328file").Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32).Open();
                    Fixed32Scalar8Profile reopenedProfile = fileIndex.GetFixed32Scalar8Profile();
                    Fixed32Scalar8RoutePathTarget reopenedPath = reopened.Session.WalkFixed32Scalar8RoutePathTarget(
                        fileIndex.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Fixed32Scalar8RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened FS32-8 did not retain its descending shelf profile.");
                    Fixed32Scalar8ReadOnly reopenedShelf = new(
                        reopened.Session.ReadFixed32Scalar8ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending ||
                        LibraDexGenericScalarCodec<long>.Decode8(reopenedShelf.ReadIdentityAt(0)) != 30 ||
                        LibraDexGenericScalarCodec<long>.Decode8(reopenedShelf.ReadIdentityAt(1)) != 20 ||
                        LibraDexGenericScalarCodec<long>.Decode8(reopenedShelf.ReadIdentityAt(2)) != 10)
                        throw new InvalidDataException("Reopened FS32-8 shelf was not physically descending.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }

            string terminalReopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fs328-descending-terminal-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(terminalReopenPath))
                {
                    using LibraDexIndex<byte[], long> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide328terminal").Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32).Create(options: descending);
                    for (long identity = 1; identity <= 1300; identity++)
                        ValidateGenericInsert(fileIndex.Insert(key, identity), "descending FS32-8 file terminal insert");
                }
                using (Catalog reopened = Catalog.Open(terminalReopenPath))
                {
                    using LibraDexIndex<byte[], long> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide328terminal").Blob
                        .Scalar<long>(LibraDexScalarWidth.Bytes32).Open();
                    Fixed32Scalar8RoutePathTarget reopenedPath = reopened.Session.WalkFixed32Scalar8RoutePathTarget(
                        fileIndex.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
                    if (reopenedPath.Target.Kind != Fixed32Scalar8RouteTargetKind.TerminalIdentityRoot ||
                        reopened.Session.ReadTerminalIdentityRootBytes(reopenedPath.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened FS32-8 terminal did not retain physical descending order.");
                    using LibraDexRangeReader<byte[], long> reader = fileIndex.OpenRangeReader(key, key, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != 1300 ||
                        !reader.MoveNext() || reader.CurrentIdentity != 1299)
                        throw new InvalidDataException("Reopened FS32-8 terminal did not stream highest identities first.");
                }
            }
            finally
            {
                if (File.Exists(terminalReopenPath))
                    File.Delete(terminalReopenPath);
            }

            LibraDexIdentityMutationResult removed = ((IIdentityPrimitiveMutator)wide).DeleteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Between, new object?[] { key, key }));
            if (removed.ChangedCount != 1300)
                throw new InvalidDataException($"Public FS32-8 descending terminal range deletion removed {removed.ChangedCount} rows instead of 1300.");
            using (LibraDexRangeReader<byte[], long> reader = wide.OpenRangeReader(key, upperKey, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != 900 || reader.MoveNext())
                    throw new InvalidDataException("Public FS32-8 descending terminal range deletion retained equal-key identities.");
            }
        }

        if (verifyFs3216Shelf)
        {
            byte[] key = new byte[32];
            key[^1] = 7;
            byte[] upperKey = new byte[32];
            upperKey[^1] = 8;
            using LibraDexIndex<byte[], UInt128> wide = catalog.Indexes.IndexSet("descending").Define("wide3216").Blob
                .Scalar<UInt128>(LibraDexScalarWidth.Bytes32).Create(options: descending);
            for (ulong identity = 1; identity <= 1300; identity++)
                ValidateGenericInsert(wide.Insert(key, (UInt128)identity), "descending public FS32-16 terminal insert");
            ValidateGenericInsert(wide.Insert(upperKey, (UInt128)900), "descending public FS32-16 upper-key insert");
            Fixed32Scalar16Profile publicProfile = wide.GetFixed32Scalar16Profile();
            Fixed32Scalar16RoutePathTarget path = catalog.Session.WalkFixed32Scalar16RoutePathTarget(
                wide.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
            if (!publicProfile.Descending || path.Target.Kind != Fixed32Scalar16RouteTargetKind.TerminalIdentityRoot)
                throw new InvalidDataException($"Public FS32-16 descending profile did not produce a terminal route: {path.Target.Kind}.");
            byte[] rootBytes = catalog.Session.ReadTerminalIdentityRootBytes(path.Target.Offset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(rootBytes);
            byte[] head = catalog.Session.ReadTerminalVarIdentityShelfBytes(headOffset, publicProfile.ShelfExtentSize);
            try
            {
                ReadOnlySpan<byte> first = TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0);
                if (rootBytes[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first) != 0 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(first.Slice(sizeof(ulong))) != 1300 ||
                    TerminalVarIdentityShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("Public FS32-16 terminal head was not physically highest-first across shelves.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            using (Fixed32Scalar16RangeReader reader = catalog.Session.OpenFixed32Scalar16RangeReader(
                wide.RootRouterOffset, publicProfile, 0, 0, 0, 7, 0, 0, 0, 7, QueryDirection.Descending, maxRouterHops: 33))
            {
                if (!reader.TryReadPreviousEncodedTuple(out _, out _, out _, out ulong actualKey, out ulong actualHigh, out ulong actualLow) ||
                    actualKey != 7 || actualHigh != 0 || actualLow != 1300)
                    throw new InvalidDataException("FS32-16 descending terminal first tuple failed.");
                object? loadedShelves = typeof(Fixed32Scalar16RangeReader)
                    .GetField("shelfCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(reader);
                if (loadedShelves is not int loadedCount || loadedCount != 1)
                    throw new InvalidDataException("FS32-16 descending first result loaded more than one terminal shelf.");
                if (reader.SkipPrevious(1200) != 1200 ||
                    !reader.TryReadPreviousEncodedTuple(out _, out _, out _, out _, out actualHigh, out actualLow) ||
                    actualHigh != 0 || actualLow != 99)
                    throw new InvalidDataException("FS32-16 descending terminal skip failed.");
            }
            using (LibraDexRangeReader<byte[], UInt128> reader = wide.OpenRangeReader(key, upperKey, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)900 ||
                    !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1300 ||
                    reader.Skip(1200) != 1200 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)99)
                    throw new InvalidDataException("Public FS32-16 descending cursor order failed.");
            }
            using (LibraDexRangeReader<byte[], UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Ascending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1 ||
                    reader.Skip(1200) != 1200 || !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1202)
                    throw new InvalidDataException("Public FS32-16 explicit ascending cursor failed.");
            }
            LibraDexConditionEndCondition natural = LibraDexCondition.ForGroup("descending")
                .Index("wide3216").AsBinary.Between(key, upperKey).EndCondition;
            UInt128[] naturalIds = catalog.Indexes.IndexSet("descending").GetIdentities<UInt128>(
                natural, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
            if (!naturalIds.SequenceEqual(new UInt128[] { 900, 1300, 1299, 1298 }))
                throw new InvalidDataException("Public FS32-16 natural descending condition order failed.");
            UInt128[] bulk = new UInt128[1301];
            LibraDexGenericRangeReadResult bulkResult = wide.ReadRange(key, upperKey, bulk);
            if (bulkResult.IdentityCount != 1301 || bulk[0] != (UInt128)900 ||
                bulk[1] != (UInt128)1300 || bulk[^1] != (UInt128)1)
                throw new InvalidDataException("Public FS32-16 descending bulk range order failed.");
            wide.Delete(key, (UInt128)1300);
            using (LibraDexRangeReader<byte[], UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1299)
                    throw new InvalidDataException("Public FS32-16 descending terminal head delete failed.");
            }
            ValidateGenericInsert(wide.Insert(key, (UInt128)1300), "descending public FS32-16 terminal reinsert");
            UInt128 highLaneIdentity = ((UInt128)1 << 64) + 5;
            ValidateGenericInsert(wide.Insert(key, highLaneIdentity), "descending public FS32-16 high-lane insert");
            using (LibraDexRangeReader<byte[], UInt128> reader = wide.OpenRangeReader(key, key, QueryDirection.Descending))
            {
                if (!reader.MoveNext() || reader.CurrentIdentity != highLaneIdentity ||
                    !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1300)
                    throw new InvalidDataException("Public FS32-16 descending high-lane identity order failed.");
            }
            wide.Delete(key, highLaneIdentity);

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fs3216-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexIndex<byte[], UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide3216file").Blob
                        .Scalar<UInt128>(LibraDexScalarWidth.Bytes32).Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)10), "descending FS32-16 file insert 10");
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)30), "descending FS32-16 file insert 30");
                    ValidateGenericInsert(fileIndex.Insert(key, (UInt128)20), "descending FS32-16 file insert 20");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexIndex<byte[], UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide3216file").Blob
                        .Scalar<UInt128>(LibraDexScalarWidth.Bytes32).Open();
                    Fixed32Scalar16Profile reopenedProfile = fileIndex.GetFixed32Scalar16Profile();
                    Fixed32Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkFixed32Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
                    if (!reopenedProfile.Descending || reopenedPath.Target.Kind != Fixed32Scalar16RouteTargetKind.Shelf)
                        throw new InvalidDataException("Reopened FS32-16 did not retain its descending shelf profile.");
                    Fixed32Scalar16ReadOnly reopenedShelf = new(
                        reopened.Session.ReadFixed32Scalar16ShelfBytesForBatch(reopenedPath.Target.Offset, reopenedProfile), reopenedProfile);
                    if (!reopenedShelf.IsDescending || reopenedShelf.ReadIdentityHighAt(0) != 0 ||
                        reopenedShelf.ReadIdentityLowAt(0) != 30 || reopenedShelf.ReadIdentityLowAt(1) != 20 ||
                        reopenedShelf.ReadIdentityLowAt(2) != 10)
                        throw new InvalidDataException("Reopened FS32-16 shelf was not physically descending.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }

            string terminalReopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fs3216-descending-terminal-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(terminalReopenPath))
                {
                    using LibraDexIndex<byte[], UInt128> fileIndex = persisted.Indexes.IndexSet("descending").Define("wide3216terminal").Blob
                        .Scalar<UInt128>(LibraDexScalarWidth.Bytes32).Create(options: descending);
                    for (ulong identity = 1; identity <= 1300; identity++)
                        ValidateGenericInsert(fileIndex.Insert(key, (UInt128)identity), "descending FS32-16 file terminal insert");
                }
                using (Catalog reopened = Catalog.Open(terminalReopenPath))
                {
                    using LibraDexIndex<byte[], UInt128> fileIndex = reopened.Indexes.IndexSet("descending").Define("wide3216terminal").Blob
                        .Scalar<UInt128>(LibraDexScalarWidth.Bytes32).Open();
                    Fixed32Scalar16RoutePathTarget reopenedPath = reopened.Session.WalkFixed32Scalar16RoutePathTarget(
                        fileIndex.RootRouterOffset, 0, 0, 0, 7, maxRouterHops: 33);
                    if (reopenedPath.Target.Kind != Fixed32Scalar16RouteTargetKind.TerminalIdentityRoot ||
                        reopened.Session.ReadTerminalIdentityRootBytes(reopenedPath.Target.Offset)[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened FS32-16 terminal did not retain physical descending order.");
                    using LibraDexRangeReader<byte[], UInt128> reader = fileIndex.OpenRangeReader(key, key, QueryDirection.Descending);
                    if (!reader.MoveNext() || reader.CurrentIdentity != (UInt128)1300 ||
                        !reader.MoveNext() || reader.CurrentIdentity != (UInt128)1299)
                        throw new InvalidDataException("Reopened FS32-16 terminal did not stream highest identities first.");
                }
            }
            finally
            {
                if (File.Exists(terminalReopenPath))
                    File.Delete(terminalReopenPath);
            }

            LibraDexIdentityMutationResult removed = ((IIdentityPrimitiveMutator)wide).DeleteIdentityPrimitive(
                new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.Between, new object?[] { key, key }));
            if (removed.ChangedCount != 1300)
                throw new InvalidDataException($"Public FS32-16 descending terminal range deletion removed {removed.ChangedCount} instead of 1300.");
        }

        using LibraDexStringScalar8Index text = catalog.Indexes.IndexSet("descending").Define("name").String.Create(stringKeys: StringKeys.Exact, sortOrder: LibraDexIndexSortOrder.Descending);
        for (ulong identity = 1; identity <= 128; identity++)
            ValidateGenericInsert(text.Insert("same", identity), "descending string equal-key insert");
        ValidateGenericInsert(text.Insert("top", 900), "descending string high-key insert");
        AssertDescendingIds(text, all, 900, 128, 127, 126);
        AssertDescendingIds(text, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { "same" }, TakeLimit: 3, Direction: QueryDirection.Descending), 128, 127, 126);
        ValidateGenericInsert(text.Insert(null, 10), "descending string null-key insert");
        ValidateGenericInsert(text.Insert(string.Empty, 20), "descending string empty-key insert");
        ulong[] stringTail = ((IIdentityPrimitiveExecutor)text).IterateIdentityPrimitive(
            new LibraDexIdentityPrimitiveRequest(LibraDexCriteriaKind.All, Array.Empty<object?>(), Direction: QueryDirection.Descending))
            .Select(value => (ulong)value).TakeLast(2).ToArray();
        if (!stringTail.SequenceEqual(new ulong[] { 20, 10 }))
            throw new InvalidDataException("Descending string key-state tail did not reverse empty and null routes.");
        ulong[] textTuplePrefix = ((IIdentityPrimitiveTupleStreamer)text).IterateTuplePrimitive(all)
            .Select(tuple => (ulong)tuple.Identity).ToArray();
        if (!textTuplePrefix.SequenceEqual(new ulong[] { 900, 128, 127, 126 }))
            throw new InvalidDataException("Descending exact-string tuple stream did not match its identity stream.");

        using LibraDexStringScalar8Index folded = catalog.Indexes.IndexSet("descending").Define("folded").String.Create(
            stringKeys: StringKeys.ExactAndFolded,
            sortOrder: LibraDexIndexSortOrder.Descending);
        for (ulong identity = 1; identity <= 128; identity++)
            ValidateGenericInsert(folded.Insert("Same", identity), "descending folded equal-key insert");
        ValidateGenericInsert(folded.Insert("Super", 900), "descending folded high-key insert");
        LibraDexConditionEndCondition foldedPrefix = LibraDexCondition.ForGroup("descending").Index("folded")
            .AsString.StartsWith("s", ignoreCase: true).EndCondition;
        ulong[] foldedNatural = catalog.Indexes.IndexSet("descending").GetIdentities<ulong>(
            foldedPrefix, deduplication: IdentityDeduplication.Preserve).Take(4).ToArray();
        if (!foldedNatural.SequenceEqual(new ulong[] { 900, 128, 127, 126 }))
            throw new InvalidDataException($"Descending folded projection returned [{string.Join(',', foldedNatural)}].");

        using LibraDexBigIntScalar8Index<long> fixedBigInt = catalog.Indexes.IndexSet("descending").Define("fixedBigInt").BigIntKeys<long>(maxBytes: 16).Create(options: descending);
        using LibraDexBigIntScalar8Index<long> variableBigInt = catalog.Indexes.IndexSet("descending").Define("variableBigInt").BigIntVarLenKeys<long>(maxBytes: 16).Create(options: descending);
        for (long identity = 1; identity <= 128; identity++)
        {
            ValidateGenericInsert(fixedBigInt.Insert(BigInteger.One, identity), "descending fixed BigInt equal-key insert");
            ValidateGenericInsert(variableBigInt.Insert(BigInteger.One, identity), "descending variable BigInt equal-key insert");
        }
        ValidateGenericInsert(fixedBigInt.Insert(new BigInteger(2), 900), "descending fixed BigInt high-key insert");
        ValidateGenericInsert(variableBigInt.Insert(new BigInteger(2), 900), "descending variable BigInt high-key insert");
        AssertDescendingIds(fixedBigInt, all, 900, 128, 127, 126);
        AssertDescendingIds(variableBigInt, all, 900, 128, 127, 126);
        LibraDexIdentityPrimitiveRequest bigIntFind = new(LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3, Direction: QueryDirection.Descending);
        AssertDescendingIds(fixedBigInt, bigIntFind, 128, 127, 126);
        AssertDescendingIds(variableBigInt, bigIntFind, 128, 127, 126);
        if (verifyFsn8Shelf)
        {
            using LibraDexBigIntScalar8Index<long> terminal = catalog.Indexes.IndexSet("descending").Define("fixedBigIntTerminal").BigIntKeys<long>(maxBytes: 512).Create(options: descending);
            FixedNScalar8Profile terminalProfile = FixedNScalar8Profile.Default64KiB(515) with { Descending = true };
            long terminalLastIdentity = terminalProfile.MaxItemCount + 8L;
            for (long identity = 1; identity <= terminalLastIdentity; identity++)
                ValidateGenericInsert(terminal.Insert(BigInteger.One, identity), "descending FSN-8 terminal insert");
            CatalogIndexInfo terminalInfo = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "fixedBigIntTerminal");
            byte[] encodedKey = LibraDexBigIntCodec.Encode(BigInteger.One, 512, LibraDexBigIntKeyStorage.FixedWidth);
            RouterSnapshot root = catalog.Session.ReadRouterSnapshot(terminalInfo.RootRouterOffset);
            long terminalOffset = catalog.Session.FindRouterTarget(root.Offset, encodedKey[root.KeyDepth]);
            byte[] terminalRoot = catalog.Session.ReadTerminalIdentityRootBytes(terminalOffset);
            long firstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(terminalRoot);
            byte[] head = catalog.Session.ReadTerminalIdentity8ShelfBytes(firstShelfOffset, terminalProfile.ShelfExtentSize);
            try
            {
                if (terminalRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    TerminalIdentity8ShelfLayout.ReadIdentity(head, 0) != LibraDexGenericScalarCodec<long>.Encode8(terminalLastIdentity))
                    throw new InvalidDataException("FSN-8 descending terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            AssertDescendingIds(terminal, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Descending), terminalLastIdentity, terminalLastIdentity - 1, terminalLastIdentity - 2);
            AssertDescendingIds(terminal, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Ascending), 1, 2, 3);

            using LibraDexBigIntScalar8Index<long> split = catalog.Indexes.IndexSet("descending").Define("fixedBigIntSplit").BigIntKeys<long>(maxBytes: 512).Create(options: descending);
            for (long identity = 1; identity <= 64; identity++)
                ValidateGenericInsert(split.Insert(new BigInteger(2), identity + 1000), "descending FSN-8 high-key split insert");
            for (long identity = 1; identity <= 64; identity++)
                ValidateGenericInsert(split.Insert(BigInteger.One, identity), "descending FSN-8 low-key split insert");
            AssertDescendingIds(split, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 3,
                Direction: QueryDirection.Descending), 1064, 1063, 1062);
            AssertDescendingIds(split, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Descending), 64, 63, 62);
            AssertDescendingIds(split, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Between, new object?[] { BigInteger.One, new BigInteger(2) }, TakeLimit: 3,
                Direction: QueryDirection.Ascending), 1, 2, 3);
            IReadOnlyList<long> bulk = split.GetIdentities(BigInteger.One, new BigInteger(2));
            if (bulk.Count != 128 || bulk[0] != 1064 || bulk[63] != 1001 || bulk[64] != 64 || bulk[bulk.Count - 1] != 1)
                throw new InvalidDataException("FSN-8 descending bulk range did not preserve routed physical tuple order.");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fsn8-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexBigIntScalar8Index<long> ordinary = persisted.Indexes.IndexSet("descending").Define("ordinary").BigIntKeys<long>(maxBytes: 512).Create(options: descending);
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 1), "descending FSN-8 ordinary file insert 1");
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 3), "descending FSN-8 ordinary file insert 3");
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 2), "descending FSN-8 ordinary file insert 2");
                    using LibraDexBigIntScalar8Index<long> routedTerminal = persisted.Indexes.IndexSet("descending").Define("terminal").BigIntKeys<long>(maxBytes: 512).Create(options: descending);
                    for (long identity = 1; identity <= terminalLastIdentity; identity++)
                        ValidateGenericInsert(routedTerminal.Insert(BigInteger.One, identity), "descending FSN-8 terminal file insert");
                }

                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexBigIntScalar8Index<long> ordinary = reopened.Indexes.IndexSet("descending").Define("ordinary").BigIntKeys<long>(maxBytes: 512).Open();
                    using LibraDexBigIntScalar8Index<long> routedTerminal = reopened.Indexes.IndexSet("descending").Define("terminal").BigIntKeys<long>(maxBytes: 512).Open();
                    AssertDescendingIds(ordinary, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), 3, 2, 1);
                    AssertDescendingIds(routedTerminal, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), terminalLastIdentity, terminalLastIdentity - 1, terminalLastIdentity - 2);
                    CatalogIndexInfo reopenedInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "terminal");
                    RouterSnapshot reopenedRouter = reopened.Session.ReadRouterSnapshot(reopenedInfo.RootRouterOffset);
                    long reopenedTerminalOffset = reopened.Session.FindRouterTarget(reopenedRouter.Offset, encodedKey[reopenedRouter.KeyDepth]);
                    byte[] reopenedRoot = reopened.Session.ReadTerminalIdentityRootBytes(reopenedTerminalOffset);
                    if (reopenedRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened FSN-8 terminal lost its physical descending direction.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        if (verifyFsn16Shelf)
        {
            using LibraDexBigIntScalar8Index<UInt128> terminal = catalog.Indexes.IndexSet("descending").Define("fixedBigInt16Terminal").BigIntKeys<UInt128>(maxBytes: 512).Create(options: descending);
            FixedNScalar16Profile profile = FixedNScalar16Profile.Default64KiB(515) with { Descending = true };
            UInt128 lastIdentity = (UInt128)(profile.MaxItemCount + 8);
            for (UInt128 identity = 1; identity <= lastIdentity; identity++)
                ValidateGenericInsert(terminal.Insert(BigInteger.One, identity), "descending FSN-16 terminal insert");
            CatalogIndexInfo info = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "fixedBigInt16Terminal");
            byte[] encodedKey = LibraDexBigIntCodec.Encode(BigInteger.One, 512, LibraDexBigIntKeyStorage.FixedWidth);
            RouterSnapshot router = catalog.Session.ReadRouterSnapshot(info.RootRouterOffset);
            long terminalOffset = catalog.Session.FindRouterTarget(router.Offset, encodedKey[router.KeyDepth]);
            byte[] root = catalog.Session.ReadTerminalIdentityRootBytes(terminalOffset);
            long firstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(root);
            byte[] head = catalog.Session.ReadTerminalVarIdentityShelfBytes(firstShelfOffset, profile.ShelfExtentSize);
            try
            {
                ReadOnlySpan<byte> firstIdentity = TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0);
                if (root[TerminalIdentityRootLayout.SortDirectionOffset] != 1 || firstIdentity.Length != 16 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(firstIdentity.Slice(8)) != (ulong)lastIdentity)
                    throw new InvalidDataException("FSN-16 descending terminal head was not physically highest-first.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            UInt128[] descendingIds = ((IIdentityPrimitiveExecutor)terminal).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
            UInt128[] ascendingIds = ((IIdentityPrimitiveExecutor)terminal).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Ascending)).Cast<UInt128>().ToArray();
            if (!descendingIds.SequenceEqual(new UInt128[] { lastIdentity, lastIdentity - 1, lastIdentity - 2 }) ||
                !ascendingIds.SequenceEqual(new UInt128[] { 1, 2, 3 }))
                throw new InvalidDataException("FSN-16 public terminal traversal failed in one direction.");
            UInt128 highLaneIdentity = ((UInt128)1 << 64) + 5;
            ValidateGenericInsert(terminal.Insert(BigInteger.One, highLaneIdentity), "descending FSN-16 high-lane identity insert");
            UInt128[] highLaneFirst = ((IIdentityPrimitiveExecutor)terminal).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 2,
                Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
            if (!highLaneFirst.SequenceEqual(new UInt128[] { highLaneIdentity, lastIdentity }))
                throw new InvalidDataException("FSN-16 high identity lane did not lead the descending terminal.");

            using LibraDexBigIntScalar8Index<UInt128> split = catalog.Indexes.IndexSet("descending").Define("fixedBigInt16Split").BigIntKeys<UInt128>(maxBytes: 512).Create(options: descending);
            for (UInt128 identity = 1; identity <= 64; identity++)
                ValidateGenericInsert(split.Insert(new BigInteger(2), identity + 1000), "descending FSN-16 high-key split insert");
            for (UInt128 identity = 1; identity <= 64; identity++)
                ValidateGenericInsert(split.Insert(BigInteger.One, identity), "descending FSN-16 low-key split insert");
            UInt128[] splitFirst = ((IIdentityPrimitiveExecutor)split).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 3,
                Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
            UInt128[] splitFind = ((IIdentityPrimitiveExecutor)split).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
            IReadOnlyList<UInt128> bulk = split.GetIdentities(BigInteger.One, new BigInteger(2));
            if (!splitFirst.SequenceEqual(new UInt128[] { 1064, 1063, 1062 }) ||
                !splitFind.SequenceEqual(new UInt128[] { 64, 63, 62 }) ||
                bulk.Count != 128 || bulk[0] != (UInt128)1064 || bulk[63] != (UInt128)1001 ||
                bulk[64] != (UInt128)64 || bulk[bulk.Count - 1] != (UInt128)1)
                throw new InvalidDataException("FSN-16 descending split and bulk range did not preserve physical tuple order.");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fsn16-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexBigIntScalar8Index<UInt128> ordinary = persisted.Indexes.IndexSet("descending").Define("ordinary16").BigIntKeys<UInt128>(maxBytes: 512).Create(options: descending);
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 1), "descending FSN-16 ordinary file insert 1");
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 3), "descending FSN-16 ordinary file insert 3");
                    ValidateGenericInsert(ordinary.Insert(BigInteger.One, 2), "descending FSN-16 ordinary file insert 2");
                    using LibraDexBigIntScalar8Index<UInt128> routedTerminal = persisted.Indexes.IndexSet("descending").Define("terminal16").BigIntKeys<UInt128>(maxBytes: 512).Create(options: descending);
                    for (UInt128 identity = 1; identity <= lastIdentity; identity++)
                        ValidateGenericInsert(routedTerminal.Insert(BigInteger.One, identity), "descending FSN-16 terminal file insert");
                }

                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexBigIntScalar8Index<UInt128> ordinary = reopened.Indexes.IndexSet("descending").Define("ordinary16").BigIntKeys<UInt128>(maxBytes: 512).Open();
                    using LibraDexBigIntScalar8Index<UInt128> routedTerminal = reopened.Indexes.IndexSet("descending").Define("terminal16").BigIntKeys<UInt128>(maxBytes: 512).Open();
                    UInt128[] ordinaryIds = ((IIdentityPrimitiveExecutor)ordinary).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
                    UInt128[] terminalIds = ((IIdentityPrimitiveExecutor)routedTerminal).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending)).Cast<UInt128>().ToArray();
                    if (!ordinaryIds.SequenceEqual(new UInt128[] { 3, 2, 1 }) ||
                        !terminalIds.SequenceEqual(new UInt128[] { lastIdentity, lastIdentity - 1, lastIdentity - 2 }))
                        throw new InvalidDataException("FSN-16 file reopen lost descending tuple order.");
                    CatalogIndexInfo reopenedInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "terminal16");
                    RouterSnapshot reopenedRouter = reopened.Session.ReadRouterSnapshot(reopenedInfo.RootRouterOffset);
                    long reopenedTerminalOffset = reopened.Session.FindRouterTarget(reopenedRouter.Offset, encodedKey[reopenedRouter.KeyDepth]);
                    byte[] reopenedRoot = reopened.Session.ReadTerminalIdentityRootBytes(reopenedTerminalOffset);
                    if (reopenedRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened FSN-16 terminal lost its physical descending direction.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        using LibraDexVariableBlobScalar8Index<long> blob = catalog.Indexes.IndexSet("descending").Define("blob").Blob.Variable<long>(maxKeyBytes: 16).Create(options: descending);
        for (long identity = 1; identity <= 128; identity++)
            ValidateGenericInsert(blob.Insert(new byte[] { 1 }, identity), "descending variable blob equal-key insert");
        ValidateGenericInsert(blob.Insert(new byte[] { 2 }, 900), "descending variable blob high-key insert");
        AssertDescendingIds(blob, all, 900, 128, 127, 126);
        AssertDescendingIds(blob, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { new byte[] { 1 } }, TakeLimit: 3, Direction: QueryDirection.Descending), 128, 127, 126);
        if (verifyVs8Optimizer)
        {
            CatalogIndexInfo optimizedInfo = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "blob");
            LibraDexMaintenanceWalkResult optimized = catalog.Session.OptimizeVarKeyScalar8Topology(
                optimizedInfo.RootRouterOffset, optimizedInfo.VarKeyMaxKeyLength, 16, maxWorkItems: null,
                descending: true);
            if (optimized.ChangedCount == 0)
                throw new InvalidDataException("Descending VS8 maintenance optimizer did not publish its replacement.");
            byte[] highKey = LibraDexVarLenKeyCodec.Encode(new byte[] { 2 }, optimizedInfo.VarKeyMaxKeyLength, "key");
            long optimizedShelfOffset = catalog.Session.FindRouterTarget(optimizedInfo.RootRouterOffset, highKey[0]);
            VarKeyScalar8ReadOnly optimizedShelf = catalog.Session.ReadVarKeyScalar8ReadOnlyShelf(
                optimizedShelfOffset, optimizedInfo.VarKeyMaxKeyLength);
            if (!optimizedShelf.IsValid || !optimizedShelf.IsDescending ||
                !optimizedShelf.ReadKeyAt(0).SequenceEqual(highKey) ||
                optimizedShelf.ReadIdentityAt(1) <= optimizedShelf.ReadIdentityAt(2))
                throw new InvalidDataException("Public descending VS8 optimizer did not retain physical tuple order.");
            AssertDescendingIds(blob, all, 900, 128, 127, 126);
        }
        if (verifyVs8Shelf)
        {
            CatalogIndexInfo blobInfo = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "blob");
            byte[] encodedHighKey = LibraDexVarLenKeyCodec.Encode(new byte[] { 2 }, blobInfo.VarKeyMaxKeyLength, "key");
            byte[] encodedLowKey = LibraDexVarLenKeyCodec.Encode(new byte[] { 1 }, blobInfo.VarKeyMaxKeyLength, "key");
            long blobShelfOffset = catalog.Session.FindRouterTarget(blobInfo.RootRouterOffset, encodedHighKey[0]);
            VarKeyScalar8ReadOnly physical = catalog.Session.ReadVarKeyScalar8ReadOnlyShelf(blobShelfOffset, blobInfo.VarKeyMaxKeyLength);
            if (!physical.IsValid || !physical.IsDescending || physical.ItemCount < 3 ||
                !physical.ReadKeyAt(0).SequenceEqual(encodedHighKey) ||
                !physical.ReadKeyAt(1).SequenceEqual(encodedLowKey) ||
                physical.ReadIdentityAt(1) <= physical.ReadIdentityAt(2))
                throw new InvalidDataException("Public descending variable-blob index did not persist key and equal-key identity order.");
            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"vs8-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexVariableBlobScalar8Index<long> created = persisted.Indexes.IndexSet("descending").Define("reopenBlob")
                        .Blob.Variable<long>(maxKeyBytes: 16).Create(options: descending);
                    ValidateGenericInsert(created.Insert(new byte[] { 1 }, 1), "VS8 descending file insert 1");
                    ValidateGenericInsert(created.Insert(new byte[] { 1 }, 3), "VS8 descending file insert 3");
                    ValidateGenericInsert(created.Insert(new byte[] { 1 }, 2), "VS8 descending file insert 2");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexVariableBlobScalar8Index<long> opened = reopened.Indexes.IndexSet("descending").Define("reopenBlob")
                        .Blob.Variable<long>(maxKeyBytes: 16).Open();
                    CatalogIndexInfo reopenedInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "reopenBlob");
                    byte[] encodedKey = LibraDexVarLenKeyCodec.Encode(new byte[] { 1 }, reopenedInfo.VarKeyMaxKeyLength, "key");
                    long shelfOffset = reopened.Session.FindRouterTarget(reopenedInfo.RootRouterOffset, encodedKey[0]);
                    VarKeyScalar8ReadOnly reopenedShelf = reopened.Session.ReadVarKeyScalar8ReadOnlyShelf(shelfOffset, reopenedInfo.VarKeyMaxKeyLength);
                    if (!reopenedShelf.IsValid || !reopenedShelf.IsDescending ||
                        reopenedShelf.ReadIdentityAt(0) <= reopenedShelf.ReadIdentityAt(1))
                        throw new InvalidDataException("Reopened VS8 variable-blob shelf lost descending physical order.");
                    AssertDescendingIds(opened, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { new byte[] { 1 } }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), 3, 2, 1);
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        using LibraDexUInt64VarIdentityIndex rawUInt64 = catalog.Indexes.IndexSet("descending").Define("rawUInt64").UInt64VarIdentityKeys(maxIdentityBytes: 16).Create(options: descending);
        using LibraDexBigIntVarIdentityIndex rawBigInt = catalog.Indexes.IndexSet("descending").Define("rawBigInt").BigIntVarIdentityKeys(maxBytes: 16, maxIdentityBytes: 16).Create(options: descending);
        for (int number = 1; number <= 512; number++)
        {
            string identity = $"id-{number:0000}";
            ValidateGenericInsert(rawUInt64.Insert(1UL, Encoding.ASCII.GetBytes(identity)), "descending UInt64 raw equal-key insert");
            ValidateGenericInsert(rawBigInt.Insert(BigInteger.One, Encoding.ASCII.GetBytes(identity)), "descending BigInt raw equal-key insert");
        }
        ValidateGenericInsert(rawUInt64.Insert(2UL, Encoding.ASCII.GetBytes("z")), "descending UInt64 raw high-key insert");
        ValidateGenericInsert(rawBigInt.Insert(new BigInteger(2), Encoding.ASCII.GetBytes("z")), "descending BigInt raw high-key insert");
        if (verifyPhysicalSv8)
            AssertSv8DescendingPhysicalHead(catalog, "descending", "rawUInt64", 1UL, "id-0512");
        AssertDescendingRawIds(rawUInt64, all, "z", "id-0512", "id-0511", "id-0510");
        AssertDescendingRawIds(rawBigInt, all, "z", "id-0512", "id-0511", "id-0510");
        AssertDescendingRawIds(rawUInt64, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { 1UL }, TakeLimit: 3, Direction: QueryDirection.Descending), "id-0512", "id-0511", "id-0510");
        AssertDescendingRawIds(rawBigInt, bigIntFind, "id-0512", "id-0511", "id-0510");
        if (verifyFsnvShelf)
        {
            using LibraDexBigIntVarIdentityIndex terminal = catalog.Indexes.IndexSet("descending").Define("fixedBigIntVarTerminal").BigIntVarIdentityKeys(maxBytes: 509, maxIdentityBytes: 256).Create(options: descending);
            byte[] encodedKey = LibraDexBigIntCodec.Encode(BigInteger.One, 509, LibraDexBigIntKeyStorage.FixedWidth);
            for (int number = 1; number <= 530; number++)
                ValidateGenericInsert(terminal.Insert(BigInteger.One, Encoding.ASCII.GetBytes($"id-{number:0000}".PadRight(128, 'x'))), "descending FSN-V terminal insert");
            CatalogIndexInfo info = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "fixedBigIntVarTerminal");
            RouterSnapshot router = catalog.Session.ReadRouterSnapshot(info.RootRouterOffset);
            long terminalOffset = catalog.Session.FindRouterTarget(router.Offset, encodedKey[router.KeyDepth]);
            byte[] root = catalog.Session.ReadTerminalIdentityRootBytes(terminalOffset);
            long headOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(root);
            byte[] head = catalog.Session.ReadTerminalVarIdentityShelfBytes(headOffset, FixedNVarIdentityProfile.DefaultShelfExtentSize);
            try
            {
                if (root[TerminalIdentityRootLayout.SortDirectionOffset] != 1 ||
                    !TerminalVarIdentityShelfLayout.ReadIdentityAt(head, 0).SequenceEqual(Encoding.ASCII.GetBytes("id-0530".PadRight(128, 'x'))) ||
                    TerminalVarIdentityShelfLayout.ReadNextShelfOffset(head) == 0)
                    throw new InvalidDataException("FSN-V descending terminal was not highest-first across multiple shelves.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head, clearArray: false);
            }
            AssertDescendingRawIds(terminal, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Descending), "id-0530".PadRight(128, 'x'), "id-0529".PadRight(128, 'x'), "id-0528".PadRight(128, 'x'));
            AssertDescendingRawIds(terminal, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                Direction: QueryDirection.Ascending), "id-0001".PadRight(128, 'x'), "id-0002".PadRight(128, 'x'), "id-0003".PadRight(128, 'x'));
            IReadOnlyList<byte[]> terminalBulk = terminal.GetIdentities(BigInteger.One);
            if (terminalBulk.Count != 530 || !terminalBulk[0].AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("id-0530".PadRight(128, 'x'))))
                throw new InvalidDataException("FSN-V descending terminal bulk range order failed.");
            LibraDexConditionEndCondition natural = LibraDexCondition.ForGroup("descending")
                .Index("fixedBigIntVarTerminal").AsBigInt.EqualTo(BigInteger.One).EndCondition;
            string[] naturalHead = catalog.Indexes.IndexSet("descending").GetIdentities<byte[]>(
                natural, deduplication: IdentityDeduplication.Preserve).Take(3)
                .Select(value => Encoding.ASCII.GetString(value).Substring(0, 7)).ToArray();
            if (!naturalHead.SequenceEqual(new[] { "id-0530", "id-0529", "id-0528" }) || terminal.Count() != 530)
                throw new InvalidDataException("FSN-V natural descending condition or count failed.");

            using LibraDexBigIntVarIdentityIndex split = catalog.Indexes.IndexSet("descending").Define("fixedBigIntVarSplit").BigIntVarIdentityKeys(maxBytes: 509, maxIdentityBytes: 256).Create(options: descending);
            for (int number = 1; number <= 90; number++)
                ValidateGenericInsert(split.Insert(new BigInteger(2), Encoding.ASCII.GetBytes($"hi-{number:0000}".PadRight(128, 'x'))), "descending FSN-V high-key split insert");
            for (int number = 1; number <= 90; number++)
                ValidateGenericInsert(split.Insert(BigInteger.One, Encoding.ASCII.GetBytes($"lo-{number:0000}".PadRight(128, 'x'))), "descending FSN-V low-key split insert");
            AssertDescendingRawIds(split, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 3,
                Direction: QueryDirection.Descending), "hi-0090".PadRight(128, 'x'), "hi-0089".PadRight(128, 'x'), "hi-0088".PadRight(128, 'x'));
            AssertDescendingRawIds(split, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Between, new object?[] { BigInteger.One, new BigInteger(2) }, TakeLimit: 3,
                Direction: QueryDirection.Ascending), "lo-0001".PadRight(128, 'x'), "lo-0002".PadRight(128, 'x'), "lo-0003".PadRight(128, 'x'));
            IReadOnlyList<byte[]> splitBulk = split.GetIdentities(BigInteger.One, new BigInteger(2));
            if (splitBulk.Count != 180 ||
                !splitBulk[0].AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("hi-0090".PadRight(128, 'x'))) ||
                !splitBulk[^1].AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("lo-0001".PadRight(128, 'x'))))
                throw new InvalidDataException("FSN-V descending routed bulk range order failed.");

            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"fsnv-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexBigIntVarIdentityIndex ordinary = persisted.Indexes.IndexSet("descending").Define("ordinaryVar").BigIntVarIdentityKeys(509, 256).Create(options: descending);
                    foreach (string identity in new[] { "a", "b", "aa" })
                        ValidateGenericInsert(ordinary.Insert(BigInteger.One, Encoding.ASCII.GetBytes(identity)), "descending FSN-V ordinary file insert");
                    using LibraDexBigIntVarIdentityIndex routedTerminal = persisted.Indexes.IndexSet("descending").Define("terminalVar").BigIntVarIdentityKeys(509, 256).Create(options: descending);
                    for (int number = 1; number <= 530; number++)
                        ValidateGenericInsert(routedTerminal.Insert(BigInteger.One, Encoding.ASCII.GetBytes($"id-{number:0000}".PadRight(128, 'x'))), "descending FSN-V terminal file insert");
                }
                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexBigIntVarIdentityIndex ordinary = reopened.Indexes.IndexSet("descending").Define("ordinaryVar").BigIntVarIdentityKeys(509, 256).Open();
                    using LibraDexBigIntVarIdentityIndex routedTerminal = reopened.Indexes.IndexSet("descending").Define("terminalVar").BigIntVarIdentityKeys(509, 256).Open();
                    AssertDescendingRawIds(ordinary, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), "b", "aa", "a");
                    AssertDescendingRawIds(routedTerminal, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { BigInteger.One }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), "id-0530".PadRight(128, 'x'), "id-0529".PadRight(128, 'x'), "id-0528".PadRight(128, 'x'));
                    CatalogIndexInfo reopenedInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "terminalVar");
                    RouterSnapshot reopenedRouter = reopened.Session.ReadRouterSnapshot(reopenedInfo.RootRouterOffset);
                    long reopenedOffset = reopened.Session.FindRouterTarget(reopenedRouter.Offset, encodedKey[reopenedRouter.KeyDepth]);
                    byte[] reopenedRoot = reopened.Session.ReadTerminalIdentityRootBytes(reopenedOffset);
                    if (reopenedRoot[TerminalIdentityRootLayout.SortDirectionOffset] != 1)
                        throw new InvalidDataException("Reopened FSN-V terminal lost its physical descending direction.");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }
        AssertDescendingRawIds(rawUInt64, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { 1UL }, TakeLimit: 3, Direction: QueryDirection.Ascending), "id-0001", "id-0002", "id-0003");
        if (verifyPhysicalSv8)
        {
            CatalogIndexInfo rawInfo = catalog.Indexes.IndexSet("descending").List().Single(item => item.Name == "rawUInt64");
            if (!catalog.Session.DeleteScalar8VarIdentityExactTuple(rawInfo.RootRouterOffset, rawInfo.VarIdentityMaxLength, 1UL, Encoding.ASCII.GetBytes("id-0512")))
                throw new InvalidDataException("SV8 descending terminal delete missed its physical head.");
            AssertSv8DescendingPhysicalHead(catalog, "descending", "rawUInt64", 1UL, "id-0511");
            ValidateGenericInsert(rawUInt64.Insert(1UL, Encoding.ASCII.GetBytes("id-0512")), "descending SV8 terminal reinsert");
            AssertSv8DescendingPhysicalHead(catalog, "descending", "rawUInt64", 1UL, "id-0512");
            string[] expectedDescendingRun = Enumerable.Range(1, 512).Reverse().Select(number => $"id-{number:0000}").ToArray();
            string[] actualDescendingRun = ((IIdentityPrimitiveExecutor)rawUInt64).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { 1UL }, Direction: QueryDirection.Descending))
                .Select(value => Encoding.ASCII.GetString((byte[])value)).ToArray();
            if (!actualDescendingRun.SequenceEqual(expectedDescendingRun))
                throw new InvalidDataException("SV8 descending terminal chain did not enumerate its complete equal-key run in physical order.");
            string[] actualAscendingRun = ((IIdentityPrimitiveExecutor)rawUInt64).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.Find, new object?[] { 1UL }, Direction: QueryDirection.Ascending))
                .Select(value => Encoding.ASCII.GetString((byte[])value)).ToArray();
            if (!actualAscendingRun.SequenceEqual(expectedDescendingRun.Reverse()))
                throw new InvalidDataException("SV8 explicit ascending terminal traversal did not reverse its complete physical chain.");
        }
        for (ulong key = 3; key <= 400; key++)
            ValidateGenericInsert(rawUInt64.Insert(key, Encoding.ASCII.GetBytes($"key-{key:0000}")), "descending SV8 mixed-key insert");
        AssertDescendingRawIds(rawUInt64, all, "key-0400", "key-0399", "key-0398", "key-0397");
        AssertDescendingRawIds(rawUInt64, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { 400UL }, TakeLimit: 1, Direction: QueryDirection.Descending), "key-0400");
        if (verifyPhysicalSv8)
        {
            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"sv8-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    using LibraDexUInt64VarIdentityIndex fileIndex = persisted.Indexes.IndexSet("descending").Define("reopen").UInt64VarIdentityKeys(16).Create(options: descending);
                    ValidateGenericInsert(fileIndex.Insert(1UL, Encoding.ASCII.GetBytes("id-0001")), "descending SV8 file insert 1");
                    ValidateGenericInsert(fileIndex.Insert(1UL, Encoding.ASCII.GetBytes("id-0003")), "descending SV8 file insert 3");
                    ValidateGenericInsert(fileIndex.Insert(1UL, Encoding.ASCII.GetBytes("id-0002")), "descending SV8 file insert 2");
                    AssertSv8DescendingPhysicalHead(persisted, "descending", "reopen", 1UL, "id-0003");
                }

                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    using LibraDexUInt64VarIdentityIndex fileIndex = reopened.Indexes.IndexSet("descending").Define("reopen").UInt64VarIdentityKeys(16).Open();
                    AssertSv8DescendingPhysicalHead(reopened, "descending", "reopen", 1UL, "id-0003");
                    AssertDescendingRawIds(fileIndex, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { 1UL }, TakeLimit: 3, Direction: QueryDirection.Descending), "id-0003", "id-0002", "id-0001");
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        IIndex composite = catalog.Indexes.IndexSet("descending").Define("composite").Composite<long>(C.Scalar<long>("stamp")).Create(options: descending);
        for (long identity = 128; identity >= 1; identity--)
            ValidateGenericInsert(composite.Insert(Key.Of(1L), identity), "descending composite equal-key insert");
        ValidateGenericInsert(composite.Insert(Key.Of(2L), 900L), "descending composite high-key insert");
        AssertDescendingIds(composite, all, 900, 128, 127, 126);
        AssertDescendingIds(composite, new LibraDexIdentityPrimitiveRequest(
            LibraDexCriteriaKind.Find, new object?[] { Key.Of(1L) }, TakeLimit: 3, Direction: QueryDirection.Descending), 128, 127, 126);

        if (verifyShapeSortOverride)
        {
            LibraDexIndexShapeSpec requestedShape = catalog.Indexes.IndexSet("descending").Define("shapeOverride").Shape.Scalar<long, long>();
            IIndex overridden = catalog.Indexes.Create(requestedShape, options: descending);
            if (overridden.SortOrder != LibraDexIndexSortOrder.Descending ||
                overridden.LogicalShape?.SortOrder != LibraDexIndexSortOrder.Descending ||
                overridden.LogicalShape.Projections[0].SortOrder != LibraDexIndexSortOrder.Descending)
                throw new InvalidDataException("Shape-driven create did not apply its descending options override to the live handle and projection metadata.");
            ValidateGenericInsert(overridden.Insert(1L, 11L), "shape override low identity");
            ValidateGenericInsert(overridden.Insert(1L, 12L), "shape override high identity");
            ValidateGenericInsert(overridden.Insert(2L, 20L), "shape override high key");
            AssertDescendingIds(overridden, new LibraDexIdentityPrimitiveRequest(
                LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 3,
                Direction: QueryDirection.Descending), 20, 12, 11);
            LibraDexIndex<long, long> typed = (LibraDexIndex<long, long>)overridden;
            Scalar8Scalar8Profile physicalProfile = typed.GetScalar8Scalar8Profile();
            Scalar8Scalar8RoutePathTarget route = catalog.Session.WalkScalar8Scalar8RoutePathTarget(
                typed.RootRouterOffset, LibraDexGenericScalarCodec<long>.Encode8(1L), maxRouterHops: 16,
                readPolicy: Scalar8Scalar8RouteReadPolicy.PreferPromotedViews);
            if (!physicalProfile.Descending || route.Target.Kind != Scalar8Scalar8RouteTargetKind.Shelf)
                throw new InvalidDataException("Shape-driven descending override did not create a descending SS8-8 shelf.");
            Scalar8Scalar8ReadOnly shelf = new(
                catalog.Session.ReadScalar8Scalar8ShelfBytesForBatch(route.Target.Offset, physicalProfile), physicalProfile);
            if (!shelf.IsDescending || shelf.ReadIdentityAt(0) != LibraDexGenericScalarCodec<long>.Encode8(20L) ||
                shelf.ReadIdentityAt(1) != LibraDexGenericScalarCodec<long>.Encode8(12L))
                throw new InvalidDataException("Shape-driven descending override metadata disagrees with physical tuple order.");
        }

        if (verifyComposite)
        {
            string reopenPath = Path.GetFullPath(Path.Combine("artifacts", $"composite-descending-reopen-{Guid.NewGuid():N}.lbdx"));
            try
            {
                using (Catalog persisted = Catalog.Create(reopenPath))
                {
                    LibraDexIndexShapeSpec numericShape = persisted.Indexes.IndexSet("descending").Define("numericComposite").Shape.Composite<long>(
                        new[] { C.Scalar<long>("stamp") });
                    IIndex numeric = persisted.Indexes.Create(numericShape, options: descending);
                    ValidateGenericInsert(numeric.Insert(Key.Of(1L), 11L), "descending composite identity 11");
                    ValidateGenericInsert(numeric.Insert(Key.Of(1L), 13L), "descending composite identity 13");
                    ValidateGenericInsert(numeric.Insert(Key.Of(1L), 12L), "descending composite identity 12");
                    for (long key = 2; key <= 4; key++)
                        ValidateGenericInsert(numeric.Insert(Key.Of(key), key * 10), "descending composite distinct key");

                    CatalogIndexInfo info = persisted.Indexes.IndexSet("descending").List().Single(item => item.Name == "numericComposite");
                    if (!persisted.Session.TryReadCompositeNodePage(info.RootRouterOffset, out byte[] rootBytes))
                        throw new InvalidDataException("Descending composite root page was not readable.");
                    LibraDexCompositeNodePage rootPage = LibraDexCompositeNodePageCodec.Decode(info.CreateShape(), rootBytes);
                    if (rootPage.Children.Count != 4 || !Equals(rootPage.Children[0].Value, 4L) ||
                        !Equals(rootPage.Children[^1].Value, 1L))
                        throw new InvalidDataException("Descending composite child directory was not physically highest-key-first.");
                    if (!persisted.Session.TryReadCompositeNodePage(rootPage.Children[^1].Offset, out byte[] terminalBytes))
                        throw new InvalidDataException("Descending composite terminal page was not readable.");
                    LibraDexCompositeNodePage terminal = LibraDexCompositeNodePageCodec.Decode(info.CreateShape(), terminalBytes);
                    if (terminal.Identities.Count != 3 || !Equals(terminal.Identities[0], 13L) ||
                        !Equals(terminal.Identities[^1], 11L))
                        throw new InvalidDataException("Descending composite equal-key identities were not physically highest-first.");

                    IIndex textIndex = persisted.Indexes.Create(
                        persisted.Indexes.IndexSet("descending").Define("textComposite").Shape.Composite<long>(new[] { C.Text("label") }),
                        options: descending);
                    ValidateGenericInsert(textIndex.Insert(Key.Of("alpha"), 101L), "descending composite text alpha");
                    ValidateGenericInsert(textIndex.Insert(Key.Of("alpine"), 102L), "descending composite text alpine");
                    ValidateGenericInsert(textIndex.Insert(Key.Of("alphabet"), 103L), "descending composite text alphabet");
                    ValidateGenericInsert(textIndex.Insert(Key.Of("beta"), 104L), "descending composite text beta");

                    LibraDexIndexShapeSpec mixedShape = persisted.Indexes.IndexSet("descending").Define("mixedComposite").Shape.Composite<long>(
                        new[]
                        {
                            C.Scalar<long>("major") with { SortOrder = LibraDexIndexSortOrder.Descending },
                            C.Scalar<long>("minor")
                        });
                    IIndex mixed = persisted.Indexes.Create(mixedShape, options: descending);
                    ValidateGenericInsert(mixed.Insert(Key.Of(2L, 1L), 21L), "descending mixed composite 2/1");
                    ValidateGenericInsert(mixed.Insert(Key.Of(1L, 2L), 12L), "descending mixed composite 1/2");
                    ValidateGenericInsert(mixed.Insert(Key.Of(1L, 1L), 11L), "descending mixed composite 1/1");
                    ValidateGenericInsert(mixed.Insert(Key.Of(2L, 2L), 22L), "descending mixed composite 2/2");
                }

                using (Catalog reopened = Catalog.Open(reopenPath))
                {
                    CatalogIndexInfo numericInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "numericComposite");
                    IIndex numeric = reopened.Indexes.Open(numericInfo.CreateShape());
                    AssertDescendingIds(numeric, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.All, Array.Empty<object?>(), TakeLimit: 4,
                        Direction: QueryDirection.Descending), 40, 30, 20, 13);
                    AssertDescendingIds(numeric, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.Find, new object?[] { Key.Of(1L) }, TakeLimit: 3,
                        Direction: QueryDirection.Descending), 13, 12, 11);
                    AssertDescendingIds(numeric, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.CompositeMatch,
                        new object?[] { new LibraDexCompositePredicate(new[] { LibraDexCompositePart.Scalar<long>("stamp").Between(2L, 4L) }) },
                        Direction: QueryDirection.Descending), 40, 30, 20);
                    AssertDescendingIds(numeric, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.CompositeMatch,
                        new object?[] { new LibraDexCompositePredicate(new[] { LibraDexCompositePart.Scalar<long>("stamp").GreaterThan(2L) }) },
                        Direction: QueryDirection.Descending), 40, 30);
                    AssertDescendingIds(numeric, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.CompositeMatch,
                        new object?[] { new LibraDexCompositePredicate(new[] { LibraDexCompositePart.Scalar<long>("stamp").LessOrEqual(2L) }) },
                        Direction: QueryDirection.Descending), 20, 13, 12, 11);
                    long[] ascending = ((IIdentityPrimitiveExecutor)numeric).IterateIdentityPrimitive(new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.All, Array.Empty<object?>(), Direction: QueryDirection.Ascending)).Cast<long>().ToArray();
                    if (!ascending.SequenceEqual(new long[] { 11, 12, 13, 20, 30, 40 }))
                        throw new InvalidDataException("Reopened descending composite did not support explicit ascending traversal.");

                    CatalogIndexInfo textInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "textComposite");
                    IIndex textIndex = reopened.Indexes.Open(textInfo.CreateShape());
                    AssertDescendingIds(textIndex, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.CompositeMatch,
                        new object?[] { new LibraDexCompositePredicate(new[] { LibraDexCompositePart.String("label").StartsWith("al") }) },
                        Direction: QueryDirection.Descending), 102, 103, 101);

                    CatalogIndexInfo mixedInfo = reopened.Indexes.IndexSet("descending").List().Single(item => item.Name == "mixedComposite");
                    IIndex mixed = reopened.Indexes.Open(mixedInfo.CreateShape());
                    AssertDescendingIds(mixed, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.All, Array.Empty<object?>(), Direction: QueryDirection.Descending), 12, 11, 22, 21);
                    AssertDescendingIds(mixed, new LibraDexIdentityPrimitiveRequest(
                        LibraDexCriteriaKind.CompositeMatch,
                        new object?[] { new LibraDexCompositePredicate(new[]
                        {
                            LibraDexCompositePart.Scalar<long>("major").EqualTo(1L),
                            LibraDexCompositePart.Scalar<long>("minor").Between(1L, 2L)
                        }) },
                        Direction: QueryDirection.Descending), 12, 11);
                }
            }
            finally
            {
                if (File.Exists(reopenPath))
                    File.Delete(reopenPath);
            }
        }

        Console.WriteLine("descending-sort-order-sanity ok families=8 projections=1 equalKeyRun=512 take=4");
        return 0;
    }

    /// <summary>
    /// Asserts the first four identity objects produced by one reverse physical primitive.<br/>
    /// </summary>
    /// <param name="index">The index family being checked.<br/></param>
    /// <param name="request">The descending limited primitive request.<br/></param>
    /// <param name="expected">The expected identity prefix in complete reverse tuple order.<br/></param>
    private static void AssertDescendingIds(IIndex index, LibraDexIdentityPrimitiveRequest request, params long[] expected)
    {
        if (index.SortOrder != LibraDexIndexSortOrder.Descending)
            throw new InvalidDataException($"Index {index.Name} did not expose persisted descending sort order.");
        long[] actual = ((IIdentityPrimitiveExecutor)index).IterateIdentityPrimitive(request).Select(Convert.ToInt64).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException($"Index {index.Name} returned [{string.Join(',', actual)}] instead of [{string.Join(',', expected)}].");
    }

    /// <summary>
    /// Asserts reverse byte-string identity order without relying on object reference comparisons.<br/>
    /// </summary>
    /// <param name="index">The raw-identity index family being checked.<br/></param>
    /// <param name="request">The descending limited primitive request.<br/></param>
    /// <param name="expected">The expected decoded identity prefix.<br/></param>
    private static void AssertDescendingRawIds(IIndex index, LibraDexIdentityPrimitiveRequest request, params string[] expected)
    {
        if (index.SortOrder != LibraDexIndexSortOrder.Descending)
            throw new InvalidDataException($"Index {index.Name} did not expose persisted descending sort order.");
        string[] actual = ((IIdentityPrimitiveExecutor)index).IterateIdentityPrimitive(request)
            .Select(value => Encoding.ASCII.GetString((byte[])value)).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException($"Index {index.Name} returned [{string.Join(',', actual)}] instead of [{string.Join(',', expected)}].");
    }

    /// <summary>
    /// Checks the first persisted tuple behind a duplicate-heavy SV8 route, not merely the order a reverse reader presents.<br/>
    /// A descending index must place its highest equal-key identity at the chain head so Take(1) does not read the full chain first.<br/>
    /// </summary>
    /// <param name="catalog">The catalog owning the physical SV8 route.<br/></param>
    /// <param name="group">The catalog index group.<br/></param>
    /// <param name="name">The catalog index name.<br/></param>
    /// <param name="key">The exact UInt64 key whose duplicate chain is inspected.<br/></param>
    /// <param name="expected">The expected first identity at the physical route head.<br/></param>
    private static void AssertSv8DescendingPhysicalHead(Catalog catalog, string group, string name, ulong key, string expected)
    {
        CatalogIndexInfo info = catalog.Indexes.IndexSet(group).List().Single(item => item.Name == name);
        Scalar8VarIdentityRoutePathTarget path = catalog.Session.WalkScalar8VarIdentityRoutePathTarget(info.RootRouterOffset, key, maxRouterHops: 32);
        string actual;
        if (path.Target.Kind == Scalar8VarIdentityRouteTargetKind.TerminalVarIdentityRoot)
        {
            byte[] root = catalog.Session.ReadTerminalIdentityRootBytes(path.Target.Offset);
            long firstShelfOffset = TerminalIdentityRootLayout.ReadFirstShelfOffset(root);
            byte[] shelf = catalog.Session.ReadTerminalVarIdentityShelfBytes(firstShelfOffset, TerminalIdentityRootLayout.ReadShelfExtentSize(root));
            try
            {
                actual = Encoding.ASCII.GetString(TerminalVarIdentityShelfLayout.ReadIdentityAt(shelf, 0));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(shelf, clearArray: false);
            }
        }
        else if (path.Target.Kind == Scalar8VarIdentityRouteTargetKind.Shelf)
        {
            byte[] shelf = catalog.Session.ReadScalar8VarIdentityShelfBytes(path.Target.Offset, info.VarIdentityMaxLength, out Scalar8VarIdentityProfile profile);
            Scalar8VarIdentityReadOnly view = new(shelf, profile);
            actual = Encoding.ASCII.GetString(view.ReadIdentityAt(0));
        }
        else
        {
            throw new InvalidDataException($"SV8 physical-head probe reached {path.Target.Kind}.");
        }

        if (actual != expected)
            throw new InvalidDataException($"SV8 descending physical head was {actual}; expected {expected}.");
    }
}
