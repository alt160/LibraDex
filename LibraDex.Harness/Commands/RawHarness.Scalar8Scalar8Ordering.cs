using LibraDex;

internal static partial class RawHarness
{
    /// <summary>
    /// Reproduces the `SS8-8` mixed-prefix shelf-transform ordering defect with one deterministic full shelf.<br/>
    /// The source shelf is shared by root prefixes `0x00` and `0x01`; the `0x00` keys use second byte `0xF0`, while the later `0x01` keys use second byte `0x10`.<br/>
    /// A transform that partitions on the second byte therefore reverses full-key ownership even though each replacement shelf remains internally sorted.<br/>
    /// The command validates the live and reopened tuple streams, including exact tuple-set parity and the first global inversion.<br/>
    /// </summary>
    /// <param name="args">The harness command-line arguments.<br/></param>
    /// <returns>Zero when live and reopened traversal preserve exact globally ascending tuples; otherwise the command throws with focused diagnostics.<br/></returns>
    private static int RunScalar8Scalar8MixedPrefixOrderingSanity(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "ss8-8-mixed-prefix-ordering-sanity.lbdx"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);

        Scalar8Scalar8Profile profile = Scalar8Scalar8Profile.Default32KiB;
        int earlyCount = profile.MaxItemCount / 2;
        int laterCount = profile.MaxItemCount - earlyCount;
        ulong[] keys = new ulong[profile.MaxItemCount];
        ulong[] identities = new ulong[profile.MaxItemCount];
        const ulong earlyBase = 0x00F0_0000_0000_0000UL;
        const ulong laterBase = 0x0110_0000_0000_0000UL;
        int target = 0;
        for (int i = 0; i < earlyCount; i++)
        {
            ulong tuple = earlyBase + (ulong)i;
            keys[target] = tuple;
            identities[target] = tuple;
            target++;
        }

        for (int i = 0; i < laterCount; i++)
        {
            ulong tuple = laterBase + (ulong)i;
            keys[target] = tuple;
            identities[target] = tuple;
            target++;
        }

        ulong insertedKey = earlyBase + 10_000UL;
        ulong insertedIdentity = insertedKey;
        HashSet<(ulong Key, ulong Identity)> expected = new(profile.MaxItemCount + 1);
        for (int i = 0; i < keys.Length; i++)
            expected.Add((keys[i], identities[i]));
        expected.Add((insertedKey, insertedIdentity));

        DataKernelOptions options = new(
            AppendBufferSize: DefaultAppendBufferSize,
            ReservedPrefixBytes: 0,
            FlushToDiskOnCommit: false,
            MaxCommitGapCoalesceBytes: 512);
        SuperblockDeveloperMetadata metadata = new(
            DevIdentity: "LibraDexSS88MixedPrefix",
            DevCustomText: "ss8-8 mixed-prefix ordering proof",
            DevGuid: Guid.Parse("76312369-2303-42f0-a83b-cd43970a7664"),
            DevDate1UtcTicks: 1,
            DevDate2UtcTicks: 2,
            DevNumber: 108);

