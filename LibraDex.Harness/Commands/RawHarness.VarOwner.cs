using System.Buffers.Binary;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using System.Reflection;

internal static partial class RawHarness
{

    /// <summary>Forces repeated Scalar8VarIdentity growth/splits, then inserts keys diverging in formerly skipped stem bytes.<br/>
    /// Checks all ordered key/identity pairs, selected exact reads and reopen against a sorted independent oracle.<br/></summary>
    private static void CheckScalar8VarIdentityOwner(string path, bool rangeParent = false)
    {
        const int count = 6000;
        const int width = 128;
        long rootOffset;
        var expected = new List<(ulong Key, ulong Identity)>();
        var options = CreateDesignPerfOptions();
        int splits = 0;
        using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9508), DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "varowner", 0));
            rootOffset = root.Offset;
            var (initialShelf, _) = session.CreateScalar8VarIdentityShelfAndLinkRootRoute(rootOffset, 0x40, Scalar8VarIdentityProfile.DefaultInitial);
            if (rangeParent)
            {
                // Build a supported one-byte range owner directly; reflection stays confined to fixture setup.
                var kernel = (DataKernel)typeof(LibraDexFileSession).GetField("kernel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                byte[] router = new byte[RouterLayout.Size];
                new RouterWriter(router).InitializeCompressed(1, 0, 1, 0, new RouterRouteSnapshot[] { new(0x20, 0x60, initialShelf) });
                kernel.StageWriteAt(rootOffset, router);
                typeof(LibraDexFileSession).GetMethod("CommitAndInvalidateRouterReadCache", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
            }
            for (int i = 0; i < count + 3; i++)
            {
                ulong key = i < count ? 0x40F0000000000000UL + (ulong)i * 65536 :
                    i == count ? 0x4010000000000000UL : i == count + 1 ? 0x40E0000000000000UL : 0x6010000000000000UL;
                byte[] identity = new byte[width];
                BinaryPrimitives.WriteUInt64BigEndian(identity, (ulong)i + 1);
                var result = session.InsertWalkedRoutedScalar8VarIdentity(rootOffset, width, key, identity, true, 40);
                if (result.InsertResult != Scalar8VarIdentityInsertResult.Inserted) throw new InvalidDataException("Scalar8VarIdentity insert failed.");
                if (result.Kind == Scalar8VarIdentityRoutedInsertKind.WalkedShelfSplit) splits++;
                expected.Add((key, (ulong)i + 1));
            }
            if (splits < 2) throw new InvalidDataException("Scalar8VarIdentity did not exercise repeated splits.");
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Scalar8VarIdentity rangeParent={rangeParent}: PASS tuples={expected.Count} splits={splits}");

        /// <summary>Verifies ordered full traversal and bounded exact-key probes using the actual routed readers.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Identity).ToArray();
            using var reader = new Scalar8VarIdentityRangeReader(session, rootOffset, width, 0, ulong.MaxValue);
            int read = 0;
            while (reader.MoveNext())
            {
                if (read >= oracle.Length || reader.CurrentEncodedKey != oracle[read].Key ||
                    BinaryPrimitives.ReadUInt64BigEndian(reader.CurrentIdentity) != oracle[read].Identity)
                    throw new InvalidDataException("Scalar8VarIdentity ordered tuple parity failed.");
                read++;
            }
            if (read != oracle.Length) throw new InvalidDataException("Scalar8VarIdentity count mismatch.");
            foreach (var row in oracle.Where((_, i) => i % 113 == 0))
            {
                using var exact = new Scalar8VarIdentityRangeReader(session, rootOffset, width, row.Key, row.Key);
                if (!exact.MoveNext() || BinaryPrimitives.ReadUInt64BigEndian(exact.CurrentIdentity) != row.Identity || exact.MoveNext())
                    throw new InvalidDataException("Scalar8VarIdentity exact probe failed.");
            }
        }
    }

    /// <summary>Forces repeated Scalar16VarIdentity growth/splits, then inserts keys diverging in formerly skipped stem bytes.<br/>
    /// Checks all ordered key/identity pairs, selected exact reads and reopen against a sorted independent oracle.<br/></summary>
    private static void CheckScalar16VarIdentityOwner(string path)
    {
        const int count = 6000;
        const int width = 128;
        long rootOffset;
        var expected = new List<(ulong Key, ulong Identity)>();
        var options = CreateDesignPerfOptions();
        int splits = 0;
        using (var session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9516), DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "varowner", 0));
            rootOffset = root.Offset;
            for (int i = 0; i < count + 3; i++)
            {
                ulong key = i < count ? 0x40F0000000000000UL + (ulong)i * 65536 :
                    i == count ? 0x4010000000000000UL : i == count + 1 ? 0x40E0000000000000UL : 0x6010000000000000UL;
                byte[] identity = new byte[width];
                BinaryPrimitives.WriteUInt64BigEndian(identity, (ulong)i + 1);
                var result = session.InsertWalkedRoutedScalar16VarIdentity(rootOffset, width, key, 0, identity, true, 40);
                if (result.InsertResult != Scalar16VarIdentityInsertResult.Inserted) throw new InvalidDataException("Scalar16VarIdentity insert failed.");
                if (result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedShelfSplit) splits++;
                expected.Add((key, (ulong)i + 1));
            }
            if (splits < 2) throw new InvalidDataException("Scalar16VarIdentity did not exercise repeated splits.");
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Scalar16VarIdentity: PASS tuples={expected.Count} splits={splits}");

        /// <summary>Verifies ordered full traversal and bounded exact-key probes using the actual routed readers.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Identity).ToArray();
            using var reader = new Scalar16VarIdentityRangeReader(session, rootOffset, width, 0, 0, ulong.MaxValue, ulong.MaxValue);
            int read = 0;
            while (reader.MoveNext())
            {
                if (read >= oracle.Length || reader.CurrentEncodedKeyHigh != oracle[read].Key ||
                    BinaryPrimitives.ReadUInt64BigEndian(reader.CurrentIdentity) != oracle[read].Identity)
                    throw new InvalidDataException("Scalar16VarIdentity ordered tuple parity failed.");
                read++;
            }
            if (read != oracle.Length) throw new InvalidDataException("Scalar16VarIdentity count mismatch.");
            foreach (var row in oracle.Where((_, i) => i % 113 == 0))
            {
                using var exact = new Scalar16VarIdentityRangeReader(session, rootOffset, width, row.Key, 0, row.Key, 0);
                if (!exact.MoveNext() || BinaryPrimitives.ReadUInt64BigEndian(exact.CurrentIdentity) != row.Identity || exact.MoveNext())
                    throw new InvalidDataException("Scalar16VarIdentity exact probe failed.");
            }
        }
    }
}
