using LibraDex;
using LibraDex.Views;

internal static partial class RawHarness
{
    /// <summary>
    /// Builds ordinary transformed and same-key duplicate `VS8` topologies, then proves their route-order shelf intervals before and after reopen.<br/>
    /// The command fails on noncontiguous aliases, multiple-parent targets, router cycles, overlapping extents, mutation drift, or tuple-sequence disagreement with the ordinary range reader.<br/>
    /// </summary>
    /// <param name="args">The harness options controlling path, ordinary item count, duplicate item count, key length, and root-prefix fanout.<br/></param>
    /// <returns>Zero when both live-session and reopened topology proofs pass.<br/></returns>
    private static int RunVarKeyScalar8ScoopTopologyProof(string[] args)
    {
        string path = GetOption(args, "--path", Path.Combine(@"T:\LibraDex", "vs8-scoop-topology-proof.lbdx"));
        int itemCount = GetIntOption(args, "--items", 65_536);
        int duplicateItemCount = GetIntOption(args, "--duplicate-items", 4_096);
        int keyLength = GetIntOption(args, "--key-length", 24);
        int prefixCount = GetIntOption(args, "--prefix-count", 8);
        bool requireTransform = GetBoolOption(args, "--require-transform", true);
        if (itemCount <= 0 || duplicateItemCount <= 1 || keyLength <= 0 || keyLength > 1024 || prefixCount <= 0 || prefixCount > 256)
            throw new ArgumentOutOfRangeException(nameof(args), "VS8 scoop topology proof requires positive ordinary items, at least two duplicate items, key length 1-1024, and prefix count 1-256.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        DataKernelOptions options = CreateDesignPerfOptions();
        long ordinaryRootOffset;
        long duplicateRootOffset;
        long growthCount = 0;
        long transformCount = 0;
        byte[] duplicateKey = new byte[keyLength];
        duplicateKey.AsSpan().Fill(0xA5);

        using (LibraDexFileSession session = LibraDexFileSession.Initialize(path, options, CreateDesignPerfMetadata(9461), DataKernelTelemetryOptions.EnabledOptions))
        {
            (RouterSnapshot ordinaryRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(0, "vs8scoop", 0));
            ordinaryRootOffset = ordinaryRoot.Offset;
            for (int prefix = 0; prefix < prefixCount; prefix++)
                _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(ordinaryRootOffset, (byte)prefix, VarKeyScalar8Profile.DefaultInitial);

            for (int i = 0; i < itemCount; i++)
            {
                byte[] key = CreateVarKeyScalar8Key(i, keyLength, prefixCount);
                VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(
                    ordinaryRootOffset,
                    maxKeyLength: 1024,
                    key,
                    CreateVarKeyScalar8Identity(i),
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
                if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                    throw new InvalidDataException($"VS8 scoop ordinary insert {i} returned {result.Kind}/{result.InsertResult}.");

                if (result.Kind == VarKeyScalar8RoutedInsertKind.WalkedGrow)
                    growthCount++;
                else if (result.Kind == VarKeyScalar8RoutedInsertKind.WalkedShelfTransformSplit)
                    transformCount++;
            }

            (RouterSnapshot duplicateRoot, _) = session.CreateRootRouterIndex(CreateHarnessSlot(1, "vs8scoopdup", 0));
            duplicateRootOffset = duplicateRoot.Offset;
            _ = session.CreateVarKeyScalar8ShelfAndLinkRootRoute(duplicateRootOffset, duplicateKey[0], VarKeyScalar8Profile.DefaultInitial);
            for (int i = 0; i < duplicateItemCount; i++)
            {
                VarKeyScalar8RoutedInsertResult result = session.InsertWalkedRoutedVarKeyScalar8(
                    duplicateRootOffset,
                    maxKeyLength: 1024,
                    duplicateKey,
                    CreateVarKeyScalar8Identity(i),
                    allowDuplicateKeys: true,
                    maxRouterHops: LibraDexFileSession.DefaultVarKeyScalar8MaxRouterHops);
                if (result.InsertResult != VarKeyScalar8InsertResult.Inserted)
                    throw new InvalidDataException($"VS8 scoop duplicate insert {i} returned {result.Kind}/{result.InsertResult}.");
            }

            VarKeyScalar8ScoopTopologyDiagnostics ordinary = session.InspectVarKeyScalar8ScoopTopology(ordinaryRootOffset, maxKeyLength: 1024);
            VarKeyScalar8ScoopTopologyDiagnostics duplicate = session.InspectVarKeyScalar8ScoopTopology(duplicateRootOffset, maxKeyLength: 1024);
            ValidateVarKeyScalar8ScoopTopologyProof("live ordinary", ordinary, itemCount, requireTransformedTopology: requireTransform);
            ValidateVarKeyScalar8ScoopTopologyProof("live duplicate", duplicate, duplicateItemCount, requireTransformedTopology: false);
            ValidateVarKeyScalar8ScoopCounts(session, ordinaryRootOffset, itemCount, keyLength, prefixCount, ordinary, "live ordinary");
            ValidateVarKeyScalar8ScoopCount(session, duplicateRootOffset, duplicateKey, duplicateKey, duplicateItemCount, "live duplicate exact");
            if (!duplicate.Extents.Any(static extent => extent.Kind != VarKeyScalar8ScoopExtentKind.OrdinaryShelf))
                throw new InvalidDataException("VS8 scoop duplicate fixture did not reach duplicate-run or terminal identity topology.");

            WriteVarKeyScalar8ScoopTopologySummary("live ordinary", ordinary);
            WriteVarKeyScalar8ScoopTopologySummary("live duplicate", duplicate);
        }

        using (LibraDexFileSession reopened = LibraDexFileSession.Open(path, options, DataKernelTelemetryOptions.EnabledOptions))
        {
            VarKeyScalar8ScoopTopologyDiagnostics ordinary = reopened.InspectVarKeyScalar8ScoopTopology(ordinaryRootOffset, maxKeyLength: 1024);
            VarKeyScalar8ScoopTopologyDiagnostics duplicate = reopened.InspectVarKeyScalar8ScoopTopology(duplicateRootOffset, maxKeyLength: 1024);
            ValidateVarKeyScalar8ScoopTopologyProof("reopened ordinary", ordinary, itemCount, requireTransformedTopology: requireTransform);
            ValidateVarKeyScalar8ScoopTopologyProof("reopened duplicate", duplicate, duplicateItemCount, requireTransformedTopology: false);
            ValidateVarKeyScalar8ScoopCounts(reopened, ordinaryRootOffset, itemCount, keyLength, prefixCount, ordinary, "reopened ordinary");
            ValidateVarKeyScalar8ScoopCount(reopened, duplicateRootOffset, duplicateKey, duplicateKey, duplicateItemCount, "reopened duplicate exact");
            WriteVarKeyScalar8ScoopTopologySummary("reopened ordinary", ordinary);
            WriteVarKeyScalar8ScoopTopologySummary("reopened duplicate", duplicate);
        }

        Console.WriteLine($"vs8-scoop-topology-proof ok path={path} items={itemCount} duplicateItems={duplicateItemCount} keyLength={keyLength} prefixCount={prefixCount} requireTransform={requireTransform} growths={growthCount} transforms={transformCount}");
        return 0;
    }

    /// <summary>
    /// Proves the production `VS8` shelf-scoop counter against the ordinary range reader across full, exact, reversed-source, and cross-extent ranges.<br/>
    /// Generated endpoints deliberately come from different insertion ordinals and are normalized by encoded-key order so the proof does not assume insertion order equals route order.<br/>
    /// The topology's first/last encoded keys additionally force one complete boundary-to-boundary scoop over every logical extent.<br/>
    /// </summary>
    /// <param name="session">The live or reopened session containing the proof index.<br/></param>
    /// <param name="rootRouterOffset">The ordinary `VS8` root router offset.<br/></param>
    /// <param name="itemCount">The number of inserted ordinary tuples.<br/></param>
    /// <param name="keyLength">The generated encoded key length.<br/></param>
    /// <param name="prefixCount">The generated root-prefix fanout.<br/></param>
    /// <param name="topology">The already validated topology used to obtain complete extent bounds.<br/></param>
    /// <param name="label">The live/reopen label included in failures and summary output.<br/></param>
    private static void ValidateVarKeyScalar8ScoopCounts(
        LibraDexFileSession session,
        long rootRouterOffset,
        int itemCount,
        int keyLength,
        int prefixCount,
        VarKeyScalar8ScoopTopologyDiagnostics topology,
        string label)
    {
        VarKeyScalar8ScoopExtentDiagnostics[] nonEmpty = topology.Extents.Where(static extent => extent.ItemCount != 0).ToArray();
        if (nonEmpty.Length == 0)
            throw new InvalidDataException($"VS8 scoop {label} contains no non-empty extents for count proof.");

        ValidateVarKeyScalar8ScoopCount(
            session,
            rootRouterOffset,
            nonEmpty[0].FirstKey,
            nonEmpty[^1].LastKey,
            itemCount,
            $"{label} full");

        const int sampleCount = 128;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            int firstOrdinal = (int)(((long)sample * 104_729 + 17) % itemCount);
            int secondOrdinal = (int)(((long)sample * 65_537 + itemCount / 3 + 31) % itemCount);
            byte[] first = CreateVarKeyScalar8Key(firstOrdinal, keyLength, prefixCount);
            byte[] second = CreateVarKeyScalar8Key(secondOrdinal, keyLength, prefixCount);
            ReadOnlySpan<byte> lower = first.AsSpan().SequenceCompareTo(second) <= 0 ? first : second;
            ReadOnlySpan<byte> upper = first.AsSpan().SequenceCompareTo(second) <= 0 ? second : first;
            ValidateVarKeyScalar8ScoopCount(session, rootRouterOffset, lower, upper, expectedCount: null, $"{label} sample {sample}");
        }

        Console.WriteLine($"vs8 scoop {label} count parity: full=1 sampled={sampleCount} status=ok");
    }

    /// <summary>
    /// Compares one production `VS8` shelf-scoop count with the ordinary reader count and an optional fixture cardinality.<br/>
    /// The reader remains an independent slot-decoding oracle; the production result must match it exactly before performance evidence is accepted.<br/>
    /// </summary>
    /// <param name="session">The session containing the routed index.<br/></param>
    /// <param name="rootRouterOffset">The routed `VS8` root router offset.<br/></param>
    /// <param name="lowerKey">The inclusive encoded lower key.<br/></param>
    /// <param name="upperKey">The inclusive encoded upper key.<br/></param>
    /// <param name="expectedCount">An optional independently known fixture cardinality.<br/></param>
    /// <param name="label">The range label included in a parity failure.<br/></param>
    private static void ValidateVarKeyScalar8ScoopCount(
        LibraDexFileSession session,
        long rootRouterOffset,
        ReadOnlySpan<byte> lowerKey,
        ReadOnlySpan<byte> upperKey,
        long? expectedCount,
        string label)
    {
        long scoopCount = session.CountVarKeyScalar8IdentityRange(rootRouterOffset, maxKeyLength: 1024, lowerKey, upperKey);
        using VarKeyScalar8RangeReader reader = session.OpenVarKeyScalar8RangeReader(
            rootRouterOffset,
            maxKeyLength: 1024,
            lowerKey,
            upperKey,
            decodeLogicalKeys: false);
        long readerCount = reader.Count;
        if (scoopCount != readerCount || (expectedCount.HasValue && scoopCount != expectedCount.Value))
        {
            throw new InvalidDataException(
                $"VS8 scoop {label} count mismatch: scoop={scoopCount}, reader={readerCount}, expected={(expectedCount.HasValue ? expectedCount.Value : -1)} lower={Convert.ToHexString(lowerKey)} upper={Convert.ToHexString(upperKey)}.");
        }
    }

    /// <summary>
    /// Applies the mandatory invariant and cardinality gates to one `VS8` scoop topology result.<br/>
    /// Ordinary fixtures may additionally require more than one logical extent so a one-shelf pass cannot masquerade as transformed-topology evidence.<br/>
    /// </summary>
    /// <param name="label">The fixture label included in any failure.<br/></param>
    /// <param name="proof">The completed read-only topology proof.<br/></param>
    /// <param name="expectedItemCount">The expected number of routed identities.<br/></param>
    /// <param name="requireTransformedTopology">Whether the fixture must expose multiple logical extents and at least one child router beyond the root.<br/></param>
    private static void ValidateVarKeyScalar8ScoopTopologyProof(
        string label,
        VarKeyScalar8ScoopTopologyDiagnostics proof,
        long expectedItemCount,
        bool requireTransformedTopology)
    {
        if (!proof.AllInvariantsHold || !proof.SequenceMatchesReader)
        {
            string overlapEvidence = BuildVarKeyScalar8ScoopOverlapEvidence(proof);
            throw new InvalidDataException(
                $"VS8 scoop {label} invariants failed: aliases={proof.NonContiguousAliasCount}, parents={proof.MultipleParentTargetCount}, cycles={proof.RouterCycleCount}, overlaps={proof.OverlappingExtentCount}, routeItems={proof.RouteTupleCount}, readerItems={proof.ReaderTupleCount}, versions={proof.StartMutationVersion}/{proof.EndMutationVersion}, sequence={proof.SequenceMatchesReader}, overlapEvidence={overlapEvidence}.");
        }

        if (proof.RouteTupleCount != expectedItemCount || proof.ReaderTupleCount != expectedItemCount)
            throw new InvalidDataException($"VS8 scoop {label} expected {expectedItemCount} identities, got route={proof.RouteTupleCount} reader={proof.ReaderTupleCount}.");

        if (requireTransformedTopology && (proof.Extents.Length < 2 || proof.RouterCount < 2))
            throw new InvalidDataException($"VS8 scoop {label} did not reach transformed topology: routers={proof.RouterCount}, extents={proof.Extents.Length}.");
    }

    /// <summary>
    /// Formats the first four non-increasing logical-extent boundaries for a failed `VS8` scoop proof.<br/>
    /// Each entry preserves extent ordinals, head offsets, boundary keys, and boundary identities so a routing defect remains diagnosable without dumping every tuple.<br/>
    /// </summary>
    /// <param name="proof">The completed topology proof whose ordered extents will be checked.<br/></param>
    /// <returns>A compact semicolon-delimited boundary description, or `none` when no key-order overlap is present.<br/></returns>
    private static string BuildVarKeyScalar8ScoopOverlapEvidence(VarKeyScalar8ScoopTopologyDiagnostics proof)
    {
        List<string> evidence = [];
        VarKeyScalar8ScoopExtentDiagnostics? previous = null;
        foreach (VarKeyScalar8ScoopExtentDiagnostics extent in proof.Extents)
        {
            if (extent.ItemCount == 0)
                continue;

            if (previous is not null && previous.Value.LastKey.AsSpan().SequenceCompareTo(extent.FirstKey) >= 0)
            {
                VarKeyScalar8ScoopExtentDiagnostics prior = previous.Value;
                evidence.Add(
                    $"{prior.Ordinal}@{prior.HeadOffset}:{Convert.ToHexString(prior.LastKey)}/{prior.LastIdentity}>={extent.Ordinal}@{extent.HeadOffset}:{Convert.ToHexString(extent.FirstKey)}/{extent.FirstIdentity}");
                if (evidence.Count == 4)
                    break;
            }

            previous = extent;
        }

        return evidence.Count == 0 ? "none" : string.Join(';', evidence);
    }

    /// <summary>
    /// Writes one compact `VS8` scoop proof summary without dumping per-extent keys or identity payloads.<br/>
    /// The summary keeps router, extent, alias, overlap, count, version, and sequence evidence visible in captured harness logs.<br/>
    /// </summary>
    /// <param name="label">The fixture label to print.<br/></param>
    /// <param name="proof">The completed topology proof to summarize.<br/></param>
    private static void WriteVarKeyScalar8ScoopTopologySummary(string label, VarKeyScalar8ScoopTopologyDiagnostics proof)
    {
        int ordinaryCount = 0;
        int duplicateCount = 0;
        int terminalCount = 0;
        foreach (VarKeyScalar8ScoopExtentDiagnostics extent in proof.Extents)
        {
            if (extent.Kind == VarKeyScalar8ScoopExtentKind.OrdinaryShelf)
                ordinaryCount++;
            else if (extent.Kind == VarKeyScalar8ScoopExtentKind.DuplicateRun)
                duplicateCount++;
            else if (extent.Kind == VarKeyScalar8ScoopExtentKind.TerminalIdentityRoot)
                terminalCount++;
        }

        Console.WriteLine(
            $"vs8 scoop {label}: routers={proof.RouterCount} targetRuns={proof.RouteTargetRunCount} extents={proof.Extents.Length} ordinary={ordinaryCount} duplicate={duplicateCount} terminal={terminalCount} empty={proof.EmptyExtentCount} aliases={proof.NonContiguousAliasCount} parents={proof.MultipleParentTargetCount} cycles={proof.RouterCycleCount} overlaps={proof.OverlappingExtentCount} tuples={proof.RouteTupleCount}/{proof.ReaderTupleCount} versions={proof.StartMutationVersion}/{proof.EndMutationVersion} sequence={proof.SequenceMatchesReader} hash={proof.RouteTupleHash}");
    }
}
