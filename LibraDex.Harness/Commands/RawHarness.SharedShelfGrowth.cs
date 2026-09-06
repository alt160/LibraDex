using System.Buffers.Binary;
using LibraDex;
using LibraDex.Layouts;

internal static partial class RawHarness
{
    /// <summary>
    /// Validates that copied `VS16` shelf growth redirects every reachable owner of one deliberately shared shelf.<br/>
    /// Two root prefixes are linked to the same 8 KiB shelf, routed inserts continue until the first growth, and exact live/reopen range cardinality proves that the obsolete copied tuples are no longer reachable through the sibling owner.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments; <c>--path</c> optionally selects the disposable LibraDex file.<br/></param>
    /// <returns>Zero when owner convergence and live/reopen cardinality validate.<br/></returns>
    private static int RunVarKeyScalar16SharedShelfGrowthSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "vs16-shared-shelf-growth-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        const int maxKeyLength = 64;
        VarKeyScalar16Profile profile = VarKeyScalar16Profile.Create(8 * 1024, maxKeyLength);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long oldShelfOffset;
        long grownShelfOffset = 0;
        int expectedCount = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(825), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs16grow", 0));
            rootOffset = root.Offset;
            (oldShelfOffset, _) = session.CreateVarKeyScalar16ShelfAndLinkRootRoute(rootOffset, 0x43, profile);
            if (!session.TryUpdateRouterRouteTargetIfCurrent(rootOffset, 0x44, 0, oldShelfOffset, out _))
                throw new InvalidDataException("The VS16 shared-growth fixture could not publish its sibling shelf owner.");