        long rootOffset;
        Scalar8Scalar8OrderingProofResult live;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "ss8mixord", 0));
            rootOffset = root.Offset;
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, keys, identities, [0x00, 0x01]);
            bool inserted = session.TrySplitScalar8Scalar8QueuedRootPrefix(
                root.Offset,
                rightPrefixByte: 0x00,
                profile,
                insertedKey,
                insertedIdentity,
                allowDuplicateKeys: true,
                out Scalar8Scalar8EncodedInsertResult insertResult);
            if (!inserted)
            {
                inserted = session.TrySplitScalar8Scalar8QueuedRootShelfTransform(
                    root.Offset,
                    rootPrefixByte: 0x00,
                    profile,
                    insertedKey,
                    insertedIdentity,
                    allowDuplicateKeys: true,
                    out insertResult);
            }

            if (!inserted)
            {
                inserted = session.TrySplitScalar8Scalar8QueuedParentRoute(
                    root.Offset,
                    profile,
                    insertedKey,
                    insertedIdentity,
                    allowDuplicateKeys: true,
                    maxRouterHops: 8,
                    out insertResult);
            }

            if (!inserted || insertResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted)
            {
                throw new InvalidDataException($"Expected queued SS8-8 fallback insertion, got {insertResult.Outcome}/{insertResult.StructuralKind}.");
            }
            live = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);
        }

        Scalar8Scalar8OrderingProofResult reopened;
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
            reopened = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);

        RunScalar8Scalar8SamePrefixAliasClearSanity(path, options, profile);
        RunScalar8Scalar8NestedParentAliasIsolationSanity(path, options, profile);

        long fileBytes = new FileInfo(path).Length;
        Console.WriteLine(
            $"ss8-8-mixed-prefix-ordering-sanity path={path} fileBytes={fileBytes:N0} " +
            $"expected={expected.Count:N0} liveCount={live.Count:N0} liveInversions={live.InversionCount:N0} liveMissing={live.MissingCount:N0} liveUnexpected={live.UnexpectedCount:N0} " +
            $"reopenedCount={reopened.Count:N0} reopenedInversions={reopened.InversionCount:N0} reopenedMissing={reopened.MissingCount:N0} reopenedUnexpected={reopened.UnexpectedCount:N0}");

        if (!live.IsValid || !reopened.IsValid)
        {
            Scalar8Scalar8OrderingProofResult firstFailure = !live.IsValid ? live : reopened;
            throw new InvalidDataException(
                $"SS8-8 mixed-prefix traversal is not globally ordered and exact. " +
                $"Count={firstFailure.Count}; Expected={expected.Count}; Inversions={firstFailure.InversionCount}; Missing={firstFailure.MissingCount}; Unexpected={firstFailure.UnexpectedCount}; " +
                $"FirstInversionOrdinal={firstFailure.FirstInversionOrdinal}; Prior=0x{firstFailure.FirstPriorKey:X16}/0x{firstFailure.FirstPriorIdentity:X16}; " +
                $"Current=0x{firstFailure.FirstCurrentKey:X16}/0x{firstFailure.FirstCurrentIdentity:X16}.");
        }

        return 0;
    }

    /// <summary>
    /// Measures exact tuple parity and global `(encoded key, encoded identity)` order for one complete `SS8-8` traversal.<br/>
    /// Expected tuples are copied into a remaining-set so duplicate or unknown rows are reported separately from missing rows and ordering inversions.<br/>
    /// The first inversion is retained for direct LXL diagnosis while the complete reader continues so the final counts describe the whole traversal.<br/>
    /// </summary>
    /// <param name="session">The live or reopened file session that owns the index.<br/></param>
    /// <param name="rootOffset">The `SS8-8` root router offset.<br/></param>
    /// <param name="profile">The shelf profile used by the routed index.<br/></param>
    /// <param name="expected">The complete expected encoded tuple set.<br/></param>
    /// <returns>Complete count, parity, and first-inversion diagnostics for the traversal.<br/></returns>
    private static Scalar8Scalar8OrderingProofResult MeasureScalar8Scalar8Ordering(
        LibraDexFileSession session,
        long rootOffset,
        Scalar8Scalar8Profile profile,
        HashSet<(ulong Key, ulong Identity)> expected)
    {
        HashSet<(ulong Key, ulong Identity)> remaining = new(expected);
        int count = 0;
        int inversionCount = 0;
        int unexpectedCount = 0;
        int firstInversionOrdinal = -1;
        ulong priorKey = 0;
        ulong priorIdentity = 0;
        ulong firstPriorKey = 0;
        ulong firstPriorIdentity = 0;
        ulong firstCurrentKey = 0;
        ulong firstCurrentIdentity = 0;
        bool hasPrior = false;

        using Scalar8Scalar8RangeReader reader = session.OpenScalar8Scalar8RangeReader(
            rootOffset,
            profile,
            lowerEncodedKey: 0,
            upperEncodedKey: ulong.MaxValue,
            QueryDirection.Ascending,
            maxRouterHops: 32);
        while (reader.MoveNext())
        {
            ulong key = reader.CurrentEncodedKey;
            ulong identity = reader.CurrentEncodedIdentity;
            if (!remaining.Remove((key, identity)))
                unexpectedCount++;

            if (hasPrior && (key < priorKey || (key == priorKey && identity < priorIdentity)))
            {
                inversionCount++;
                if (firstInversionOrdinal < 0)
                {
                    firstInversionOrdinal = count;
                    firstPriorKey = priorKey;
                    firstPriorIdentity = priorIdentity;
                    firstCurrentKey = key;
                    firstCurrentIdentity = identity;
                }
            }

            priorKey = key;
            priorIdentity = identity;
            hasPrior = true;
            count++;
        }

        return new Scalar8Scalar8OrderingProofResult(
            count,
            inversionCount,
            remaining.Count,
            unexpectedCount,
            firstInversionOrdinal,
            firstPriorKey,
            firstPriorIdentity,
            firstCurrentKey,
            firstCurrentIdentity,
            count == expected.Count && inversionCount == 0 && remaining.Count == 0 && unexpectedCount == 0);
    }

    /// <summary>
    /// Verifies that a safe same-prefix root shelf transform clears broader root aliases that no longer own any tuple.<br/>
    /// The full shelf contains only `0x00` root-prefix keys while root routes `0x00` and `0x01` initially share it, matching the broad empty-capacity aliases used during growth.<br/>
    /// After the transform, route `0x00` must retain the new child router and route `0x01` must be unset so future keys cannot enter a child topology owned by another full-key prefix.<br/>
    /// </summary>
    /// <param name="basePath">The main mixed-prefix proof path used to derive a sibling disposable file.<br/></param>
    /// <param name="options">The DataKernel options shared with the main ordering proof.<br/></param>
    /// <param name="profile">The fixed `SS8-8` shelf profile under test.<br/></param>
    private static void RunScalar8Scalar8SamePrefixAliasClearSanity(
        string basePath,
        DataKernelOptions options,
        Scalar8Scalar8Profile profile)
    {
        string path = Path.Combine(
            Path.GetDirectoryName(basePath)!,
            Path.GetFileNameWithoutExtension(basePath) + "-alias" + Path.GetExtension(basePath));
        File.Delete(path);

        int leftCount = profile.MaxItemCount / 2;
        int rightCount = profile.MaxItemCount - leftCount;
        ulong[] keys = new ulong[profile.MaxItemCount];
        ulong[] identities = new ulong[profile.MaxItemCount];
        const ulong rightBase = 0x0080_0000_0000_0000UL;
        int target = 0;
        for (int i = 0; i < leftCount; i++)
        {
            ulong tuple = (ulong)i;
            keys[target] = tuple;
            identities[target] = tuple;
            target++;
        }

        for (int i = 0; i < rightCount; i++)
        {
            ulong tuple = rightBase + (ulong)i;
            keys[target] = tuple;
            identities[target] = tuple;
            target++;
        }

        ulong insertedKey = rightBase + 10_000UL;
        ulong insertedIdentity = insertedKey;
        HashSet<(ulong Key, ulong Identity)> expected = new(profile.MaxItemCount + 1);
        for (int i = 0; i < keys.Length; i++)
            expected.Add((keys[i], identities[i]));
        expected.Add((insertedKey, insertedIdentity));

        SuperblockDeveloperMetadata metadata = new(
            DevIdentity: "LibraDexSS88AliasClear",
            DevCustomText: "ss8-8 same-prefix alias clear proof",
            DevGuid: Guid.Parse("162057a4-4487-4654-a23a-3594ac0ae42f"),
            DevDate1UtcTicks: 1,
            DevDate2UtcTicks: 2,
            DevNumber: 109);

        long rootOffset;
        Scalar8Scalar8OrderingProofResult live;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "ss8alias", 0));
            rootOffset = root.Offset;
            _ = session.CreateScalar8Scalar8ShelfAndLinkRootRoutes(root.Offset, profile, keys, identities, [0x00, 0x01]);
            if (!session.TrySplitScalar8Scalar8QueuedRootShelfTransform(
                root.Offset,
                rootPrefixByte: 0x00,
                profile,
                insertedKey,
                insertedIdentity,
                allowDuplicateKeys: true,
                out Scalar8Scalar8EncodedInsertResult insertResult) ||
                insertResult.Outcome != Scalar8Scalar8EncodedInsertOutcome.Inserted ||
                insertResult.StructuralKind != Scalar8Scalar8RoutedInsertKind.ShelfTransformSplit)
            {
                throw new InvalidDataException($"Expected same-prefix queued SS8-8 shelf transform, got {insertResult.Outcome}/{insertResult.StructuralKind}.");
            }

            if (session.FindRouterTarget(root.Offset, 0x00) == 0 || session.FindRouterTarget(root.Offset, 0x01) != 0)
                throw new InvalidDataException("Same-prefix SS8-8 shelf transform did not clear the broader root alias.");

            live = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);
        }

        Scalar8Scalar8OrderingProofResult reopened;
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            if (session.FindRouterTarget(rootOffset, 0x00) == 0 || session.FindRouterTarget(rootOffset, 0x01) != 0)
                throw new InvalidDataException("Reopened same-prefix SS8-8 topology did not preserve the cleared root alias.");

            reopened = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);
        }

        if (!live.IsValid || !reopened.IsValid)
            throw new InvalidDataException("Same-prefix SS8-8 alias clearing did not preserve exact globally ordered traversal.");

        Console.WriteLine($"ss8-8-same-prefix-alias-clear-sanity path={path} fileBytes={new FileInfo(path).Length:N0} tuples={expected.Count:N0}");
    }

    /// <summary>
    /// Carries complete parity and first-inversion evidence for one deterministic `SS8-8` tuple traversal.<br/>
    /// The result is diagnostic-only harness state and does not participate in production storage behavior.<br/>
    /// </summary>
    /// <param name="Count">The total number of tuples returned by the reader.<br/></param>
    /// <param name="InversionCount">The number of adjacent global-order inversions.<br/></param>
    /// <param name="MissingCount">The number of expected tuples not returned.<br/></param>
    /// <param name="UnexpectedCount">The number of duplicate or unknown tuples returned.<br/></param>
    /// <param name="FirstInversionOrdinal">The zero-based ordinal of the first inverted current tuple, or `-1` when ordered.<br/></param>
    /// <param name="FirstPriorKey">The encoded key immediately preceding the first inversion.<br/></param>
    /// <param name="FirstPriorIdentity">The encoded identity immediately preceding the first inversion.<br/></param>
    /// <param name="FirstCurrentKey">The encoded key at the first inversion.<br/></param>
    /// <param name="FirstCurrentIdentity">The encoded identity at the first inversion.<br/></param>
    /// <param name="IsValid">Whether count, tuple parity, and global ordering all match the expected stream.<br/></param>
    private readonly record struct Scalar8Scalar8OrderingProofResult(
        int Count,
        int InversionCount,
        int MissingCount,
        int UnexpectedCount,
        int FirstInversionOrdinal,
        ulong FirstPriorKey,
        ulong FirstPriorIdentity,
        ulong FirstCurrentKey,
        ulong FirstCurrentIdentity,
        bool IsValid);

    /// <summary>
    /// Proves that an in-place `SS8-8` shelf-to-router transform isolates the shelf from every sibling alias in its direct parent.<br/>
    /// The source uses the exact Wherzit date-key endpoints that exposed `FirstDifferentDepth=2` beneath a deeper claimed owner, distilled behind one nested parent whose `0xA0` and `0xA1` routes initially share the full shelf.<br/>
    /// After the `0xA0` shelf becomes a depth-two router, `0xA1` must be unset and its first later tuple must allocate an independent shelf rather than entering the transformed subtree.<br/>
    /// Live and reopened traversal then prove exact tuple conservation and global `(key, identity)` order.<br/>
    /// </summary>
    /// <param name="basePath">The main ordering-proof path used to derive a sibling disposable catalog.<br/></param>
    /// <param name="options">The DataKernel options shared with the main ordering proof.<br/></param>
    /// <param name="profile">The fixed `SS8-8` shelf profile under test.<br/></param>
    private static void RunScalar8Scalar8NestedParentAliasIsolationSanity(
        string basePath,
        DataKernelOptions options,
        Scalar8Scalar8Profile profile)
    {
        string path = Path.Combine(
            Path.GetDirectoryName(basePath)!,
            Path.GetFileNameWithoutExtension(basePath) + "-nested-parent-alias" + Path.GetExtension(basePath));
        File.Delete(path);

        const ulong firstKey = 0x1FA0_84E7_F300_0000UL;
        const ulong lastKey = 0x1FA0_F6F8_B000_0000UL;
        ulong range = lastKey - firstKey;
        ulong[] keys = new ulong[profile.MaxItemCount];
        ulong[] identities = new ulong[profile.MaxItemCount];
        HashSet<(ulong Key, ulong Identity)> expected = new(profile.MaxItemCount + 2);
        for (int i = 0; i < keys.Length; i++)
        {
            ulong key = firstKey + range * (ulong)i / (ulong)(keys.Length - 1);
            ulong identity = (ulong)i + 1;
            keys[i] = key;
            identities[i] = identity;
            expected.Add((key, identity));
        }

        ulong transformKey = firstKey + range / 2 + 1;
        ulong transformIdentity = (ulong)profile.MaxItemCount + 1;
        ulong siblingKey = 0x1FA1_84E7_F300_0000UL;
        ulong siblingIdentity = transformIdentity + 1;
        expected.Add((transformKey, transformIdentity));
        expected.Add((siblingKey, siblingIdentity));

        SuperblockDeveloperMetadata metadata = new(
            DevIdentity: "LibraDexSS88ParentAlias",
            DevCustomText: "ss8-8 nested parent alias isolation proof",
            DevGuid: Guid.Parse("ca2c18a8-282c-4c30-9452-f3b4a11a97a5"),
            DevDate1UtcTicks: 1,
            DevDate2UtcTicks: 2,
            DevNumber: 110);

        long rootOffset;
        long parentRouterOffset;
        long transformedRouterOffset;
        long siblingShelfOffset;
        Scalar8Scalar8OrderingProofResult live;
        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, metadata, DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot root, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "ss8parentalias", 0));
            rootOffset = root.Offset;
            (parentRouterOffset, transformedRouterOffset, _) = session.CreateScalar8Scalar8ShelfBehindChildRouterRoutes(
                root.Offset,
                rootPrefixByte: 0x1F,
                childPrefixBytes: [0xA0, 0xA1],
                profile,
                keys,
                identities);

            Scalar8Scalar8RoutedInsertResult transform = session.InsertWalkedRoutedScalar8Scalar8(
                root.Offset,
                profile,
                transformKey,
                transformIdentity,
                allowDuplicateKeys: true,
                maxRouterHops: 16);
            if (transform.InsertResult != Scalar8Scalar8InsertResult.Inserted ||
                transform.Kind != Scalar8Scalar8RoutedInsertKind.WalkedShelfTransformSplit)
            {
                throw new InvalidDataException($"Expected nested SS8-8 shelf transform, got {transform.Kind}/{transform.InsertResult}.");
            }
            if (session.FindRouterTarget(parentRouterOffset, 0xA0) != transformedRouterOffset ||
                session.FindRouterTarget(parentRouterOffset, 0xA1) != 0)
            {
                throw new InvalidDataException("Nested SS8-8 shelf transform did not isolate the transformed router from its sibling parent alias.");
            }

            Scalar8Scalar8RoutedInsertResult siblingInsert = session.InsertWalkedRoutedScalar8Scalar8(
                root.Offset,
                profile,
                siblingKey,
                siblingIdentity,
                allowDuplicateKeys: true,
                maxRouterHops: 16);
            if (siblingInsert.InsertResult != Scalar8Scalar8InsertResult.Inserted)
                throw new InvalidDataException($"Expected independent sibling-prefix SS8-8 insert, got {siblingInsert.Kind}/{siblingInsert.InsertResult}.");

            siblingShelfOffset = session.FindRouterTarget(parentRouterOffset, 0xA1);
            if (siblingShelfOffset == 0 || siblingShelfOffset == transformedRouterOffset)
                throw new InvalidDataException("The first sibling-prefix tuple did not receive independent SS8-8 storage.");

            live = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);
        }

        Scalar8Scalar8OrderingProofResult reopened;
        using (LibraDexFileSession session = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            if (session.FindRouterTarget(parentRouterOffset, 0xA0) != transformedRouterOffset ||
                session.FindRouterTarget(parentRouterOffset, 0xA1) != siblingShelfOffset)
            {
                throw new InvalidDataException("Reopened nested SS8-8 parent routes did not preserve isolated prefix ownership.");
            }

            reopened = MeasureScalar8Scalar8Ordering(session, rootOffset, profile, expected);
        }

        if (!live.IsValid || !reopened.IsValid)
            throw new InvalidDataException("Nested SS8-8 parent-alias isolation did not preserve exact globally ordered traversal.");

        Console.WriteLine(
            $"ss8-8-nested-parent-alias-isolation-sanity path={path} fileBytes={new FileInfo(path).Length:N0} " +
            $"tuples={expected.Count:N0} transformedRouter={transformedRouterOffset} siblingShelf={siblingShelfOffset}");
    }
}
