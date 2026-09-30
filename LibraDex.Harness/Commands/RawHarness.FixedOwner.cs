using LibraDex;

internal static partial class RawHarness
{
    /// <summary>Exercises shared direct-parent ownership across all five repaired fixed-scalar families, both transform entry paths, mixed prefixes and terminal conversion.<br/>
    /// Full range results are compared with an independent ordered identity oracle live and after reopen; successful disposable databases are removed.<br/></summary>
    private static int RunFixedOwnerSanity(string[] args)
    {
        string directory = GetOption(args, "--path", Path.Combine(Path.GetTempPath(), "LibraDex-owner-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        foreach (string mode in new[] { "serialized", "direct", "mixed", "terminal", "nested-serialized", "nested-direct", "nested-mixed", "nested-terminal" })
        {
            CheckScalar8Scalar16Owner(Path.Combine(directory, "Scalar8Scalar16-" + mode + ".lbdx"), mode);
            CheckScalar16Scalar8Owner(Path.Combine(directory, "Scalar16Scalar8-" + mode + ".lbdx"), mode);
            CheckScalar16Scalar16Owner(Path.Combine(directory, "Scalar16Scalar16-" + mode + ".lbdx"), mode);
            CheckFixed32Scalar8Owner(Path.Combine(directory, "Fixed32Scalar8-" + mode + ".lbdx"), mode);
            CheckFixed32Scalar16Owner(Path.Combine(directory, "Fixed32Scalar16-" + mode + ".lbdx"), mode);
        }
        CheckScalar8VarIdentityOwner(Path.Combine(directory, "SV8.lbdx"));
        CheckScalar8VarIdentityOwner(Path.Combine(directory, "SV8-range.lbdx"), rangeParent: true);
        CheckScalar16VarIdentityOwner(Path.Combine(directory, "SV16.lbdx"));
        return 0;
    }

    /// <summary>Checks Scalar8Scalar16 route ownership and exact live/reopened range identity ordering for one isolated fixture.<br/>
    /// Noncontiguous aliases must be removed without changing an unrelated sibling, and mixed-prefix inputs must still use parent-level partitioning.<br/></summary>
    private static void CheckScalar8Scalar16Owner(string path, string mode)
    {
        bool nested = mode.StartsWith("nested-", StringComparison.Ordinal);
        if (nested) mode = mode.Substring(7);
        var profile = Scalar8Scalar16Profile.Default32KiB;
        int count = profile.MaxItemCount;
        ulong[] keys = new ulong[count], ids = new ulong[count], zeros = new ulong[count];
        var expected = new List<(ulong Key, ulong Id)>();
        for (int i = 0; i < count; i++)
        {
            keys[i] = mode == "terminal" ? 0x40F0000000000000UL :
                mode == "mixed" && i >= count / 2 ? 0x6010000000000000UL + (ulong)i :
                0x40F0000000000000UL + (ulong)i * 2;
            ids[i] = (ulong)i + 1;
            if (nested) keys[i] = 0x1F00000000000000UL | (keys[i] >> 8);
            expected.Add((keys[i], ids[i]));
        }
        var options = new DataKernelOptions(AppendBufferSize: DefaultAppendBufferSize, ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 512);
        var metadata = new SuperblockDeveloperMetadata("OwnerRepair", "Scalar8Scalar16", Guid.NewGuid(), 1, 2, 1);
        long rootOffset;
        using (var session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "owner", 0));
            rootOffset = root.Offset;
            long ownerOffset = rootOffset;
            if (nested)
                (ownerOffset, _, _) = session.CreateScalar8Scalar16ShelfBehindChildRouter(rootOffset, 0x1F, 0x40, profile, keys, zeros, ids);
            var (source, _) = session.CreateScalar8Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile, keys, zeros, ids, [0x20, 0x40, 0x60]);
            var (unrelated, _) = session.CreateScalar8Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile,
                new ulong[] { nested ? 0x1F80800000000000UL : 0x8080000000000000UL }, new ulong[] { 0 }, new ulong[] { 900000 }, [0x80]);
            expected.Add((nested ? 0x1F80800000000000UL : 0x8080000000000000UL, 900000));
            ulong incoming = mode == "terminal" ? keys[0] : keys[0] + 1;
            if (mode == "direct")
            {
                if (!session.TryTransformScalar8Scalar16DirectShelf(rootOffset, profile, incoming, 0, (ulong)count + 1, 40, out var result) ||
                    result.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Scalar8Scalar16 direct transform did not execute.");
            }
            else
            {
                var result = session.InsertWalkedRoutedScalar8Scalar16(rootOffset, profile, incoming, 0, (ulong)count + 1, true, 40);
                if (result.InsertResult != Scalar8Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Scalar8Scalar16 serialized insert did not execute.");
            }
            expected.Add((incoming, (ulong)count + 1));
            if (mode != "mixed" && (session.FindRouterTarget(ownerOffset, 0x20) != 0 || session.FindRouterTarget(ownerOffset, 0x60) != 0))
                throw new InvalidDataException("Scalar8Scalar16 retained an unrelated source alias.");
            if (session.FindRouterTarget(ownerOffset, 0x80) != unrelated)
                throw new InvalidDataException("Scalar8Scalar16 changed an unrelated sibling.");
            foreach (ulong rawNeighbor in new[] { 0x2010000000000000UL, 0x6010000000000000UL, 0x40F0000000000100UL })
            {
                ulong neighbor = nested ? 0x1F00000000000000UL | (rawNeighbor >> 8) : rawNeighbor;
                ulong identity = (ulong)expected.Count + 10000;
                var result = session.InsertWalkedRoutedScalar8Scalar16(rootOffset, profile, neighbor, 0, identity, true, 40);
                if (result.InsertResult != Scalar8Scalar16InsertResult.Inserted) throw new InvalidDataException("Scalar8Scalar16 neighbor insert failed.");
                expected.Add((neighbor, identity));
            }
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Scalar8Scalar16 {mode} nested={nested}: PASS tuples={expected.Count}");

        /// <summary>Compares every returned identity with independently sorted key/identity tuples, without sorting the reader output.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
            foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                using var reader = new Scalar8Scalar16RangeReader(session, rootOffset, profile, 0, ulong.MaxValue, direction, 40);
                int countRead = 0;
                while (direction == QueryDirection.Ascending ? reader.MoveNext() : reader.MovePrevious())
                {
                    ulong identity = reader.CurrentEncodedIdentityLow;
                    ulong high = reader.CurrentEncodedIdentityHigh;
                    int slot = direction == QueryDirection.Ascending ? countRead : oracle.Length - 1 - countRead;
                    if (slot < 0 || slot >= oracle.Length || identity != oracle[slot] || high != 0)
                        throw new InvalidDataException("Scalar8Scalar16 full-range ordering/parity failed.");
                    countRead++;
                }
                if (countRead != oracle.Length) throw new InvalidDataException("Scalar8Scalar16 full-range count failed.");
            }
        }
    }

    /// <summary>Checks Scalar16Scalar8 route ownership and exact live/reopened range identity ordering for one isolated fixture.<br/>
    /// Noncontiguous aliases must be removed without changing an unrelated sibling, and mixed-prefix inputs must still use parent-level partitioning.<br/></summary>
    private static void CheckScalar16Scalar8Owner(string path, string mode)
    {
        bool nested = mode.StartsWith("nested-", StringComparison.Ordinal);
        if (nested) mode = mode.Substring(7);
        var profile = Scalar16Scalar8Profile.Default32KiB;
        int count = profile.MaxItemCount;
        ulong[] keys = new ulong[count], ids = new ulong[count], zeros = new ulong[count];
        var expected = new List<(ulong Key, ulong Id)>();
        for (int i = 0; i < count; i++)
        {
            keys[i] = mode == "terminal" ? 0x40F0000000000000UL :
                mode == "mixed" && i >= count / 2 ? 0x6010000000000000UL + (ulong)i :
                0x40F0000000000000UL + (ulong)i * 2;
            ids[i] = (ulong)i + 1;
            if (nested) keys[i] = 0x1F00000000000000UL | (keys[i] >> 8);
            expected.Add((keys[i], ids[i]));
        }
        var options = new DataKernelOptions(AppendBufferSize: DefaultAppendBufferSize, ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 512);
        var metadata = new SuperblockDeveloperMetadata("OwnerRepair", "Scalar16Scalar8", Guid.NewGuid(), 1, 2, 1);
        long rootOffset;
        using (var session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "owner", 0));
            rootOffset = root.Offset;
            long ownerOffset = rootOffset;
            if (nested)
                (ownerOffset, _, _) = session.CreateScalar16Scalar8ShelfBehindChildRouter(rootOffset, 0x1F, 0x40, profile, keys, zeros, ids);
            var (source, _) = session.CreateScalar16Scalar8ShelfAndLinkRootRoutes(ownerOffset, profile, keys, zeros, ids, [0x20, 0x40, 0x60]);
            var (unrelated, _) = session.CreateScalar16Scalar8ShelfAndLinkRootRoutes(ownerOffset, profile,
                new ulong[] { nested ? 0x1F80800000000000UL : 0x8080000000000000UL }, new ulong[] { 0 }, new ulong[] { 900000 }, [0x80]);
            expected.Add((nested ? 0x1F80800000000000UL : 0x8080000000000000UL, 900000));
            ulong incoming = mode == "terminal" ? keys[0] : keys[0] + 1;
            if (mode == "direct")
            {
                if (!session.TryTransformScalar16Scalar8DirectShelf(rootOffset, profile, incoming, 0, (ulong)count + 1, 40, out var result) ||
                    result.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                    throw new InvalidDataException("Scalar16Scalar8 direct transform did not execute.");
            }
            else
            {
                var result = session.InsertWalkedRoutedScalar16Scalar8(rootOffset, profile, incoming, 0, (ulong)count + 1, true, 40);
                if (result.InsertResult != Scalar16Scalar8InsertResult.Inserted)
                    throw new InvalidDataException("Scalar16Scalar8 serialized insert did not execute.");
            }
            expected.Add((incoming, (ulong)count + 1));
            if (mode != "mixed" && (session.FindRouterTarget(ownerOffset, 0x20) != 0 || session.FindRouterTarget(ownerOffset, 0x60) != 0))
                throw new InvalidDataException("Scalar16Scalar8 retained an unrelated source alias.");
            if (session.FindRouterTarget(ownerOffset, 0x80) != unrelated)
                throw new InvalidDataException("Scalar16Scalar8 changed an unrelated sibling.");
            foreach (ulong rawNeighbor in new[] { 0x2010000000000000UL, 0x6010000000000000UL, 0x40F0000000000100UL })
            {
                ulong neighbor = nested ? 0x1F00000000000000UL | (rawNeighbor >> 8) : rawNeighbor;
                ulong identity = (ulong)expected.Count + 10000;
                var result = session.InsertWalkedRoutedScalar16Scalar8(rootOffset, profile, neighbor, 0, identity, true, 40);
                if (result.InsertResult != Scalar16Scalar8InsertResult.Inserted) throw new InvalidDataException("Scalar16Scalar8 neighbor insert failed.");
                expected.Add((neighbor, identity));
            }
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Scalar16Scalar8 {mode} nested={nested}: PASS tuples={expected.Count}");

        /// <summary>Compares every returned identity with independently sorted key/identity tuples, without sorting the reader output.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
            foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                using var reader = new Scalar16Scalar8RangeReader(session, rootOffset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, direction, 40);
                int countRead = 0;
                while (direction == QueryDirection.Ascending ? reader.MoveNext() : reader.MovePrevious())
                {
                    ulong identity = reader.CurrentEncodedIdentity;

                    int slot = direction == QueryDirection.Ascending ? countRead : oracle.Length - 1 - countRead;
                    if (slot < 0 || slot >= oracle.Length || identity != oracle[slot])
                        throw new InvalidDataException("Scalar16Scalar8 full-range ordering/parity failed.");
                    countRead++;
                }
                if (countRead != oracle.Length) throw new InvalidDataException("Scalar16Scalar8 full-range count failed.");
            }
        }
    }

    /// <summary>Checks Scalar16Scalar16 route ownership and exact live/reopened range identity ordering for one isolated fixture.<br/>
    /// Noncontiguous aliases must be removed without changing an unrelated sibling, and mixed-prefix inputs must still use parent-level partitioning.<br/></summary>
    private static void CheckScalar16Scalar16Owner(string path, string mode)
    {
        bool nested = mode.StartsWith("nested-", StringComparison.Ordinal);
        if (nested) mode = mode.Substring(7);
        var profile = Scalar16Scalar16Profile.Default32KiB;
        int count = profile.MaxItemCount;
        ulong[] keys = new ulong[count], ids = new ulong[count], zeros = new ulong[count];
        var expected = new List<(ulong Key, ulong Id)>();
        for (int i = 0; i < count; i++)
        {
            keys[i] = mode == "terminal" ? 0x40F0000000000000UL :
                mode == "mixed" && i >= count / 2 ? 0x6010000000000000UL + (ulong)i :
                0x40F0000000000000UL + (ulong)i * 2;
            ids[i] = (ulong)i + 1;
            if (nested) keys[i] = 0x1F00000000000000UL | (keys[i] >> 8);
            expected.Add((keys[i], ids[i]));
        }
        var options = new DataKernelOptions(AppendBufferSize: DefaultAppendBufferSize, ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 512);
        var metadata = new SuperblockDeveloperMetadata("OwnerRepair", "Scalar16Scalar16", Guid.NewGuid(), 1, 2, 1);
        long rootOffset;
        using (var session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "owner", 0));
            rootOffset = root.Offset;
            long ownerOffset = rootOffset;
            if (nested)
                (ownerOffset, _, _) = session.CreateScalar16Scalar16ShelfBehindChildRouter(rootOffset, 0x1F, 0x40, profile, keys, zeros, zeros, ids);
            var (source, _) = session.CreateScalar16Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile, keys, zeros, zeros, ids, [0x20, 0x40, 0x60]);
            var (unrelated, _) = session.CreateScalar16Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile,
                new ulong[] { nested ? 0x1F80800000000000UL : 0x8080000000000000UL }, new ulong[] { 0 }, new ulong[] { 0 }, new ulong[] { 900000 }, [0x80]);
            expected.Add((nested ? 0x1F80800000000000UL : 0x8080000000000000UL, 900000));
            ulong incoming = mode == "terminal" ? keys[0] : keys[0] + 1;
            if (mode == "direct")
            {
                if (!session.TryTransformScalar16Scalar16DirectShelf(rootOffset, profile, incoming, 0, 0, (ulong)count + 1, 40, out var result) ||
                    result.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Scalar16Scalar16 direct transform did not execute.");
            }
            else
            {
                var result = session.InsertWalkedRoutedScalar16Scalar16(rootOffset, profile, incoming, 0, 0, (ulong)count + 1, true, 40);
                if (result.InsertResult != Scalar16Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Scalar16Scalar16 serialized insert did not execute.");
            }
            expected.Add((incoming, (ulong)count + 1));
            if (mode != "mixed" && (session.FindRouterTarget(ownerOffset, 0x20) != 0 || session.FindRouterTarget(ownerOffset, 0x60) != 0))
                throw new InvalidDataException("Scalar16Scalar16 retained an unrelated source alias.");
            if (session.FindRouterTarget(ownerOffset, 0x80) != unrelated)
                throw new InvalidDataException("Scalar16Scalar16 changed an unrelated sibling.");
            foreach (ulong rawNeighbor in new[] { 0x2010000000000000UL, 0x6010000000000000UL, 0x40F0000000000100UL })
            {
                ulong neighbor = nested ? 0x1F00000000000000UL | (rawNeighbor >> 8) : rawNeighbor;
                ulong identity = (ulong)expected.Count + 10000;
                var result = session.InsertWalkedRoutedScalar16Scalar16(rootOffset, profile, neighbor, 0, 0, identity, true, 40);
                if (result.InsertResult != Scalar16Scalar16InsertResult.Inserted) throw new InvalidDataException("Scalar16Scalar16 neighbor insert failed.");
                expected.Add((neighbor, identity));
            }
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Scalar16Scalar16 {mode} nested={nested}: PASS tuples={expected.Count}");

        /// <summary>Compares every returned identity with independently sorted key/identity tuples, without sorting the reader output.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
            foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                using var reader = new Scalar16Scalar16RangeReader(session, rootOffset, profile, 0, 0, ulong.MaxValue, ulong.MaxValue, direction, 40);
                int countRead = 0;
                while (direction == QueryDirection.Ascending ? reader.MoveNext() : reader.MovePrevious())
                {
                    ulong identity = reader.CurrentEncodedIdentityLow;
                    ulong high = reader.CurrentEncodedIdentityHigh;
                    int slot = direction == QueryDirection.Ascending ? countRead : oracle.Length - 1 - countRead;
                    if (slot < 0 || slot >= oracle.Length || identity != oracle[slot] || high != 0)
                        throw new InvalidDataException("Scalar16Scalar16 full-range ordering/parity failed.");
                    countRead++;
                }
                if (countRead != oracle.Length) throw new InvalidDataException("Scalar16Scalar16 full-range count failed.");
            }
        }
    }

    /// <summary>Checks Fixed32Scalar8 route ownership and exact live/reopened range identity ordering for one isolated fixture.<br/>
    /// Noncontiguous aliases must be removed without changing an unrelated sibling, and mixed-prefix inputs must still use parent-level partitioning.<br/></summary>
    private static void CheckFixed32Scalar8Owner(string path, string mode)
    {
        bool nested = mode.StartsWith("nested-", StringComparison.Ordinal);
        if (nested) mode = mode.Substring(7);
        var profile = Fixed32Scalar8Profile.Default32KiB;
        int count = profile.MaxItemCount;
        ulong[] keys = new ulong[count], ids = new ulong[count], zeros = new ulong[count];
        var expected = new List<(ulong Key, ulong Id)>();
        for (int i = 0; i < count; i++)
        {
            keys[i] = mode == "terminal" ? 0x40F0000000000000UL :
                mode == "mixed" && i >= count / 2 ? 0x6010000000000000UL + (ulong)i :
                0x40F0000000000000UL + (ulong)i * 2;
            ids[i] = (ulong)i + 1;
            if (nested) keys[i] = 0x1F00000000000000UL | (keys[i] >> 8);
            expected.Add((keys[i], ids[i]));
        }
        var options = new DataKernelOptions(AppendBufferSize: DefaultAppendBufferSize, ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 512);
        var metadata = new SuperblockDeveloperMetadata("OwnerRepair", "Fixed32Scalar8", Guid.NewGuid(), 1, 2, 1);
        long rootOffset;
        using (var session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "owner", 0));
            rootOffset = root.Offset;
            long ownerOffset = rootOffset;
            if (nested)
                (ownerOffset, _, _) = session.CreateFixed32Scalar8ShelfBehindChildRouter(rootOffset, 0x1F, 0x40, profile, keys, zeros, zeros, zeros, ids);
            var (source, _) = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(ownerOffset, profile, keys, zeros, zeros, zeros, ids, [0x20, 0x40, 0x60]);
            var (unrelated, _) = session.CreateFixed32Scalar8ShelfAndLinkRootRoutes(ownerOffset, profile,
                new ulong[] { nested ? 0x1F80800000000000UL : 0x8080000000000000UL }, new ulong[] { 0 }, new ulong[] { 0 }, new ulong[] { 0 }, new ulong[] { 900000 }, [0x80]);
            expected.Add((nested ? 0x1F80800000000000UL : 0x8080000000000000UL, 900000));
            ulong incoming = mode == "terminal" ? keys[0] : keys[0] + 1;
            if (mode == "direct")
            {
                if (!session.TryTransformFixed32Scalar8DirectShelf(rootOffset, profile, incoming, 0, 0, 0, (ulong)count + 1, 40, out var result) ||
                    result.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                    throw new InvalidDataException("Fixed32Scalar8 direct transform did not execute.");
            }
            else
            {
                var result = session.InsertWalkedRoutedFixed32Scalar8(rootOffset, profile, incoming, 0, 0, 0, (ulong)count + 1, true, 40);
                if (result.InsertResult != Fixed32Scalar8InsertResult.Inserted)
                    throw new InvalidDataException("Fixed32Scalar8 serialized insert did not execute.");
            }
            expected.Add((incoming, (ulong)count + 1));
            if (mode != "mixed" && (session.FindRouterTarget(ownerOffset, 0x20) != 0 || session.FindRouterTarget(ownerOffset, 0x60) != 0))
                throw new InvalidDataException("Fixed32Scalar8 retained an unrelated source alias.");
            if (session.FindRouterTarget(ownerOffset, 0x80) != unrelated)
                throw new InvalidDataException("Fixed32Scalar8 changed an unrelated sibling.");
            foreach (ulong rawNeighbor in new[] { 0x2010000000000000UL, 0x6010000000000000UL, 0x40F0000000000100UL })
            {
                ulong neighbor = nested ? 0x1F00000000000000UL | (rawNeighbor >> 8) : rawNeighbor;
                ulong identity = (ulong)expected.Count + 10000;
                var result = session.InsertWalkedRoutedFixed32Scalar8(rootOffset, profile, neighbor, 0, 0, 0, identity, true, 40);
                if (result.InsertResult != Fixed32Scalar8InsertResult.Inserted) throw new InvalidDataException("Fixed32Scalar8 neighbor insert failed.");
                expected.Add((neighbor, identity));
            }
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Fixed32Scalar8 {mode} nested={nested}: PASS tuples={expected.Count}");

        /// <summary>Compares every returned identity with independently sorted key/identity tuples, without sorting the reader output.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
            foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                using var reader = new Fixed32Scalar8RangeReader(session, rootOffset, profile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, direction, 40);
                int countRead = 0;
                while (direction == QueryDirection.Ascending ? reader.MoveNext() : reader.MovePrevious())
                {
                    ulong identity = reader.CurrentEncodedIdentity;

                    int slot = direction == QueryDirection.Ascending ? countRead : oracle.Length - 1 - countRead;
                    if (slot < 0 || slot >= oracle.Length || identity != oracle[slot])
                        throw new InvalidDataException("Fixed32Scalar8 full-range ordering/parity failed.");
                    countRead++;
                }
                if (countRead != oracle.Length) throw new InvalidDataException("Fixed32Scalar8 full-range count failed.");
            }
        }
    }

    /// <summary>Checks Fixed32Scalar16 route ownership and exact live/reopened range identity ordering for one isolated fixture.<br/>
    /// Noncontiguous aliases must be removed without changing an unrelated sibling, and mixed-prefix inputs must still use parent-level partitioning.<br/></summary>
    private static void CheckFixed32Scalar16Owner(string path, string mode)
    {
        bool nested = mode.StartsWith("nested-", StringComparison.Ordinal);
        if (nested) mode = mode.Substring(7);
        var profile = Fixed32Scalar16Profile.Default32KiB;
        int count = profile.MaxItemCount;
        ulong[] keys = new ulong[count], ids = new ulong[count], zeros = new ulong[count];
        var expected = new List<(ulong Key, ulong Id)>();
        for (int i = 0; i < count; i++)
        {
            keys[i] = mode == "terminal" ? 0x40F0000000000000UL :
                mode == "mixed" && i >= count / 2 ? 0x6010000000000000UL + (ulong)i :
                0x40F0000000000000UL + (ulong)i * 2;
            ids[i] = (ulong)i + 1;
            if (nested) keys[i] = 0x1F00000000000000UL | (keys[i] >> 8);
            expected.Add((keys[i], ids[i]));
        }
        var options = new DataKernelOptions(AppendBufferSize: DefaultAppendBufferSize, ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false, MaxCommitGapCoalesceBytes: 512);
        var metadata = new SuperblockDeveloperMetadata("OwnerRepair", "Fixed32Scalar16", Guid.NewGuid(), 1, 2, 1);
        long rootOffset;
        using (var session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            var (root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "owner", 0));
            rootOffset = root.Offset;
            long ownerOffset = rootOffset;
            if (nested)
                (ownerOffset, _, _) = session.CreateFixed32Scalar16ShelfBehindChildRouter(rootOffset, 0x1F, 0x40, profile, keys, zeros, zeros, zeros, ids);
            var (source, _) = session.CreateFixed32Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile, keys, zeros, zeros, zeros, ids, [0x20, 0x40, 0x60]);
            var (unrelated, _) = session.CreateFixed32Scalar16ShelfAndLinkRootRoutes(ownerOffset, profile,
                new ulong[] { nested ? 0x1F80800000000000UL : 0x8080000000000000UL }, new ulong[] { 0 }, new ulong[] { 0 }, new ulong[] { 0 }, new ulong[] { 900000 }, [0x80]);
            expected.Add((nested ? 0x1F80800000000000UL : 0x8080000000000000UL, 900000));
            ulong incoming = mode == "terminal" ? keys[0] : keys[0] + 1;
            if (mode == "direct")
            {
                if (!session.TryTransformFixed32Scalar16DirectShelf(rootOffset, profile, incoming, 0, 0, 0, 0, (ulong)count + 1, 40, out var result) ||
                    result.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Fixed32Scalar16 direct transform did not execute.");
            }
            else
            {
                var result = session.InsertWalkedRoutedFixed32Scalar16(rootOffset, profile, incoming, 0, 0, 0, 0, (ulong)count + 1, true, 40);
                if (result.InsertResult != Fixed32Scalar16InsertResult.Inserted)
                    throw new InvalidDataException("Fixed32Scalar16 serialized insert did not execute.");
            }
            expected.Add((incoming, (ulong)count + 1));
            if (mode != "mixed" && (session.FindRouterTarget(ownerOffset, 0x20) != 0 || session.FindRouterTarget(ownerOffset, 0x60) != 0))
                throw new InvalidDataException("Fixed32Scalar16 retained an unrelated source alias.");
            if (session.FindRouterTarget(ownerOffset, 0x80) != unrelated)
                throw new InvalidDataException("Fixed32Scalar16 changed an unrelated sibling.");
            foreach (ulong rawNeighbor in new[] { 0x2010000000000000UL, 0x6010000000000000UL, 0x40F0000000000100UL })
            {
                ulong neighbor = nested ? 0x1F00000000000000UL | (rawNeighbor >> 8) : rawNeighbor;
                ulong identity = (ulong)expected.Count + 10000;
                var result = session.InsertWalkedRoutedFixed32Scalar16(rootOffset, profile, neighbor, 0, 0, 0, 0, identity, true, 40);
                if (result.InsertResult != Fixed32Scalar16InsertResult.Inserted) throw new InvalidDataException("Fixed32Scalar16 neighbor insert failed.");
                expected.Add((neighbor, identity));
            }
            Validate(session);
        }
        using (var session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions)) Validate(session);
        File.Delete(path);
        Console.WriteLine($"owner-sanity Fixed32Scalar16 {mode} nested={nested}: PASS tuples={expected.Count}");

        /// <summary>Compares every returned identity with independently sorted key/identity tuples, without sorting the reader output.<br/></summary>
        void Validate(LibraDexFileSession session)
        {
            var oracle = expected.OrderBy(x => x.Key).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
            foreach (var direction in new[] { QueryDirection.Ascending, QueryDirection.Descending })
            {
                using var reader = new Fixed32Scalar16RangeReader(session, rootOffset, profile, 0, 0, 0, 0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, direction, 40);
                int countRead = 0;
                while (direction == QueryDirection.Ascending ? reader.MoveNext() : reader.MovePrevious())
                {
                    ulong identity = reader.CurrentEncodedIdentityLow;
                    ulong high = reader.CurrentEncodedIdentityHigh;
                    int slot = direction == QueryDirection.Ascending ? countRead : oracle.Length - 1 - countRead;
                    if (slot < 0 || slot >= oracle.Length || identity != oracle[slot] || high != 0)
                        throw new InvalidDataException("Fixed32Scalar16 full-range ordering/parity failed.");
                    countRead++;
                }
                if (countRead != oracle.Length) throw new InvalidDataException("Fixed32Scalar16 full-range count failed.");
            }
        }
    }
}