            for (int ordinal = 0; ordinal < 1_000_000; ordinal++)
            {
                byte[] key = CreateSharedShelfVarKey(ordinal);
                CreateVarKeyScalar16Identity(ordinal, out ulong identityHigh, out ulong identityLow);
                VarKeyScalar16RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar16(
                    rootOffset,
                    maxKeyLength,
                    key,
                    identityHigh,
                    identityLow,
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
                if (result.InsertResult != VarKeyScalar16InsertResult.Inserted)
                    throw new InvalidDataException($"The VS16 shared-growth fixture insert returned {result.Kind}/{result.InsertResult} at ordinal {ordinal:N0}.");

                expectedCount++;
                if (result.Kind == VarKeyScalar16RoutedInsertKind.WalkedGrow)
                {
                    grownShelfOffset = result.NewShelfOffset;
                    break;
                }
            }
            if (grownShelfOffset == 0)
                throw new InvalidDataException("The VS16 shared-growth fixture did not reach its first shelf-growth boundary.");

            VarKeyScalar16RoutePathTarget leftOwner = session.WalkVarKeyScalar16RoutePathTarget(rootOffset, CreateSharedShelfVarKey(0), LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
            VarKeyScalar16RoutePathTarget rightOwner = session.WalkVarKeyScalar16RoutePathTarget(rootOffset, CreateSharedShelfVarKey(1), LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
            if (leftOwner.Target.Offset != grownShelfOffset || rightOwner.Target.Offset != grownShelfOffset)
            {
                throw new InvalidDataException(
                    $"The VS16 shared-growth owner set did not converge on the replacement shelf. " +
                    $"Left={leftOwner.Target.Offset:N0}; Right={rightOwner.Target.Offset:N0}; Expected={grownShelfOffset:N0}.");
            }

            ValidateVarKeyScalar16SharedShelfGrowthCount(session, rootOffset, maxKeyLength, expectedCount);
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            ValidateVarKeyScalar16SharedShelfGrowthCount(reopened, rootOffset, maxKeyLength, expectedCount);

        File.Delete(path);
        Console.WriteLine(
            $"vs16-shared-shelf-growth-sanity ok items={expectedCount:N0} oldShelf={oldShelfOffset:N0} " +
            $"grownShelf={grownShelfOffset:N0} owners=2 live/reopen cardinality parity");
        return 0;
    }

    /// <summary>
    /// Validates exact `VS16` tuple cardinality across both prefixes participating in the shared-shelf growth fixture.<br/>
    /// The reader's physical-offset guard suppresses a genuinely shared shelf but cannot hide copied tuples at distinct offsets, so any stale owner makes this count exceed the authoritative insert count.<br/>
    /// </summary>
    /// <param name="session">The live or reopened LibraDex session to validate.<br/></param>
    /// <param name="rootOffset">The routed `VS16` root offset.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length configured for the fixture.<br/></param>
    /// <param name="expectedCount">The exact number of successfully inserted tuples.<br/></param>
    private static void ValidateVarKeyScalar16SharedShelfGrowthCount(
        LibraDexFileSession session,
        long rootOffset,
        int maxKeyLength,
        int expectedCount)
    {
        byte[] lower = [0x43, 0x10, 0, 0, 0, 0];
        byte[] upper = [0x44, 0x10, 0xFF, 0xFF, 0xFF, 0xFF];
        using VarKeyScalar16RangeReader reader = new(
            session,
            rootOffset,
            maxKeyLength,
            lower,
            upper,
            LibraDexFileSession.DefaultVarKeyScalar16MaxRouterHops);
        if (reader.Count != expectedCount)
            throw new InvalidDataException($"The VS16 shared-growth traversal returned {reader.Count:N0} tuples; expected {expectedCount:N0}.");
    }

    /// <summary>
    /// Validates that copied `VV` shelf growth redirects every reachable owner of one deliberately shared shelf.<br/>
    /// The fixture aliases two root prefixes to one 8 KiB variable-key/variable-identity shelf and proves exact owner convergence plus live/reopen range cardinality after the first copied growth.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments; <c>--path</c> optionally selects the disposable LibraDex file.<br/></param>
    /// <returns>Zero when owner convergence and live/reopen cardinality validate.<br/></returns>
    private static int RunVarKeyVarIdentitySharedShelfGrowthSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "vv-shared-shelf-growth-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        const int maxKeyLength = 64;
        const int maxIdentityLength = 64;
        VarKeyVarIdentityProfile profile = VarKeyVarIdentityProfile.Create(8 * 1024, maxKeyLength, maxIdentityLength);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long oldShelfOffset;
        long grownShelfOffset = 0;
        int expectedCount = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(826), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vvgrow", 0));
            rootOffset = root.Offset;
            (oldShelfOffset, _) = session.CreateVarKeyVarIdentityShelfAndLinkRootRoute(rootOffset, 0x43, profile);
            if (!session.TryUpdateRouterRouteTargetIfCurrent(rootOffset, 0x44, 0, oldShelfOffset, out _))
                throw new InvalidDataException("The VV shared-growth fixture could not publish its sibling shelf owner.");

            for (int ordinal = 0; ordinal < 1_000_000; ordinal++)
            {
                byte[] key = CreateSharedShelfVarKey(ordinal);
                byte[] identity = CreateScalar8VarIdentity(ordinal, 48, fixedLength: true);
                VarKeyVarIdentityRoutedInsertResult result = session.InsertWalkedRoutedVarKeyVarIdentity(
                    rootOffset,
                    maxKeyLength,
                    maxIdentityLength,
                    key,
                    identity,
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
                if (result.InsertResult != VarKeyVarIdentityInsertResult.Inserted)
                    throw new InvalidDataException($"The VV shared-growth fixture insert returned {result.Kind}/{result.InsertResult} at ordinal {ordinal:N0}.");

                expectedCount++;
                if (result.Kind == VarKeyVarIdentityRoutedInsertKind.WalkedGrow)
                {
                    grownShelfOffset = result.NewOffset;
                    break;
                }
            }
            if (grownShelfOffset == 0)
                throw new InvalidDataException("The VV shared-growth fixture did not reach its first shelf-growth boundary.");

            VarKeyVarIdentityRoutePathTarget leftOwner = session.WalkVarKeyVarIdentityRoutePathTarget(rootOffset, CreateSharedShelfVarKey(0), LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            VarKeyVarIdentityRoutePathTarget rightOwner = session.WalkVarKeyVarIdentityRoutePathTarget(rootOffset, CreateSharedShelfVarKey(1), LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
            if (leftOwner.Target.Offset != grownShelfOffset || rightOwner.Target.Offset != grownShelfOffset)
            {
                throw new InvalidDataException(
                    $"The VV shared-growth owner set did not converge on the replacement shelf. " +
                    $"Left={leftOwner.Target.Offset:N0}; Right={rightOwner.Target.Offset:N0}; Expected={grownShelfOffset:N0}.");
            }

            ValidateVarKeyVarIdentitySharedShelfGrowthCount(session, rootOffset, maxKeyLength, maxIdentityLength, expectedCount);
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            ValidateVarKeyVarIdentitySharedShelfGrowthCount(reopened, rootOffset, maxKeyLength, maxIdentityLength, expectedCount);

        File.Delete(path);
        Console.WriteLine(
            $"vv-shared-shelf-growth-sanity ok items={expectedCount:N0} oldShelf={oldShelfOffset:N0} " +
            $"grownShelf={grownShelfOffset:N0} owners=2 live/reopen cardinality parity");
        return 0;
    }

    /// <summary>
    /// Validates exact `VV` tuple cardinality across both prefixes participating in the shared-shelf growth fixture.<br/>
    /// A stale sibling owner exposes both the old shelf and its copied replacement as distinct physical offsets, making this complete range count fail without relying on reader-side identity deduplication.<br/>
    /// </summary>
    /// <param name="session">The live or reopened LibraDex session to validate.<br/></param>
    /// <param name="rootOffset">The routed `VV` root offset.<br/></param>
    /// <param name="maxKeyLength">The maximum raw key length configured for the fixture.<br/></param>
    /// <param name="maxIdentityLength">The maximum raw identity length configured for the fixture.<br/></param>
    /// <param name="expectedCount">The exact number of successfully inserted tuples.<br/></param>
    private static void ValidateVarKeyVarIdentitySharedShelfGrowthCount(
        LibraDexFileSession session,
        long rootOffset,
        int maxKeyLength,
        int maxIdentityLength,
        int expectedCount)
    {
        byte[] lower = [0x43, 0x10, 0, 0, 0, 0];
        byte[] upper = [0x44, 0x10, 0xFF, 0xFF, 0xFF, 0xFF];
        using VarKeyVarIdentityRangeReader reader = session.OpenVarKeyVarIdentityRangeReader(
            rootOffset,
            maxKeyLength,
            maxIdentityLength,
            lower,
            upper,
            LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
        if (reader.Count != expectedCount)
            throw new InvalidDataException($"The VV shared-growth traversal returned {reader.Count:N0} tuples; expected {expectedCount:N0}.");
    }

    /// <summary>
    /// Validates `SV16` owner-set publication at the exhausted scalar-key depth where copied shelf growth is allowed.<br/>
    /// Two near-identical scalar keys first force an ordinary 8 KiB shelf to split at byte depth fifteen; the fixture then finds an unused direct-route alias of one resulting shelf, grows that shelf with duplicate-key identities, and proves the alias follows the replacement live and after reopen.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments; <c>--path</c> optionally selects the disposable LibraDex file.<br/></param>
    /// <returns>Zero when terminal-depth owner convergence and live/reopen cardinality validate.<br/></returns>
    private static int RunScalar16VarIdentitySharedShelfGrowthSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "sv16-shared-shelf-growth-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        const int identityLength = 96;
        Scalar16VarIdentityProfile profile = Scalar16VarIdentityProfile.Create(8 * 1024, identityLength);
        DataKernelOptions options = CreateDesignPerfOptions();
        long rootOffset;
        long oldShelfOffset = 0;
        long grownShelfOffset = 0;
        ulong aliasKeyLow = 0;
        bool aliasFound = false;
        int expectedCount = 0;
        int nextIdentityOrdinal = 0;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(827), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "sv16grow", 0));
            rootOffset = root.Offset;
            _ = session.CreateScalar16VarIdentityShelfAndLinkRootRoute(rootOffset, 0, profile);

            bool splitObserved = false;
            for (; nextIdentityOrdinal < 1_000_000; nextIdentityOrdinal++)
            {
                ulong keyLow = (nextIdentityOrdinal & 1) == 0 ? 0x43UL : 0x44UL;
                byte[] identity = CreateScalar8VarIdentity(nextIdentityOrdinal, identityLength, fixedLength: true);
                Scalar16VarIdentityRoutedInsertResult result = session.InsertWalkedRoutedScalar16VarIdentity(
                    rootOffset,
                    identityLength,
                    encodedKeyHigh: 0,
                    keyLow,
                    identity,
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
                if (result.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                    throw new InvalidDataException($"The SV16 shared-growth setup insert returned {result.Kind}/{result.InsertResult} at ordinal {nextIdentityOrdinal:N0}.");

                expectedCount++;
                if (result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedShelfSplit)
                {
                    splitObserved = true;
                    nextIdentityOrdinal++;
                    break;
                }
            }
            if (!splitObserved)
                throw new InvalidDataException("The SV16 shared-growth fixture did not reach its terminal-byte split.");

            Scalar16VarIdentityRoutePathTarget keyOwner = session.WalkScalar16VarIdentityRoutePathTarget(
                rootOffset,
                encodedKeyHigh: 0,
                encodedKeyLow: 0x43,
                LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
            oldShelfOffset = keyOwner.Target.Offset;
            for (ulong candidate = 0; candidate <= byte.MaxValue; candidate++)
            {
                if (candidate == 0x43 || candidate == 0x44)
                    continue;

                Scalar16VarIdentityRoutePathTarget candidateOwner = session.WalkScalar16VarIdentityRoutePathTarget(
                    rootOffset,
                    encodedKeyHigh: 0,
                    candidate,
                    LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
                if (candidateOwner.Target.Offset == oldShelfOffset && candidateOwner.Target.RouterDepth == keyOwner.Target.RouterDepth)
                {
                    aliasKeyLow = candidate;
                    aliasFound = true;
                    break;
                }
            }
            if (!aliasFound || keyOwner.Target.RouterDepth < Scalar16VarIdentityLayout.KeySize - 1)
            {
                throw new InvalidDataException(
                    $"The SV16 shared-growth fixture did not find a terminal-depth sibling owner. " +
                    $"Shelf={oldShelfOffset:N0}; Depth={keyOwner.Target.RouterDepth:N0}; Alias={aliasKeyLow:X2}.");
            }

            for (; nextIdentityOrdinal < 1_000_000; nextIdentityOrdinal++)
            {
                byte[] identity = CreateScalar8VarIdentity(nextIdentityOrdinal, identityLength, fixedLength: true);
                Scalar16VarIdentityRoutedInsertResult result = session.InsertWalkedRoutedScalar16VarIdentity(
                    rootOffset,
                    identityLength,
                    encodedKeyHigh: 0,
                    encodedKeyLow: 0x43,
                    identity,
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
                if (result.InsertResult != Scalar16VarIdentityInsertResult.Inserted)
                    throw new InvalidDataException($"The SV16 shared-growth insert returned {result.Kind}/{result.InsertResult} at ordinal {nextIdentityOrdinal:N0}.");

                expectedCount++;
                if (result.Kind == Scalar16VarIdentityRoutedInsertKind.WalkedGrow)
                {
                    grownShelfOffset = result.NewShelfOffset;
                    break;
                }
            }
            if (grownShelfOffset == 0)
                throw new InvalidDataException("The SV16 shared-growth fixture did not reach its first terminal-depth shelf-growth boundary.");

            Scalar16VarIdentityRoutePathTarget grownOwner = session.WalkScalar16VarIdentityRoutePathTarget(
                rootOffset,
                encodedKeyHigh: 0,
                encodedKeyLow: 0x43,
                LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
            Scalar16VarIdentityRoutePathTarget aliasOwner = session.WalkScalar16VarIdentityRoutePathTarget(
                rootOffset,
                encodedKeyHigh: 0,
                aliasKeyLow,
                LibraDexFileSession.DefaultScalar16VarIdentityMaxRouterHops);
            if (grownOwner.Target.Offset != grownShelfOffset || aliasOwner.Target.Offset != grownShelfOffset)
            {
                throw new InvalidDataException(
                    $"The SV16 shared-growth owner set did not converge on the replacement shelf. " +
                    $"Key={grownOwner.Target.Offset:N0}; Alias={aliasOwner.Target.Offset:N0}; Expected={grownShelfOffset:N0}.");
            }

            ValidateScalar16VarIdentitySharedShelfGrowthCount(session, rootOffset, identityLength, expectedCount);
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            ValidateScalar16VarIdentitySharedShelfGrowthCount(reopened, rootOffset, identityLength, expectedCount);

        File.Delete(path);
        Console.WriteLine(
            $"sv16-shared-shelf-growth-sanity ok items={expectedCount:N0} oldShelf={oldShelfOffset:N0} " +
            $"grownShelf={grownShelfOffset:N0} alias={aliasKeyLow:X2} live/reopen cardinality parity");
        return 0;
    }

    /// <summary>
    /// Validates exact `SV16` cardinality over the terminal-byte range containing the fixture's two populated keys and unused shared-route aliases.<br/>
    /// The range deliberately spans every final-byte route so stale copied shelves remain observable even when the incoming key's direct owner was updated correctly.<br/>
    /// </summary>
    /// <param name="session">The live or reopened LibraDex session to validate.<br/></param>
    /// <param name="rootOffset">The routed `SV16` root offset.<br/></param>
    /// <param name="identityLength">The maximum raw identity length configured for the fixture.<br/></param>
    /// <param name="expectedCount">The exact number of successfully inserted tuples.<br/></param>
    private static void ValidateScalar16VarIdentitySharedShelfGrowthCount(
        LibraDexFileSession session,
        long rootOffset,
        int identityLength,
        int expectedCount)
    {
        using Scalar16VarIdentityRangeReader reader = session.OpenScalar16VarIdentityRangeReader(
            rootOffset,
            identityLength,
            lowerEncodedKeyHigh: 0,
            lowerEncodedKeyLow: 0,
            upperEncodedKeyHigh: 0,
            upperEncodedKeyLow: byte.MaxValue);
        if (reader.Count != expectedCount)
            throw new InvalidDataException($"The SV16 shared-growth traversal returned {reader.Count:N0} tuples; expected {expectedCount:N0}.");
    }

    /// <summary>
    /// Creates one compact variable key whose first byte alternates between the two shared root owners while the remaining bytes preserve tuple uniqueness.<br/>
    /// The fixed second byte keeps the test range narrow and the big-endian ordinal makes lexical ordering deterministic without allocating formatted strings.<br/>
    /// </summary>
    /// <param name="ordinal">The zero-based tuple ordinal.<br/></param>
    /// <returns>The six-byte raw key used by the `VS16` and `VV` shared-growth fixtures.<br/></returns>
    private static byte[] CreateSharedShelfVarKey(int ordinal)
    {
        byte[] key = new byte[6];
        key[0] = (ordinal & 1) == 0 ? (byte)0x43 : (byte)0x44;
        key[1] = 0x10;
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(2), ordinal);
        return key;
    }
}
